namespace Fx.Envd

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// A file that process output overflows into: appended to, read back at offsets. Its path is
/// reported to the host, so the whole output stays greppable (spec §6.6).
type SpillFile =
    {
        /// Where the file is, in the environment's path syntax.
        Path: string
        /// Appends bytes at the end.
        Append: byte array -> CancellationToken -> Task<unit>
        /// At most `count` bytes from an offset.
        ReadAt: int64 -> int -> CancellationToken -> Task<byte array>
        /// Deletes the file. Idempotent.
        Delete: unit -> Task<unit>
    }

    override this.ToString() = $"SpillFile({this.Path})"

/// Creates spill files. Interpreters: `LocalSpill` (Fx.Envd.Live, files in envd's state directory)
/// and `Spill.inMemory`.
type Spill =
    {
        /// A new, empty spill file with a name unique to this spill (e.g. `3.out`).
        Create: string -> CancellationToken -> Task<SpillFile>
    }

    override _.ToString() = "Spill"

module Spill =
    /// Spill files in memory, under a pretend directory.
    let inMemory (directory: string) : Spill =
        {
            Create =
                fun name _ ->
                    let gate = obj ()
                    let content = new IO.MemoryStream()

                    Task.FromResult
                        {
                            Path = directory + "/" + name
                            Append =
                                fun bytes _ ->
                                    lock gate (fun () -> content.Write(bytes, 0, bytes.Length))
                                    Task.FromResult()
                            ReadAt =
                                fun offset count _ ->
                                    lock gate (fun () ->
                                        let all = content.GetBuffer()
                                        let size = content.Length
                                        let start = min offset size
                                        let length = int (min (int64 count) (size - start))
                                        Task.FromResult(Array.sub all (int start) length))
                            Delete = fun () -> Task.FromResult()
                        }
        }

/// What reading an output log at an offset gives.
[<RequireQualifiedAccess>]
type LogRead =
    /// Bytes starting at `offset`.
    | Data of offset: int64 * bytes: byte array
    /// The bytes in `[from, until)` are no longer kept (no spill file, and beyond the retention).
    | Gap of from: int64 * until: int64
    /// The log has ended; it holds `total` bytes.
    | End of total: int64

    override this.ToString() =
        match this with
        | Data(offset, bytes) -> $"data at {offset} ({bytes.Length} bytes)"
        | Gap(from, until) -> $"gap {from}..{until}"
        | End total -> $"end at {total}"

/// Where an output log stands.
type LogStatus =
    {
        /// Bytes appended so far: the next byte's sequence number.
        Total: int64
        /// The first byte still readable (0 when nothing was lost).
        Available: int64
        /// The spill file, once output overflowed the memory window.
        SpillPath: string option
        /// Whether the stream has ended.
        Ended: bool
    }

    override this.ToString() =
        let spill = this.SpillPath |> Option.defaultValue "none"
        $"{this.Total} bytes (from {this.Available}, spill {spill}, ended {this.Ended})"

