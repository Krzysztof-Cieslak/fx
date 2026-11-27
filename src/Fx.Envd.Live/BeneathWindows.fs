/// The operations beneath a root on Windows (T113b). Windows has no descriptor-relative calls in its
/// documented API, so the directories on the way are held instead: each one from the root to the
/// entry's parent is opened without following reparse points (`FILE_FLAG_OPEN_REPARSE_POINT`), checked
/// to be a directory and not a reparse point (symbolic links, junctions), and kept open without
/// `FILE_SHARE_DELETE`, so nothing can rename, delete or replace it until the operation is done. The
/// operation then goes by path (`LocalFs`), and opens the entry itself without following it where it
/// reads or writes content.
module internal Fx.Envd.Live.BeneathWindows

open System
open System.IO
open System.Runtime.ExceptionServices
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles
open Fx.Effects
open Fx.Effects.Live
open Fx.Envd.Live.Native

/// Handles held for an operation.
type private Held(handles: SafeFileHandle list) =
    interface IDisposable with
        member _.Dispose() =
            for handle in handles do
                handle.Dispose()

type private Stop =
    | Failed of FsError
    /// Component `index` (0-based) is not a directory.
    | NotDirectory of index: int

let private failure (path: string) (error: int) : FsError =
    if error = Windows.ErrorFileNotFound || error = Windows.ErrorPathNotFound then
        NotFound path
    elif error = Windows.ErrorAccessDenied then
        AccessDenied path
    else
        IoFailed(path, Marshal.GetPInvokeErrorMessage error)

let private attributes (handle: SafeFileHandle) = File.GetAttributes handle

let private isLink (attributes: FileAttributes) =
    attributes.HasFlag FileAttributes.ReparsePoint

/// Opens a directory to hold it: listing it and reading its attributes are allowed to others, renaming
/// and deleting it are not.
let private holdDirectory (path: string) (follow: bool) =
    Windows.open'
        path
        (Windows.FileListDirectory ||| Windows.FileReadAttributes ||| Windows.Synchronize)
        (Windows.FileShareRead ||| Windows.FileShareWrite)
        Windows.OpenExisting
        (Windows.FileFlagBackupSemantics
         ||| (if follow then 0u else Windows.FileFlagOpenReparsePoint))

/// Holds the root and the first `count` components, creating missing directories with `create`.
let private hold (target: BeneathTarget) (count: int) (create: bool) : Result<Held, Stop> =
    let held = ResizeArray<SafeFileHandle>()
    let mutable stop = None

    match holdDirectory target.Root true with
    | Error e -> stop <- Some(Failed(failure target.Path e))
    | Ok handle -> held.Add handle

    let mutable index = 0

    while stop.IsNone && index < count do
        let prefix = target.Prefix(index + 1)

        let opened =
            match holdDirectory prefix false with
            | Error e when create && (e = Windows.ErrorFileNotFound || e = Windows.ErrorPathNotFound) ->
                // Its parent is held, so this creates exactly this directory where it was checked.
                Directory.CreateDirectory prefix |> ignore
                holdDirectory prefix false
            | other -> other

        match opened with
        | Error e when e = Windows.ErrorDirectory -> stop <- Some(NotDirectory index)
        | Error e -> stop <- Some(Failed(failure target.Path e))
        | Ok handle ->
            let found = attributes handle

            if isLink found then
                handle.Dispose()
                stop <- Some(Failed(Beneath.link target.Path prefix))
            elif not (found.HasFlag FileAttributes.Directory) then
                handle.Dispose()
                stop <- Some(NotDirectory index)
            else
                held.Add handle
                index <- index + 1

    match stop with
    | Some stop ->
        for handle in held do
            handle.Dispose()

        Error stop
    | None -> Ok(new Held(List.ofSeq held))

let private holdParent (target: BeneathTarget) : Result<Held, FsError> =
    match hold target target.Directories.Length false with
    | Ok held -> Ok held
    | Error(Failed e) -> Error e
    | Error(NotDirectory _) -> Error(NotFound target.Path)

