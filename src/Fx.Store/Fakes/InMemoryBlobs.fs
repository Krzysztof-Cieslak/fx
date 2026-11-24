namespace Fx.Store.Fakes

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open Fx.Store

/// A blob store in memory, for tests, simulations and `fx test`. Thread-safe; every call completes
/// synchronously. Passes the same conformance suite as `FileBlobStore`.
[<Sealed>]
type InMemoryBlobs() =
    let gate = obj ()
    let blobs = Dictionary<BlobHash, byte array>()

    let checkCancelled (cancellation: CancellationToken) =
        cancellation.ThrowIfCancellationRequested()

    let put (content: byte array) (cancellation: CancellationToken) =
        checkCancelled cancellation
        let hash = BlobHash.ofBytes content

        lock gate (fun () ->
            if not (blobs.ContainsKey hash) then
                blobs[hash] <- Array.copy content)

        Task.FromResult
            {
                Hash = hash
                Size = content.LongLength
            }

    let tryContent (hash: BlobHash) =
        lock gate (fun () ->
            match blobs.TryGetValue hash with
            | true, content -> Some content
            | false, _ -> None)

    let get (hash: BlobHash) (cancellation: CancellationToken) =
        checkCancelled cancellation

        match tryContent hash with
        | Some content when BlobHash.ofBytes content = hash -> Task.FromResult(Ok(Array.copy content))
        | Some _ -> Task.FromResult(Error(BlobError.Corrupt hash))
        | None -> Task.FromResult(Error(BlobError.NotFound hash))

    let read (hash: BlobHash) (offset: int64) (count: int) (cancellation: CancellationToken) =
        Blobs.checkRange offset count
        checkCancelled cancellation

        match tryContent hash with
        | Some content ->
            let start = int (min offset content.LongLength)
            let length = min count (content.Length - start)
            Task.FromResult(Ok(Array.sub content start length))
        | None -> Task.FromResult(Error(BlobError.NotFound hash))

    let stat (hash: BlobHash) (cancellation: CancellationToken) =
        checkCancelled cancellation

        tryContent hash
        |> Option.map (fun content ->
            {
                Hash = hash
                Size = content.LongLength
            })
        |> Task.FromResult

    let create (cancellation: CancellationToken) =
        checkCancelled cancellation
        let buffer = new MemoryStream()
        let mutable finished = false

        let ensureOpen () =
            if finished then
                raise (ObjectDisposedException("BlobWriter", "The writer is finished."))

        Task.FromResult
            {
                Write =
                    fun chunk cancellation ->
                        checkCancelled cancellation

                        lock buffer (fun () ->
                            ensureOpen ()
                            buffer.Write(chunk, 0, chunk.Length))

                        Task.FromResult()
                Commit =
                    fun cancellation ->
                        let content =
                            lock buffer (fun () ->
                                ensureOpen ()
                                checkCancelled cancellation
                                finished <- true
                                buffer.ToArray())

                        put content cancellation
                Abort =
                    fun () ->
                        lock buffer (fun () -> finished <- true)
                        Task.FromResult()
            }

    member val Blobs: Blobs =
        {
            Put = put
            Get = get
            Read = read
            Stat = stat
            Create = create
        }

    /// How many distinct blobs it holds.
    member _.Count = lock gate (fun () -> blobs.Count)

    /// Replaces a blob's content without changing its hash, as a damaged disk would.
    member _.Damage(hash: BlobHash, content: byte array) =
        lock gate (fun () -> blobs[hash] <- Array.copy content)

    override _.ToString() = "InMemoryBlobs"
