namespace Fx.Rpc

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// A queue bounded by a budget (bytes, or 1 per item for a count): many producers, one consumer.
///
/// - `Enqueue` waits while the queue is over budget. Waiting producers are admitted in order, and an
///   item larger than the whole budget is admitted once the queue is empty, so it is delayed, never
///   refused.
/// - The budget is held from `Enqueue` until the consumer calls `Release`, so it can also cover what
///   the consumer is still working on (a frame being written).
/// - `EnqueueUrgent` items skip the budget and go before the others. Use it only for items whose
///   number is bounded otherwise (credit frames, cancellations).
///
/// Continuations of waiting producers and of the consumer run asynchronously, never inline on the
/// thread that freed space or added an item.
[<Sealed>]
type internal BoundedQueue<'T>(budget: int64) =
    let gate = obj ()
    let urgent = Queue<'T>()
    let items = Queue<struct ('T * int64)>()
    let producers = LinkedList<struct ('T * int64 * TaskCompletionSource<bool>)>()
    let mutable used = 0L

    let mutable consumer: TaskCompletionSource<struct ('T * int64) voption> option =
        None
    // 0 open, 1 completed (drains, then ends), 2 failed (ends at once)
    let mutable state = 0

    let fits (size: int64) = used = 0L || used + size <= budget

    /// Under the lock: hands an admitted item to the waiting consumer or queues it.
    let admit (item: 'T) (size: int64) =
        used <- used + size

        match consumer with
        | Some waiting ->
            consumer <- None
            Some(waiting, struct (item, size))
        | None ->
            items.Enqueue(struct (item, size))
            None

    let deliver (handOff: (TaskCompletionSource<struct ('T * int64) voption> * struct ('T * int64)) option) =
        match handOff with
        | Some(waiting, entry) -> waiting.TrySetResult(ValueSome entry) |> ignore
        | None -> ()

    /// Admits waiting producers that fit now, in order. Returns what to complete outside the lock.
    let admitWaiting () =
        let admitted = ResizeArray()

        let mutable go = true

        while go && state = 0 && producers.Count > 0 do
            match producers.First with
            | null -> go <- false
            | first ->
                let struct (item, size, waiting) = first.Value

                if fits size then
                    producers.RemoveFirst()
                    admitted.Add((waiting, admit item size))
                else
                    go <- false

        admitted

    let finishAdmitted (admitted: ResizeArray<TaskCompletionSource<bool> * _>) =
        for waiting, handOff in admitted do
            deliver handOff
            waiting.TrySetResult true |> ignore

    let stop (next: int) : 'T list =
        let left, waitingProducers, waitingConsumer =
            lock gate (fun () ->
                if state = 0 || next > state then
                    state <- next

                let left =
                    if next = 2 then
                        let left =
                            List.ofSeq urgent
                            @ (items |> Seq.map (fun struct (item, _) -> item) |> List.ofSeq)

                        urgent.Clear()
                        items.Clear()
                        left
                    else
                        []

                let waitingProducers =
                    producers |> Seq.map (fun struct (_, _, w) -> w) |> List.ofSeq

                producers.Clear()

                let waitingConsumer =
                    if items.Count = 0 && urgent.Count = 0 then
                        let c = consumer
                        consumer <- None
                        c
                    else
                        None

                left, waitingProducers, waitingConsumer)

        for w in waitingProducers do
            w.TrySetResult false |> ignore

        match waitingConsumer with
        | Some c -> c.TrySetResult ValueNone |> ignore
        | None -> ()

        left

    /// Adds an item that counts `size` against the budget, waiting for room. False if the queue no
    /// longer takes items; cancelled with the token while waiting.
    member _.Enqueue(item: 'T, size: int64, cancellation: CancellationToken) : Task<bool> =
        let outcome =
            lock gate (fun () ->
                if state <> 0 then
                    Choice1Of3 false
                elif producers.Count = 0 && fits size then
                    Choice2Of3(admit item size)
                else
                    let waiting =
                        TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

                    Choice3Of3(waiting, producers.AddLast(struct (item, size, waiting))))

        match outcome with
        | Choice1Of3 accepted -> Task.FromResult accepted
        | Choice2Of3 handOff ->
            deliver handOff
            Task.FromResult true
        | Choice3Of3(waiting, node) ->
            if cancellation.CanBeCanceled then
                let registration =
                    cancellation.Register(fun () ->
                        let removed =
                            lock gate (fun () ->
                                if not (isNull node.List) then
                                    producers.Remove node
                                    true
                                else
                                    false)

                        if removed then
                            waiting.TrySetCanceled cancellation |> ignore)

                waiting.Task.ContinueWith(
                    (fun (_: Task<bool>) -> registration.Dispose()),
                    TaskContinuationOptions.ExecuteSynchronously
                )
                |> ignore

            waiting.Task

    /// Adds an item before all others, outside the budget. False if the queue no longer takes items.
    member _.EnqueueUrgent(item: 'T) : bool =
        let outcome =
            lock gate (fun () ->
                if state <> 0 then
                    None
                else
                    match consumer with
                    | Some waiting ->
                        consumer <- None
                        Some(Some waiting)
                    | None ->
                        urgent.Enqueue item
                        Some None)

        match outcome with
        | None -> false
        | Some(Some waiting) ->
            waiting.TrySetResult(ValueSome(struct (item, 0L))) |> ignore
            true
        | Some None -> true

    /// The next item and its size, urgent items first; waits if there is none. `ValueNone` once the
    /// queue is completed and drained, or failed. One consumer at a time.
    member _.Take() : Task<struct ('T * int64) voption> =
        lock gate (fun () ->
            if state = 2 then
                Task.FromResult ValueNone
            elif urgent.Count > 0 then
                Task.FromResult(ValueSome(struct (urgent.Dequeue(), 0L)))
            elif items.Count > 0 then
                Task.FromResult(ValueSome(items.Dequeue()))
            elif state = 1 then
                Task.FromResult ValueNone
            else
                let waiting =
                    TaskCompletionSource<struct ('T * int64) voption>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    )

                consumer <- Some waiting
                waiting.Task)

    /// Returns budget the consumer is done with.
    member _.Release(size: int64) : unit =
        if size > 0L then
            let admitted =
                lock gate (fun () ->
                    used <- used - size
                    admitWaiting ())

            finishAdmitted admitted

    /// Takes no more items; the consumer drains what is queued, then gets `ValueNone`. Waiting
    /// producers get false.
    member _.Complete() : unit = stop 1 |> ignore

    /// Takes no more items and removes the queued ones, which it returns (urgent ones first). The
    /// consumer gets `ValueNone`.
    member _.Drain() : 'T list = stop 2

    /// Like `Drain`, discarding the items.
    member _.Fail() : unit = stop 2 |> ignore

    /// Whether the queue was failed or drained (rather than completed).
    member _.IsFailed = lock gate (fun () -> state = 2)

/// Background work that must finish before its owner does. Thread-safe.
[<Sealed>]
type internal TaskGroup() =
    let gate = obj ()
    let running = HashSet<Task>()

    member _.Track(work: Task) : unit =
        if not work.IsCompleted then
            lock gate (fun () -> running.Add work |> ignore)

            work.ContinueWith(
                (fun (finished: Task) -> lock gate (fun () -> running.Remove finished |> ignore)),
                TaskContinuationOptions.ExecuteSynchronously
            )
            |> ignore

    /// Waits until no tracked work is left, including work started meanwhile. Ignores outcomes.
    member _.WaitAll() : Task<unit> =
        let unfinished () =
            lock gate (fun () -> running |> Seq.filter (fun work -> not work.IsCompleted) |> List.ofSeq)

        task {
            let mutable pending = unfinished ()

            while not pending.IsEmpty do
                for work in pending do
                    try
                        do! work
                    with _ ->
                        ()

                pending <- unfinished ()
        }
