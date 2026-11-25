module Fx.Effects.Tests.CombinatorTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Tests.BuilderTests
open Fx.Effects.Tests.TestEnv

let seconds (n: float) = TimeSpan.FromSeconds n

/// Sleeps on the clock, then returns the value; logs whether it finished or was cancelled.
let sleepThen (log: ResizeArray<string>) (name: string) (duration: TimeSpan) (value: 'T) : Eff<Env, 'T> =
    eff {
        try
            do! Clock.sleep duration
            lock log (fun () -> log.Add $"{name} done")
            return value
        with :? OperationCanceledException as e ->
            lock log (fun () -> log.Add $"{name} cancelled")
            return raise e
    }

/// Sleeps on the clock, then fails.
let sleepThenFail (duration: TimeSpan) (message: string) : Eff<Env, 'T> =
    eff {
        do! Clock.sleep duration
        return invalidOp message
    }

/// A read-only view of an environment: the attenuation example.
type ReadOnlyEnv(env: Env) =
    interface IFsRead with
        member _.FsRead = env.FsRead

    interface IScope with
        member _.Scope = env.Scope

[<Tests>]
let tests =
    testList
        "Eff combinators"
        [
            test "map, bind, ignore, ret and unit" {
                let env = create().Env
                Expect.equal (runIn env (Eff.map ((+) 1) (later 1))) 2 "map"
                Expect.equal (runIn env (Eff.bind (fun x -> Eff.ret (x * 3)) (later 2))) 6 "bind"
                Expect.equal (runIn env (Eff.ignore (later 2))) () "ignore"
                Expect.equal (runIn env Eff.unit) () "unit"
            }

            test "ask, asks, local and provide work with the environment" {
                let fakes = create ()
                Expect.isTrue (obj.ReferenceEquals(runIn fakes.Env Eff.ask, fakes.Env)) "ask"
                Expect.equal (runIn fakes.Env (Eff.asks (fun (e: Env) -> e.Scope))) Scope.root "asks"
                Expect.equal (runIn 2 (Eff.local (fun n -> n * 10) Eff.ask)) 20 "local"
                Expect.equal (runIn "ignored" (Eff.provide 7 Eff.ask)) 7 "provide"
            }

            test "local attenuates: the effect sees only what the derived environment exposes" {
                let fakes = create ()
                fakes.Fs.AddText(path [ "work"; "a.txt" ], "hello")

                let readOnly: Eff<ReadOnlyEnv, Result<string, FsError>> =
                    Fs.readText (path [ "work"; "a.txt" ])

                let program = Eff.local (fun (env: Env) -> ReadOnlyEnv env) readOnly
                Expect.equal (runIn fakes.Env program) (Ok "hello") "read through the attenuated environment"
            }

            test "catch turns exceptions into errors" {
                let env = create().Env
                Expect.equal (runIn env (Eff.catch (later 1))) (Ok 1) "ok"

                match runIn env (Eff.catch (failing "x")) with
                | Error(:? InvalidOperationException) -> ()
                | other -> failtest $"{other}"
            }

            test "traverse and sequence run in order" {
                let env = create().Env
                let log = ResizeArray()

                let program =
                    Eff.traverse
                        (fun x ->
                            eff {
                                do! step log $"{x}"
                                return! later (x * x)
                            })
                        [ 1..5 ]

                Expect.equal (runIn env program) [ 1; 4; 9; 16; 25 ] "results"
                Expect.equal (List.ofSeq log) [ "1"; "2"; "3"; "4"; "5" ] "order"
                Expect.equal (runIn env (Eff.sequence [ later 1; Eff.ret 2 ])) [ 1; 2 ] "sequence"
            }

            testProperty "traverse of pure effects is List.map" (fun (xs: int list) ->
                let env = create().Env
                runIn env (Eff.traverse (fun x -> Eff.ret (x + 1)) xs) = List.map ((+) 1) xs)

            testProperty "all returns results in order, whatever order they finish in" (fun (delays: byte list) ->
                let fakes = create ()
                let log = ResizeArray()

                let running =
                    Eff.run
                        fakes.Env
                        (Eff.all
                            [
                                for i, d in List.indexed delays -> sleepThen log $"{i}" (seconds (float d)) i
                            ])

                while fakes.Clock.AdvanceToNextDelay() do
                    ()

                running.Result = List.mapi (fun i _ -> i) delays)

            test "all cancels the others when one fails, waits for them, then rethrows" {
                let fakes = create ()
                let log = ResizeArray()

                let running =
                    Eff.run
                        fakes.Env
                        (Eff.all
                            [
                                sleepThen log "slow" (seconds 10.0) 1
                                sleepThenFail (seconds 1.0) "boom"
                                sleepThen log "slower" (seconds 20.0) 3
                            ])

                fakes.Clock.Advance(seconds 1.0)
                Expect.isTrue running.IsCompleted "done once the failure cancelled the rest"

                Expect.throwsT<InvalidOperationException>
                    (fun () -> running.GetAwaiter().GetResult() |> ignore)
                    "rethrown"

                Expect.equal (Set.ofSeq log) (set [ "slow cancelled"; "slower cancelled" ]) "others cancelled"
                Expect.equal fakes.Clock.PendingDelays 0 "no timers left"
            }

            test "both runs two effects concurrently" {
                let fakes = create ()
                let log = ResizeArray()

                let running =
                    Eff.run
                        fakes.Env
                        (Eff.both (sleepThen log "a" (seconds 2.0) "a") (sleepThen log "b" (seconds 1.0) 2))

                fakes.Clock.Advance(seconds 2.0)
                Expect.equal running.Result ("a", 2) "results"
                Expect.equal (List.ofSeq log) [ "b done"; "a done" ] "concurrent"
            }

            test "race returns the first to finish and cancels the rest" {
                let fakes = create ()
                let log = ResizeArray()

                let running =
                    Eff.run
                        fakes.Env
                        (Eff.race
                            [
                                sleepThen log "slow" (seconds 5.0) "slow"
                                sleepThen log "fast" (seconds 1.0) "fast"
                            ])

                fakes.Clock.Advance(seconds 1.0)
                Expect.equal running.Result "fast" "winner"
                Expect.equal (List.ofSeq log) [ "fast done"; "slow cancelled" ] "loser cancelled"
            }

            test "race with a failure first rethrows it" {
                let fakes = create ()
                let log = ResizeArray()

                let running =
                    Eff.run
                        fakes.Env
                        (Eff.race [ sleepThen log "slow" (seconds 5.0) 1; sleepThenFail (seconds 1.0) "first" ])

                fakes.Clock.Advance(seconds 1.0)

                Expect.throwsT<InvalidOperationException>
                    (fun () -> running.GetAwaiter().GetResult() |> ignore)
                    "failure"

                Expect.equal (List.ofSeq log) [ "slow cancelled" ] "loser cancelled"
            }

            test "timeout on the clock" {
                let fakes = create ()
                let log = ResizeArray()

                let slow =
                    Eff.run fakes.Env (Eff.timeout (seconds 5.0) (sleepThen log "slow" (seconds 10.0) 1))

                fakes.Clock.Advance(seconds 4.0)
                Expect.isFalse slow.IsCompleted "not yet"
                fakes.Clock.Advance(seconds 1.0)
                Expect.equal slow.Result None "timed out"

                let fast =
                    Eff.run fakes.Env (Eff.timeout (seconds 5.0) (sleepThen log "fast" (seconds 1.0) 2))

                fakes.Clock.Advance(seconds 1.0)
                Expect.equal fast.Result (Some 2) "in time"
                Expect.equal (List.ofSeq log) [ "slow cancelled"; "fast done" ] "log"
                Expect.equal fakes.Clock.PendingDelays 0 "the timeout's own delay was cancelled"
            }

            test "cancelling the scope cancels concurrent children" {
                let fakes = create ()
                let log = ResizeArray()
                use cts = new CancellationTokenSource()

                let env =
                    { fakes.Env with
                        Scope = Scope.ofCancellation cts.Token
                    }

                let running =
                    Eff.run env (Eff.all [ sleepThen log "a" (seconds 1.0) 1; sleepThen log "b" (seconds 2.0) 2 ])

                cts.Cancel()
                Expect.throwsT<TaskCanceledException> (fun () -> running.GetAwaiter().GetResult() |> ignore) "cancelled"
                Expect.equal (Set.ofSeq log) (set [ "a cancelled"; "b cancelled" ]) "children cancelled"
            }

            test "bracket releases after success, failure and cancellation, shielded from cancellation" {
                let fakes = create ()
                let log = ResizeArray()

                let use' (body: Eff<Env, int>) =
                    Eff.bracket
                        (eff {
                            log.Add "acquire"
                            return 1
                        })
                        (fun _ ->
                            eff {
                                let! token = Eff.cancellation
                                log.Add $"release (cancelled: {token.IsCancellationRequested})"
                            })
                        (fun r -> Eff.map ((+) r) body)

                Expect.equal (runIn fakes.Env (use' (later 1))) 2 "value"

                Expect.throwsT<InvalidOperationException>
                    (fun () -> runIn fakes.Env (use' (failing "x")) |> ignore)
                    "failure"

                use cts = new CancellationTokenSource()

                let env =
                    { fakes.Env with
                        Scope = Scope.ofCancellation cts.Token
                    }

                let running = Eff.run env (use' (Eff.map (fun () -> 0) (Clock.sleep (seconds 9.0))))
                cts.Cancel()
                Expect.throwsT<TaskCanceledException> (fun () -> running.GetAwaiter().GetResult() |> ignore) "cancelled"

                Expect.equal
                    (List.ofSeq log)
                    [
                        "acquire"
                        "release (cancelled: False)"
                        "acquire"
                        "release (cancelled: False)"
                        "acquire"
                        "release (cancelled: False)"
                    ]
                    "released every time"
            }

            test "ofCancellableTask passes the scope's token" {
                let fakes = create ()
                use cts = new CancellationTokenSource()

                let env =
                    { fakes.Env with
                        Scope = Scope.ofCancellation cts.Token
                    }

                let seen = runIn env (Eff.ofCancellableTask (fun token -> Task.FromResult token))
                Expect.equal seen cts.Token "token"
            }

            testTask "structured children have their own flows, after awaits too, and the parent keeps its" {
                let fakes = create ()
                let flow: Eff<Env, string> = fun _ -> Task.FromResult(Flow.path ())

                let child: Eff<Env, string list> =
                    eff {
                        let! before = flow
                        do! Clock.sleep (seconds 1.0)
                        let! after = flow
                        let! inner = Eff.all [ flow; flow ]
                        return before :: after :: inner
                    }

                let program =
                    eff {
                        let! children = Eff.all [ child; child ]
                        let! raced = Eff.race [ flow; flow ]
                        let! timed = Eff.timeout (seconds 5.0) flow
                        let! parent = flow
                        return children, raced, timed, parent
                    }

                let running = Flow.root (fun () -> Eff.run fakes.Env program)
                fakes.Clock.Advance(seconds 1.0)
                let! children, raced, timed, parent = running

                Expect.equal
                    children
                    [ [ "0"; "0"; "0.0"; "0.1" ]; [ "1"; "1"; "1.0"; "1.1" ] ]
                    "each child's path, also after it waited"

                Expect.equal raced "0" "race's first child won"
                Expect.equal timed (Some "0") "timeout runs the effect as its first child"
                Expect.equal parent "" "the parent's flow is unchanged"
            }
        ]
