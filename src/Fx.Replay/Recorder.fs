namespace Fx.Replay

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Machines
open Fx.Machines.Host
open Fx.Store

/// How a recorder keeps journals.
type RecorderSettings =
    {
        /// Values up to this many bytes of JSON stay in the journal's events; larger ones go to the blob
        /// store (content-addressed, so equal values are stored once).
        InlineBytes: int
        /// Entries per journal event: a batch stays well under the 1 MiB an event may have.
        BatchEntries: int
        /// A recorded call flushes the buffer first once it holds this many entries or bytes.
        FlushEntries: int
        FlushBytes: int64
    }

    override this.ToString() =
        $"RecorderSettings(inline {this.InlineBytes} B, batches of {this.BatchEntries})"

module RecorderSettings =
    let defaults: RecorderSettings =
        {
            InlineBytes = 1024
            BatchEntries = 256
            FlushEntries = 2048
            FlushBytes = 8L * 1024L * 1024L
        }

/// A call that started: where it goes in the journal.
type JournalCall =
    {
        Lane: string
        Index: int64
        Op: string
        Input: string
        Summary: string
        /// Its place in `Seq` order, if taken when it started (a call that acts on the world).
        Seq: int64 option
    }

    override this.ToString() = $"{this.Lane}[{this.Index}] {this.Op}"

/// How a recorded call ended, before its value is stored.
[<RequireQualifiedAccess>]
type internal Ended =
    | Value of Json
    | Threw of string
    | Cancelled
    | Pending

    override this.ToString() =
        match this with
        | Value json -> Json.serialize json
        | Threw name -> "threw " + name
        | Cancelled -> "cancelled"
        | Pending -> "pending"

type internal Buffered =
    {
        Seq: int64
        Call: JournalCall
        Ended: Ended
    }

    override this.ToString() = $"#{this.Seq} {this.Call}"

