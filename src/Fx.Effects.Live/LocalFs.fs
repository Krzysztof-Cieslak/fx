namespace Fx.Effects.Live

open System
open System.IO
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// The real filesystem of this machine. The only file that touches it directly. Failures the caller
/// can expect become `FsError`s (checked up front where .NET would report them inconsistently across
/// operating systems); anything else is a defect and stays an exception.
module LocalFs =
    let private ignoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()

    let private classify (path: string) (e: exn) : FsError option =
        match e with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Some(NotFound path)
        | :? UnauthorizedAccessException -> Some(AccessDenied path)
        | :? PathTooLongException -> Some(InvalidPath(path, "too long"))
        | :? IOException as e -> Some(IoFailed(path, e.Message))
        | :? ArgumentException as e -> Some(InvalidPath(path, e.Message))
        | _ -> None

    /// Runs an operation on an absolute path, turning expected exceptions into errors.
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

    let private now (result: Result<'T, FsError>) = Task.FromResult result

    let private entryOf (info: FileSystemInfo) : Entry =
        let kind =
            if (info.Attributes &&& FileAttributes.ReparsePoint) = FileAttributes.ReparsePoint then
                EntryKind.Symlink
            else
                match info with
                | :? DirectoryInfo -> EntryKind.Directory
                | :? FileInfo -> EntryKind.File
                | _ -> EntryKind.Other

        {
            Path = info.FullName
            Name = info.Name
            Kind = kind
            Size =
                match info with
                | :? FileInfo as file when kind = EntryKind.File -> file.Length
                | _ -> 0L
            Modified = Instant.ofDateTimeOffset (DateTimeOffset(info.LastWriteTimeUtc))
        }

    let private existing (path: string) : FileSystemInfo option =
        let file = FileInfo path

        if file.Exists then
            Some file
        else
            let directory = DirectoryInfo path
            if directory.Exists then Some directory else None

    let private readFile (path: string) (cancellation: CancellationToken) =
        attempt path (fun () ->
            if Directory.Exists path then
                now (Error(IsADirectory path))
            else
                task {
                    let! bytes = File.ReadAllBytesAsync(path, cancellation)
                    return Ok bytes
                })

    let private stat (path: string) (_: CancellationToken) =
        attempt path (fun () ->
            match existing path with
            | Some info -> now (Ok(entryOf info))
            | None -> now (Error(NotFound path)))

    let private list (path: string) (_: CancellationToken) =
        attempt path (fun () ->
            let directory = DirectoryInfo path

            if directory.Exists then
                directory.EnumerateFileSystemInfos()
                |> Seq.map entryOf
                |> Seq.sortWith (fun a b -> String.CompareOrdinal(a.Name, b.Name))
                |> List.ofSeq
                |> Ok
                |> now
            elif File.Exists path then
                now (Error(NotADirectory path))
            else
                now (Error(NotFound path)))

    let private writeFile (path: string) (content: byte array) (cancellation: CancellationToken) =
        attempt path (fun () ->
            if Directory.Exists path then
                now (Error(IsADirectory path))
            else
                task {
                    do! File.WriteAllBytesAsync(path, content, cancellation)
                    return Ok()
                })

    /// The path or its nearest ancestor that exists.
    let rec private nearestExisting (path: string) : string option =
        if File.Exists path || Directory.Exists path then
            Some path
        else
            match Path.GetDirectoryName path with
            | null
            | "" -> None
            | parent -> nearestExisting parent

    let private createDirectory (path: string) (_: CancellationToken) =
        attempt path (fun () ->
            match nearestExisting path with
            | Some found when File.Exists found ->
                if found = path then
                    now (Error(AlreadyExists path))
                else
                    now (Error(NotADirectory found))
            | _ ->
                Directory.CreateDirectory path |> ignore
                now (Ok()))

    let private delete (path: string) (_: CancellationToken) =
        attempt path (fun () ->
            if File.Exists path then
                File.Delete path
                now (Ok())
            elif Directory.Exists path then
                if Directory.EnumerateFileSystemEntries path |> Seq.isEmpty then
                    Directory.Delete(path, false)
                    now (Ok())
                else
                    now (Error(DirectoryNotEmpty path))
            else
                now (Error(NotFound path)))

    let private isUnder (ancestor: string) (path: string) =
        let ancestor =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(ancestor, ancestor))

        let path = Path.GetFullPath(path, path)

        path.StartsWith(
            ancestor + string Path.DirectorySeparatorChar,
            if ignoreCase then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal
        )

    let private move (source: string) (destination: string) (_: CancellationToken) =
        attempt source (fun () ->
            attempt destination (fun () ->
                let parent = Path.GetDirectoryName destination

                match existing source with
                | None -> now (Error(NotFound source))
                | Some _ when (existing destination).IsSome -> now (Error(AlreadyExists destination))
                | Some _ when isUnder source destination -> now (Error(InvalidPath(destination, "inside the source")))
                | Some _ when not (String.IsNullOrEmpty parent || Directory.Exists parent) ->
                    now (Error(NotFound destination))
                | Some(:? DirectoryInfo) ->
                    Directory.Move(source, destination)
                    now (Ok())
                | Some _ ->
                    File.Move(source, destination)
                    now (Ok())))

    /// Reading the local filesystem.
    let read: FsRead =
        {
            ReadFile = readFile
            Stat = stat
            List = list
        }

    /// Changing the local filesystem.
    let write: Fs =
        {
            WriteFile = writeFile
            CreateDirectory = createDirectory
            Delete = delete
            Move = move
        }
