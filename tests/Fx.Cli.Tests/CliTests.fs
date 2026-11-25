module Fx.Cli.Tests.CliTests

open Expecto
open Fx
open Fx.Cli
open Fx.Envd.Live
open Fx.Sqlite

/// What a printing command prints.
let private printed (args: string list) : Cli.Outcome =
    match Cli.parse args with
    | Cli.Print outcome -> outcome
    | other -> failtest $"expected output, got {other}"

[<Tests>]
let tests =
    testList
        "Cli"
        [
            test "no arguments prints usage" {
                let o = printed []
                Expect.equal o.ExitCode 0 "exit code"
                Expect.stringContains o.Stdout "Usage:" "usage"
                Expect.equal o.Stderr "" "stderr"
            }

            test "--help prints usage" {
                let o = printed [ "--help" ]
                Expect.equal o.ExitCode 0 "exit code"
                Expect.equal o.Stdout Cli.usage "usage"
            }

            test "--version prints name and version" {
                let o = printed [ "--version" ]
                Expect.equal o.ExitCode 0 "exit code"
                Expect.equal o.Stdout $"{Product.Name} {Product.Version}\n" "version"
            }

            test "--build-info names the embedded SQLite" {
                let o = printed [ "--build-info" ]
                Expect.equal o.ExitCode 0 "exit code"

                Expect.equal
                    o.Stdout
                    $"{Product.Name} {Product.Version}\nsqlite {Library.PinnedVersion} ({Library.PinnedSourceId})\n"
                    "build info"
            }

            test "unknown arguments fail with usage on stderr" {
                let o = printed [ "--bogus"; "x" ]
                Expect.equal o.ExitCode 2 "exit code"
                Expect.equal o.Stdout "" "stdout"
                Expect.stringContains o.Stderr "--bogus x" "names the arguments"
                Expect.stringContains o.Stderr "Usage:" "usage"
            }

            test "envd --stdio serves the environment protocol, with options" {
                match Cli.parse [ "envd"; "--stdio" ] with
                | Cli.Envd options -> Expect.equal options EnvdOptions.defaults "defaults"
                | other -> failtest $"{other}"

                match
                    Cli.parse
                        [
                            "envd"
                            "--workspace"
                            "w"
                            "--stdio"
                            "--state"
                            "s"
                            "--shell"
                            "sh"
                            "--retain"
                            "4096"
                        ]
                with
                | Cli.Envd options ->
                    Expect.equal
                        options
                        { EnvdOptions.defaults with
                            Workspace = Some "w"
                            State = Some "s"
                            Shell = Some "sh"
                            Retain = 4096
                        }
                        "options"
                | other -> failtest $"{other}"
            }

            test "envd needs --stdio and known options" {
                for args in
                    [
                        [ "envd" ]
                        [ "envd"; "--stdio"; "--bogus" ]
                        [ "envd"; "--stdio"; "--state" ]
                    ] do
                    let o = printed args
                    Expect.equal o.ExitCode 2 "exit code"
                    Expect.stringContains o.Stderr "envd --stdio" "envd usage"
            }

            test "envd is not in the usage" { Expect.isFalse (Cli.usage.Contains "envd") "hidden" }
        ]
