/// Screens of the TUI from scripted streams: what the golden renders and the JIT-vs-native scenario
/// show.
module Fx.Tui.Tests.Screens

open Fx.Contracts
open Fx.Models
open Fx.Sessions
open Fx.Tui
open Fx.Tui.Tests.Script

let private ready (s: Session) (width: int) (height: int) (msgs: Msg list) : Model =
    let model = App.init setup width height start

    let info: SessionInfo =
        {
            Id = s.Id
            Title = Some "billing"
            Environment = "env-1"
            Workspace = setup.Workspace
        }

    fst (play model ([ Msg.Started info; Msg.Env env ] @ msgs @ [ Msg.Tick s.Now ]))

/// Before the session exists: creating it (and its environment).
let starting (width: int) (height: int) : Model =
    fst (play (App.init setup width height start) [ Msg.Starting "creating the session and its environment" ])

/// A finished turn with its activity lines and result card.
let finished (width: int) (height: int) : Model =
    let s, msgs = finishedTurn ()
    ready s width height msgs

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

/// The agent's question: the turn ended needing input.
let question (width: int) (height: int) : Model =
    let s = Session "01JD2W7Z9Q0000000000000001"

    let msgs =
        [
            yield! s.Start None
            yield! s.Prompt "deploy it"
            yield! s.Boundary 1
            s.Advance 3.0
            yield! s.Open(Script.question s.Running 1 "Deploy to staging or production?")
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

/// Every screen, by name.
let all: (string * (int -> int -> Model)) list =
    [
        "starting", starting
        "finished", finished
        "running", running
        "question", question
        "grant", grant
        "decision", decision
        "wide", wide
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
