namespace Fx.Replay

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Fx.Contracts

/// Why a replay does not match its recording: the first difference, with what the journal (or the
/// recorded logs) has there and what replay did.
type Divergence =
    {
        Segment: int
        Lane: string
        /// What the recording has: the journal entry (or recorded event) replay should have matched.
        Expected: string
        /// What replay did instead.
        Actual: string
        Reason: string
    }

    override this.ToString() =
        $"segment {this.Segment}, {this.Lane}: {this.Reason}\n  recorded: {this.Expected}\n  replayed: {this.Actual}"

/// A replayed call that failed as the recorded one did (`Outcome.Threw`): the recorded exception's type.
type ReplayedFailure(exceptionType: string) =
    inherit Exception("replayed failure: " + exceptionType)

    member _.ExceptionType = exceptionType

/// When a replayed call completes.
[<RequireQualifiedAccess>]
type Gate =
    /// At once: its value does not depend on when it arrives (files, commands, the model's events).
    | Immediate
    /// Once every call recorded before it has completed in replay: it completed when something else
    /// happened (a delay elapsed, a process wrote, a person answered), so replay keeps that order.
    | Ordered

    override this.ToString() =
        match this with
        | Immediate -> "immediate"
        | Ordered -> "ordered"

/// A journal entry in replay: claimed by the call that matched it, done once that call completed.
[<Sealed>]
type ReplayEntry internal (entry: JournalEntry, value: Json option, position: int) =
    member _.Entry = entry
    /// The value of a `Returned` outcome (read from the blob store if it was stored there).
    member _.Value = value
    /// Its place in `Seq` order.
    member _.Position = position
    member val Claimed = false with get, set
    member val Done = false with get, set

    override _.ToString() = string entry

/// What a replayed call found in its lane.
[<RequireQualifiedAccess>]
type Found =
    /// The recorded call it matches (now claimed).
    | Entry of ReplayEntry
    /// None, and the lane's next unclaimed entry: a divergence.
    | Missing of next: ReplayEntry option
    /// None, but the segment stopped without ending: the call may have been running then.
    | Beyond
    /// Replay already diverged.
    | Stopped

    override this.ToString() =
        match this with
        | Entry item -> string item
        | Missing _ -> "missing"
        | Beyond -> "beyond the journal"
        | Stopped -> "stopped"

