module Fx.Cli.Tests.CliTests

open Expecto
open Fx
open Fx.Cli
open Fx.Envd.Live
open Fx.Sqlite
open Fx.Contracts

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

            test "run takes a prompt, output, a model, a surface, variants and a session" {
                match
                    Cli.parse
                        [
                            "run"
                            "-p"
                            "fix the build"
                            "--json"
                            "--model"
                            "anthropic/claude-sonnet-5-5"
                            "--surface"
                            "hybrid"
                            "--variant"
                            "codeact.budget.steps=20"
                            "--session"
                            "s1"
                        ]
                with
                | Cli.Run args ->
                    Expect.equal args.Prompt "fix the build" "prompt"
                    Expect.isTrue args.Json "json"
                    Expect.equal args.Session (Some "s1") "session"

                    Expect.equal
                        args.Selections
                        [
                            "model.role.main", Json.String "anthropic/claude-sonnet-5-5"
                            "agent.actionSurface", Json.String "hybrid"
                            "codeact.budget.steps", Json.int 20
                        ]
                        "the explicit layer, in order"
                | other -> failtest $"{other}"
            }

            test "run refuses what it cannot use" {
                for args in
                    [
                        [ "run" ]
                        [ "run"; "-p"; "  " ]
                        [ "run"; "-p"; "x"; "--model"; "claude" ]
                        [ "run"; "-p"; "x"; "--surface"; "both" ]
                        [ "run"; "-p"; "x"; "--variant"; "nothing" ]
                        [ "run"; "-p"; "x"; "--bogus" ]
                    ] do
                    let o = printed args
                    Expect.equal o.ExitCode 2 "exit code"
                    Expect.stringContains o.Stderr "run -p <prompt>" "run usage"
            }

            test "trust and variants" {
                Expect.equal (string (Cli.parse [ "trust" ])) "trust ." "the current directory"
                Expect.equal (string (Cli.parse [ "trust"; "../app" ])) "trust ../app" "a directory"
                Expect.equal (string (Cli.parse [ "trust"; "--remove" ])) "untrust ." "remove"
                Expect.equal (string (Cli.parse [ "trust"; "--list" ])) "trust --list" "list"
                Expect.equal (printed [ "trust"; "--bogus" ]).ExitCode 2 "unknown"
                Expect.equal (string (Cli.parse [ "variants" ])) "variants list" "list"
                Expect.equal (string (Cli.parse [ "variants"; "explain" ])) "variants explain" "explain"
                Expect.equal (string (Cli.parse [ "variants"; "schema" ])) "variants schema" "schema"
                Expect.equal (printed [ "variants"; "x" ]).ExitCode 2 "unknown"
            }

            test "auth logs in, out, lists and shows status" {
                Expect.equal (string (Cli.parse [ "auth"; "login"; "anthropic" ])) "auth login anthropic" "login"

                Expect.equal
                    (string (Cli.parse [ "auth"; "login"; "openai"; "--method"; "api_key" ]))
                    "auth login openai --method api_key"
                    "a method"

                Expect.equal (string (Cli.parse [ "auth"; "logout"; "openai" ])) "auth logout openai" "logout"
                Expect.equal (string (Cli.parse [ "auth"; "list" ])) "auth list" "list"
                Expect.equal (string (Cli.parse [ "auth"; "status" ])) "auth status" "status"

                let o = printed [ "auth"; "login" ]
                Expect.equal o.ExitCode 2 "exit code"
                Expect.stringContains o.Stderr "auth login <provider>" "the usage"
                Expect.stringContains o.Stderr "auth status" "every command"
                Expect.equal (printed [ "auth" ]).ExitCode 2 "no command"
                Expect.stringContains (printed [ "--help" ]).Stdout "auth login <provider>" "in the usage"
            }
        ]
