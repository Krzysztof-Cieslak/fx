namespace Fx.Envd

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// Coalescing of file changes (pure).
module Changes =
    /// What two changes to one path in a row amount to; `None`: nothing (added, then deleted).
    let combine (earlier: ChangeKind option) (later: ChangeKind) : ChangeKind option =
        match earlier, later with
        | None, kind -> Some kind
        | Some ChangeKind.Added, ChangeKind.Deleted -> None
        | Some ChangeKind.Added, _ -> Some ChangeKind.Added
        | Some ChangeKind.Deleted, ChangeKind.Deleted -> Some ChangeKind.Deleted
        | Some ChangeKind.Deleted, _ -> Some ChangeKind.Modified
        | Some ChangeKind.Modified, ChangeKind.Deleted -> Some ChangeKind.Deleted
        | Some ChangeKind.Modified, _ -> Some ChangeKind.Modified

    /// One change per path, in the order the paths first changed; paths added and deleted again
    /// drop out.
    let coalesce (changes: FileChange seq) : FileChange list =
        let order = ResizeArray<string>()
        let state = Dictionary<string, ChangeKind option>(StringComparer.Ordinal)

        for change in changes do
            match state.TryGetValue change.Path with
            | true, earlier -> state[change.Path] <- combine earlier change.Kind
            | false, _ ->
                order.Add change.Path
                state[change.Path] <- Some change.Kind

        [
            for path in order do
                match state[path] with
                | Some kind -> { Path = path; Kind = kind }
                | None -> ()
        ]

/// Changes waiting to be delivered as debounced batches: the watcher interpreters' buffer. Adding
/// never blocks (it runs on the OS watcher's thread); beyond `capacity` pending changes it records an
/// overflow instead.
[<Sealed>]
type ChangeQueue(capacity: int) =
    let gate = obj ()
    let pending = ResizeArray<FileChange>()
    let mutable overflow = false
    let mutable completed = false
    let mutable waiter: TaskCompletionSource<unit> option = None

    let wake () =
        let w = waiter
        waiter <- None

        match w with
        | Some w -> w.TrySetResult() |> ignore
        | None -> ()

    /// Records a change.
    member _.Add(change: FileChange) : unit =
        lock gate (fun () ->
            if not completed then
                if pending.Count >= capacity then
                    overflow <- true
                else
                    pending.Add change

                wake ())

    /// Records that changes were lost.
    member _.Overflowed() : unit =
        lock gate (fun () ->
            if not completed then
                overflow <- true
                wake ())

    /// The watch is closed: pending changes are dropped, and `Next` returns `None` from now on, also
    /// a call that is waiting or debouncing. The OS watcher may still report changes it saw before it
    /// stopped (on Windows, after `Dispose`); they are dropped too.
    member _.Complete() : unit =
        lock gate (fun () ->
            completed <- true
            pending.Clear()
            overflow <- false
            wake ())

    /// Waits for a change, then for `debounce` on the clock, and returns everything pending as one
    /// coalesced batch; `None` once completed.
    member _.Next(clock: Clock, debounce: TimeSpan, cancellation: CancellationToken) : Task<WatchBatch option> =
        task {
            let mutable result = None
            let mutable finished = false

            while not finished do
                let state =
                    lock gate (fun () ->
                        if completed then
                            Choice2Of3()
                        elif pending.Count > 0 || overflow then
                            Choice1Of3()
                        else
                            let w =
                                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                            waiter <- Some w
                            Choice3Of3 w.Task)

                match state with
                | Choice1Of3() ->
                    if debounce > TimeSpan.Zero then
                        do! clock.Delay debounce cancellation

                    let taken =
                        lock gate (fun () ->
                            if completed then
                                None
                            else
                                let changes = List.ofSeq pending
                                let lost = overflow
                                pending.Clear()
                                overflow <- false
                                Some(changes, lost))

                    match taken with
                    | None -> finished <- true
                    | Some(changes, lost) ->
                        let coalesced = Changes.coalesce changes

                        if not coalesced.IsEmpty || lost then
                            result <- Some { Changes = coalesced; Overflow = lost }

                            finished <- true
                | Choice2Of3() -> finished <- true
                | Choice3Of3 wait -> do! wait.WaitAsync cancellation

            return result
        }

    override _.ToString() = $"ChangeQueue({pending.Count} pending)"
