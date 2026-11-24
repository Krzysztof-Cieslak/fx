namespace Fx.Store.Live

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Sqlite
open Fx.Store

/// The blob store's index database, version 1: one row per blob, for enumeration and (later)
/// garbage collection. The files are the truth about content; see `FileBlobStore`.
module internal BlobIndex =
    [<Literal>]
    let Version = 1

    let create =
        """
        CREATE TABLE blobs (
            hash TEXT PRIMARY KEY,
            size INTEGER NOT NULL,
            created INTEGER NOT NULL
        ) STRICT, WITHOUT ROWID;
        """

    let insert =
        "INSERT INTO blobs (hash, size, created) VALUES (?, ?, ?) ON CONFLICT (hash) DO NOTHING"

    let contains = "SELECT 1 FROM blobs WHERE hash = ?"

    let selectAll = "SELECT hash, size FROM blobs ORDER BY hash"

/// A content-addressed blob store on the local filesystem (spec §3, §5, §6.6), for the host's blob
/// cache and envd's store alike. The only code that touches blob files. See docs/modules/store.md.
///
/// - **Files.** Blob `sha256:abcd…` lives at `<root>/sha256/ab/cd…`. Content is written to a file in
///   `<root>/tmp`, synced (with `Durability.Full`), then renamed into place, so a blob file is always
///   complete. Writing content that exists already writes nothing.
/// - **Index.** `<root>/index.db` (SQLite) lists every blob with its size and when it was stored, for
///   enumeration and garbage collection. The files are the truth: reads and `Stat` look at files, and
///   a `Put` repairs an index row or a file that went missing.
/// - **One owner.** One process uses a root at a time: opening it deletes temporary files that a
///   crashed writer left behind.
[<Sealed>]
type FileBlobStore internal (root: string, index: Database, clock: Clock, ids: Ids, durability: Durability) =
    let blobsDir = Path.Combine(root, "sha256")
    let tmpDir = Path.Combine(root, "tmp")

    let pathOf (hash: BlobHash) =
        Path.Combine(blobsDir, hash.Hex.Substring(0, 2), hash.Hex.Substring 2)

    let durable = (durability = Durability.Full)

    let newTemp () =
        Path.Combine(tmpDir, Convert.ToHexStringLower(ids.RandomBytes 16) + ".part")

    /// Adds the blob to the index, unless it is there (a read, so storing known content costs no sync).
    let record (blob: BlobRef) =
        let known =
            Database.tryFirst index BlobIndex.contains [ Value.Text blob.Hash.Hex ] ignore
            |> Connections.orThrow "looking up a blob"

        if known.IsNone then
            Database.execute
                index
                BlobIndex.insert
                [
                    Value.Text blob.Hash.Hex
                    Value.Integer blob.Size
                    Value.Integer (clock.Now()).UnixMicroseconds
                ]
            |> Connections.orThrow "indexing a blob"
            |> ignore

    /// The size of the stored file, if there is one.
    let sizeOf (hash: BlobHash) : int64 option =
        let file = FileInfo(pathOf hash)
        if file.Exists then Some file.Length else None

    /// Moves a complete temporary file into place, unless the blob is there already.
    let install (temp: string) (blob: BlobRef) =
        let target = pathOf blob.Hash

        match sizeOf blob.Hash with
        | Some size when size = blob.Size -> File.Delete temp
        | existing ->
            Directory.CreateDirectory(Path.GetDirectoryName target |> nonNull) |> ignore

            try
                // A file of the wrong size is damaged: replace it.
                File.Move(temp, target, existing.IsSome)
            with :? IOException when File.Exists target ->
                // Another writer installed the same content meanwhile.
                File.Delete temp

        record blob

    let writeTemp (content: byte array) =
        let temp = newTemp ()

        do
            use stream =
                new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)

            stream.Write(content, 0, content.Length)
            stream.Flush durable

        temp

    let put (content: byte array) (cancellation: CancellationToken) =
        cancellation.ThrowIfCancellationRequested()

        let blob =
            {
                Hash = BlobHash.ofBytes content
                Size = content.LongLength
            }

        match sizeOf blob.Hash with
        | Some size when size = blob.Size -> record blob
        | _ -> install (writeTemp content) blob

        Task.FromResult blob

    let openRead (path: string) =
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)

    /// Reads up to `count` bytes from the stream's position.
    let readUpTo (stream: Stream) (count: int) : byte array =
        let buffer: byte array = Array.zeroCreate count
        let mutable filled = 0
        let mutable finished = false

        while not finished && filled < count do
            let read = stream.Read(buffer, filled, count - filled)

            if read = 0 then
                finished <- true
            else
                filled <- filled + read

        if filled = count then buffer else Array.sub buffer 0 filled

    let get (hash: BlobHash) (cancellation: CancellationToken) =
        cancellation.ThrowIfCancellationRequested()

        let result =
            try
                use stream = openRead (pathOf hash)

                if stream.Length > int64 Array.MaxLength then
                    invalidOp $"{hash} has {stream.Length} bytes, more than an array holds; read it in ranges."

                let content = readUpTo stream (int stream.Length)

                if BlobHash.ofBytes content = hash then
                    Ok content
                else
                    Error(BlobError.Corrupt hash)
            with
            | :? FileNotFoundException
            | :? DirectoryNotFoundException -> Error(BlobError.NotFound hash)

        Task.FromResult result

    let read (hash: BlobHash) (offset: int64) (count: int) (cancellation: CancellationToken) =
        Blobs.checkRange offset count
        cancellation.ThrowIfCancellationRequested()

        let result =
            try
                use stream = openRead (pathOf hash)

                if offset >= stream.Length || count = 0 then
                    Ok [||]
                else
                    stream.Position <- offset
                    Ok(readUpTo stream (int (min (int64 count) (stream.Length - offset))))
            with
            | :? FileNotFoundException
            | :? DirectoryNotFoundException -> Error(BlobError.NotFound hash)

        Task.FromResult result

    let stat (hash: BlobHash) (cancellation: CancellationToken) =
        cancellation.ThrowIfCancellationRequested()

        sizeOf hash
        |> Option.map (fun size -> { Hash = hash; Size = size })
        |> Task.FromResult

    let create (cancellation: CancellationToken) =
        cancellation.ThrowIfCancellationRequested()
        let temp = newTemp ()

        let stream =
            new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)

        let hasher = IncrementalHash.CreateHash HashAlgorithmName.SHA256
        let sync = obj ()
        let mutable size = 0L
        let mutable finished = false

        let finish () =
            finished <- true
            stream.Dispose()
            hasher.Dispose()

        let ensureOpen () =
            if finished then
                raise (ObjectDisposedException("BlobWriter", "The writer is finished."))

        Task.FromResult
            {
                Write =
                    fun chunk cancellation ->
                        cancellation.ThrowIfCancellationRequested()

                        lock sync (fun () ->
                            ensureOpen ()
                            stream.Write(chunk, 0, chunk.Length)
                            hasher.AppendData chunk
                            size <- size + chunk.LongLength)

                        Task.FromResult()
                Commit =
                    fun cancellation ->
                        cancellation.ThrowIfCancellationRequested()

                        let blob =
                            lock sync (fun () ->
                                ensureOpen ()
                                stream.Flush durable

                                let blob =
                                    {
                                        Hash = BlobHash.ofDigest (hasher.GetHashAndReset())
                                        Size = size
                                    }

                                finish ()
                                blob)

                        install temp blob
                        Task.FromResult blob
                Abort =
                    fun () ->
                        let discard =
                            lock sync (fun () ->
                                if finished then
                                    false
                                else
                                    finish ()
                                    true)

                        if discard then
                            File.Delete temp

                        Task.FromResult()
            }

    /// The directory the store lives in.
    member _.Root = root

    member val Blobs: Blobs =
        {
            Put = put
            Get = get
            Read = read
            Stat = stat
            Create = create
        }

    /// Every blob in the index, by hash.
    member _.Index() : BlobRef list =
        Database.query index BlobIndex.selectAll [] (fun row ->
            match BlobHash.tryParse ("sha256:" + row.Text 0) with
            | Some hash -> { Hash = hash; Size = row.Int64 1 }
            | None -> invalidOp $"the blob index holds an invalid hash: {row.Text 0}")
        |> Connections.orThrow "listing blobs"

    member _.Dispose() = index.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Dispose()

    override _.ToString() = "FileBlobStore(" + root + ")"