/// One segment of a session's journal (one host process), replayed: calls are matched to its entries
/// lane by lane, by operation and input digest, in the order they started; calls whose completion
/// depended on time complete in the order recorded (`Gate.Ordered`). The first mismatch is the
/// divergence: replay stops (`Stop` is cancelled) and every replayed call throws
/// `ReplayDivergedException`. Thread-safe.
[<Sealed>]
type ReplaySegment(number: int, entries: (JournalEntry * Json option) list, ended: bool) =
    let gate = obj ()

    // A call that was cancelled (or still running at the end) gave nothing the program used: replay
    // may or may not make it (whether a timeout's timer even starts can depend on timing), so it is
    // done from the start and never holds up what comes after it. A turn's end is not a call: the
    // host's closing waits for it, however the turn ended.
    let ordered =
        entries
        |> List.sortBy (fun (entry, _) -> entry.Seq)
        |> List.mapi (fun position (entry, value) ->
            let item = ReplayEntry(entry, value, position)

            match entry.Outcome with
            | Outcome.Cancelled when entry.Lane <> "turns" && entry.Lane <> "host" -> item.Done <- true
            | Outcome.Pending -> item.Done <- true
            | _ -> ()

            item)
        |> Array.ofList

    let lanes =
        let byLane = Dictionary<string, ResizeArray<ReplayEntry>>(StringComparer.Ordinal)

        for item in ordered |> Array.sortBy (fun e -> e.Entry.Lane, e.Entry.Index) do
            match byLane.TryGetValue item.Entry.Lane with
            | true, list -> list.Add item
            | _ ->
                let list = ResizeArray()
                list.Add item
                byLane[item.Entry.Lane] <- list

        byLane

    let waiters = ResizeArray<int * TaskCompletionSource>()
    let stop = new CancellationTokenSource()

    let mutable floor =
        ordered
        |> Array.tryFindIndex (fun e -> not e.Done)
        |> Option.defaultValue ordered.Length

    let mutable progress = 0L

    let mutable progressed =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable divergence: Divergence option = None

    let release () =
        let ready =
            waiters |> Seq.filter (fun (position, _) -> position <= floor) |> List.ofSeq

        for item in ready do
            waiters.Remove item |> ignore

        ready

    /// Stops the segment: everything waiting gives up.
    let halt () =
        try
            stop.Cancel()
        with :? ObjectDisposedException ->
            ()

        let all, signal =
            lock gate (fun () ->
                let all = List.ofSeq waiters
                waiters.Clear()
                let signal = progressed
                progressed <- TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                all, signal)

        for _, waiter in all do
            waiter.TrySetResult() |> ignore

        signal.TrySetResult() |> ignore

    let describe (item: ReplayEntry option) =
        match item with
        | Some item -> string item.Entry
        | None -> "nothing: the lane ends here"

    let trace = ResizeArray<string>()

    member _.Trace: string list = lock gate (fun () -> List.ofSeq trace)

    member _.Note(text: string) = lock gate (fun () -> trace.Add text)

    member _.Number = number

    /// Whether the host closed this segment cleanly (else it stopped without that: a crash, or
    /// recording was switched off).
    member _.Ended = ended

    /// Cancelled when replay of the segment stops (it ended, or diverged).
    member _.Stop: CancellationToken = stop.Token

    member _.Entries: ReplayEntry list = List.ofArray ordered

    /// The first divergence, if any.
    member _.Divergence = lock gate (fun () -> divergence)

    /// How many entries completed so far.
    member _.Progress = lock gate (fun () -> progress)

    /// Completes at the next completion of an entry.
    member _.Progressed: Task = lock gate (fun () -> progressed.Task)

    /// Whether every entry completed.
    member _.Finished = lock gate (fun () -> floor >= ordered.Length)

    /// The first entry that did not complete, if any.
    member _.Waiting: ReplayEntry option =
        lock gate (fun () -> ordered |> Array.tryFind (fun e -> not e.Done))

    /// Records the first divergence and stops the segment. Returns the exception replayed calls throw.
    member _.Diverge(lane: string, expected: string, actual: string, reason: string) : exn =
        let first =
            lock gate (fun () ->
                match divergence with
                | Some _ -> false
                | None ->
                    divergence <-
                        Some
                            {
                                Segment = number
                                Lane = lane
                                Expected = expected
                                Actual = actual
                                Reason = reason
                            }

                    true)

        if first then
            halt ()

        ReplayDivergedException $"replay diverged in {lane}: {reason}" :> exn

    /// The entry a call matches, claimed: the first unclaimed one of its lane with the same operation
    /// and input.
    member _.Find(lane: string, op: string, input: string) : Found =
        let input = Digest.inFlow input

        lock gate (fun () ->
            match divergence with
            | Some _ -> Found.Stopped
            | None ->
                match lanes.TryGetValue lane with
                | true, list ->
                    let unclaimed = list |> Seq.filter (fun e -> not e.Claimed)

                    match unclaimed |> Seq.tryFind (fun e -> e.Entry.Op = op && e.Entry.Input = input) with
                    | Some item ->
                        item.Claimed <- true
                        trace.Add $"claim #{item.Entry.Seq} {lane} {op}"
                        Found.Entry item
                    | None when not ended -> Found.Beyond
                    | None -> Found.Missing(Seq.tryHead unclaimed)
                | _ when not ended -> Found.Beyond
                | _ -> Found.Missing None)

    /// The entry a call matches (`Find`). A call the journal does not have is a divergence, unless the
    /// segment stopped without ending (the call may have been running when the host stopped): `None`,
    /// and the call waits for the end.
    member this.Claim(lane: string, op: string, input: string, actual: string) : ReplayEntry option =
        match this.Find(lane, op, input) with
        | Found.Entry item -> Some item
        | Found.Beyond -> None
        | Found.Stopped -> raise (ReplayDivergedException "replay already diverged")
        | Found.Missing expected ->
            raise (
                this.Diverge(
                    lane,
                    describe expected,
                    op
                    + (if actual = "" then "" else " " + actual)
                    + (match Fx.Effects.Flow.path () with
                       | "" -> ""
                       | flow -> $" (flow {flow})"),
                    "replay made a call the recording does not have here"
                )
            )

    /// Claims a marker (the driver's): a turn's start or cancellation, the host closing.
    member _.ClaimMarker(item: ReplayEntry) =
        lock gate (fun () -> item.Claimed <- true)

    /// Marks an entry done: entries after it that waited for it may complete.
    member _.Complete(item: ReplayEntry) =
        let ready, signal =
            lock gate (fun () ->
                if not item.Done then
                    item.Done <- true
                    item.Claimed <- true
                    trace.Add $"done #{item.Entry.Seq} {item.Entry.Lane} {item.Entry.Op}"

                    while floor < ordered.Length && ordered[floor].Done do
                        floor <- floor + 1

                progress <- progress + 1L
                let signal = progressed
                progressed <- TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                release (), signal)

        for _, waiter in ready do
            waiter.TrySetResult() |> ignore

        signal.TrySetResult() |> ignore

    /// Completes once every entry recorded before `item` is done (or the segment stopped).
    member _.Before(item: ReplayEntry) : Task =
        lock gate (fun () ->
            if floor >= item.Position || stop.IsCancellationRequested then
                Task.CompletedTask
            else
                let waiter =
                    TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

                waiters.Add(item.Position, waiter)
                waiter.Task)

    /// Completes when `cancellation` or the segment stops: what a call waits for when the recorded one
    /// was cancelled, was still running when the host stopped, or is past the journal's end.
    member _.UntilCancelled(cancellation: CancellationToken) : Task =
        let waiter =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let a = cancellation.Register(fun () -> waiter.TrySetResult() |> ignore)
        let b = stop.Token.Register(fun () -> waiter.TrySetResult() |> ignore)

        waiter.Task.ContinueWith(fun (_: Task) ->
            a.Dispose()
            b.Dispose())

    member private this.Stopped(cancellation: CancellationToken) : exn =
        match this.Divergence with
        | Some _ when not cancellation.IsCancellationRequested -> ReplayDivergedException "replay diverged" :> exn
        | _ -> OperationCanceledException(cancellation) :> exn

    /// The recorded outcome of a claimed call, decoded, when its gate opens.
    member this.Answer<'T>
        (item: ReplayEntry option, gate: Gate, decode: Json -> 'T, cancellation: CancellationToken)
        : Task<'T> =
        this.AnswerThen(item, gate, decode, cancellation, Task.FromResult)

    /// The recorded outcome of a claimed call, decoded, when its gate opens, after `apply` did replay's
    /// own part of the call with the recorded value (an append written again, a read waiting for what
    /// the recorded one saw). The entry is done only then: what was recorded after the call (a turn's
    /// cancellation) cannot happen before replay's part of it. The recorded call completed, so `apply`
    /// must not give up on the caller's cancellation (only when the segment stops).
    member this.AnswerThen<'T, 'U>
        (
            item: ReplayEntry option,
            gate: Gate,
            decode: Json -> 'T,
            cancellation: CancellationToken,
            apply: 'T -> Task<'U>
        ) : Task<'U> =
        task {
            match item with
            | None ->
                do! this.UntilCancelled cancellation
                return raise (this.Stopped cancellation)
            | Some item ->
                match item.Entry.Outcome with
                | Outcome.Returned _
                | Outcome.Threw _ ->
                    if gate = Gate.Ordered then
                        do! this.Before item

                    if stop.IsCancellationRequested then
                        return raise (this.Stopped cancellation)
                    else
                        match item.Entry.Outcome, item.Value with
                        | Outcome.Threw name, _ ->
                            this.Complete item
                            return raise (ReplayedFailure name)
                        | _, Some value ->
                            let decoded =
                                try
                                    decode value
                                with _ ->
                                    this.Complete item
                                    reraise ()

                            let! applied =
                                task {
                                    try
                                        return! apply decoded
                                    with :? OperationCanceledException when stop.IsCancellationRequested ->
                                        return raise (this.Stopped cancellation)
                                }

                            this.Complete item
                            return applied
                        | _, None ->
                            return
                                raise (
                                    this.Diverge(
                                        item.Entry.Lane,
                                        string item.Entry,
                                        "",
                                        "the recorded value is missing"
                                    )
                                )
                | Outcome.Cancelled
                | Outcome.Pending ->
                    do! this.UntilCancelled cancellation
                    this.Complete item
                    return raise (this.Stopped cancellation)
        }

    /// The recorded outcome of a synchronous call. Past the journal's end it blocks until the segment
    /// stops (only when replaying a host that stopped without closing).
    member this.AnswerSync<'T>(item: ReplayEntry option, decode: Json -> 'T) : 'T =
        match item with
        | None ->
            stop.Token.WaitHandle.WaitOne() |> ignore
            raise (this.Stopped CancellationToken.None)
        | Some item ->
            this.Complete item

            match item.Entry.Outcome, item.Value with
            | Outcome.Threw name, _ -> raise (ReplayedFailure name)
            | Outcome.Returned _, Some value -> decode value
            | _ -> raise (OperationCanceledException())

    /// Stops the segment: calls still waiting give up.
    member _.End() = halt ()

    override _.ToString() =
        $"segment {number} ({ordered.Length} entries)"

/// Decoding recorded values: a value that does not decode is a divergence of the recording itself
/// (written by another version), a defect.
[<RequireQualifiedAccess>]
module Decode =
    let using (codec: Codec<'T>) : Json -> 'T =
        fun json ->
            match codec.Decode json with
            | Ok value -> value
            | Error issues ->
                raise (ReplayDivergedException("a recorded value does not decode: " + Issue.renderAtMost 3 issues))
