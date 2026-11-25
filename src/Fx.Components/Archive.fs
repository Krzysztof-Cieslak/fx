namespace Fx.Components

open System
open System.Collections.Generic
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Threading
open System.Threading.Tasks
open Fx.Sdk

/// Bounds on what one archive may unpack to (zip bombs, runaway paths).
type ExtractLimits =
    {
        MaxEntries: int
        MaxFileBytes: int64
        MaxTotalBytes: int64
        /// Characters in an entry's path relative to the destination.
        MaxPathLength: int
        MaxDepth: int
    }

    override this.ToString() =
        $"limits ({this.MaxEntries} entries, {this.MaxFileBytes} bytes per file, {this.MaxTotalBytes} bytes in all)"

module ExtractLimits =
    /// Generous enough for any toolchain (a JDK unpacks to about 300 MB in 600 files), small enough
    /// to stop a bomb.
    let defaults: ExtractLimits =
        {
            MaxEntries = 200_000
            MaxFileBytes = 4L * 1024L * 1024L * 1024L
            MaxTotalBytes = 8L * 1024L * 1024L * 1024L
            MaxPathLength = 400
            MaxDepth = 64
        }

/// Why an archive was not unpacked. Nothing it unpacked is to be used.
type ArchiveError =
    /// Not a valid archive of its format (truncated, wrong format, bad checksums).
    | Corrupt of reason: string
    /// An entry that would write outside the destination or where it must not: an absolute path,
    /// `..`, a link that escapes, a device file, a name that is unsafe on some OS.
    | UnsafeEntry of entry: string * reason: string
    /// A limit was exceeded.
    | TooLarge of reason: string
    /// An entry this installer cannot unpack here (e.g. a symbolic link on Windows).
    | Unsupported of entry: string * reason: string
    /// Writing to the destination failed.
    | WriteFailed of path: string * reason: string

    override this.ToString() =
        match this with
        | Corrupt reason -> $"corrupt archive: {reason}"
        | UnsafeEntry(entry, reason) -> $"unsafe entry {entry}: {reason}"
        | TooLarge reason -> $"archive too large: {reason}"
        | Unsupported(entry, reason) -> $"unsupported entry {entry}: {reason}"
        | WriteFailed(path, reason) -> $"could not write {path}: {reason}"

/// What was unpacked.
type ExtractReport =
    {
        Files: int
        Directories: int
        Links: int
        /// The bytes of every file written.
        Bytes: int64
    }

    override this.ToString() =
        $"{this.Files} files, {this.Directories} directories, {this.Links} links, {this.Bytes} bytes"

/// Where an extraction writes, given paths as segments relative to the destination. The archive
/// code decides what to write and checks it; a sink only writes (to a disk or to memory). Its
/// failures are messages, reported as `WriteFailed`.
[<NoEquality; NoComparison>]
type ExtractSink =
    {
        CreateDirectory: string list -> Task<Result<unit, string>>
        /// Writes a new file from a stream, which ends at the file's end or at the size limit.
        WriteFile: string list -> bool -> Stream -> Task<Result<unit, string>>
        /// Creates a symbolic link to a `/`-separated relative target that was checked to stay inside.
        CreateSymlink: string list -> string -> Task<Result<unit, string>>
        /// Writes a copy of a file already written (a tar hard link).
        CopyFile: string list -> string list -> bool -> Task<Result<unit, string>>
    }

/// What to unpack and how.
type ExtractOptions =
    {
        Format: ArchiveFormat
        Strip: int
        Limits: ExtractLimits
        /// Paths (after stripping, `/`-separated) that are made executable whatever the archive says.
        Executables: string list
    }

    override this.ToString() = $"{this.Format}, strip {this.Strip}"

