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

            test "nobody at a terminal: denied once, saying what and why" {
                let asked = request inSession []
                Expect.equal GrantPrompts.unanswered.Effect GrantEffect.Deny "denied"
                Expect.equal GrantPrompts.unanswered.Scope ScopeChoice.Once "once"
                let shown = GrantPrompts.deniedUnasked asked
                Expect.stringContains shown "shell.exec mkdir made" "what"
                Expect.stringContains shown "nobody can answer here" "why"
            }
        ]
