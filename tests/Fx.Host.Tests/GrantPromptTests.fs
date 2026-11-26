/// Grant requests at the terminal (`GrantPrompts`, what `fx run` shows and how it reads the reply).
module Fx.Host.Tests.GrantPromptTests

open Expecto
open Fx.Grants
open Fx.Host

let private request (context: GrantContext) (evidence: Evidence list) : GrantRequest =
    let ask: GrantAsk =
        {
            Key = "session/s1#3/t1-s1-1/shell.exec/abc"
            Action = ActionName.create "shell.exec"
            Resource = "mkdir made"
            Context = context
            Requester =
                {
                    Principal = "user:alice"
                    Agent = Some "main"
                    Extension = None
                }
            Summary = "Run `mkdir made`"
            Evidence = evidence
            Suggestions = [ "mkdir **" ]
            Scopes = []
            Expires = None
        }

    match GrantLog.request Actions.builtIn ask (RequestId "q1") 10L with
    | Ok made -> made
    | Error e -> failtest (string e)

let private inSession =
    { GrantContext.none with
        Session = Some "s1"
    }

[<Tests>]
let tests =
    testList
        "Grant prompts"
        [
            test "the prompt shows what is asked, the action and resource, and the evidence on one line each" {
                let shown =
                    GrantPrompts.lines (
                        request
                            inSession
                            [
                                {
                                    Label = "command line"
                                    Text = "mkdir made && make\nsecond line"
                                }
                                {
                                    Label = "asked by"
                                    Text = "session s1, turn 1, t1-s1-1"
                                }
                            ]
                    )

                Expect.equal
                    shown
                    [
                        "? Run `mkdir made`"
                        "  shell.exec mkdir made"
                        "  command line: mkdir made && make …"
                        "  asked by: session s1, turn 1, t1-s1-1"
                    ]
                    "lines"

                let long = String.replicate 300 "x"

                let bounded =
                    GrantPrompts.lines (request inSession [ for i in 1..8 -> { Label = $"e{i}"; Text = long } ])

                Expect.equal bounded.Length (2 + GrantPrompts.MaxEvidence + 1) "at most MaxEvidence, then a count"
                Expect.equal (List.last bounded) "  (and 2 more)" "the rest counted"
                Expect.isTrue (bounded |> List.forall (fun l -> l.Length < GrantPrompts.MaxLine + 20)) "bounded lines"
            }

            test "replies: once, for the session when offered, anything else denies once" {
                let asked = request inSession []

                Expect.equal
                    (GrantPrompts.question asked)
                    "  Allow? [y] once, [s] for this session, [a] always on this host, [N] no: "
                    "the offered answers"

                let parsed (reply: string) =
                    let answer = GrantPrompts.parse asked reply
                    answer.Effect, answer.Scope

                Expect.equal (parsed "y") (GrantEffect.Allow, ScopeChoice.Once) "y"
                Expect.equal (parsed " Yes ") (GrantEffect.Allow, ScopeChoice.Once) "yes"
                Expect.equal (parsed "s") (GrantEffect.Allow, ScopeChoice.Session) "s"
                Expect.equal (parsed "") (GrantEffect.Deny, ScopeChoice.Once) "nothing"
                Expect.equal (parsed "a") (GrantEffect.Allow, ScopeChoice.Host) "a"
                Expect.equal (parsed "maybe") (GrantEffect.Deny, ScopeChoice.Once) "anything else"

                // A request of no session (a component install) offers no session scope.
                let install = request GrantContext.none []
                Expect.isFalse ((GrantPrompts.question install).Contains "session") "not offered"
                Expect.equal (GrantPrompts.parse install "s").Effect GrantEffect.Deny "so s denies"

                for answer in
                    [
                        GrantPrompts.parse asked "s"
                        GrantPrompts.parse asked "a"
                        GrantPrompts.parse asked "y"
                        GrantPrompts.parse asked "n"
                    ] do
                    Expect.equal (GrantLog.checkAnswer Actions.builtIn asked answer 11L) (Ok()) "a valid answer"
            }

            test "nobody at a terminal: not done, saying what, why, and the command that allows it beforehand" {
                let asked = request inSession []
                let shown = GrantPrompts.unasked asked
                Expect.stringContains shown "shell.exec mkdir made" "what"
                Expect.stringContains shown "nobody can answer here" "why"
                Expect.isFalse (shown.Contains "denied") "nobody denied it"

                Expect.stringContains
                    shown
                    $"`{Fx.Product.binaryName} grants allow shell.exec 'mkdir made'`"
                    "the command, quoted"

                let install =
                    match
                        GrantLog.request
                            Actions.builtIn
                            {
                                Key = "component.install/bun@1.4.5-abc/environment:e1/1"
                                Action = ActionName.create "component.install"
                                Resource = "bun@1.4.5"
                                Context = GrantContext.none
                                Requester =
                                    {
                                        Principal = "system"
                                        Agent = None
                                        Extension = None
                                    }
                                Summary = "Install bun 1.4.5 for linux-x64-glibc"
                                Evidence = []
                                Suggestions = [ "bun@*" ]
                                Scopes = []
                                Expires = None
                            }
                            (RequestId "q2")
                            10L
                    with
                    | Ok made -> made
                    | Error e -> failtest (string e)

                Expect.stringContains
                    (GrantPrompts.unasked install)
                    $"Allow it beforehand with `{Fx.Product.binaryName} grants allow component.install bun@1.4.5`, or run from a terminal."
                    "an install's command needs no quotes"

                // Only once: no command allows it beforehand.
                let budget =
                    match
                        GrantLog.request
                            Actions.builtIn
                            {
                                Key = "budget/r1"
                                Action = ActionName.create "budget.raise"
                                Resource = "r1/steps"
                                Context = GrantContext.none
                                Requester =
                                    {
                                        Principal = "system"
                                        Agent = None
                                        Extension = None
                                    }
                                Summary = "Raise the step budget"
                                Evidence = []
                                Suggestions = []
                                Scopes = []
                                Expires = None
                            }
                            (RequestId "q3")
                            10L
                    with
                    | Ok made -> made
                    | Error e -> failtest (string e)

                Expect.isNone (GrantsCommand.allowing budget) "nothing to run"
                Expect.stringContains (GrantPrompts.unasked budget) "Run from a terminal" "only a terminal"
            }

            test "fx grants drafts only grants of known actions, for scopes they allow" {
                let draft = GrantsCommand.draft Actions.builtIn GrantEffect.Allow

                match draft "component.install" "bun@*" GrantScope.Host (Some "ci") with
                | Ok d ->
                    Expect.equal
                        (d.Action.Value, d.Resource, d.Scope, d.Reason)
                        ("component.install", "bun@*", GrantScope.Host, Some "ci")
                        "host"
                | Error e -> failtest e

                match draft "shell.exec" "git **" (GrantScope.Session "s1") None with
                | Ok d -> Expect.equal d.Scope (GrantScope.Session "s1") "a session"
                | Error e -> failtest e

                let refused action scope =
                    match draft action "x" scope None with
                    | Ok d -> failtest $"drafted {d}"
                    | Error e -> e

                Expect.stringContains
                    (refused "component.install" (GrantScope.Session "s1"))
                    "component.install grants are not for session scope (they are for: repo, host)"
                    "installs are not per session"

                Expect.stringContains
                    (refused "nothing.known" GrantScope.Host)
                    "no action is named nothing.known"
                    "unknown"

                Expect.stringContains (refused "install" GrantScope.Host) "not an action name" "not a name"
            }

            test "commands are quoted as a shell reads them, and patterns match exactly what was asked" {
                Expect.equal (GrantsCommand.quote "bun@1.4.5") "bun@1.4.5" "plain"
                Expect.equal (GrantsCommand.quote "") "''" "empty"

                if System.OperatingSystem.IsWindows() then
                    Expect.equal (GrantsCommand.quote "it's") "'it''s'" "PowerShell"
                else
                    Expect.equal (GrantsCommand.quote "it's") "'it'\\''s'" "sh"

                let star =
                    { request inSession [] with
                        Resource = "ls *"
                    }

                Expect.equal
                    (GrantsCommand.allowing star)
                    (Some $"{Fx.Product.binaryName} grants allow shell.exec 'ls \\*'")
                    "the wildcard escaped: exactly this command"
            }
        ]
