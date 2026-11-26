module Fx.Effects.Tests.EffResultTests

open System
open Expecto
open Fx.Effects
open Fx.Effects.Tests.BuilderTests
open Fx.Effects.Tests.TestEnv

let private okLater (value: 'T) : Eff<'R, Result<'T, string>> = Eff.map Ok (later value)

let private errorLater (error: string) : Eff<'R, Result<'T, string>> = Eff.map Error (later error)

[<Tests>]
let tests =
    testList
        "effResult builder"
        [
            test "returns Ok" {
                let env = create().Env
                Expect.equal (runIn env (effResult { return 1 })) (Ok 1: Result<int, string>) "ok"
            }

            test "stops at the first error" {
                let env = create().Env
                let log = ResizeArray()

                let program =
                    effResult {
                        let! a = okLater 1
                        do! step log "before"
                        let! (_: int) = errorLater "first"
                        do! step log "after"
                        let! (_: int) = errorLater "second"
                        return a
                    }

                Expect.equal (runIn env program) (Error "first") "error"
                Expect.equal (List.ofSeq log) [ "before" ] "nothing after the error ran"
            }

            test "binds plain effects and pure results too" {
                let env = create().Env

                let program =
                    effResult {
                        let! a = later 1
                        let! b = Ok 2
                        let! c = okLater 3
                        let! now = Clock.now
                        return a + b + c, now
                    }

                Expect.equal (runIn env program |> Result.map fst) (Ok 6: Result<int, string>) "sum"

                let failed: Eff<Env, Result<int, string>> =
                    effResult {
                        let! a = later 1
                        let! b = Error "pure"
                        return a + b
                    }

                Expect.equal (runIn env failed) (Error "pure") "pure error"
            }

            test "loops stop at the first error, without evaluating the condition again" {
                let env = create().Env
                let mutable conditions = 0
                let seen = ResizeArray()

                let program =
                    effResult {
                        let mutable i = 0

                        while (conditions <- conditions + 1
                               i < 10) do
                            let! v = if i = 3 then errorLater "at 3" else okLater i
                            seen.Add v
                            i <- i + 1

                        return i
                    }

                Expect.equal (runIn env program) (Error "at 3") "error"
                Expect.equal (List.ofSeq seen) [ 0; 1; 2 ] "iterations before the error"
                Expect.equal conditions 4 "condition evaluations"

                let forLoop =
                    effResult {
                        for x in [ 1..10 ] do
                            let! v = if x = 2 then errorLater "at 2" else okLater x
                            seen.Add v

                        return ()
                    }

                seen.Clear()
                Expect.equal (runIn env forLoop) (Error "at 2") "for error"
                Expect.equal (List.ofSeq seen) [ 1 ] "for iterations"
            }

            // Builds without optimization (Debug) call `inline` helpers instead of inlining them (F# 11).
            // A helper handed the machine (`&sm`) read another field of the generated struct as the
            // block's result: a loop ended after its first asynchronous iteration, and the block
            // returned the default result (`Ok null`). CI runs this project in Debug too.
            test "loops run every asynchronous iteration, then the rest of the block" {
                let env = create().Env
                let written = ResizeArray<string>()

                let write (name: string) : Eff<Env, Result<unit, string>> =
                    Eff.map (fun (_: string) -> Ok()) (later name)

                // The shape of extracting a kernel's assets: a nested block that writes every file.
                let extract (names: string list) : Eff<Env, Result<string, string>> =
                    eff {
                        let! result =
                            effResult {
                                do! okLater 0 |> EffResult.map ignore

                                for name in names do
                                    do! write name
                                    written.Add name
                            }

                        match result with
                        | Ok() -> return Ok $"wrote {written.Count}"
                        | Error e -> return Error e
                    }

                Expect.equal (runIn env (extract [ "a"; "b"; "c"; "d" ])) (Ok "wrote 4") "for"
                Expect.equal (List.ofSeq written) [ "a"; "b"; "c"; "d" ] "every iteration"

                let program: Eff<Env, Result<string list * int, string>> =
                    effResult {
                        let seen = ResizeArray<string>()
                        let mutable total = 0
                        let mutable i = 0

                        while i < 5 do
                            let! v = okLater i
                            total <- total + v
                            i <- i + 1

                        for x in [| "x"; "y"; "z" |] do
                            let! v = okLater x
                            seen.Add v

                        for (key, value) in Map.ofList [ "k", 1; "l", 2 ] |> Map.toSeq do
                            let! v = okLater value
                            seen.Add $"{key}={v}"
                            total <- total + v

                        return List.ofSeq seen, total
                    }

                Expect.equal (runIn env program) (Ok([ "x"; "y"; "z"; "k=1"; "l=2" ], 13)) "while and for"
            }

            test "try/finally runs after an error, and try/with catches exceptions only" {
                let env = create().Env
                let log = ResizeArray()

                let program =
                    effResult {
                        try
                            try
                                let! (_: int) = errorLater "e"
                                return 1
                            with _ ->
                                log.Add "with"
                                return 2
                        finally
                            log.Add "finally"
                    }

                Expect.equal (runIn env program) (Error "e") "error"
                Expect.equal (List.ofSeq log) [ "finally" ] "only finally ran"
            }

            test "return! returns an effect's result" {
                let env = create().Env

                Expect.equal (runIn env (effResult { return! okLater 3 })) (Ok 3) "ok"

                Expect.equal
                    (runIn env (effResult { return! (errorLater "x": Eff<Env, Result<int, string>>) }))
                    (Error "x")
                    "error"
            }

            test "combinators" {
                let env = create().Env
                let double x = EffResult.ok (x * 2)

                Expect.equal (runIn env (EffResult.bind double (okLater 2))) (Ok 4) "bind"
                Expect.equal (runIn env (EffResult.map string (okLater 2))) (Ok "2") "map"

                Expect.equal
                    (runIn env (EffResult.mapError String.length (errorLater "four": Eff<Env, Result<int, string>>)))
                    (Error 4)
                    "mapError"

                Expect.equal (runIn env (EffResult.defaultWith String.length (errorLater "four"))) 4 "defaultWith"

                Expect.equal
                    (runIn
                        env
                        (EffResult.traverse (fun x -> if x > 2 then EffResult.error $"{x}" else EffResult.ok x) [ 1..5 ]))
                    (Error "3")
                    "traverse stops"

                Expect.equal (runIn env (EffResult.sequence [ okLater 1; EffResult.ok 2 ])) (Ok [ 1; 2 ]) "sequence"
            }
        ]
