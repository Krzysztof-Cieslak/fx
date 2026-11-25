namespace Fx.Machines.Host

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Fx.Machines

/// An event log in memory, for tests, simulations and `fx test`. Thread-safe.
///
/// Every call completes synchronously, and an append wakes waiting readers before it returns, on the
/// appending thread. So actors, subscribers and process managers on this log run inline, in an order
/// fixed by their inputs: tests with a fake clock and fake ids get identical logs on every run.
[<Sealed>]
type InMemoryEventLog<'Evt>() =
    let gate = obj ()
    let streams = Dictionary<StreamId, ResizeArray<Recorded<'Evt>>>()
    let commits = Dictionary<struct (StreamId * IdempotencyKey), Appended>()

    let waiters =
        ResizeArray<struct (StreamId * int64 * int64 * TaskCompletionSource<int64>)>()

    let mutable lastWaiter = 0L

    let eventsOf (stream: StreamId) =
        match streams.TryGetValue stream with
        | true, events -> events
        | false, _ ->
            let events = ResizeArray()
            streams[stream] <- events
            events

    let headOf (stream: StreamId) =
        match streams.TryGetValue stream with
        | true, events -> int64 events.Count
        | false, _ -> 0L

    let append
        (stream: StreamId)
        (expected: int64)
        (meta: EventMeta)
        (events: 'Evt list)
        (cancellation: CancellationToken)
        =
        if events.IsEmpty then
            invalidArg (nameof events) "A commit has at least one event."

        if cancellation.IsCancellationRequested then
            Task.FromCanceled<Result<int64, AppendError>> cancellation
        else
            let result, woken =
                lock gate (fun () ->
                    let log = eventsOf stream
                    let head = int64 log.Count

                    match commits.TryGetValue(struct (stream, meta.Key)) with
                    | true, first -> Error(Duplicate(meta.Key, first)), []
                    | false, _ when head <> expected -> Error(Conflict(expected, head)), []
                    | false, _ ->
                        events
                        |> List.iteri (fun i event ->
                            log.Add(
                                {
                                    Stream = stream
                                    Seq = head + int64 i + 1L
                                    Event = event
                                    Meta = meta
                                }
                                : Recorded<'Evt>
                            ))

                        commits[struct (stream, meta.Key)] <- { After = head; Count = events.Length }

                        let head = int64 log.Count

                        let woken =
                            [
                                for struct (waiting, after, _, completion) in waiters do
                                    if waiting = stream && after < head then
                                        yield completion
                            ]

                        waiters.RemoveAll(fun struct (waiting, after, _, _) -> waiting = stream && after < head)
                        |> ignore

                        Ok head, woken |> List.map (fun completion -> completion, head))

            // Outside the lock: woken readers continue inline, and may append themselves.
            for completion, head in woken do
                completion.TrySetResult head |> ignore

            Task.FromResult result

    let read (stream: StreamId) (after: int64) (max: int) (cancellation: CancellationToken) =
        if max < 1 then
            invalidArg (nameof max) "Read at least one event."

        if cancellation.IsCancellationRequested then
            Task.FromCanceled<Recorded<'Evt> list> cancellation
        else
            lock gate (fun () ->
                match streams.TryGetValue stream with
                | true, log ->
                    let start = int (Math.Max(after, 0L))
                    let count = Math.Min(max, log.Count - start)

                    if count <= 0 then
                        []
                    else
                        List.ofSeq (log.GetRange(start, count))
                | false, _ -> [])
            |> Task.FromResult

    let waitPast (stream: StreamId) (after: int64) (cancellation: CancellationToken) : Task<int64> =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<int64> cancellation
        else
            let completion = TaskCompletionSource<int64>()

            let waiting =
                lock gate (fun () ->
                    let head = headOf stream

                    if head > after then
                        Error head
                    else
                        lastWaiter <- lastWaiter + 1L
                        waiters.Add(struct (stream, after, lastWaiter, completion))
                        Ok lastWaiter)

            match waiting with
            | Error head -> Task.FromResult head
            | Ok id ->
                if cancellation.CanBeCanceled then
                    // Registered outside the lock: a token cancelled meanwhile runs the callback at once.
                    let registration =
                        cancellation.Register(fun () ->
                            let removed =
                                lock gate (fun () -> waiters.RemoveAll(fun struct (_, _, other, _) -> other = id) > 0)

                            if removed then
                                completion.TrySetCanceled cancellation |> ignore)

                    completion.Task.ContinueWith(
                        (fun (_: Task<int64>) -> registration.Dispose()),
                        TaskContinuationOptions.ExecuteSynchronously
                    )
                    |> ignore

                completion.Task

    member val Log: EventLog<'Evt> =
        {
            Append = append
            Read = read
            Head = fun stream _ -> lock gate (fun () -> headOf stream) |> Task.FromResult
            WaitPast = waitPast
            FindCommit =
                fun stream key cancellation ->
                    if cancellation.IsCancellationRequested then
                        Task.FromCanceled<Appended option> cancellation
                    else
                        lock gate (fun () ->
                            match commits.TryGetValue(struct (stream, key)) with
                            | true, appended -> Some appended
                            | false, _ -> None)
                        |> Task.FromResult
        }

    /// Every event of a stream, in order.
    member _.Events(stream: StreamId) : Recorded<'Evt> list =
        lock gate (fun () ->
            match streams.TryGetValue stream with
            | true, log -> List.ofSeq log
            | false, _ -> [])

    /// The streams that have events, sorted by name.
    member _.Streams: StreamId list =
        lock gate (fun () ->
            streams
            |> Seq.filter (fun pair -> pair.Value.Count > 0)
            |> Seq.map (fun pair -> pair.Key)
            |> Seq.sortBy (fun stream -> stream.Value)
            |> List.ofSeq)

    /// How many readers are waiting for appends.
    member _.Waiting: int = lock gate (fun () -> waiters.Count)

    override _.ToString() = "InMemoryEventLog"
