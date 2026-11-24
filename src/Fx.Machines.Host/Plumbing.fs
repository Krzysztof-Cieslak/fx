namespace Fx.Machines.Host

open System.Collections.Generic
open System.Runtime.ExceptionServices
open System.Threading.Tasks

/// An actor's input queue: many writers, one reader. Urgent items (the control path, spec §5) go
/// before every queued item. Thread-safe.
///
/// Items are handed to a waiting reader on the posting thread, so the reader continues inline: with
/// synchronous logs and a fake clock, the order of everything an actor does follows from the order
/// of its inputs.
[<Sealed>]
type internal Mailbox<'T>() =
    let gate = obj ()
    let urgent = Queue<'T>()
    let normal = Queue<'T>()
    let mutable reader: TaskCompletionSource<'T voption> option = None
    let mutable closed = false

    /// Adds an item; false if the mailbox is closed.
    member _.Post(item: 'T, isUrgent: bool) : bool =
        let accepted, handTo =
            lock gate (fun () ->
                if closed then
                    false, None
                else
                    match reader with
                    | Some waiting ->
                        reader <- None
                        true, Some waiting
                    | None ->
                        (if isUrgent then urgent else normal).Enqueue item
                        true, None)

        // Outside the lock: the reader continues inline.
        match handTo with
        | Some waiting -> waiting.TrySetResult(ValueSome item) |> ignore
        | None -> ()

        accepted

    /// The next item, urgent ones first; waits if there is none. `ValueNone` once the mailbox is
    /// closed. One reader at a time.
    member _.Take() : Task<'T voption> =
        lock gate (fun () ->
            if urgent.Count > 0 then
                Task.FromResult(ValueSome(urgent.Dequeue()))
            elif normal.Count > 0 then
                Task.FromResult(ValueSome(normal.Dequeue()))
            elif closed then
                Task.FromResult ValueNone
            else
                let waiting = TaskCompletionSource<'T voption>()
                reader <- Some waiting
                waiting.Task)

    /// Closes the mailbox and returns the items nobody took, in the order they would have been taken.
    member _.Close() : 'T list =
        let left, waiting =
            lock gate (fun () ->
                closed <- true
                let left = List.ofSeq urgent @ List.ofSeq normal
                urgent.Clear()
                normal.Clear()
                let waiting = reader
                reader <- None
                left, waiting)

        match waiting with
        | Some waiting -> waiting.TrySetResult ValueNone |> ignore
        | None -> ()

        left

    member _.IsClosed = lock gate (fun () -> closed)

/// Background work that belongs to an actor: tracked so that stopping waits for all of it.
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

    /// Waits until no tracked work is left, including work started meanwhile. Ignores outcomes:
    /// tracked work reports its own failures.
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

    member _.Count = lock gate (fun () -> running.Count)

module internal Exn =
    /// Rethrows an exception with its original stack trace.
    let rethrow (e: exn) : 'T =
        ExceptionDispatchInfo.Capture(e).Throw()
        Unchecked.defaultof<'T>
