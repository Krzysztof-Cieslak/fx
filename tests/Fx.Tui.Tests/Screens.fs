/// Screens of the TUI from scripted streams: what the golden renders and the JIT-vs-native scenario
/// show.
module Fx.Tui.Tests.Screens

open Fx.Contracts
open Fx.Models
open Fx.Sessions
open Fx.Tui
open Fx.CodeAct
open Fx.Tui.Tests.Script

let private readyIn (environment: EnvInfo) (s: Session) (width: int) (height: int) (msgs: Msg list) : Model =
    let model = App.init setup width height start

    let info: SessionInfo =
        {
            Id = s.Id
            Title = Some "billing"
            Environment = "env-1"
            Workspace = setup.Workspace
        }

    fst (play model ([ Msg.Started info; Msg.Env environment ] @ msgs @ [ Msg.Tick s.Now ]))

let private ready = readyIn env

/// Before the session exists: creating it (and its environment).
let starting (width: int) (height: int) : Model =
    fst (play (App.init setup width height start) [ Msg.Starting "creating the session and its environment" ])

/// A finished turn with its activity lines and result card.
let finished (width: int) (height: int) : Model =
    let s, msgs = finishedTurn ()
    ready s width height msgs

/// A finished turn in an environment a sandbox confines: its driver in the header, no banner.
let sandboxed (width: int) (height: int) : Model =
    let s, msgs = finishedTurn ()
    readyIn confined s width height msgs

/// A turn in progress: a cell running, a steer waiting for the next step, a prompt queued.
let running (width: int) (height: int) : Model =
    let s, earlier = finishedTurn ()

    let msgs =
        [
            yield! earlier
            yield! s.Prompt "now update the changelog"
            yield! s.Boundary 1
            s.Advance 1.5
            yield
                s.Reply(
                    1,
                    [
                        text "Updating CHANGELOG.md."
                        cell "toolu_5" "// intent: add the entry\nawait fx.fs.read(\"CHANGELOG.md\")"
                    ]
                )
            s.Advance 4.2
            yield! s.Prompt("mention the issue number", PromptMode.Steer)
            yield! s.Prompt("then bump the version", PromptMode.Queue)
            yield Msg.Streaming(s.Running, true, "", Some "cell")
        ]

    ready s width height msgs

/// The agent's question: a reply without a tool call (a draft the text-only nudge superseded), a
/// second one that became the question, and the turn ended needing input.
let private questionTurn () : Session * Msg list =
    let s = Session "01JD2W7Z9Q0000000000000001"

    let msgs =
        [
            yield! s.Start None
            yield! s.Prompt "deploy it"
            yield! s.Boundary 1
            s.Advance 1.5
            yield s.Reply(1, [ text "Sure, which environment should I deploy to?" ])
            yield! s.Boundary 2
            s.Advance 1.5
            yield s.Reply(2, [ text "I can deploy now. Deploy to staging or production?" ])
            yield! s.Open(Script.question s.Running 2 "I can deploy now. Deploy to staging or production?")
            yield! s.End(TurnResult.NeedInput [ "q-1-2" ])
        ]

    s, msgs

let question (width: int) (height: int) : Model =
    let s, msgs = questionTurn ()
    ready s width height msgs

/// A question the loop guard asked (not a reply): its text where it was asked, once.
let stuck (width: int) (height: int) : Model =
    let s = Session "01JD2W7Z9Q0000000000000006"

    let msgs =
        [
            yield! s.Start None
            yield! s.Prompt "fix the flaky test"
            yield! s.Boundary 1
            s.Advance 2.0
            yield s.Reply(1, [ cell "toolu_1" "// intent: run the test\nawait fx.shell.run(\"make test\")" ])
            s.Advance 1.0
            yield s.Receipt(1, "toolu_1", "cell", "1 failed")
            yield!
                s.Open(
                    RequestKind.request
                        Questions.kind
                        "q-1-1"
                        "The agent repeats itself"
                        (RequestOrigin.Turn s.Running)
                        Reply.AsInput
                        Lifetime.Session
                        {
                            Question =
                                "The agent did the same thing (cell) 3 times with the same result and stopped. How should it go on?"
                            Reason = "loop"
                        }
                )
            yield! s.End(TurnResult.NeedInput [ "q-1-1" ])
        ]

    ready s width height msgs