module FileBlobStore =
    [<Literal>]
    let private IndexName = "index.db"

    /// Opens the blob store in a directory (an absolute path from the composition root), creating it
    /// and its index if needed. Deletes temporary files that a crashed writer left behind. `clock`
    /// dates index rows; `ids` names temporary files.
    let openAt (root: string) (clock: Clock) (ids: Ids) (durability: Durability) : Result<FileBlobStore, OpenError> =
        if not (Path.IsPathFullyQualified root) then
            Error(OpenError.InvalidPath(root, "not absolute"))
        elif File.Exists root then
            Error(OpenError.InvalidPath(root, "a file"))
        else
            let tmp = Path.Combine(root, "tmp")
            Directory.CreateDirectory(Path.Combine(root, "sha256")) |> ignore
            Directory.CreateDirectory tmp |> ignore

            for leftover in Directory.GetFiles tmp do
                try
                    File.Delete leftover
                with
                | :? IOException
                | :? UnauthorizedAccessException -> () // still open somewhere (Windows): next time

            match Connections.openWriter (Path.Combine(root, IndexName)) (TimeSpan.FromSeconds 5.0) durability with
            | Error error -> Error(OpenError.Database error)
            | Ok index ->
                match Connections.migrate index BlobIndex.Version BlobIndex.create with
                | Error error ->
                    index.Dispose()
                    Error error
                | Ok() -> Ok(new FileBlobStore(root, index, clock, ids, durability))