/// One session's journal in this host process (one segment): every call its turns, its hands and its
/// kernels make is numbered (`Seq`) as it completes, buffered, and appended to `journal/<session>` in
/// batches (at the end of each turn, when the buffer is large, and when the host closes). Values larger
/// than `InlineBytes` go to the blob store. Thread-safe.
[<Sealed>]
type SessionJournal
    (session: string, log: EventLog<JournalEvent>, blobs: Blobs, clock: Fx.Effects.Clock, settings: RecorderSettings) =
    let gate = obj ()
    let lanes = Dictionary<string, int64>(StringComparer.Ordinal)
    let buffer = ResizeArray<Buffered>()
    let running = Dictionary<struct (string * int64), JournalCall>()
    let once = HashSet<string>(StringComparer.Ordinal)
    let flushes = new SemaphoreSlim(1, 1)
    let stream = JournalLog.stream session
    let mutable seq = 0L
    let mutable buffered = 0L
    let mutable segment: int option = None
    let mutable batches = 0
    let mutable closed = false

    let meta (key: string) : EventMeta =
        {
            At = clock.Now()
            By = Attribution.system "recorder"
            Key = IdempotencyKey key
            Cause = None
        }

    let append (key: string) (event: JournalEvent) : Task<unit> =
        task {
            let mutable written = false
            let mutable attempts = 0

            while not written do
                attempts <- attempts + 1
                let! head = log.Head stream CancellationToken.None

                match! log.Append stream head (meta key) [ event ] CancellationToken.None with
                | Ok _
                | Error(Duplicate _) -> written <- true
                | Error(Conflict _) when attempts < 5 -> ()
                | Error e -> raise (ConcurrencyException(stream, e))
        }

    /// This process's segment: the one after the journal's last, begun on the first flush.
    let beginSegment () : Task<int> =
        task {
            match segment with
            | Some current -> return current
            | None ->
                let! head = log.Head stream CancellationToken.None

                let! last =
                    if head = 0L then
                        Task.FromResult []
                    else
                        log.Read stream (head - 1L) 1 CancellationToken.None

                let next =
                    match last with
                    | recorded :: _ -> recorded.Event.Segment + 1
                    | [] -> 1

                do! append $"{next}/began" (JournalEvent.Began next)
                segment <- Some next
                return next
        }

    let store (item: Buffered) : Task<JournalEntry> =
        task {
            let! outcome =
                match item.Ended with
                | Ended.Value json ->
                    let text = Json.serializeCanonical json
                    let bytes = Encoding.UTF8.GetBytes text

                    if bytes.Length <= settings.InlineBytes then
                        Task.FromResult(Outcome.Returned(Stored.Inline json))
                    else
                        task {
                            let! blob = blobs.Put bytes CancellationToken.None
                            return Outcome.Returned(Stored.Blob blob)
                        }
                | Ended.Threw name -> Task.FromResult(Outcome.Threw name)
                | Ended.Cancelled -> Task.FromResult Outcome.Cancelled
                | Ended.Pending -> Task.FromResult Outcome.Pending

            return
                {
                    Seq = item.Seq
                    Lane = item.Call.Lane
                    Index = item.Call.Index
                    Op = item.Call.Op
                    Input = item.Call.Input
                    Summary = item.Call.Summary
                    Outcome = outcome
                }
        }

    let flush () : Task<unit> =
        task {
            do! flushes.WaitAsync()

            try
                let taken =
                    lock gate (fun () ->
                        let taken = List.ofSeq buffer
                        buffer.Clear()
                        buffered <- 0L
                        taken)

                if not taken.IsEmpty then
                    let! current = beginSegment ()

                    for chunk in List.chunkBySize settings.BatchEntries taken do
                        let entries = ResizeArray<JournalEntry>()

                        for item in chunk do
                            let! entry = store item
                            entries.Add entry

                        batches <- batches + 1
                        do! append $"{current}/{batches}" (JournalEvent.Recorded(current, List.ofSeq entries))
            finally
                flushes.Release() |> ignore
        }

    let numbered (call: JournalCall) =
        match call.Seq with
        | Some reserved -> reserved
        | None ->
            seq <- seq + 1L
            seq

    let complete (call: JournalCall) (ended: Ended) =
        lock gate (fun () ->
            running.Remove(struct (call.Lane, call.Index)) |> ignore

            if not closed then
                let size =
                    match ended with
                    | Ended.Value json -> int64 (Json.serialize json).Length
                    | _ -> 0L

                buffered <- buffered + size + 64L

                buffer.Add
                    {
                        Seq = numbered call
                        Call = call
                        Ended = ended
                    })

    let start (lane: string) (op: string) (input: string) (summary: string) (acts: bool) : JournalCall =
        lock gate (fun () ->
            let index =
                match lanes.TryGetValue lane with
                | true, next -> next
                | _ -> 0L

            lanes[lane] <- index + 1L

            let reserved =
                if acts then
                    seq <- seq + 1L
                    Some seq
                else
                    None

            let flow = Fx.Effects.Flow.path ()

            let call =
                {
                    Lane = lane
                    Index = index
                    Op = op
                    Input = Digest.inFlow input
                    Summary = Digest.bound 160 (if flow = "" then summary else $"{summary} (flow {flow})")
                    Seq = reserved
                }

            running[struct (lane, index)] <- call
            call)

    member _.Session = session

    /// Starts a call in a lane: it takes the lane's next index (the order calls start in), and its place
    /// in `Seq` order when it completes (what it brings from the world arrives then).
    member _.Start(lane: string, op: string, input: string, summary: string) : JournalCall =
        start lane op input summary false

    /// Starts a call that acts on the world (writes to a process): it takes its place in `Seq` order
    /// now, since others may see its effect before it completes.
    member _.StartActing(lane: string, op: string, input: string, summary: string) : JournalCall =
        start lane op input summary true

    /// Ends a call with a value.
    member _.Returned(call: JournalCall, value: Json) = complete call (Ended.Value value)

    /// Ends a call with an exception: cancelled, or a defect (its type).
    member _.Failed(call: JournalCall, e: exn) =
        match e with
        | :? OperationCanceledException -> complete call Ended.Cancelled
        | e -> complete call (Ended.Threw(e.GetType().Name))

    /// A marker: a call that starts and ends at once.
    member this.Mark(lane: string, op: string, input: string, summary: string, value: Json) =
        let call = this.Start(lane, op, input, summary)
        this.Returned(call, value)

    /// Whether `key` was not seen before (and marks it seen): for what is journaled once.
    member _.Once(key: string) : bool = lock gate (fun () -> once.Add key)

    /// Flushes first if the buffer is large.
    member _.Due() : Task<unit> =
        let full =
            lock gate (fun () -> buffer.Count >= settings.FlushEntries || buffered >= settings.FlushBytes)

        if full then flush () else Task.FromResult()

    member private this.RunAs<'T>(call: unit -> JournalCall, encode: 'T -> Json, work: unit -> Task<'T>) : Task<'T> =
        task {
            do! this.Due()
            let call = call ()

            try
                let! value = work ()
                this.Returned(call, encode value)
                return value
            with e ->
                this.Failed(call, e)
                return Internal.rethrow e
        }

    /// Runs and journals an asynchronous call: its value, or how it failed.
    member this.Run<'T>
        (lane: string, op: string, input: string, summary: string, encode: 'T -> Json, work: unit -> Task<'T>)
        : Task<'T> =
        this.RunAs((fun () -> start lane op input summary false), encode, work)

    /// Runs and journals a call that acts on the world (`StartActing`).
    member this.RunActing<'T>
        (lane: string, op: string, input: string, summary: string, encode: 'T -> Json, work: unit -> Task<'T>)
        : Task<'T> =
        this.RunAs((fun () -> start lane op input summary true), encode, work)

    /// Runs and journals a synchronous call.
    member this.RunSync<'T>
        (lane: string, op: string, input: string, summary: string, encode: 'T -> Json, run: unit -> 'T)
        : 'T =
        let call = this.Start(lane, op, input, summary)

        try
            let value = run ()
            this.Returned(call, encode value)
            value
        with e ->
            this.Failed(call, e)
            Internal.rethrow e

    /// Appends what is buffered to the journal.
    member _.Flush() : Task<unit> = flush ()

    /// Ends the segment: calls still running are journaled as pending, everything is flushed, and the
    /// segment is marked ended. Later calls are not journaled.
    member _.Close() : Task<unit> =
        task {
            lock gate (fun () ->
                for call in List.ofSeq running.Values |> List.sortBy (fun c -> c.Lane, c.Index) do
                    buffer.Add
                        {
                            Seq = numbered call
                            Call = call
                            Ended = Ended.Pending
                        }

                running.Clear()
                closed <- true)

            do! flush ()

            match segment with
            | Some current -> do! append $"{current}/ended" (JournalEvent.Ended current)
            | None -> ()
        }

    override _.ToString() = $"journal of {session}"

/// The journals of this host process, one per session, kept in the host's event store and blob store.
/// `clock` (not recorded) stamps the journal's own events.
[<Sealed>]
type Recorder(log: EventLog<Payload>, blobs: Blobs, clock: Fx.Effects.Clock, settings: RecorderSettings) =
    let journals = ConcurrentDictionary<string, SessionJournal>(StringComparer.Ordinal)
    let typed = JournalLog.typed log

    /// The session's journal in this process.
    member _.Session(session: string) : SessionJournal =
        journals.GetOrAdd(session, (fun id -> SessionJournal(id, typed, blobs, clock, settings)))

    /// Flushes every journal.
    member _.Flush() : Task<unit> =
        task {
            for journal in List.ofSeq journals.Values do
                do! journal.Flush()
        }

    /// Ends every journal's segment (the host stops).
    member _.Close() : Task<unit> =
        task {
            for journal in List.ofSeq journals.Values do
                do! journal.Close()
        }

    override _.ToString() = "Recorder"
