/// The operations beneath a root on Linux and macOS (T113b): every directory below the root is opened
/// relative to its parent without following links (`openat2` with `RESOLVE_BENEATH` and
/// `RESOLVE_NO_SYMLINKS` on Linux, `O_NOFOLLOW_ANY` on macOS, one `openat(O_NOFOLLOW)` per component
/// where those are missing or fail), and the entry itself is reached from its parent's descriptor
/// (`fstatat(AT_SYMLINK_NOFOLLOW)`, `openat(O_NOFOLLOW)`, `renameat`, `unlinkat`). A process that swaps
/// a directory for a link at any time gets nowhere: descriptors do not follow renames.
module internal Fx.Envd.Live.BeneathPosix

open System
open System.IO
open Microsoft.Win32.SafeHandles
open Fx.Effects
open Fx.Envd.Live.Native

let private c = Posix.c

/// Whether the fast walk (one call for every directory) is there; cleared once it is not.
let mutable private fast = true

let private closeFd (fd: int) = Posix.close fd |> ignore

/// What the walk to a directory ran into.
type private Stop =
    | Failed of FsError
    /// Component `index` (0-based) is not a directory (the caller chooses the error).
    | NotDirectory of index: int

let private failure (path: string) (error: int) : FsError =
    if error = Posix.ENoEnt then
        NotFound path
    elif error = Posix.EAcces || error = Posix.EPerm then
        AccessDenied path
    elif error = Posix.EIsDir then
        IsADirectory path
    elif error = c.ELoop then
        Beneath.link path path
    else
        IoFailed(path, Posix.describe error)

let private lstat (dirfd: int) (name: string) : Result<Posix.Stat, int> =
    Posix.withPath name (fun n -> Posix.fstatat dirfd n c.AtSymlinkNoFollow)

let private openRoot (target: BeneathTarget) : Result<int, FsError> =
    // The root's own path is trusted, so it may go through links (/tmp on macOS). An absolute path
    // makes openat ignore its descriptor.
    let fd =
        Posix.withPath target.Root (fun p -> Posix.openat -1 p (Posix.ORdOnly ||| c.ODirectory ||| c.OCloExec) 0u)

    if fd >= 0 then
        Ok fd
    else
        let error = Posix.errno ()

        Error(
            if error = Posix.ENotDir then
                NotFound target.Path
            else
                failure target.Path error
        )

/// The fast walk: `Some fd`, or `None` to take the slow one (which also says what is wrong).
let private openFast (rootFd: int) (components: string list) : int option =
    if not fast then
        None
    else
        let relative = String.Join("/", components)
        let flags = Posix.ORdOnly ||| c.ODirectory ||| c.OCloExec

        let fd =
            Posix.withPath relative (fun p ->
                if OperatingSystem.IsMacOS() then
                    Posix.openat rootFd p (flags ||| c.ONoFollowAny) 0u
                else
                    Posix.openat2
                        rootFd
                        p
                        flags
                        (Posix.ResolveBeneath ||| Posix.ResolveNoSymlinks ||| Posix.ResolveNoMagicLinks))

        if fd >= 0 then
            Some fd
        else
            let error = Posix.errno ()

            // No openat2 here (an old kernel, or a seccomp filter that does not know it).
            if error = c.ENoSys || error = Posix.E2Big then
                fast <- false

            None

/// Opens the directory that the first `count` components name, one at a time, creating missing ones
/// with `create`. Takes `rootFd` over.
let private openSlow (target: BeneathTarget) (rootFd: int) (count: int) (create: bool) : Result<int, Stop> =
    let components = List.truncate count target.Components |> Array.ofList
    let mutable fd = rootFd
    let mutable stop = None
    let mutable index = 0

    let openAt (name: string) =
        Posix.withPath name (fun n ->
            Posix.openat fd n (Posix.ORdOnly ||| c.ODirectory ||| c.ONoFollow ||| c.OCloExec) 0u)

    while stop.IsNone && index < components.Length do
        let name = components[index]
        let mutable next = openAt name
        let mutable error = if next < 0 then Posix.errno () else 0

        if next < 0 && error = Posix.ENoEnt && create then
            let made = Posix.withPath name (fun n -> Posix.mkdirat (fd, n, 0o777u))

            if made = 0 || Posix.errno () = Posix.EExist then
                next <- openAt name
                error <- if next < 0 then Posix.errno () else 0
            else
                error <- Posix.errno ()

        if next >= 0 then
            closeFd fd
            fd <- next
            index <- index + 1
        else
            stop <-
                Some(
                    if error = Posix.ENotDir || error = c.ELoop then
                        match lstat fd name with
                        | Ok stat when stat.Type = Posix.SIfLnk ->
                            Failed(Beneath.link target.Path (target.Prefix(index + 1)))
                        | Ok _ -> NotDirectory index
                        | Error e -> Failed(failure target.Path e)
                    else
                        Failed(failure target.Path error)
                )

    match stop with
    | Some stop ->
        closeFd fd
        Error stop
    | None -> Ok fd

