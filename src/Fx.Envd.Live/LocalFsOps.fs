namespace Fx.Envd.Live

open System
open System.IO
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Effects.Live
open Fx.Envd

/// envd's filesystem operations on this machine: ranged reads and atomic writes touch files here;
/// batch stat, patches and search are the generic ones over `LocalFs`; watching is `LocalWatch`. The
/// only file besides `LocalFs` that reads and writes files for the protocol.
module LocalFsOps =
    let private classify (path: string) (e: exn) : FsError option =
        match e with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Some(NotFound path)
        | :? UnauthorizedAccessException -> Some(AccessDenied path)
        | :? PathTooLongException -> Some(InvalidPath(path, "too long"))
        | :? IOException as e -> Some(IoFailed(path, e.Message))
        | :? ArgumentException as e -> Some(InvalidPath(path, e.Message))
        | _ -> None

    let private rethrow (e: exn) : 'T =
        ExceptionDispatchInfo.Capture(e).Throw()
        Unchecked.defaultof<'T>

    let private attempt (path: string) (operation: unit -> Task<Result<'T, FsError>>) : Task<Result<'T, FsError>> =
        if String.IsNullOrEmpty path || not (Path.IsPathFullyQualified path) then
            Task.FromResult(Error(InvalidPath(path, "not absolute")))
        else
            task {
                try
                    return! operation ()
                with e ->
                    match classify path e with
                    | Some error -> return Error error
                    | None -> return rethrow e
            }

    let readRange (path: string) (offset: int64) (count: int) (cancellation: CancellationToken) =
        FsOps.checkRange offset count

        attempt path (fun () ->
            if Directory.Exists path then
                Task.FromResult(Error(IsADirectory path))
            else
                task {
                    use stream =
                        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)

                    let size = stream.Length
                    let start = min offset size
                    let buffer = Array.zeroCreate<byte>(int (min (int64 count) (size - start)))
                    stream.Seek(start, SeekOrigin.Begin) |> ignore
                    let mutable filled = 0
                    let mutable reading = buffer.Length > 0

                    while reading do
                        let! read = stream.ReadAsync(buffer, filled, buffer.Length - filled, cancellation)
                        filled <- filled + read
                        reading <- read > 0 && filled < buffer.Length

                    return
                        Ok
                            {
                                Offset = offset
                                Content =
                                    (if filled = buffer.Length then
                                         buffer
                                     else
                                         Array.sub buffer 0 filled)
                                FileSize = size
                            }
                })

    /// Writes a temporary file next to the target and renames it over the target, keeping the
    /// target's file mode on Unix. A crash leaves at most a `.<name>.<random>.tmp` file behind.
    let writeAtomic (ids: Ids) (path: string) (content: byte array) (cancellation: CancellationToken) =
        attempt path (fun () ->
            let directory =
                match Path.GetDirectoryName path with
                | null -> ""
                | directory -> directory

            if Directory.Exists path then
                Task.FromResult(Error(IsADirectory path))
            elif directory = "" || not (Directory.Exists directory) then
                Task.FromResult(Error(NotFound path))
            else
                let name = Path.GetFileName path

                let temp =
                    Path.Combine(
                        directory,
                        "."
                        + (if name.Length > 40 then name.Substring(0, 40) else name)
                        + "."
                        + Convert.ToHexStringLower(ids.RandomBytes 6)
                        + ".tmp"
                    )

                task {
                    try
                        do! File.WriteAllBytesAsync(temp, content, cancellation)

                        if not (OperatingSystem.IsWindows()) && File.Exists path then
                            File.SetUnixFileMode(temp, File.GetUnixFileMode path)

                        File.Move(temp, path, true)
                        return Ok()
                    with e ->
                        try
                            File.Delete temp
                        with _ ->
                            ()

                        return rethrow e
                })

    /// The operations, with `ids` naming temporary files and `clock` debouncing watches.
    let create (clock: Clock) (ids: Ids) : FsOps =
        let write = writeAtomic ids

        {
            StatMany = FsOpsOver.statMany LocalFs.read
            ReadRange = readRange
            WriteAtomic = write
            ApplyPatch = FsOpsOver.applyPatch LocalFs.read write LocalFs.write.Delete
            Search = Search.run LocalFs.read
            Watch = LocalWatch.watch clock
        }
