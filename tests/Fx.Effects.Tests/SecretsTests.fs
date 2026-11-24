module Fx.Effects.Tests.SecretsTests

open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Tests.TestEnv

let private reference text =
    match SecretRef.tryParse text with
    | Ok reference -> reference
    | Error e -> failtest e

[<Tests>]
let tests =
    testList
        "Secrets"
        [
            test "references parse from secret:<name>" {
                Expect.equal (reference "secret:github/token").Name "github/token" "name"
                Expect.equal (string (reference "secret:a.b_c-d")) "secret:a.b_c-d" "round trip"

                for invalid in
                    [
                        "github"
                        "secret:"
                        "secret:-x"
                        "secret:a b"
                        "secret:" + String.replicate 129 "a"
                        "SECRET:x"
                    ] do
                    Expect.isError (SecretRef.tryParse invalid) invalid
            }

            test "resolved secrets never print their value" {
                let env = create().Env

                match runIn env (Secrets.resolve (reference "secret:github/token")) with
                | Ok secret ->
                    Expect.equal (secret.Reveal()) "ghp_test" "value"
                    Expect.equal (string secret) "secret:github/token (redacted)" "ToString"
                    Expect.equal $"{secret}" "secret:github/token (redacted)" "interpolation"
                | Error e -> failtest (string e)
            }

            test "missing secrets are an expected failure" {
                let env = create().Env
                let missing = reference "secret:nope"

                Expect.equal
                    (runIn env (Secrets.resolve missing) |> Result.mapError string)
                    (Error "secret:nope not found")
                    "error"
            }

            test "in-memory secrets reject invalid names" {
                Expect.throwsT<System.ArgumentException>
                    (fun () -> InMemorySecrets.create [ "bad name", "x" ] |> ignore)
                    "invalid"
            }
        ]
