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
            test "no arguments is the TUI; the usage names it and attach" {
                match Cli.parse [] with
                | Cli.Tui(args, []) -> Expect.equal args Fx.Tui.Live.TuiArgs.defaults "defaults"
                | other -> failtest $"{other}"

                Expect.stringContains Cli.usage "attach [--session <id> | --continue]" "attach"
                Expect.isFalse (Cli.usage.Contains "not there yet") "the TUI is there"
            }

            test "the TUI's options: a session, the newest, a title, selections; attach" {
                match Cli.parse [ "--session"; "01ABC" ] with
                | Cli.Tui(args, []) -> Expect.equal args.Session (Some "01ABC") "session"
                | other -> failtest $"{other}"

                match Cli.parse [ "-c"; "--model"; "anthropic/claude-sonnet-5-5"; "--surface"; "hybrid" ] with
                | Cli.Tui(args, selections) ->
                    Expect.isTrue args.Continue "continue"

                    Expect.equal
                        selections
                        [
                            "model.role.main", Json.String "anthropic/claude-sonnet-5-5"
                            "agent.actionSurface", Json.String "hybrid"
                        ]
                        "selections"
                | other -> failtest $"{other}"

                match Cli.parse [ "--title"; "first"; "--variant"; "codeact.budget.steps=5" ] with
                | Cli.Tui(args, [ slot, _ ]) ->
                    Expect.equal args.Title (Some "first") "title"
                    Expect.equal slot "codeact.budget.steps" "a variant"
                | other -> failtest $"{other}"

                match Cli.parse [ "attach"; "--session"; "01ABC" ] with
                | Cli.Attach args -> Expect.equal args.Session (Some "01ABC") "attach"
                | other -> failtest $"{other}"

                for wrong in
                    [
                        [ "--session"; "a"; "--continue" ]
                        [ "--model"; "nope" ]
                        [ "attach"; "--model"; "anthropic/x" ]
                        [ "--session" ]
                    ] do
                    let o = printed wrong
                    Expect.equal o.ExitCode 2 (String.concat " " wrong)
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

            test "serve, on the local listener or loopback TCP" {
                Expect.equal (string (Cli.parse [ "serve" ])) "serve" "the preferred listener"
                Expect.equal (string (Cli.parse [ "serve"; "--tcp" ])) "serve --tcp 0" "any free port"
                Expect.equal (string (Cli.parse [ "serve"; "--tcp"; "7357" ])) "serve --tcp 7357" "a port"
                Expect.equal (printed [ "serve"; "--tcp"; "70000" ]).ExitCode 2 "not a port"
                Expect.equal (printed [ "serve"; "--x" ]).ExitCode 2 "unknown"
                Expect.stringContains Cli.usage "serve" "in the usage"
            }

            test "grants lists, allows, denies and revokes, host-wide or for a session" {
                let parsed (args: string list) =
                    match Cli.parse ("grants" :: args) with
                    | Cli.Grants command -> command
                    | other -> failtest $"{other}"

                Expect.equal (parsed []) (Fx.Host.GrantsCommand.List false) "list by default"
                Expect.equal (parsed [ "list" ]) (Fx.Host.GrantsCommand.List false) "list"
                Expect.equal (parsed [ "list"; "--all" ]) (Fx.Host.GrantsCommand.List true) "everything"

                Expect.equal
                    (parsed [ "allow"; "component.install"; "bun@1.4.5" ])
                    (Fx.Host.GrantsCommand.Grant(
                        Fx.Grants.GrantEffect.Allow,
                        "component.install",
                        "bun@1.4.5",
                        Fx.Host.GrantsScope.Host,
                        None
                    ))
                    "host-wide by default"

                Expect.equal
                    (parsed
                        [
                            "deny"
                            "shell.exec"
                            "git push **"
                            "--scope"
                            "session:01ABC"
                            "--reason"
                            "not here"
                        ])
                    (Fx.Host.GrantsCommand.Grant(
                        Fx.Grants.GrantEffect.Deny,
                        "shell.exec",
                        "git push **",
                        Fx.Host.GrantsScope.Session "01ABC",
                        Some "not here"
                    ))
                    "a session, a reason"

                Expect.equal
                    (parsed [ "allow"; "shell.exec"; "ls"; "--scope"; "host" ])
                    (Fx.Host.GrantsCommand.Grant(
                        Fx.Grants.GrantEffect.Allow,
                        "shell.exec",
                        "ls",
                        Fx.Host.GrantsScope.Host,
                        None
                    ))
                    "host, said"

                Expect.equal (parsed [ "revoke"; "01G" ]) (Fx.Host.GrantsCommand.Revoke("01G", None)) "revoke"

                Expect.equal
                    (parsed [ "revoke"; "01G"; "--reason"; "done" ])
                    (Fx.Host.GrantsCommand.Revoke("01G", Some "done"))
                    "with a reason"

                for bad in
                    [
                        [ "grants"; "allow"; "component.install" ]
                        [ "grants"; "allow"; "component.install"; "bun@*"; "--scope"; "repo" ]
                        [ "grants"; "allow"; "component.install"; "bun@*"; "--scope"; "session:" ]
                        [ "grants"; "allow"; "component.install"; "bun@*"; "--bogus" ]
                        [ "grants"; "revoke" ]
                        [ "grants"; "bogus" ]
                    ] do
                    let o = printed bad
                    Expect.equal o.ExitCode 2 (String.concat " " bad)
                    Expect.stringContains o.Stderr "grants allow <action> <pattern>" "the usage"

                Expect.equal (printed [ "grants"; "--help" ]).ExitCode 0 "help"
                Expect.stringContains Cli.usage "grants list" "in the usage"
            }
        ]
