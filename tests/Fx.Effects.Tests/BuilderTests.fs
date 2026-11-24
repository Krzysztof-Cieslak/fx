module Fx.Effects.Tests.BuilderTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Tests.TestEnv

/// An effect that completes after yielding the thread, to exercise the asynchronous path.
let later (value: 'T) : Eff<'R, 'T> =
    Eff.ofTask (fun () ->
        task {
            do! Task.Yield()
            return value
        })

/// An effect that logs a step.
let step (log: ResizeArray<string>) (name: string) : Eff<'R, unit> =
    fun _ ->
        lock log (fun () -> log.Add name)
        Task.FromResult()

let failing (message: string) : Eff<'R, int> =
    Eff.ofTask (fun () ->
        task {
            do! Task.Yield()
            return invalidOp message
        })

[<Struct>]
type StructEnv =
    val Clock: Clock
    new(clock) = { Clock = clock }

    interface IClock with
        member env.Clock = env.Clock

type Disposable(log: ResizeArray<string>, name: string) =
    interface IDisposable with
        member _.Dispose() = log.Add $"dispose {name}"

[<Tests>]
let tests =
    testList
        "eff builder"
        [
            test "returns a value" {
                let env = create().Env
                Expect.equal (runIn env (eff { return 42 })) 42 "value"
            }

            test "binds synchronous and asynchronous effects in order" {
                let env = create().Env
                let log = ResizeArray()

                let program =
                    eff {
                        do! step log "a"
                        let! x = later 1
                        do! step log "b"
                        let! y = Eff.ret 2
                        do! step log "c"
                        let! z = later 3
                        return x + y + z
                    }

                Expect.equal (runIn env program) 6 "sum"
                Expect.equal (List.ofSeq log) [ "a"; "b"; "c" ] "order"
            }

            test "every bound effect gets the environment the block was invoked with" {
                let fakes = create ()
                let env1 = fakes.Env

                let env2 =
                    { env1 with
                        Scope = Scope.ofCancellation (new CancellationTokenSource()).Token
                    }

                let program =
                    eff {
                        let! a = Eff.ask
                        let! _ = later ()
                        let! b = Eff.ask
                        return obj.ReferenceEquals(a, b), a
                    }

                let same1, seen1 = runIn env1 program
                let same2, seen2 = runIn env2 program
                Expect.isTrue (same1 && same2) "same environment after an await"
                Expect.isTrue (obj.ReferenceEquals(seen1, env1)) "first"
                Expect.isTrue (obj.ReferenceEquals(seen2, env2)) "second"
            }

            test "an effect value runs many times, also concurrently, without sharing state" {
                let program (n: int) =
                    eff {
                        let mutable total = 0

                        for i in 1..n do
                            let! v = later i
                            total <- total + v

                        let! env = Eff.ask
                        return total, env
                    }

                let block = program 10

                let results =
                    [|
                        for i in 1..50 -> Task.Run<int * int>(Func<Task<int * int>>(fun () -> Eff.run i block))
                    |]
                    |> Task.WhenAll
                    |> _.Result

                Expect.equal results [| for i in 1..50 -> 55, i |] "independent runs"
            }

            test "a synchronous exception in a bound effect faults the task instead of throwing" {
                let boom: Eff<unit, int> = fun _ -> invalidOp "sync"

                let program =
                    eff {
                        let! x = boom
                        return x + 1
                    }

                let task = program ()
                Expect.isTrue task.IsFaulted "faulted"
                Expect.throwsT<InvalidOperationException> (fun () -> task.GetAwaiter().GetResult() |> ignore) "type"
            }

            test "try/with catches synchronous and asynchronous failures" {
                let env = create().Env

                let recover (effect: Eff<Env, int>) =
                    eff {
                        try
                            let! value = effect
                            return Ok value
                        with :? InvalidOperationException as e ->
                            return Error e.Message
                    }

                Expect.equal (runIn env (recover (failing "async"))) (Error "async") "async"
                Expect.equal (runIn env (recover (fun _ -> invalidOp "sync"))) (Error "sync") "sync"
                Expect.equal (runIn env (recover (later 5))) (Ok 5) "success"
            }

            test "try/finally runs after success, failure and cancellation" {
                let fakes = create ()
                let log = ResizeArray()

                let guarded name (effect: Eff<Env, int>) =
                    eff {
                        try
                            let! value = effect
                            return value
                        finally
                            log.Add name
                    }

                Expect.equal (runIn fakes.Env (guarded "ok" (later 1))) 1 "value"

                Expect.throwsT<InvalidOperationException>
                    (fun () -> runIn fakes.Env (guarded "failed" (failing "x")) |> ignore)
                    "failure"

                use cts = new CancellationTokenSource()

                let env =
                    { fakes.Env with
                        Scope = Scope.ofCancellation cts.Token
                    }

                let running =
                    Eff.run env (guarded "cancelled" (Eff.map (fun () -> 0) (Clock.sleep (TimeSpan.FromHours 1.0))))

                cts.Cancel()

                Expect.throwsT<TaskCanceledException> (fun () -> running.GetAwaiter().GetResult() |> ignore) "cancelled"

                Expect.equal (List.ofSeq log) [ "ok"; "failed"; "cancelled" ] "finally ran"
            }

            test "while loops run many synchronous iterations without growing the stack" {
                let env = create().Env

                let program =
                    eff {
                        let mutable i = 0

                        while i < 1_000_000 do
                            let! one = Eff.ret 1
                            i <- i + one

                        return i
                    }

                Expect.equal (runIn env program) 1_000_000 "iterations"
            }

            test "loops with asynchronous iterations" {
                let env = create().Env

                let program =
                    eff {
                        let results = ResizeArray()
                        let mutable i = 0

                        while i < 100 do
                            let! v = later i
                            results.Add v
                            i <- i + 1

                        for x in [ 100..199 ] do
                            let! v = later x
                            results.Add v

                        return List.ofSeq results
                    }

                Expect.equal (runIn env program) [ 0..199 ] "values in order"
            }

            test "use disposes at the end of the block, also after a failure" {
                let env = create().Env
                let log = ResizeArray()

                let program (effect: Eff<Env, int>) =
                    eff {
                        use _ = new Disposable(log, "a")
                        let! value = effect
                        log.Add $"got {value}"
                        return value
                    }

                Expect.equal (runIn env (program (later 1))) 1 "value"

                Expect.throwsT<InvalidOperationException>
                    (fun () -> runIn env (program (failing "x")) |> ignore)
                    "failure"

                Expect.equal (List.ofSeq log) [ "got 1"; "dispose a"; "dispose a" ] "disposed"
            }

            test "match! and return! bind effects" {
                let env = create().Env

                let program =
                    eff {
                        match! later (Some 2) with
                        | Some n -> return! later (n * 10)
                        | None -> return 0
                    }

                Expect.equal (runIn env program) 20 "value"
            }

            test "blocks nest and compose" {
                let env = create().Env
                let inner x = eff { return x * 2 }

                let outer =
                    eff {
                        let! a = inner 1
                        let! b = eff { return! inner 2 }
                        let! c = Eff.map ((+) 1) (inner 3)
                        return [ a; b; c ]
                    }

                Expect.equal (runIn env outer) [ 2; 4; 7 ] "values"
            }

            test "environments can be structs" {
                let clock = Fx.Effects.Fakes.FakeClock.Create()

                let program =
                    eff {
                        let! now = Clock.now
                        let! _ = later ()
                        let! again = Clock.now
                        return now = again
                    }

                Expect.isTrue (runIn (StructEnv clock.Clock) program) "struct environment"
            }
        ]