let private withParent (target: BeneathTarget) (operation: unit -> Task<Result<'T, FsError>>) =
    task {
        match holdParent target with
        | Error e -> return Error e
        | Ok held ->
            use _ = held
            return! operation ()
    }

let stat (target: BeneathTarget) (cancellation: CancellationToken) =
    withParent target (fun () -> LocalFs.read.Stat target.Path cancellation)

let list (target: BeneathTarget) (cancellation: CancellationToken) =
    task {
        match hold target target.Components.Length false with
        | Error(Failed e) -> return Error e
        | Error(NotDirectory index) when index = target.Directories.Length -> return Error(NotADirectory target.Path)
        | Error(NotDirectory _) -> return Error(NotFound target.Path)
        | Ok held ->
            use _ = held
            return! LocalFs.read.List target.Path cancellation
    }

let private openEntry (target: BeneathTarget) (access: uint32) (disposition: uint32) =
    match
        Windows.open'
            target.Path
            access
            (Windows.FileShareRead ||| Windows.FileShareWrite ||| Windows.FileShareDelete)
            disposition
            Windows.FileFlagOpenReparsePoint
    with
    | Error e when e = Windows.ErrorAccessDenied && Directory.Exists target.Path -> Error(IsADirectory target.Path)
    | Error e -> Error(failure target.Path e)
    | Ok handle ->
        let found = attributes handle

        if isLink found then
            handle.Dispose()
            Error(Beneath.link target.Path target.Path)
        elif found.HasFlag FileAttributes.Directory then
            handle.Dispose()
            Error(IsADirectory target.Path)
        else
            Ok handle

/// At most `count` bytes from `offset` (the rest without a count): the bytes, and the file's size.
let read (target: BeneathTarget) (offset: int64) (count: int option) (_: CancellationToken) =
    withParent target (fun () ->
        match openEntry target Windows.GenericRead Windows.OpenExisting with
        | Error e -> Task.FromResult(Error e)
        | Ok handle ->
            use handle = handle
            let size = RandomAccess.GetLength handle
            let start = min offset size

            let length =
                match count with
                | Some count -> int (min (int64 count) (size - start))
                | None -> int (size - start)

            let buffer = Array.zeroCreate<byte> length
            let mutable filled = 0
            let mutable reading = length > 0

            while reading do
                let read =
                    RandomAccess.Read(handle, Span<byte>(buffer, filled, length - filled), start + int64 filled)

                filled <- filled + read
                reading <- read > 0 && filled < length

            Task.FromResult(
                Ok(
                    (if filled = length then
                         buffer
                     else
                         Array.sub buffer 0 filled),
                    size
                )
            ))

/// Creates or replaces the file; refuses a link or a directory there.
let writeFile (target: BeneathTarget) (content: byte array) (_: CancellationToken) =
    withParent target (fun () ->
        match openEntry target (Windows.GenericWrite ||| Windows.FileReadAttributes) Windows.OpenAlways with
        | Error e -> Task.FromResult(Error e)
        | Ok handle ->
            use stream = new FileStream(handle, FileAccess.Write)
            stream.SetLength 0L
            stream.Write(content, 0, content.Length)
            Task.FromResult(Ok()))

/// Writes a new temporary file in the held parent and renames it over the entry (a link there is
/// replaced, not followed).
let writeAtomic
    (target: BeneathTarget)
    (random: unit -> byte array)
    (content: byte array)
    (cancellation: CancellationToken)
    =
    withParent target (fun () ->
        task {
            match! LocalFs.read.Stat target.Path cancellation with
            | Ok entry when entry.Kind = EntryKind.Directory -> return Error(IsADirectory target.Path)
            | Error(NotFound _)
            | Ok _ ->
                let temp =
                    Path.Combine(target.Prefix target.Directories.Length, Beneath.tempName target.Name (random ()))

                try
                    do!
                        task {
                            use stream =
                                new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                            do! stream.WriteAsync(content, 0, content.Length, cancellation)
                        }

                    File.Move(temp, target.Path, true)
                    return Ok()
                with e ->
                    try
                        File.Delete temp
                    with _ ->
                        ()

                    ExceptionDispatchInfo.Capture(e).Throw()
                    return Unchecked.defaultof<_>
            | Error e -> return Error e
        })

/// Creates the directory and any missing parents; succeeds if it exists.
let createDirectory (target: BeneathTarget) (_: CancellationToken) =
    Task.FromResult(
        match hold target target.Components.Length true with
        | Ok held ->
            (held :> IDisposable).Dispose()
            Ok()
        | Error(Failed e) -> Error e
        | Error(NotDirectory index) when index = target.Directories.Length -> Error(AlreadyExists target.Path)
        | Error(NotDirectory index) -> Error(NotADirectory(target.Prefix(index + 1)))
    )

/// Deletes a file, a link (not what it points to) or an empty directory.
let delete (target: BeneathTarget) (cancellation: CancellationToken) =
    withParent target (fun () -> LocalFs.write.Delete target.Path cancellation)

/// Moves an entry (a link moves as itself); fails if the destination exists.
let move (source: BeneathTarget) (destination: BeneathTarget) (cancellation: CancellationToken) =
    withParent source (fun () ->
        withParent destination (fun () -> LocalFs.write.Move source.Path destination.Path cancellation))
