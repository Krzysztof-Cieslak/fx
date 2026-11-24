module Fx.Effects.Tests.ClockTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Tests.CombinatorTests
open Fx.Effects.Tests.TestEnv

[<Tests>]
let tests =
    testList
        "Clock and FakeClock"
        [
            test "time moves only when advanced" {
                let fakes = create ()
                let start = runIn fakes.Env Clock.now
                Expect.equal (string start) "2026-01-01T00:00:00.000000Z" "start"
                Expect.equal (runIn fakes.Env Clock.now) start "unchanged"
                fakes.Clock.Advance(seconds 90.0)
                Expect.equal (runIn fakes.Env Clock.now) (start + seconds 90.0) "advanced"
                Expect.equal (runIn fakes.Env Clock.monotonic) (MonoTime(seconds 90.0)) "monotonic"
            }

            test "setting the wall clock moves neither the monotonic clock nor delays" {
                let fakes = create ()
                let sleeping = Eff.run fakes.Env (Clock.sleep (seconds 10.0))
                fakes.Clock.SetNow(Instant.unixEpoch)
                Expect.equal (runIn fakes.Env Clock.now) Instant.unixEpoch "wall clock jumped"
                Expect.equal (runIn fakes.Env Clock.monotonic) (MonoTime TimeSpan.Zero) "monotonic did not"
                Expect.isFalse sleeping.IsCompleted "delay still waiting"
                fakes.Clock.Advance(seconds 10.0)
                Expect.isTrue sleeping.IsCompleted "delay fired"
                Expect.equal (runIn fakes.Env Clock.now) (Instant.unixEpoch + seconds 10.0) "wall clock moves on"
            }

            test "delays fire in deadline order, then start order, each at its deadline" {
                let fakes = create ()
                let log = ResizeArray()

                let sleeper name (duration: float) =
                    eff {
                        do! Clock.sleep (seconds duration)
                        let! now = Clock.monotonic
                        log.Add $"{name} at {now}"
                    }

                let tasks =
                    [ sleeper "c" 3.0; sleeper "a" 1.0; sleeper "b1" 2.0; sleeper "b2" 2.0 ]
                    |> List.map (Eff.run fakes.Env)

                Expect.equal fakes.Clock.PendingDelays 4 "pending"
                fakes.Clock.Advance(seconds 2.5)

                Expect.equal
                    (List.ofSeq log)
                    [ "a at mono+1.000000s"; "b1 at mono+2.000000s"; "b2 at mono+2.000000s" ]
                    "fired so far"

                Expect.isTrue (fakes.Clock.AdvanceToNextDelay()) "one left"
                Expect.equal (Seq.last log) "c at mono+3.000000s" "last"
                Expect.isFalse (fakes.Clock.AdvanceToNextDelay()) "none left"
                Task.WaitAll(tasks |> List.map (fun t -> t :> Task) |> Array.ofList)
            }

            test "a loop of sleeps runs as many times as fit in one advance" {
                let fakes = create ()
                let ticks = ResizeArray()

                let running =
                    Eff.run
                        fakes.Env
                        (Clock.every
                            (seconds 1.0)
                            (eff {
                                let! now = Clock.monotonic
                                ticks.Add now.SinceOrigin.TotalSeconds
                                return ticks.Count < 5
                            }))

                fakes.Clock.Advance(seconds 3.5)
                Expect.equal (List.ofSeq ticks) [ 1.0; 2.0; 3.0 ] "three ticks"
                fakes.Clock.Advance(seconds 10.0)
                Expect.equal (List.ofSeq ticks) [ 1.0; 2.0; 3.0; 4.0; 5.0 ] "stopped after five"
                running.GetAwaiter().GetResult()
            }

            test "cancelling a delay removes it" {
                let fakes = create ()
                use cts = new CancellationTokenSource()

                let env =
                    { fakes.Env with
                        Scope = Scope.ofCancellation cts.Token
                    }

                let sleeping = Eff.run env (Clock.sleep (seconds 5.0))
                Expect.equal fakes.Clock.PendingDelays 1 "pending"
                cts.Cancel()
                Expect.isTrue sleeping.IsCanceled "cancelled"
                Expect.equal fakes.Clock.PendingDelays 0 "removed"

                let already = Eff.run env (Clock.sleep (seconds 5.0))
                Expect.isTrue already.IsCanceled "an already cancelled scope does not wait"
            }

            test "zero and negative delays complete at once" {
                let fakes = create ()
                Expect.isTrue (Eff.run fakes.Env (Clock.sleep TimeSpan.Zero)).IsCompletedSuccessfully "zero"
                Expect.isTrue (Eff.run fakes.Env (Clock.sleep (seconds -1.0))).IsCompletedSuccessfully "negative"
            }

            test "timed measures on the monotonic clock" {
                let fakes = create ()

                let running =
                    Eff.run fakes.Env (Clock.timed (Eff.map (fun () -> "x") (Clock.sleep (seconds 2.0))))

                fakes.Clock.SetNow(Instant.unixEpoch)
                fakes.Clock.Advance(seconds 2.0)
                Expect.equal running.Result ("x", seconds 2.0) "duration"
            }

            test "sleepUntil waits for a monotonic deadline" {
                let fakes = create ()
                let running = Eff.run fakes.Env (Clock.sleepUntil (MonoTime(seconds 3.0)))
                fakes.Clock.Advance(seconds 2.0)
                Expect.isFalse running.IsCompleted "not yet"
                fakes.Clock.Advance(seconds 1.0)
                Expect.isTrue running.IsCompletedSuccessfully "at the deadline"
            }

            test "a fake clock never goes backwards" {
                let clock = FakeClock.Create()
                Expect.throwsT<ArgumentException> (fun () -> clock.Advance(seconds -1.0)) "negative advance"
            }
        ]
