/// The capabilities service credentials use (docs/modules/auth.md): the fakes of private files and of
/// the host's variables pass their conformance suites, and secret text never prints.
module Fx.Effects.Tests.CredentialCapabilityTests

open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Tests.Conformance

[<Tests>]
let tests =
    testList
        "credential capabilities"
        [
            PrivateFilesConformance.tests "in memory" (fun () -> InMemoryPrivateFiles().PrivateFiles)
            HostVariablesConformance.tests "in memory" InMemoryVariables.create

            test "secret text never prints, and compares by its text" {
                let secret = SecretText "sk-x"
                Expect.equal (string secret) "(redacted)" "ToString"
                Expect.equal $"{secret}" "(redacted)" "interpolation"
                Expect.equal secret (SecretText "sk-x") "equal texts"
                Expect.notEqual secret (SecretText "SK-X") "ordinal"
                Expect.equal (hash secret) (hash (SecretText "sk-x")) "hash"
            }

            test "private file names are plain names" {
                for good in [ "credentials.json"; "a"; "A-b_c.1"; String.replicate 64 "x" ] do
                    Expect.isTrue (PrivateFiles.isName good) good

                for bad in [ ""; ".x"; "a/b"; "a\\b"; ".."; "a b"; String.replicate 65 "x" ] do
                    Expect.isFalse (PrivateFiles.isName bad) bad
            }

            test "an in-memory private file can fail on demand" {
                let files = InMemoryPrivateFiles()
                files.Failure <- Some(AccessDenied "x")

                Expect.equal
                    (files.PrivateFiles.Write "x" [||] System.Threading.CancellationToken.None).Result
                    (Error(AccessDenied "x"))
                    "fails"
            }
        ]