/// Opens the directory that the first `count` components of the target name (`0`: the root).
let private openDirectory (target: BeneathTarget) (count: int) (create: bool) : Result<int, Stop> =
    match openRoot target with
    | Error e -> Error(Failed e)
    | Ok rootFd when count = 0 -> Ok rootFd
    | Ok rootFd ->
        let quick =
            if create || not target.Fast then
                None
            else
                openFast rootFd (List.truncate count target.Components)

        match quick with
        | Some fd ->
            closeFd rootFd
            Ok fd
        | None -> openSlow target rootFd count create

/// The target's parent directory; a component that is a file means the target is not there.
let private openParent (target: BeneathTarget) : Result<int, FsError> =
    match openDirectory target target.Directories.Length false with
    | Ok fd -> Ok fd
    | Error(Failed e) -> Error e
    | Error(NotDirectory _) -> Error(NotFound target.Path)

let private withParent (target: BeneathTarget) (operation: int -> Result<'T, FsError>) : Result<'T, FsError> =
    match openParent target with
    | Error e -> Error e
    | Ok fd ->
        try
            operation fd
        finally
            closeFd fd

let private kindOf (stat: Posix.Stat) : EntryKind =
    let kind = stat.Type

    if kind = Posix.SIfReg then EntryKind.File
    elif kind = Posix.SIfDir then EntryKind.Directory
    elif kind = Posix.SIfLnk then EntryKind.Symlink
    else EntryKind.Other

let private entryOf (path: string) (name: string) (stat: Posix.Stat) : Entry =
    Beneath.entry
        path
        name
        (kindOf stat)
        stat.Size
        (Instant.ofUnixMicroseconds (stat.ModifiedSeconds * 1_000_000L + stat.ModifiedNanoseconds / 1000L))

let private missing (path: string) (error: int) : FsError =
    if error = Posix.ENotDir then
        NotFound path
    else
        failure path error

let stat (target: BeneathTarget) : Result<Entry, FsError> =
    withParent target (fun parent ->
        match lstat parent target.Name with
        | Ok stat -> Ok(entryOf target.Path target.Name stat)
        | Error e -> Error(missing target.Path e))

let list (target: BeneathTarget) : Result<Entry list, FsError> =
    match openDirectory target target.Components.Length false with
    | Error(Failed e) -> Error e
    | Error(NotDirectory index) when index = target.Directories.Length -> Error(NotADirectory target.Path)
    | Error(NotDirectory _) -> Error(NotFound target.Path)
    | Ok fd ->
        try
            // readNames takes over the descriptor it reads; this one stays for fstatat.
            let reading =
                Posix.withPath "." (fun p -> Posix.openat fd p (Posix.ORdOnly ||| c.ODirectory ||| c.OCloExec) 0u)

            if reading < 0 then
                Error(failure target.Path (Posix.errno ()))
            else
                match Posix.readNames reading with
                | Error e -> Error(failure target.Path e)
                | Ok names ->
                    names
                    |> List.choose (fun name ->
                        match lstat fd name with
                        | Ok stat -> Some(entryOf (Path.Combine(target.Path, name)) name stat)
                        | Error _ -> None) // deleted since it was listed
                    |> List.sortWith (fun a b -> String.CompareOrdinal(a.Name, b.Name))
                    |> Ok
        finally
            closeFd fd

/// Opens the entry for reading: not a link, not a directory.
let private openFile (target: BeneathTarget) (parent: int) : Result<SafeFileHandle, FsError> =
    match lstat parent target.Name with
    | Error e -> Error(missing target.Path e)
    | Ok stat when stat.Type = Posix.SIfDir -> Error(IsADirectory target.Path)
    | Ok stat when stat.Type = Posix.SIfLnk -> Error(Beneath.link target.Path target.Path)
    | Ok _ ->
        let fd =
            Posix.withPath target.Name (fun n ->
                Posix.openat parent n (Posix.ORdOnly ||| c.ONoFollow ||| c.OCloExec) 0u)

        if fd < 0 then
            Error(failure target.Path (Posix.errno ()))
        else
            Ok(new SafeFileHandle(nativeint fd, true))

/// At most `count` bytes from `offset` (the rest of the file without a count): the bytes, and the
/// file's size.
let read (target: BeneathTarget) (offset: int64) (count: int option) : Result<byte array * int64, FsError> =
    withParent target (fun parent ->
        match openFile target parent with
        | Error e -> Error e
        | Ok handle ->
            use handle = handle

            try
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

                Ok(
                    (if filled = length then
                         buffer
                     else
                         Array.sub buffer 0 filled),
                    size
                )
            with :? IOException as e ->
                Error(IoFailed(target.Path, e.Message)))

/// Writes the content through a descriptor, and closes it.
let private writeAll (fd: int) (content: byte array) =
    use handle = new SafeFileHandle(nativeint fd, true)
    RandomAccess.Write(handle, ReadOnlySpan<byte>(content), 0L)

/// Creates or replaces the file; refuses a link or a directory there.
let writeFile (target: BeneathTarget) (content: byte array) : Result<unit, FsError> =
    withParent target (fun parent ->
        match lstat parent target.Name with
        | Ok stat when stat.Type = Posix.SIfDir -> Error(IsADirectory target.Path)
        | Ok stat when stat.Type = Posix.SIfLnk -> Error(Beneath.link target.Path target.Path)
        | Error e when e <> Posix.ENoEnt -> Error(missing target.Path e)
        | _ ->
            let fd =
                Posix.withPath target.Name (fun n ->
                    Posix.openat
                        parent
                        n
                        (Posix.OWrOnly ||| c.OCreat ||| c.OTrunc ||| c.ONoFollow ||| c.OCloExec)
                        0o666u)

            if fd < 0 then
                Error(failure target.Path (Posix.errno ()))
            else
                try
                    writeAll fd content
                    Ok()
                with :? IOException as e ->
                    Error(IoFailed(target.Path, e.Message)))

/// Writes a temporary file in the parent (created exclusively, not through a link) and renames it
/// over the entry, keeping a file's mode. A link there is replaced, not followed.
let writeAtomic (target: BeneathTarget) (random: unit -> byte array) (content: byte array) : Result<unit, FsError> =
    withParent target (fun parent ->
        let existing = lstat parent target.Name

        match existing with
        | Ok stat when stat.Type = Posix.SIfDir -> Error(IsADirectory target.Path)
        | Error e when e <> Posix.ENoEnt -> Error(missing target.Path e)
        | _ ->
            let temp = Beneath.tempName target.Name (random ())

            let fd =
                Posix.withPath temp (fun n ->
                    Posix.openat
                        parent
                        n
                        (Posix.OWrOnly ||| c.OCreat ||| c.OExcl ||| c.ONoFollow ||| c.OCloExec)
                        0o666u)

            if fd < 0 then
                Error(failure target.Path (Posix.errno ()))
            else
                let removeTemp () =
                    Posix.withPath temp (fun n -> Posix.unlinkat (parent, n, 0)) |> ignore

                try
                    match existing with
                    | Ok stat when stat.Type = Posix.SIfReg -> Posix.fchmod (fd, stat.Mode &&& 0o7777u) |> ignore
                    | _ -> ()

                    writeAll fd content

                    let renamed =
                        Posix.withPath temp (fun source ->
                            Posix.withPath target.Name (fun dest -> Posix.renameat (parent, source, parent, dest)))

                    if renamed = 0 then
                        Ok()
                    else
                        let error = Posix.errno ()
                        removeTemp ()
                        Error(failure target.Path error)
                with _ ->
                    removeTemp ()
                    reraise ())

/// Creates the directory and any missing parents; succeeds if it exists.
let createDirectory (target: BeneathTarget) : Result<unit, FsError> =
    match openDirectory target target.Components.Length true with
    | Ok fd ->
        closeFd fd
        Ok()
    | Error(Failed e) -> Error e
    | Error(NotDirectory index) when index = target.Directories.Length -> Error(AlreadyExists target.Path)
    | Error(NotDirectory index) -> Error(NotADirectory(target.Prefix(index + 1)))

/// Deletes a file, a link (not what it points to) or an empty directory.
let delete (target: BeneathTarget) : Result<unit, FsError> =
    withParent target (fun parent ->
        match lstat parent target.Name with
        | Error e -> Error(missing target.Path e)
        | Ok stat ->
            let directory = stat.Type = Posix.SIfDir

            let status =
                Posix.withPath target.Name (fun n ->
                    Posix.unlinkat (parent, n, (if directory then c.AtRemoveDir else 0)))

            if status = 0 then
                Ok()
            else
                let error = Posix.errno ()

                if directory && (error = c.ENotEmpty || error = Posix.EExist) then
                    Error(DirectoryNotEmpty target.Path)
                else
                    Error(failure target.Path error))

/// Moves an entry (a link moves as itself); fails if the destination exists.
let move (source: BeneathTarget) (destination: BeneathTarget) : Result<unit, FsError> =
    withParent source (fun from ->
        match lstat from source.Name with
        | Error e -> Error(missing source.Path e)
        | Ok _ ->
            match openParent destination with
            | Error e -> Error e
            | Ok into ->
                try
                    match lstat into destination.Name with
                    | Ok _ -> Error(AlreadyExists destination.Path)
                    | Error e when e <> Posix.ENoEnt -> Error(missing destination.Path e)
                    | Error _ ->
                        let status =
                            Posix.withPath source.Name (fun s ->
                                Posix.withPath destination.Name (fun d -> Posix.renameat (from, s, into, d)))

                        if status = 0 then
                            Ok()
                        else
                            Error(failure destination.Path (Posix.errno ()))
                finally
                    closeFd into)
