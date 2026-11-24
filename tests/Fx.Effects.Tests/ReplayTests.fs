module Fx.Effects.Tests.ReplayTests

open System
open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Replay
open Fx.Effects.Tests.CombinatorTests

/// Only what the replay proof covers: a clock and ids.
type ReplayEnv =
    {
        Clock: Clock
        Ids: Ids
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IIds with
        member env.Ids = env.Ids

    interface IScope with
        member _.Scope = Scope.root

    override _.ToString() = "ReplayEnv"

/// A sequential effect that uses the clock and ids.
let private program (sleep: TimeSpan) =
    eff {
        let! started = Clock.now
        let! first = Ids.newId
        do! Clock.sleep sleep
        let! elapsed = Eff.map snd (Clock.timed (Clock.sleep sleep))
        let! nonce = Ids.randomBytes 8
        let! second = Ids.newId
        return $"{started} {first} {elapsed} {Convert.ToHexString nonce} {second}"
    }

let private run env effect =
    (Eff.run env effect).GetAwaiter().GetResult()

/// Records a run against a live-like interpreter (a fake clock that a background loop advances).
let private record () =
    let clock = FakeClock.Create()
    let journal = Journal()

    let env =
        {
            Clock = Record.clock journal clock.Clock
            Ids = Record.ids journal (FakeIds(3UL, clock.Clock)).Ids
        }

    let running = Eff.run env (program (seconds 2.0))

    while not running.IsCompleted do
        clock.AdvanceToNextDelay() |> ignore

    running.Result, journal.Entries

[<Tests>]
let tests =
    testList
        "Record and replay (Clock and Ids)"
        [
            test "replay reproduces the recorded result without the real interpreters" {
                let recorded, entries = record ()
                let cursor = ReplayCursor entries

                let replayed =
                    run
                        {
                            Clock = Replay.clock cursor
                            Ids = Replay.ids cursor
                        }
                        (program (seconds 2.0))

                Expect.equal replayed recorded "same result"
                Expect.isTrue cursor.Finished "every entry used"

                Expect.equal
                    (entries |> List.map (fun e -> (string e).Split(' ')[0]))
                    [
                        "Clock.Now"
                        "Ids.NewId"
                        "Clock.Delay"
                        "Clock.Monotonic"
                        "Clock.Delay"
                        "Clock.Monotonic"
                        "Ids.RandomBytes"
                        "Ids.NewId"
                    ]
                    "journal"
            }

            test "a changed program diverges" {
                let _, entries = record ()
                let cursor = ReplayCursor entries

                let env =
                    {
                        Clock = Replay.clock cursor
                        Ids = Replay.ids cursor
                    }

                Expect.throwsT<ReplayDivergedException>
                    (fun () -> run env (program (seconds 3.0)) |> ignore)
                    "different delay"

                let reordered =
                    eff {
                        let! id = Ids.newId
                        let! now = Clock.now
                        return $"{id} {now}"
                    }

                let fresh = ReplayCursor entries

                Expect.throwsT<ReplayDivergedException>
                    (fun () ->
                        run
                            {
                                Clock = Replay.clock fresh
                                Ids = Replay.ids fresh
                            }
                            reordered
                        |> ignore)
                    "different order"
            }
        ]
