/// The builders' dynamic path: what runs when the compiler cannot turn a block into a static state
/// machine and warns with FS3511 (here forced with a `let rec` inside the block). Production code is
/// compiled with warnings as errors, so it never takes this path, but the path must still be right.
module Fx.Effects.Tests.DynamicBuilderTests

#nowarn "3511" // Each block here deliberately falls back to the dynamic implementation.

open System
open System.Threading
open Expecto
open Fx.Effects
open Fx.Effects.Tests.BuilderTests
open Fx.Effects.Tests.CombinatorTests
open Fx.Effects.Tests.TestEnv

[<Tests>]
let tests =
    testList
        "builders (dynamic path)"
        [
            test "eff binds, loops, catches and finalizes" {
                let env = create().Env
                let log = ResizeArray<string>()

                let program =
                    eff {
                        let rec countdown n = if n = 0 then 0 else countdown (n - 1)
                        let! a = later 1

                        for i in 1..3 do
                            let! v = later i
                            log.Add(string v)

                        let mutable i = 0

                        while i < 100_000 do
                            let! one = Eff.ret 1
                            i <- i + one

                        try
                            try
                                let! b = failing "boom"
                                return a + b + countdown 3
                            with e ->
                                return a + e.Message.Length + i - 100_000
                        finally
                            log.Add "finally"
                    }

                Expect.equal (runIn env program) 5 "value"
                Expect.equal (List.ofSeq log) [ "1"; "2"; "3"; "finally" ] "loop and finally"
            }

            test "eff is cancelled with its scope" {
                let fakes = create ()
                use cts = new CancellationTokenSource()

                let env =
                    { fakes.Env with
                        Scope = Scope.ofCancellation cts.Token
                    }

                let program =
                    eff {
                        let rec id x = if false then id x else x
                        do! Clock.sleep (seconds 10.0)
                        return id 1
                    }

                let running = Eff.run env program
                cts.Cancel()

                Expect.throwsT<Tasks.TaskCanceledException>
                    (fun () -> running.GetAwaiter().GetResult() |> ignore)
                    "cancelled"
            }

            test "effResult short-circuits" {
                let env = create().Env
                let log = ResizeArray<string>()

                let program: Eff<Env, Result<int, string>> =
                    effResult {
                        let rec id x = if false then id x else x
                        let! a = later 1

                        for x in 1..5 do
                            let! v = if x = 3 then Eff.ret (Error "at 3") else Eff.ret (Ok x)
                            log.Add(string v)

                        log.Add "not reached"
                        return id a
                    }

                Expect.equal (runIn env program) (Error "at 3") "error"
                Expect.equal (List.ofSeq log) [ "1"; "2" ] "stopped"
            }

            test "effResult loops run every asynchronous iteration" {
                let env = create().Env

                let program: Eff<Env, Result<int, string>> =
                    effResult {
                        let rec id x = if false then id x else x
                        let mutable total = 0

                        for x in [ 1..4 ] do
                            let! v = Eff.map Ok (later x)
                            total <- total + v

                        while total < 20 do
                            let! v = Eff.map Ok (later 5)
                            total <- total + v

                        return id total
                    }

                Expect.equal (runIn env program) (Ok 20) "every iteration"
            }
        ]
