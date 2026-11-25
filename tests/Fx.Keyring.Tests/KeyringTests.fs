module Fx.Keyring.Tests.KeyringTests

open System.Threading
open Expecto
open Fx.Keyring
open Fx.Keyring.Tests.Conformance

let private key service account =
    match KeyringKey.tryCreate service account with
    | Ok key -> key
    | Error e -> failtest e

[<Tests>]
let keys =
    testList
        "Keys"
        [
            test "services are 1 to 128 letters, digits and . _ - /, starting with a letter or digit" {
                for valid in [ "fx"; "a.b_c-d/e"; String.replicate 128 "s"; "9lives" ] do
                    Expect.isOk (KeyringService.tryCreate valid) valid

                for invalid in [ ""; ".hidden"; "a:b"; "a b"; "ü"; String.replicate 129 "s" ] do
                    Expect.isError (KeyringService.tryCreate invalid) invalid
            }

            test "accounts are 1 to 256 characters without control characters" {
                for valid in [ "a"; "user@example.com"; "zoë 日本"; "a:b"; String.replicate 256 "a" ] do
                    Expect.isOk (KeyringKey.tryCreate "fx" valid) valid

                for invalid in [ ""; "a\nb"; "a\u0000b"; String.replicate 257 "a" ] do
                    Expect.isError (KeyringKey.tryCreate "fx" invalid) invalid
            }

            test "keys print their service and account, secrets never their value" {
                Expect.equal (string (key "fx" "anthropic")) "fx (anthropic)" "key"
                let secret = SecretText "sk-ant-123"
                Expect.equal (string secret) "(redacted)" "ToString"
                Expect.equal $"{secret}" "(redacted)" "interpolation"
                Expect.equal (secret.Reveal()) "sk-ant-123" "Reveal"
            }
        ]

[<Tests>]
let inMemory =
    testList
        "InMemoryKeyring"
        [
            KeyringConformance.tests "conformance" (InMemoryKeyring().Keyring)
            KeyringConformance.tests "conformance with the Credential Manager's limit" (InMemoryKeyring(2560).Keyring)

            test "a locked or unavailable keyring fails every operation the same way" {
                let fake = InMemoryKeyring()
                let k = key "fx" "a"

                let attempt () =
                    fake.Keyring.Read k CancellationToken.None
                    |> fun t -> t.Result |> Result.map ignore

                fake.Status <- KeyringStatus.Locked
                Expect.equal (attempt ()) (Error KeyringError.Locked) "locked"
                fake.Status <- KeyringStatus.Unavailable Unavailable.NoSessionBus
                Expect.equal (attempt ()) (Error(KeyringError.Unavailable Unavailable.NoSessionBus)) "unavailable"
                Expect.equal (fake.Keyring.Probe CancellationToken.None).Result fake.Status "probe"
                fake.Status <- KeyringStatus.Ready
                Expect.equal (attempt ()) (Ok()) "ready"
            }

            test "Keyring.unavailable fails everything with its reason" {
                let keyring = Keyring.unavailable "none" (Unavailable.NotSupported "plan9")

                Expect.equal
                    (keyring.Write (key "fx" "a") (SecretText "x") CancellationToken.None).Result
                    (Error(KeyringError.Unavailable(Unavailable.NotSupported "plan9")))
                    "write"

                Expect.equal
                    (string (keyring.Probe CancellationToken.None).Result)
                    "unavailable: not-supported: plan9"
                    "probe"
            }
        ]