/// A permission the running turn waits for (T127b asks before shell commands).
let grant (width: int) (height: int) : Model =
    let s = Session "01JD2W7Z9Q0000000000000002"

    let msgs =
        [
            yield! s.Start None
            yield! s.Prompt "ship it"
            yield! s.Boundary 1
            s.Advance 2.0
            yield s.Reply(1, [ call "toolu_1" "bash" [ "command", Json.String "make deploy" ] ])
            yield grantRequest 7L "g-1" (Some s.Id) "Run make deploy"
            yield grantRequest 8L "g-2" None "Install Bun 1.4.5"
        ]

    ready s width height msgs

/// A decision about a call that was running when the host stopped.
let decision (width: int) (height: int) : Model =
    let s = Session "01JD2W7Z9Q0000000000000003"

    let msgs =
        [
            yield! s.Start None
            yield! s.Prompt "migrate the database"
            yield! s.Boundary 1
            s.Advance 2.0
            yield! s.Open(Script.decision s.Running)
        ]

    ready s width height msgs

/// Wide characters in prompts and replies, and a failed turn.
let wide (width: int) (height: int) : Model =
    let s = Session "01JD2W7Z9Q0000000000000004"

    let msgs =
        [
            yield! s.Start None
            yield! s.Prompt "翻訳を確認して 👍🏽 — vérifie les traductions"
            yield! s.Boundary 1
            s.Advance 1.0
            yield
                s.Reply(
                    1,
                    [
                        text "日本語のファイルを読みます。"
                        call "toolu_1" "read" [ "path", Json.String "docs/日本語.md" ]
                    ]
                )
            s.Advance 0.4
            yield s.Receipt(1, "toolu_1", "read", "no such file: docs/日本語.md", true)
            yield! s.Fail "the model call failed: overloaded"
        ]

    ready s width height msgs

/// A long turn whose oldest part was compacted: its activity line between the steps.
let compacted (width: int) (height: int) : Model =
    let s = Session "01JD2W7Z9Q0000000000000005"

    let msgs =
        [
            yield! s.Start None
            yield! s.Prompt "port the parser"
            yield! s.Boundary 1
            s.Advance 2.0
            yield
                s.Reply(
                    1,
                    [
                        cell "toolu_1" "// intent: read the grammar\nawait fx.fs.read(\"grammar.y\")"
                    ]
                )
            s.Advance 0.3
            yield s.Receipt(1, "toolu_1", "cell", "1200 lines")
            yield! s.Boundary 2
            s.Advance 1.0
            yield s.Reply(2, [ cell "toolu_2" "// intent: port the rules\nawait port()" ])
            s.Advance 9.0
            yield s.Receipt(2, "toolu_2", "cell", "ported 84 rules")
            yield! s.Boundary 3
            s.Advance 4.0
            yield
                s.Collapse(s.Seq, "Porting the parser: read grammar.y (1200 lines), ported 84 rules.\nNext: the tests.")
            s.Advance 1.0
            yield s.Reply(3, [ cell "toolu_3" "// intent: run the tests\nawait fx.shell.run(\"make test\")" ])
            s.Advance 3.0
            yield s.Receipt(3, "toolu_3", "cell", "all passed")
            yield! s.End(TurnResult.Done("Ported the parser", [], None))
        ]

    ready s width height msgs