/// The rules for archive entry names, pure.
module ArchivePath =

    let private reserved =
        set [ "CON"; "PRN"; "AUX"; "NUL" ]
        + set [ for i in 0..9 -> $"COM{i}" ]
        + set [ for i in 0..9 -> $"LPT{i}" ]

    /// Why a segment is unsafe on some OS, if it is.
    let private unsafeSegment (segment: string) : string option =
        if segment = ".." then
            Some "it climbs out of the destination (..)"
        elif segment |> Seq.exists (fun c -> c < ' ' || c = '\u007f') then
            Some "it contains a control character"
        elif
            segment
            |> Seq.exists (fun c -> c = ':' || c = '<' || c = '>' || c = '"' || c = '|' || c = '?' || c = '*')
        then
            Some "it contains a character that Windows forbids in names"
        elif segment.EndsWith '.' || segment.EndsWith ' ' then
            Some "it ends with a dot or a space, which Windows drops"
        else
            let stem =
                match segment.IndexOf '.' with
                | -1 -> segment
                | i -> segment.Substring(0, i)

            if reserved.Contains(stem.ToUpperInvariant()) then
                Some "it is a device name on Windows"
            else
                None

    let private isAbsolute (name: string) =
        name.StartsWith '/'
        || name.StartsWith '\\'
        || (name.Length >= 2 && Char.IsAsciiLetter name[0] && name[1] = ':')

    /// The segments of an archive path (`/` or `\` separated), without empty and `.` segments, or
    /// why it is unsafe.
    let segments (name: string) : Result<string list, string> =
        if isAbsolute name then
            Error "it is an absolute path"
        else
            let segments =
                name.Split([| '/'; '\\' |])
                |> Array.filter (fun s -> s <> "" && s <> ".")
                |> List.ofArray

            match segments |> List.tryPick unsafeSegment with
            | Some reason -> Error reason
            | None -> Ok segments

    /// An entry's path after dropping `strip` leading segments: `None` if nothing is left (the
    /// entry is skipped), or why it is unsafe.
    let entry (strip: int) (name: string) : Result<string list option, string> =
        segments name
        |> Result.map (fun segments ->
            if List.length segments <= strip then
                None
            else
                Some(List.skip strip segments))

    /// Resolves a symbolic link's target, written relative to the link's directory, to segments
    /// relative to the destination, or why it escapes. An absolute target is refused.
    let linkTarget (link: string list) (target: string) : Result<string list, string> =
        if isAbsolute target then
            Error "the link's target is an absolute path"
        else
            let mutable resolved = List.rev (List.take (List.length link - 1) link)
            let mutable error = None

            for segment in target.Split([| '/'; '\\' |]) do
                if error.IsNone then
                    match segment with
                    | ""
                    | "." -> ()
                    | ".." ->
                        match resolved with
                        | _ :: rest -> resolved <- rest
                        | [] -> error <- Some "the link's target is outside the destination"
                    | segment ->
                        match unsafeSegment segment with
                        | Some reason -> error <- Some $"the link's target is unsafe: {reason}"
                        | None -> resolved <- segment :: resolved

            match error with
            | Some e -> Error e
            | None -> Ok(List.rev resolved)

    let join (segments: string list) = String.Join("/", segments)

