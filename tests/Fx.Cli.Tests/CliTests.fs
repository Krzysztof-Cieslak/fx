module Fx.Cli.Tests.CliTests

open Expecto
open Fx
open Fx.Cli

[<Tests>]
let tests =
    testList
        "Cli"
        [
            test "no arguments prints usage" {
                let o = Cli.run []
                Expect.equal o.ExitCode 0 "exit code"
                Expect.stringContains o.Stdout "Usage:" "usage"
                Expect.equal o.Stderr "" "stderr"
            }

            test "--help prints usage" {
                let o = Cli.run [ "--help" ]
                Expect.equal o.ExitCode 0 "exit code"
                Expect.equal o.Stdout Cli.usage "usage"
            }

            test "--version prints name and version" {
                let o = Cli.run [ "--version" ]
                Expect.equal o.ExitCode 0 "exit code"
                Expect.equal o.Stdout $"{Product.Name} {Product.Version}\n" "version"
            }

            test "unknown arguments fail with usage on stderr" {
                let o = Cli.run [ "--bogus"; "x" ]
                Expect.equal o.ExitCode 2 "exit code"
                Expect.equal o.Stdout "" "stdout"
                Expect.stringContains o.Stderr "--bogus x" "names the arguments"
                Expect.stringContains o.Stderr "Usage:" "usage"
            }
        ]
