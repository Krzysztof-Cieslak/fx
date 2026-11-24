namespace Fx.Effects.Fakes

open System
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// A clock that moves only when told to, for deterministic tests of time-dependent effects.
///
/// `Advance` fires due delays one at a time in deadline order (ties in the order they started), with
/// the clock set to each delay's deadline, and runs what was waiting on it up to its next incomplete
/// await before firing the next. So an effect that sleeps in a loop runs as many iterations as fit in
/// the advance, and sees the right time in each.
///
/// That relies on .NET running task continuations inline, which it does only on a thread with enough
/// known stack. The main thread of a musl Native AOT binary is not one (its stack size is unknown), so
/// `Advance` throws there: drive the clock from a thread-pool thread instead.
[<Sealed>]
type FakeClock(start: Instant) =
    let gate = obj ()
    let mutable mono = TimeSpan.Zero
    // The wall clock is `wallAtZero + mono`, so it moves with the monotonic clock unless `SetNow` jumps it.
    let mutable wallAtZero = start
    let mutable started = 0L

    let pending = ResizeArray<struct (TimeSpan * int64 * TaskCompletionSource<unit>)>()

    let delay (duration: TimeSpan) (cancellation: CancellationToken) : Task<unit> =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<unit> cancellation
        elif duration <= TimeSpan.Zero then
            Task.FromResult()
        else
            let completion = TaskCompletionSource<unit>()

            let id =
                lock gate (fun () ->
                    started <- started + 1L
                    pending.Add(struct (mono + duration, started, completion))
                    started)

            if cancellation.CanBeCanceled then
                // Registered outside the lock: a token cancelled meanwhile runs the callback at once.
                let registration =
                    cancellation.Register(fun () ->
                        let removed =
                            lock gate (fun () ->
                                let index = pending.FindIndex(fun struct (_, other, _) -> other = id)

                                if index >= 0 then
                                    pending.RemoveAt index

                                index >= 0)

                        if removed then
                            completion.TrySetCanceled cancellation |> ignore)

                completion.Task.ContinueWith(
                    (fun (_: Task<unit>) -> registration.Dispose()),
                    TaskContinuationOptions.ExecuteSynchronously
                )
                |> ignore

            completion.Task

    /// Fires the earliest delay due by `target`, if any.
    let fireNext (target: TimeSpan) : bool =
        let due =
            lock gate (fun () ->
                if pending.Count = 0 then
                    None
                else
                    let mutable best = 0

                    for i in 1 .. pending.Count - 1 do
                        let struct (deadline, id, _) = pending[i]
                        let struct (bestDeadline, bestId, _) = pending[best]

                        if deadline < bestDeadline || (deadline = bestDeadline && id < bestId) then
                            best <- i

                    let struct (deadline, _, completion) = pending[best]

                    if deadline <= target then
                        pending.RemoveAt best
                        mono <- deadline
                        Some completion
                    else
                        None)

        match due with
        | Some completion ->
            completion.TrySetResult() |> ignore
            true
        | None -> false

    member val Clock: Clock =
        {
            Now = fun () -> lock gate (fun () -> wallAtZero + mono)
            Monotonic = fun () -> lock gate (fun () -> MonoTime mono)
            Delay = delay
        }

    /// The current wall-clock time.
    member _.Now = lock gate (fun () -> wallAtZero + mono)

    /// The time since the clock was created, on its monotonic clock.
    member _.Elapsed = lock gate (fun () -> mono)

    /// How many delays are waiting.
    member _.PendingDelays = lock gate (fun () -> pending.Count)

    /// Moves both clocks forward, firing every delay that falls due on the way.
    member _.Advance(duration: TimeSpan) =
        if duration < TimeSpan.Zero then
            invalidArg (nameof duration) "A fake clock only moves forward."

        if not (RuntimeHelpers.TryEnsureSufficientExecutionStack()) then
            invalidOp
                "FakeClock.Advance needs a thread where task continuations run inline, or delays fire out of order. Advance from a thread-pool thread."

        let target = lock gate (fun () -> mono + duration)

        while fireNext target do
            ()

        lock gate (fun () -> mono <- target)

    /// Moves to the deadline of the earliest waiting delay and fires it (and any due at the same
    /// time). Returns false if nothing was waiting.
    member this.AdvanceToNextDelay() : bool =
        let next =
            lock gate (fun () ->
                if pending.Count = 0 then
                    None
                else
                    Some(pending |> Seq.map (fun struct (deadline, _, _) -> deadline) |> Seq.min))

        match next with
        | Some deadline ->
            this.Advance(deadline - lock gate (fun () -> mono))
            true
        | None -> false

    /// Sets the wall clock without moving the monotonic clock or firing delays, like a system clock
    /// adjustment.
    member _.SetNow(now: Instant) =
        lock gate (fun () -> wallAtZero <- now - mono)

    /// A clock starting at 2026-01-01T00:00:00Z.
    static member Create() =
        FakeClock(Instant.ofDateTimeOffset (DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)))
