module Fx.Host.Tests.UnitTests

open System.IO
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Models
open Fx.Prompt
open Fx.Sdk
open Fx.Sessions
open Fx.Variants
open Fx.Host

let private json (text: string) : Json =
    match Json.parse text with
    | Ok json -> json
    | Error e -> failtest (string e)

let private absolute (segments: string list) =
    let root = Path.GetPathRoot(Path.GetTempPath()) |> nonNull
    Path.Combine(Array.ofList (root :: segments))

type private ScopeOnly =
    {
        Scope: Scope
    }

    interface IScope with
        member env.Scope = env.Scope

let private branchIn (fs: Fx.Effects.Fakes.InMemoryFs) (workspace: string) =
    (Eff.run { Scope = Scope.root } (GitHead.read fs.FsRead (fun a b -> Path.Combine(a, b)) workspace))
        .GetAwaiter()
        .GetResult()

let private variable (pairs: (string * string) list) (name: string) =
    pairs |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd

[<Tests>]
let tests =
    testList
        "Host units"
        [
            testList
                "workspace branch"
                [
                    test "HEAD names the branch, or a detached commit's short id" {
                        Expect.equal (GitHead.parse "ref: refs/heads/main\n") (Some "main") "a branch"
                        Expect.equal (GitHead.parse "ref: refs/heads/feature/x") (Some "feature/x") "with a slash"

                        Expect.equal
                            (GitHead.parse "0123456789abcdef0123456789abcdef01234567")
                            (Some "0123456")
                            "detached"

                        Expect.equal (GitHead.parse "garbage") None "not a HEAD"
                    }

                    test "read from the workspace's files: a checkout, a worktree, no git" {
                        let fs =
                            Fx.Effects.Fakes.InMemoryFs(Fx.Effects.Fakes.FakeClock.Create().Clock, false)

                        let root = Path.Combine(Path.GetTempPath(), "githeads")
                        let repo = Path.Combine(root, "repo")
                        fs.AddText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n")
                        Expect.equal (branchIn fs repo) (Some "main") "a checkout"

                        let tree = Path.Combine(root, "tree")
                        let gitdir = Path.Combine(repo, ".git", "worktrees", "tree")
                        fs.AddText(Path.Combine(tree, ".git"), "gitdir: " + gitdir + "\n")
                        fs.AddText(Path.Combine(gitdir, "HEAD"), "ref: refs/heads/t133e-usage\n")
                        Expect.equal (branchIn fs tree) (Some "t133e-usage") "a worktree"

                        let plain = Path.Combine(root, "plain")
                        fs.AddDirectory plain
                        Expect.equal (branchIn fs plain) None "not a checkout"
                    }
                ]

            testList
                "data directory"
                [
                    test "the product's home variable wins, then the user's directory" {
                        let home = absolute [ "home"; "u" ]
                        let custom = absolute [ "srv"; "data" ]

                        Expect.equal
                            (HostPaths.dataDirectory (variable [ HostPaths.homeVariable, custom; "HOME", home ]) false)
                            (Ok custom)
                            "the variable"

                        Expect.equal
                            (HostPaths.dataDirectory (variable [ "HOME", home ]) false)
                            (Ok(Path.Combine(home, Product.homeDirName)))
                            "~/.<product>"

                        Expect.equal
                            (HostPaths.dataDirectory (variable [ "LOCALAPPDATA", home ]) true)
                            (Ok(Path.Combine(home, Product.windowsDataDirName)))
                            "%LOCALAPPDATA%\\<product>"
                    }

                    test "relative or missing directories are refused" {
                        Expect.isError
                            (HostPaths.dataDirectory (variable [ HostPaths.homeVariable, "data" ]) false)
                            "relative"

                        Expect.isError (HostPaths.dataDirectory (variable []) false) "nothing set"
                        Expect.isError (HostPaths.dataDirectory (variable [ "HOME", "" ]) false) "blank"
                    }

                    test "paths live under the data directory" {
                        let data = absolute [ "d" ]
                        let paths = HostPaths.under data

                        for path in
                            [
                                paths.Config
                                paths.Policy
                                paths.Lock
                                paths.Events
                                paths.Blobs
                                paths.Trust
                                paths.MachineId
                                paths.Runtimes
                            ] do
                            Expect.stringStarts path data path

                        Expect.equal
                            (HostPaths.projectConfig data)
                            (Path.Combine(data, Product.projectDirName, "config.json"))
                            "project config"
                    }
                ]

            testList
                "config files"
                [
                    test "a host config has variants, environments and disabled extensions" {
                        let config =
                            json
                                """{"variants": {"edit.guard": "require"},
                                    "environments": {"default": {"provider": "local", "transport": "piped"}},
                                    "extensions": {"disabled": ["fx.model.openai"]}}"""

                        match ConfigFile.ofJson Layer.Host "host.json" config with
                        | Ok file ->
                            Expect.equal file.Variants.Values [ "edit.guard", Json.String "require" ] "variants"
                            Expect.equal file.Variants.Source.Layer Layer.Host "layer"
                            Expect.equal (file.Environments |> List.map _.Name) [ "default" ] "environments"
                            Expect.equal file.Disabled [ "fx.model.openai" ] "disabled"
                        | Error issues -> failtest (Issue.render issues)
                    }

                    test "unknown members and bad values are issues with paths" {
                        match
                            ConfigFile.ofJson
                                Layer.Host
                                "host.json"
                                (json """{"variant": {}, "extensions": {"disabled": "x"}}""")
                        with
                        | Ok _ -> failtest "accepted"
                        | Error issues ->
                            let text = Issue.render issues
                            Expect.stringContains text "$.variant: unknown member" "the typo"
                            Expect.stringContains text "$.extensions.disabled" "the bad value"
                    }

                    test "a project's config cannot disable extensions" {
                        Expect.isError
                            (ConfigFile.ofJson Layer.Project "p.json" (json """{"extensions": {"disabled": []}}"""))
                            "extensions"

                        Expect.isOk (ConfigFile.ofJson Layer.Project "p.json" (json """{"variants": {}}""")) "variants"
                    }

                    test "a config file must be an object" {
                        Expect.isError (ConfigFile.ofJson Layer.Host "h.json" (json "[]")) "array"
                    }
                ]

            testList
                "trust"
                [
                    let project = absolute [ "work"; "app" ]

                    test "a trusted directory covers what is below it, not its siblings" {
                        let store =
                            match TrustStore.add false project 1L TrustStore.empty with
                            | Ok store -> store
                            | Error e -> failtest e

                        Expect.isTrue (TrustStore.isTrusted false project store) "itself"
                        Expect.isTrue (TrustStore.isTrusted false (Path.Combine(project, "src", "x")) store) "below"
                        Expect.isFalse (TrustStore.isTrusted false (project + "2") store) "a sibling with its prefix"
                        Expect.isFalse (TrustStore.isTrusted false (absolute [ "work" ]) store) "above"
                        Expect.isFalse (TrustStore.isTrusted false "app" store) "relative"
                        Expect.isTrue (TrustStore.isTrusted true (project.ToUpperInvariant()) store) "without case"
                        Expect.isFalse (TrustStore.isTrusted false (project.ToUpperInvariant()) store) "with case"
                    }

                    test "trusting again keeps the first time; removing needs the exact directory" {
                        let once =
                            TrustStore.add false project 1L TrustStore.empty |> Result.defaultWith failwith

                        let twice =
                            TrustStore.add false (project + string Path.DirectorySeparatorChar) 2L once

                        Expect.equal twice (Ok once) "unchanged"

                        match TrustStore.remove false (Path.Combine(project, "src")) once with
                        | Ok(_, removed) -> Expect.isFalse removed "below is not removed"
                        | Error e -> failtest e

                        match TrustStore.remove false project once with
                        | Ok(store, removed) ->
                            Expect.isTrue removed "removed"
                            Expect.isEmpty store.Projects "empty"
                        | Error e -> failtest e

                        Expect.isError (TrustStore.add false "relative" 1L TrustStore.empty) "relative"
                    }

                    test "the store's JSON reads back" {
                        let store =
                            TrustStore.add false project 42L TrustStore.empty |> Result.defaultWith failwith

                        let text = Json.serialize (Codec.encode TrustStore.codec store)
                        Expect.equal (Codec.decodeText TrustStore.codec text) (Ok store) "round trip"
                    }
                ]

            testList
                "built-ins"
                [
                    test "every bundled package is in the static list, the samples are not" {
                        Expect.equal
                            (Builtins.ids |> List.sort)
                            ([
                                "env.local"
                                "identity.local-os-user"
                                "model.anthropic"
                                "model.openai"
                                "egress.exclusions"
                                "interpreter.bun-ts"
                                "runtime.bun"
                                "auth.standard"
                                "store.keyring"
                                "store.file"
                                "store.ephemeral"
                                "sandbox.bubblewrap"
                                "sandbox.seatbelt"
                                "sandbox.none"
                             ]
                             |> List.map Product.extensionId
                             |> List.sort)
                            "ids"

                        let packages =
                            Directory.GetDirectories(Fx.Kernel.Tests.Repo.path [ "extensions" ])
                            |> Array.map (fun dir -> Path.GetFileName dir)
                            // fx.exthost.bun joins the built-ins with T115b (its files extracted, its Bun installed at the start).
                            |> Array.filter (fun name ->
                                name <> "Fx.Ext.Sample"
                                && name <> "Fx.Ext.ModelCanonical"
                                && name <> "Fx.Ext.ExthostBun")

                        Expect.equal
                            packages.Length
                            Builtins.ids.Length
                            "one per package under extensions/ (add new ones to Builtins)"
                    }

                    test "the probe's environment is small and holds no credentials" {
                        match
                            Host.probeEnvironment
                                [
                                    "PATH", "/bin"
                                    "HOME", "/h"
                                    "ANTHROPIC_API_KEY", "x"
                                    "NODE_OPTIONS", "--x"
                                    "SECRET_TOKEN", "y"
                                    "EDITOR", "vi"
                                ]
                        with
                        | Exactly vars -> Expect.equal (vars |> List.map fst) [ "PATH"; "HOME" ] "kept"
                        | other -> failtest (string other)
                    }
                ]

            testList
                "activity"
                [
                    test "replies become one line per action" {
                        let call (name: string) (arguments: (string * Json) list) =
                            Part.ToolCall
                                {
                                    Id = "c"
                                    Name = name
                                    Arguments = Json.serialize (Json.Object arguments)
                                    State = None
                                }

                        let lines =
                            Activity.ofReply
                                {
                                    Turn = 1
                                    Step = 1
                                    Shown = []
                                    Parts =
                                        [
                                            Part.text "Let me look."
                                            call "cell" [ "code", Json.String "// intent: read the notes\nconst n = 1" ]
                                            call "edit" [ "path", Json.String "a.txt" ]
                                            call "bash" [ "command", Json.String "make test\nmore" ]
                                        ]
                                    Used = None
                                }

                        Expect.equal
                            lines
                            [
                                "  Let me look."
                                "◇ cell  read the notes"
                                "◆ edit  a.txt"
                                "◇ bash  make test"
                            ]
                            "lines"
                    }

                    test "a compaction is an activity line" {
                        let at = EventRef.create (Session.stream "s1") 1L

                        Expect.equal
                            (Activity.ofContext (
                                ContextEvent.Collapsed
                                    {
                                        From = at
                                        To = at
                                        Summary = [ Piece.Quote { Text = "Read a.txt."; Sources = [] } ]
                                    }
                            ))
                            [ "◇ compacted  the earlier conversation into a summary (11 characters)" ]
                            "one line"
                    }

                    test "a cell in a new kernel says so on a line of its own, before its error" {
                        let receipt (restarted: KernelRestart option) (isError: bool) =
                            ContextEvent.Returned
                                {
                                    Turn = 2
                                    Step = 1
                                    Call = Some "call-1"
                                    Name = "cell"
                                    Pieces =
                                        [
                                            if restarted.IsSome then
                                                Piece.Text "[A new kernel started for this cell (…)]"
                                            Piece.Text "ReferenceError: x is not defined"
                                        ]
                                    IsError = isError
                                    Restarted = restarted
                                    Approvals = []
                                }

                        let restart =
                            Some
                                {
                                    Launch = 2
                                    Reason = "the previous kernel was killed by signal 9 (SIGKILL)"
                                }

                        Expect.equal
                            (Activity.ofContext (receipt restart true))
                            [
                                "  ↻ new kernel  the previous kernel was killed by signal 9 (SIGKILL); names from earlier cells are gone"
                                "  ✗ ReferenceError: x is not defined"
                            ]
                            "the restart, then the error after the note"

                        Expect.equal
                            (Activity.ofContext (receipt restart false))
                            [
                                "  ↻ new kernel  the previous kernel was killed by signal 9 (SIGKILL); names from earlier cells are gone"
                            ]
                            "a restart without an error"

                        Expect.equal (Activity.ofContext (receipt None false)) [] "nothing to say"
                    }

                    test "the ending reads like a result card" {
                        Expect.equal
                            (Activity.ending
                                []
                                (TurnEnd.Completed(
                                    TurnResult.Done(
                                        "Fixed it",
                                        [
                                            {
                                                Kind = "command"
                                                Summary = "tests pass"
                                                Detail = Json.Null
                                            }
                                        ],
                                        Some "make test"
                                    )
                                )))
                            [ "✓ Done  Fixed it"; "  Evidence  tests pass"; "  Verify    make test" ]
                            "done"

                        Expect.equal
                            (Activity.ending [ "Which one?" ] (TurnEnd.Completed(TurnResult.NeedInput [ "q-1-1" ])))
                            [ "? Needs input"; "  Which one?" ]
                            "a question"
                    }
                ]

            testList
                "session index"
                [
                    let entry: SessionEntry =
                        {
                            Session = "01JDB0000000000000000000S1"
                            Workspace = "/work/app"
                            Environment = "01JDB0000000000000000000E1"
                            Title = Some "fix the build"
                        }

                    test "version 1 of the index's events, kept for ever" {
                        let events =
                            [
                                IndexEvent.Created entry
                                IndexEvent.Created
                                    { entry with
                                        Session = "01JDB0000000000000000000S2"
                                        Title = None
                                    }
                            ]

                        let lines =
                            events
                            |> List.map (fun e -> Json.serializeCanonical (Codec.encode SessionIndex.event e) + "\n")
                            |> String.concat ""

                        Fx.Kernel.Tests.Repo.expectGenerated
                            [ "tests"; "Fx.Host.Tests"; "golden"; "index-events-v1.jsonl" ]
                            lines

                        for line, event in List.zip (lines.TrimEnd('\n').Split('\n') |> List.ofArray) events do
                            Expect.equal (Codec.decodeText SessionIndex.event line) (Ok event) "reads back"
                    }

                    test "a headless run's outcome reads back" {
                        let outcome: RunOutcome =
                            {
                                Session = "s1"
                                Turn = 2
                                Ending = TurnEnd.Failed "no model"
                                Questions = []
                            }

                        let text = Json.serialize (Codec.encode Headless.outcomeCodec outcome)

                        Expect.equal
                            text
                            """{"session":"s1","turn":2,"ending":{"kind":"failed","reason":"no model"},"questions":[]}"""
                            "json"

                        Expect.equal (Codec.decodeText Headless.outcomeCodec text) (Ok outcome) "round trip"
                    }
                ]
        ]
