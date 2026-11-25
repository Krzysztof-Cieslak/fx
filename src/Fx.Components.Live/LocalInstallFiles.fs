namespace Fx.Components.Live

open System
open System.IO
open System.Runtime.ExceptionServices
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Fx.Components
open Fx.Effects
open Fx.Sdk

/// The installer's file operations on this machine's filesystem: the only file of the installer
/// that touches the disk directly (downloads aside).
///
/// - Unpacking streams the archive from disk through `Archive.extract`, which does every check.
///   Files are created new (never through an existing file or link) with mode 0755 or 0644 on
///   Linux and macOS (the umask applies). Symbolic links are created on Linux and macOS; on Windows
///   they are refused for now (creating them needs a privilege).
/// - Locks are files opened with `FileShare.None`: .NET takes `flock(LOCK_EX)` on Linux and macOS,
///   and Windows refuses a second open. The OS releases them when a process dies. Lock files are
///   never deleted (deleting one that another process is opening would split the lock).
module LocalInstallFiles =

    let private classify (path: string) (e: exn) : FsError option =
        match e with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Some(NotFound path)
        | :? UnauthorizedAccessException -> Some(AccessDenied path)
        | :? PathTooLongException -> Some(InvalidPath(path, "too long"))
        | :? IOException as e -> Some(FsError.IoFailed(path, e.GetType().Name))
        | :? ArgumentException as e -> Some(InvalidPath(path, e.GetType().Name))
        | _ -> None

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
                    | None ->
                        ExceptionDispatchInfo.Capture(e).Throw()
                        return Unchecked.defaultof<_>
            }

    let private hash (path: string) (cancellation: CancellationToken) =
        attempt path (fun () ->
            task {
                use stream =
                    new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true)

                let! digest = SHA256.HashDataAsync(stream, cancellation)

                return
                    Ok
                        {
                            Size = stream.Length
                            Digest = Sha256(Convert.ToHexStringLower digest)
                        }
            })

    let private fileMode (executable: bool) =
        if executable then
            UnixFileMode.UserRead
            ||| UnixFileMode.UserWrite
            ||| UnixFileMode.UserExecute
            ||| UnixFileMode.GroupRead
            ||| UnixFileMode.GroupExecute
            ||| UnixFileMode.OtherRead
            ||| UnixFileMode.OtherExecute
        else
            UnixFileMode.UserRead
            ||| UnixFileMode.UserWrite
            ||| UnixFileMode.GroupRead
            ||| UnixFileMode.OtherRead

    let private newFile (path: string) (executable: bool) : FileStream =
        let options =
            FileStreamOptions(
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 81920,
                Options = FileOptions.Asynchronous
            )

        if not (OperatingSystem.IsWindows()) then
            options.UnixCreateMode <- Nullable(fileMode executable)

        new FileStream(path, options)

    /// A sink failure: the exception's type (messages differ between runtimes).
    let private guarded (operation: unit -> Task<unit>) : Task<Result<unit, string>> =
        task {
            try
                do! operation ()
                return Ok()
            with e when (e :? IOException || e :? UnauthorizedAccessException) ->
                return Error(e.GetType().Name)
        }

    let private sink (root: string) (cancellation: CancellationToken) : ExtractSink =
        let under (segments: string list) =
            Path.Combine(Array.ofList (root :: segments))

        {
            CreateDirectory =
                fun path ->
                    guarded (fun () ->
                        Directory.CreateDirectory(under path) |> ignore
                        Task.FromResult())
            WriteFile =
                fun path executable content ->
                    guarded (fun () ->
                        task {
                            use file = newFile (under path) executable
                            do! content.CopyToAsync(file, 81920, cancellation)
                        })
            CreateSymlink =
                fun path target ->
                    if OperatingSystem.IsWindows() then
                        Task.FromResult(Error "symbolic links are not supported on Windows yet")
                    else
                        guarded (fun () ->
                            File.CreateSymbolicLink(under path, target) |> ignore
                            Task.FromResult())
            CopyFile =
                fun path source executable ->
                    guarded (fun () ->
                        task {
                            use input =
                                new FileStream(
                                    under source,
                                    FileMode.Open,
                                    FileAccess.Read,
                                    FileShare.Read,
                                    81920,
                                    true
                                )

                            use output = newFile (under path) executable
                            do! input.CopyToAsync(output, 81920, cancellation)
                        })
        }

    let private extract (request: ExtractRequest) (cancellation: CancellationToken) =
        task {
            let destination = request.Destination

            if
                not (
                    Path.IsPathFullyQualified destination
                    && Path.IsPathFullyQualified request.Archive
                )
            then
                return Error(ArchiveError.WriteFailed(destination, "not an absolute path"))
            elif Directory.Exists destination || File.Exists destination then
                return Error(ArchiveError.WriteFailed(destination, "it exists already"))
            else
                let opened =
                    try
                        Directory.CreateDirectory destination |> ignore

                        Ok(
                            new FileStream(
                                request.Archive,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read,
                                81920,
                                false
                            )
                        )
                    with e when (e :? IOException || e :? UnauthorizedAccessException) ->
                        Error(ArchiveError.WriteFailed(request.Archive, e.GetType().Name))

                match opened with
                | Error e -> return Error e
                | Ok archive ->
                    use archive = archive
                    return! Archive.extract request.Options archive (sink destination cancellation) cancellation
        }

    /// Makes every file under a directory writable, so that Windows lets it be deleted.
    let private clearReadOnly (dir: string) =
        for file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) do
            let attributes = File.GetAttributes file

            if attributes.HasFlag FileAttributes.ReadOnly then
                File.SetAttributes(file, attributes &&& ~~~FileAttributes.ReadOnly)

    let private deleteTree (path: string) (_: CancellationToken) =
        attempt path (fun () ->
            let info = FileInfo path

            if info.LinkTarget <> null then
                // A link (to a file or a directory): the link itself, never what it points to. Windows
                // removes a directory link as a directory; elsewhere a link is unlinked like a file.
                if OperatingSystem.IsWindows() && info.Attributes.HasFlag FileAttributes.Directory then
                    Directory.Delete path
                else
                    File.Delete path
            elif info.Exists then
                File.Delete path
            elif Directory.Exists path then
                try
                    Directory.Delete(path, true)
                with :? UnauthorizedAccessException when OperatingSystem.IsWindows() ->
                    clearReadOnly path
                    Directory.Delete(path, true)

            Task.FromResult(Ok()))

    let private replaceFile (path: string) (content: byte array) (cancellation: CancellationToken) =
        attempt path (fun () ->
            task {
                let temporary = path + ".tmp"

                do!
                    task {
                        use file =
                            new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true)

                        do! file.WriteAsync(content, cancellation)
                        file.Flush true
                    }

                File.Move(temporary, path, true)
                return Ok()
            })

    let private tryLock (path: string) (_: CancellationToken) =
        attempt path (fun () ->
            match Path.GetDirectoryName path with
            | null -> ()
            | parent -> Directory.CreateDirectory parent |> ignore

            let opened =
                try
                    Some(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                with
                | :? FileNotFoundException
                | :? DirectoryNotFoundException
                | :? PathTooLongException -> reraise ()
                // Held by another process (or another handle of this one).
                | :? IOException -> None

            match opened with
            | Some stream ->
                Task.FromResult(
                    Ok(
                        Some
                            {
                                Release =
                                    fun () ->
                                        stream.Dispose()
                                        Task.FromResult()
                            }
                    )
                )
            | None -> Task.FromResult(Ok None))

    /// The installer's file operations on the local filesystem.
    let files: InstallFiles =
        {
            Hash = hash
            Extract = extract
            DeleteTree = deleteTree
            ReplaceFile = replaceFile
            TryLock = tryLock
        }
