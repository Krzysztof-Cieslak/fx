module Fx.Sdk.Tests.MethodTests

open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Sdk
open Fx.Sdk.Tests.Support

type Failure =
    | TooBig of limit: int

    override this.ToString() =
        match this with
        | TooBig limit -> $"too big (limit {limit})"

let private failure: Codec<Failure> =
    Codec.union "kind" {
        let! tooBig = Codec.case1 "tooBig" TooBig (Field.required "limit" Codec.int)

        return
            fun value ->
                match value with
                | TooBig limit -> tooBig limit
    }

let private double =
    ExtMethod.define "math/double" Codec.int Codec.int
    |> ExtMethod.withErrors failure
    |> ExtMethod.describe "Doubles a number up to 100."

let private impl =
    ExtMethod.handle double (fun n ->
        if n > 100 then
            EffResult.error (TooBig 100)
        else
            EffResult.ok (n * 2))

let private invoke (json: Json) =
    run (ExtensionEnv.within (extEnv "acme") (impl.Invoke json))

[<Tests>]
let tests =
    testList
        "Methods"
        [
            test "a method decodes its params, runs, and encodes its result or typed error" {
                Expect.equal (invoke (Json.int 4)) (Ok(Json.int 8)) "result"

                Expect.equal
                    (invoke (Json.int 101))
                    (Error(
                        MethodFailure.Failed(
                            "too big (limit 100)",
                            Json.Object [ "kind", Json.String "tooBig"; "limit", Json.int 100 ]
                        )
                    ))
                    "typed error"

                match invoke (Json.String "4") with
                | Error(MethodFailure.InvalidParams [ issue ]) ->
                    Expect.equal (string issue) "$: expected integer, got string \"4\"" "issue"
                | other -> failtest (string other)
            }

            test "a typed call decodes the outcome with the method's codecs" {
                Expect.equal (ExtMethod.decodeOutcome double (invoke (Json.int 4))) (Ok 8) "ok"

                Expect.equal
                    (ExtMethod.decodeOutcome double (invoke (Json.int 200)))
                    (Error(ExtMethod.CallError.Failed(TooBig 100)))
                    "typed"

                match ExtMethod.decodeOutcome double (Ok(Json.String "eight")) with
                | Error(ExtMethod.CallError.Other(MethodFailure.Unreachable reason)) ->
                    Expect.stringContains reason "invalid result" "reason"
                | other -> failtest (string other)

                Expect.equal
                    (ExtMethod.decodeOutcome double (Error(MethodFailure.UnknownMethod "x")))
                    (Error(ExtMethod.CallError.Other(MethodFailure.UnknownMethod "x")))
                    "other"
            }

            test "the declaration carries the JSON Schemas" {
                let decl = ExtMethod.declare double
                Expect.equal decl.Name "math/double" "name"
                Expect.equal decl.Description (Some "Doubles a number up to 100.") "description"
                Expect.equal decl.Params (Some(Json.Object [ "type", Json.String "integer" ])) "params"
            }
        ]
