namespace Fx.Effects.Live

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// The real clock: the system's UTC time (truncated to microseconds), `Stopwatch` for monotonic
/// time, and `Task.Delay`. The only reader of the system clock.
module SystemClock =
    let private origin = Stopwatch.GetTimestamp()

    // Task.Delay accepts at most uint32.MaxValue - 1 milliseconds (about 49.7 days).
    let private longestDelay = TimeSpan.FromMilliseconds(float (UInt32.MaxValue - 1u))

    let private delay (duration: TimeSpan) (cancellation: CancellationToken) : Task<unit> =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<unit> cancellation
        elif duration <= TimeSpan.Zero then
            Task.FromResult()
        else
            task {
                let mutable remaining = duration

                while remaining > TimeSpan.Zero do
                    let step = if remaining > longestDelay then longestDelay else remaining
                    do! Task.Delay(step, cancellation)
                    remaining <- remaining - step
            }

    let clock: Clock =
        {
            Now = fun () -> Instant.ofDateTimeOffset DateTimeOffset.UtcNow
            Monotonic = fun () -> MonoTime(Stopwatch.GetElapsedTime origin)
            Delay = delay
        }