/// The output of one stream of a process, with sequence numbers: every byte's number is its offset
/// in the stream. The last `retain` bytes stay in memory; when output overflows that window, the
/// whole stream goes to a spill file (if there is a spill), so readers can resume from any offset.
/// Without a spill, older bytes are dropped and reading them gives a `Gap`.
///
/// One writer (the process's output pump) appends; any number of readers read concurrently.
[<Sealed>]
type OutputLog(name: string, retain: int, spill: Spill option) =
    let gate = obj ()
    // Chunks in memory: offset of the first byte, and the bytes.
    let memory = Queue<struct (int64 * byte array)>()
    let mutable memoryBytes = 0L
    let mutable total = 0L
    let mutable ended = false
    let mutable spillFile: SpillFile option = None
    let mutable spilledUntil = 0L

    let mutable signal =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let memoryStart () =
        if memory.Count > 0 then
            let struct (offset, _) = memory.Peek()
            offset
        else
            total

    let wake () =
        let previous = signal
        signal <- TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        previous.TrySetResult() |> ignore

    /// Drops chunks from memory while it holds more than the window, and only bytes that are safe in
    /// the spill file (or, without one, are given up).
    let evict () =
        let mutable go = true

        while go && memoryBytes > int64 retain && memory.Count > 0 do
            let struct (offset, bytes) = memory.Peek()
            let stop = offset + int64 bytes.Length

            if spill.IsNone || stop <= spilledUntil then
                memory.Dequeue() |> ignore
                memoryBytes <- memoryBytes - int64 bytes.Length
            else
                go <- false

    member _.Name = name

    /// Appends output. Called by one writer at a time.
    member _.Append(bytes: byte array, cancellation: CancellationToken) : Task<unit> =
        if bytes.Length = 0 then
            Task.FromResult()
        else
            let toSpill =
                lock gate (fun () ->
                    if ended then
                        invalidOp "The log has ended."

                    memory.Enqueue(struct (total, bytes))
                    memoryBytes <- memoryBytes + int64 bytes.Length
                    total <- total + int64 bytes.Length
                    wake ()

                    if memoryBytes > int64 retain && spill.IsSome then
                        // Everything not yet in the spill file is still in memory.
                        memory
                        |> Seq.filter (fun (struct (offset, _)) -> offset >= spilledUntil)
                        |> Seq.map (fun (struct (_, chunk)) -> chunk)
                        |> List.ofSeq
                    else
                        evict ()
                        [])

            match toSpill, spill with
            | [], _
            | _, None -> Task.FromResult()
            | chunks, Some spill ->
                task {
                    let! file =
                        match lock gate (fun () -> spillFile) with
                        | Some file -> Task.FromResult file
                        | None ->
                            task {
                                let! file = spill.Create name cancellation
                                lock gate (fun () -> spillFile <- Some file)
                                return file
                            }

                    let mutable written = 0L

                    for chunk in chunks do
                        do! file.Append chunk cancellation
                        written <- written + int64 chunk.Length

                    lock gate (fun () ->
                        spilledUntil <- spilledUntil + written
                        evict ())
                }

    /// No more output.
    member _.Finish() : unit =
        lock gate (fun () ->
            ended <- true
            wake ())

    member _.Status: LogStatus =
        lock gate (fun () ->
            {
                Total = total
                Available = (if spillFile.IsSome then 0L else memoryStart ())
                SpillPath = spillFile |> Option.map (fun f -> f.Path)
                Ended = ended
            })

    /// At most `max` bytes from `offset`, waiting for output if there is none there yet.
    member _.Read(offset: int64, max: int, cancellation: CancellationToken) : Task<LogRead> =
        if offset < 0L || max < 1 then
            invalidArg (nameof offset) "The offset must not be negative and the count positive."

        task {
            let mutable result = None

            while result.IsNone do
                let step =
                    lock gate (fun () ->
                        let start = memoryStart ()

                        if offset < total then
                            if offset >= start then
                                let buffer = Array.zeroCreate<byte>(int (min (int64 max) (total - offset)))
                                let mutable filled = 0

                                for struct (chunkOffset, chunk) in memory do
                                    let chunkEnd = chunkOffset + int64 chunk.Length
                                    let position = offset + int64 filled

                                    if filled < buffer.Length && position >= chunkOffset && position < chunkEnd then
                                        let from = int (position - chunkOffset)
                                        let count = min (chunk.Length - from) (buffer.Length - filled)
                                        Buffer.BlockCopy(chunk, from, buffer, filled, count)
                                        filled <- filled + count

                                Choice1Of3(LogRead.Data(offset, buffer))
                            else
                                match spillFile with
                                | Some file when offset < spilledUntil ->
                                    Choice2Of3(file, int (min (int64 max) (spilledUntil - offset)))
                                | _ -> Choice1Of3(LogRead.Gap(offset, start))
                        elif ended then
                            Choice1Of3(LogRead.End total)
                        else
                            Choice3Of3 signal.Task)

                match step with
                | Choice1Of3 read -> result <- Some read
                | Choice2Of3(file, count) ->
                    let! bytes = file.ReadAt offset count cancellation
                    result <- Some(LogRead.Data(offset, bytes))
                | Choice3Of3 wait -> do! wait.WaitAsync cancellation

            return result.Value
        }

    /// Ends the log and deletes its spill file.
    member _.Dispose() : Task<unit> =
        let file =
            lock gate (fun () ->
                ended <- true
                memory.Clear()
                memoryBytes <- 0L
                wake ()
                let file = spillFile
                spillFile <- None
                file)

        match file with
        | Some file -> file.Delete()
        | None -> Task.FromResult()

    override _.ToString() = $"OutputLog({name})"
