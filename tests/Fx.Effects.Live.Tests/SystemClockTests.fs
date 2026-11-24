module Fx.Effects.Live.Tests.SystemClockTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Live

let private clock = SystemClock.clock

[<Tests>]
let tests =
    testList
        "SystemClock"
        [
            test "now is the system time in microseconds" {
                let before = DateTimeOffset.UtcNow
                let now = clock.Now()
                let after = DateTimeOffset.UtcNow
                Expect.isGreaterThanOrEqual now (Instant.ofDateTimeOffset before) "not before"
                Expect.isLessThanOrEqual now (Instant.ofDateTimeOffset after) "not after"
            }

            test "monotonic time never goes backwards" {
                let readings = List.init 10_000 (fun _ -> clock.Monotonic())

                for a, b in List.pairwise readings do
                    Expect.isLessThanOrEqual a b "non-decreasing"
            }

            test "delays wait at least their duration on the monotonic clock" {
                let start = clock.Monotonic()
                (clock.Delay (TimeSpan.FromMilliseconds 50.0) CancellationToken.None).Wait()
                let elapsed = clock.Monotonic() - start
                Expect.isGreaterThanOrEqual elapsed (TimeSpan.FromMilliseconds 45.0) "waited"
            }

            test "delays are cancellable and zero delays complete at once" {
                use cts = new CancellationTokenSource()
                let waiting = clock.Delay (TimeSpan.FromHours 1.0) cts.Token
                cts.Cancel()
                Expect.throwsT<TaskCanceledException> (fun () -> waiting.GetAwaiter().GetResult()) "cancelled"
                Expect.isTrue (clock.Delay TimeSpan.Zero CancellationToken.None).IsCompletedSuccessfully "zero"
            }

            test "timeouts work on the real clock" {
                let env = LiveEnv.create (IO.Path.GetTempPath())

                let result =
                    LiveEnv.runIn
                        env
                        (Eff.timeout (TimeSpan.FromMilliseconds 50.0) (Clock.sleep (TimeSpan.FromHours 1.0)))

                Expect.isNone result "timed out"
            }
        ]