/// A reply in Markdown, and a result card whose summary and check are Markdown too.
let markdown (width: int) (height: int) : Model =
    let s = Session "01JD2W7Z9Q0000000000000007"

    let msgs =
        [
            yield! s.Start None
            yield! s.Prompt "how do I use opus by default?"
            yield! s.Boundary 1
            s.Advance 2.0
            yield
                s.Reply(
                    1,
                    [
                        text
                            "Set **model.role.main** in `~/.fx/config.json`:\n\n```json\n{ \"variants\": { \"model.role.main\": \"anthropic/claude-opus-5-5\" } }\n```\n\n- `--model` overrides it for **one run**\n- a trusted project's config for *one project*"
                        cell "toolu_1" "// intent: check the config\nawait fx.shell.run(\"fx variants explain\")"
                    ]
                )
            s.Advance 1.0
            yield s.Receipt(1, "toolu_1", "cell", "model.role.main = anthropic/claude-opus-5-5")
            yield!
                s.End(
                    TurnResult.Done(
                        "Created `~/.fx/config.json`: fx uses **Opus 5.5** by default",
                        [],
                        Some "`fx variants explain model.role.main`"
                    )
                )
        ]

    ready s width height msgs

/// A session whose replies recorded what they used: the turn's footer has the model and its speed,
/// the footer (or the sidebar, on a wide terminal) the context used and the cost.
let usage (width: int) (height: int) : Model =
    let s = Session "01JD2W7Z9Q0000000000000008"

    let msgs =
        [
            yield! s.Start(Some "Fast mode for Opus")
            yield! s.Prompt "can fx use opus fast mode?"
            yield! s.Boundary 1
            s.Advance 4.0
            yield
                s.Reply(
                    1,
                    [
                        cell "toolu_1" "// intent: look for a speed option\nawait fx.fs.read(\"src/Fx.Models/Chat.fs\")"
                    ],
                    used 60_000 400 0.95m
                )
            s.Advance 0.5
            yield s.Receipt(1, "toolu_1", "cell", "no speed option")
            yield! s.Boundary 2
            s.Advance 6.0
            yield s.Reply(2, [ text "Not yet: the request format has **no speed option**." ], used 73_016 600 1.16m)
            yield! s.End(TurnResult.Done("Fast mode needs a speed option in `ChatRequest`", [], None))
        ]

    ready s width height msgs

/// The command palette over a finished turn, with a query typed.
let palette (width: int) (height: int) : Model =
    let s, msgs = finishedTurn ()
    ready s width height (msgs @ [ ctrl 'p'; yield! typing "se" ])

/// A `/` prompt being typed: the commands it may name.
let slash (width: int) (height: int) : Model =
    let s, msgs = finishedTurn ()
    ready s width height (msgs @ typing "/")

/// A new session with no turns yet: the start screen.
let fresh (width: int) (height: int) : Model =
    let s = Session "01JD2W7Z9Q0000000000000009"
    ready s width height (s.Start None)

/// The start screen with the palette open.
let freshPalette (width: int) (height: int) : Model =
    let s = Session "01JD2W7Z9Q0000000000000009"
    ready s width height (s.Start None @ [ ctrl 'p' ])

/// Every screen, by name.
let all: (string * (int -> int -> Model)) list =
    [
        "starting", starting
        "finished", finished
        "sandboxed", sandboxed
        "running", running
        "question", question
        "stuck", stuck
        "grant", grant
        "decision", decision
        "wide", wide
        "compacted", compacted
        "markdown", markdown
        "usage", usage
        "palette", palette
        "slash", slash
        "fresh", fresh
        "fresh-palette", freshPalette
    ]

/// A frame as text, with the cursor's place.
let render (model: Model) : string =
    let frame = View.view model

    let cursor =
        match frame.Cursor with
        | Some(row, column) -> $"cursor {row},{column}"
        | None -> "cursor hidden"

    let border = String.replicate frame.Width "─"
    border + "\n" + Frame.text frame + "\n" + border + "\n" + cursor + "\n"
