module Fx.Sdk.Tests.VersionTests

open Expecto
open Fx.Sdk

[<Tests>]
let tests =
    testList
        "Versions"
        [
            test "SDK versions are major.minor" {
                for version in [ "0.1"; "1.0"; "12.34" ] do
                    Expect.isTrue (Versions.isSdkVersion version) version

                for version in [ ""; "1"; "1.0.0"; "01.2"; "1.02"; "-1.0"; "a.b"; "1. 0" ] do
                    Expect.isFalse (Versions.isSdkVersion version) version

                Expect.isTrue (Versions.isSdkVersion Versions.Sdk) "the SDK's own"
            }

            test "before 1.0 the minor version must match; from 1.0 the host's minor must be as new" {
                let cases =
                    [
                        "0.1", "0.1", true
                        "0.2", "0.1", false
                        "0.1", "0.2", false
                        "1.2", "1.0", true
                        "1.2", "1.2", true
                        "1.2", "1.3", false
                        "2.0", "1.0", false
                        "1.0", "x", false
                    ]

                for host, required, expected in cases do
                    Expect.equal (Versions.sdkCompatible host required) expected $"host {host}, extension {required}"
            }
        ]
