namespace Fx.Effects

open System
open System.Threading
open System.Threading.Tasks

/// The clock capability: wall-clock time, monotonic time and delays.
///
/// Interpreters: `SystemClock` (Fx.Effects.Live), `FakeClock` (Fx.Effects.Fakes, time moves only
/// when a test advances it), `Record.clock` / `Replay.clock` (Fx.Effects.Replay).
type Clock =
    {
        /// The current wall-clock time. May jump when the system clock is adjusted.
        Now: unit -> Instant
        /// A monotonic reading for durations and deadlines.
        Monotonic: unit -> MonoTime
        /// Completes once the duration has passed on the monotonic clock; at once for zero or
        /// negative durations. Cancelling the token fails it with an `OperationCanceledException`.
        Delay: TimeSpan -> CancellationToken -> Task<unit>
    }

    override _.ToString() = "Clock"

/// An environment with a clock.
type IClock =
    abstract Clock: Clock

module Clock =
    /// The current wall-clock time.
    let now<'R when 'R :> IClock> : Eff<'R, Instant> =
        fun env -> Task.FromResult(env.Clock.Now())

    /// The current monotonic time.
    let monotonic<'R when 'R :> IClock> : Eff<'R, MonoTime> =
        fun env -> Task.FromResult(env.Clock.Monotonic())

    /// Waits for the duration. Cancelled with the scope.
    let sleep<'R when 'R :> IClock and 'R :> IScope> (duration: TimeSpan) : Eff<'R, unit> =
        fun env -> env.Clock.Delay duration env.Scope.Cancellation

    /// Waits until a monotonic deadline. Cancelled with the scope.
    let sleepUntil<'R when 'R :> IClock and 'R :> IScope> (deadline: MonoTime) : Eff<'R, unit> =
        fun env -> env.Clock.Delay (deadline - env.Clock.Monotonic()) env.Scope.Cancellation

    /// Runs the effect and measures how long it took on the monotonic clock.
    let timed (effect: Eff<'R, 'T>) : Eff<'R, 'T * TimeSpan> =
        eff {
            let! start = monotonic
            let! result = effect
            let! finish = monotonic
            return result, finish - start
        }

    /// A periodic timer: runs `tick` every `period` (at fixed deadlines from the start, skipping
    /// deadlines that have already passed) until it returns false. Cancelled with the scope.
    let every (period: TimeSpan) (tick: Eff<'R, bool>) : Eff<'R, unit> =
        if period <= TimeSpan.Zero then
            invalidArg (nameof period) "The period must be positive."

        eff {
            let! start = monotonic
            let mutable deadline = start + period
            let mutable running = true

            while running do
                do! sleepUntil deadline
                let! again = tick
                running <- again
                let! now = monotonic

                deadline <- deadline + period

                if deadline < now then
                    let missed = (now - deadline).Ticks / period.Ticks + 1L
                    deadline <- deadline + TimeSpan.FromTicks(missed * period.Ticks)
        }