/// Unpacking archives safely (spec §6.1 `Archive`, §18 supply chain). Pure: it reads the archive
/// from a stream and writes through a sink, so the live installer (to disk) and the fakes (to
/// memory) share every check.
///
/// - Entry names: no absolute paths, no `..`, no names that are unsafe on Windows (device names,
///   `:` and the other forbidden characters, a trailing dot or space), no two entries whose paths
///   differ only in case, no duplicates.
/// - Only files, directories, symbolic links and (tar) hard links. Devices, FIFOs, sparse and
///   multi-volume entries are refused.
/// - Symbolic links are created last, after every file, so nothing is ever written through one;
///   their targets must stay inside the destination without passing through another link. Hard
///   links become copies of a file written before.
/// - Modes are not copied: files are executable if the archive marks them so (any `x` bit) or if
///   the layout lists them; setuid, setgid and sticky bits are dropped.
/// - Limits on entries, bytes per file, bytes in all, path length and depth (`ExtractLimits`),
///   counted on what is actually read, not on what headers claim.
module Archive =

    [<RequireQualifiedAccess>]
    type private Kind =
        | File of executable: bool
        | Directory
        | Symlink of target: string
        | Hardlink of target: string
        | Unsupported of what: string

    /// A stream that ends after a number of bytes, and remembers whether there was more.
    [<Sealed>]
    type private Bounded(inner: Stream, limit: int64) =
        inherit Stream()
        let mutable read = 0L
        let mutable exceeded = false

        member _.BytesRead = read
        member _.Exceeded = exceeded

        override _.CanRead = true
        override _.CanSeek = false
        override _.CanWrite = false
        override _.Length = raise (NotSupportedException())

        override _.Position
            with get () = read
            and set _ = raise (NotSupportedException())

        override _.Flush() = ()
        override _.Seek(_, _) = raise (NotSupportedException())
        override _.SetLength _ = raise (NotSupportedException())
        override _.Write(_, _, _) = raise (NotSupportedException())

        override _.Read(buffer: byte array, offset: int, count: int) =
            if exceeded then
                0
            else
                let room = limit - read

                if room <= 0L then
                    // Probe for one more byte: the file is larger than the limit only if there is one.
                    let probe = Array.zeroCreate<byte> 1

                    if inner.Read(probe, 0, 1) > 0 then
                        exceeded <- true

                    0
                else
                    let n = inner.Read(buffer, offset, int (Math.Min(int64 count, room)))
                    read <- read + int64 n
                    n

    /// One entry as the format describes it.
    type private Raw =
        {
            Name: string
            Kind: Kind
            /// What the header claims, if anything.
            Declared: int64
            Content: unit -> Stream
        }

    let private executableMode (mode: int) = mode &&& 0o111 <> 0

    let private tarKind (entry: TarEntry) : Kind =
        let executable = executableMode (int entry.Mode)

        match entry.EntryType with
        | TarEntryType.RegularFile
        | TarEntryType.V7RegularFile
        | TarEntryType.ContiguousFile -> Kind.File executable
        | TarEntryType.Directory -> Kind.Directory
        | TarEntryType.SymbolicLink -> Kind.Symlink entry.LinkName
        | TarEntryType.HardLink -> Kind.Hardlink entry.LinkName
        | TarEntryType.CharacterDevice -> Kind.Unsupported "a character device"
        | TarEntryType.BlockDevice -> Kind.Unsupported "a block device"
        | TarEntryType.Fifo -> Kind.Unsupported "a FIFO"
        | TarEntryType.SparseFile -> Kind.Unsupported "a sparse file"
        | TarEntryType.MultiVolume -> Kind.Unsupported "a multi-volume entry"
        | other -> Kind.Unsupported $"a tar entry of type {int other}"

    let private zipKind (entry: ZipArchiveEntry) : Kind =
        // Archives made on Unix keep the mode in the high 16 bits of the external attributes.
        let mode = (entry.ExternalAttributes >>> 16) &&& 0xFFFF
        let fileType = mode &&& 0o170000

        if
            entry.FullName.EndsWith '/'
            || entry.FullName.EndsWith '\\'
            || fileType = 0o040000
        then
            Kind.Directory
        elif fileType = 0o120000 then
            Kind.Symlink ""
        elif fileType = 0 || fileType = 0o100000 then
            Kind.File(executableMode mode)
        else
            Kind.Unsupported "a special file"

    /// The state of one extraction: everything checked across entries.
    type private Extraction(options: ExtractOptions, sink: ExtractSink) =
        let ignoreCase = StringComparer.OrdinalIgnoreCase
        // Exact path -> kind, and lower-cased path -> exact path (case collisions).
        let kinds = Dictionary<string, Kind>(StringComparer.Ordinal)
        let folded = Dictionary<string, string>(ignoreCase)
        let links = ResizeArray<string list * string>()
        let executables = HashSet<string>(options.Executables, StringComparer.Ordinal)
        let mutable entries = 0
        let mutable files = 0
        let mutable directories = 0
        let mutable bytes = 0L

        let failed (path: string list) (result: Result<unit, string>) =
            match result with
            | Ok() -> Ok()
            | Error reason -> Error(WriteFailed(ArchivePath.join path, reason))

        /// Records a path; refuses duplicates and case collisions.
        let claim (path: string list) (kind: Kind) : Result<unit, ArchiveError> =
            let key = ArchivePath.join path

            match folded.TryGetValue key with
            | true, existing when existing <> key -> Error(UnsafeEntry(key, $"it differs from {existing} only in case"))
            | true, _ ->
                match kinds[key], kind with
                | Kind.Directory, Kind.Directory -> Ok()
                | _ -> Error(UnsafeEntry(key, "it is a duplicate entry"))
            | false, _ ->
                kinds[key] <- kind
                folded[key] <- key
                Ok()

        /// Creates the missing parent directories of a path; refuses a parent that is not a directory.
        member _.Parents(path: string list) : Task<Result<unit, ArchiveError>> =
            task {
                let mutable result = Ok()
                let parents = List.take (List.length path - 1) path

                for depth in 1 .. parents.Length do
                    if Result.isOk result then
                        let parent = List.take depth parents
                        let key = ArchivePath.join parent

                        match kinds.TryGetValue key with
                        | true, Kind.Directory -> ()
                        | true, _ -> result <- Error(UnsafeEntry(ArchivePath.join path, $"{key} is not a directory"))
                        | false, _ ->
                            match claim parent Kind.Directory with
                            | Error e -> result <- Error e
                            | Ok() ->
                                let! created = sink.CreateDirectory parent
                                directories <- directories + 1
                                result <- failed parent created

                return result
            }

        member this.Add(raw: Raw) : Task<Result<unit, ArchiveError>> =
            task {
                entries <- entries + 1

                if entries > options.Limits.MaxEntries then
                    return Error(TooLarge $"more than {options.Limits.MaxEntries} entries")
                else
                    match ArchivePath.entry options.Strip raw.Name with
                    | Error reason -> return Error(UnsafeEntry(raw.Name, reason))
                    | Ok None -> return Ok()
                    | Ok(Some path) ->
                        let key = ArchivePath.join path

                        if key.Length > options.Limits.MaxPathLength then
                            return Error(TooLarge $"the path {raw.Name} is longer than {options.Limits.MaxPathLength}")
                        elif path.Length > options.Limits.MaxDepth then
                            return Error(TooLarge $"the path {raw.Name} is deeper than {options.Limits.MaxDepth}")
                        else
                            match raw.Kind with
                            | Kind.Unsupported what -> return Error(UnsafeEntry(raw.Name, $"it is {what}"))
                            | Kind.Directory when
                                (match kinds.TryGetValue key with
                                 | true, Kind.Directory -> true
                                 | _ -> false)
                                ->
                                return Ok()
                            | kind ->
                                match! this.Parents path with
                                | Error e -> return Error e
                                | Ok() ->
                                    match claim path kind with
                                    | Error e -> return Error e
                                    | Ok() -> return! this.Write(raw, path, kind)
            }

        member private _.Write(raw: Raw, path: string list, kind: Kind) : Task<Result<unit, ArchiveError>> =
            task {
                let key = ArchivePath.join path

                match kind with
                | Kind.Directory ->
                    let! created = sink.CreateDirectory path
                    directories <- directories + 1
                    return failed path created
                | Kind.File executable ->
                    let limit =
                        Math.Min(options.Limits.MaxFileBytes, options.Limits.MaxTotalBytes - bytes)

                    if raw.Declared > limit then
                        return Error(TooLarge $"{raw.Name} is {raw.Declared} bytes; the limit is {limit}")
                    else
                        use content = raw.Content()
                        use bounded = new Bounded(content, limit)
                        let! written = sink.WriteFile path (executable || executables.Contains key) bounded

                        if bounded.Exceeded then
                            return Error(TooLarge $"{raw.Name} is larger than the limit of {limit} bytes")
                        else
                            files <- files + 1
                            bytes <- bytes + bounded.BytesRead
                            return failed path written
                | Kind.Symlink target ->
                    let target =
                        if target = "" then
                            // Zip stores a link's target as its content.
                            use content = raw.Content()
                            use reader = new StreamReader(new Bounded(content, 4096L))
                            reader.ReadToEnd()
                        else
                            target

                    match ArchivePath.linkTarget path target with
                    | Error reason -> return Error(UnsafeEntry(raw.Name, reason))
                    | Ok _ ->
                        links.Add((path, target))
                        return Ok()
                | Kind.Hardlink target ->
                    match ArchivePath.entry options.Strip target with
                    | Ok(Some source) ->
                        match kinds.TryGetValue(ArchivePath.join source) with
                        | true, Kind.File executable ->
                            let! copied = sink.CopyFile path source (executable || executables.Contains key)
                            files <- files + 1
                            return failed path copied
                        | _ -> return Error(UnsafeEntry(raw.Name, $"its target {target} is not a file written before"))
                    | Ok None -> return Error(UnsafeEntry(raw.Name, $"its target {target} is outside the destination"))
                    | Error reason -> return Error(UnsafeEntry(raw.Name, $"its target is unsafe: {reason}"))
                | Kind.Unsupported what -> return Error(UnsafeEntry(raw.Name, $"it is {what}"))
            }

        /// Creates the symbolic links, now that every file is written.
        member _.Finish() : Task<Result<ExtractReport, ArchiveError>> =
            task {
                let mutable result = Ok()

                for path, target in links do
                    if Result.isOk result then
                        let key = ArchivePath.join path

                        // The target may not pass through another link: the check above was lexical.
                        let throughLink =
                            match ArchivePath.linkTarget path target with
                            | Ok resolved ->
                                [ 1 .. resolved.Length - 1 ]
                                |> List.exists (fun n ->
                                    match kinds.TryGetValue(ArchivePath.join (List.take n resolved)) with
                                    | true, Kind.Symlink _ -> true
                                    | _ -> false)
                            | Error _ -> true

                        if throughLink then
                            result <- Error(UnsafeEntry(key, "its target passes through another link"))
                        else
                            let! created = sink.CreateSymlink path target

                            match created with
                            | Ok() -> ()
                            | Error reason -> result <- Error(Unsupported(key, reason))

                return
                    result
                    |> Result.map (fun () ->
                        {
                            Files = files
                            Directories = directories
                            Links = links.Count
                            Bytes = bytes
                        })
            }

    let private corrupt (format: ArchiveFormat) (e: exn) =
        Corrupt $"it is not a valid {format} archive ({e.GetType().Name})"

    let private isCorruption (e: exn) =
        e :? InvalidDataException
        || e :? FormatException
        || e :? EndOfStreamException
        || e :? ArgumentOutOfRangeException

    let private readTar
        (extraction: Extraction)
        (stream: Stream)
        (cancellation: CancellationToken)
        : Task<Result<unit, ArchiveError>> =
        task {
            use reader = new TarReader(stream, true)
            let mutable result = Ok()
            let mutable reading = true

            while reading && Result.isOk result do
                cancellation.ThrowIfCancellationRequested()
                let! entry = reader.GetNextEntryAsync(false, cancellation)

                match entry with
                | null -> reading <- false
                | entry ->
                    let content () =
                        match entry.DataStream with
                        | null -> Stream.Null
                        | data -> data

                    let! added =
                        extraction.Add
                            {
                                Name = entry.Name
                                Kind = tarKind entry
                                Declared = entry.Length
                                Content = content
                            }

                    result <- added

            return result
        }

    let private readZip
        (extraction: Extraction)
        (stream: Stream)
        (cancellation: CancellationToken)
        : Task<Result<unit, ArchiveError>> =
        task {
            use archive = new ZipArchive(stream, ZipArchiveMode.Read, true)
            let mutable result = Ok()

            for entry in archive.Entries do
                if Result.isOk result then
                    cancellation.ThrowIfCancellationRequested()

                    let! added =
                        extraction.Add
                            {
                                Name = entry.FullName
                                Kind = zipKind entry
                                Declared = entry.Length
                                Content = fun () -> entry.Open()
                            }

                    result <- added

            return result
        }

    /// Unpacks an archive read from a stream into a sink. The destination must start empty.
    let extract
        (options: ExtractOptions)
        (archive: Stream)
        (sink: ExtractSink)
        (cancellation: CancellationToken)
        : Task<Result<ExtractReport, ArchiveError>> =
        task {
            let extraction = Extraction(options, sink)

            let! read =
                task {
                    try
                        match options.Format with
                        | ArchiveFormat.Tar -> return! readTar extraction archive cancellation
                        | ArchiveFormat.TarGz ->
                            use gzip = new GZipStream(archive, CompressionMode.Decompress, true)
                            return! readTar extraction gzip cancellation
                        | ArchiveFormat.Zip -> return! readZip extraction archive cancellation
                    with e when isCorruption e ->
                        return Error(corrupt options.Format e)
                }

            match read with
            | Error e -> return Error e
            | Ok() -> return! extraction.Finish()
        }
