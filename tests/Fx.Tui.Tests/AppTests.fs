module Fx.Tui.Tests.AppTests

open Expecto
open Fx.Contracts
open Fx.Sessions
open Fx.Grants
open Fx.Tui
open Fx.Tui.Tests.Script

let private sizes = [ 80, 24; 40, 20; 24, 14 ]

let private ready () : Session * Model =
    let s, msgs = finishedTurn ()

    let info: SessionInfo =
        {
            Id = s.Id
            Title = None
            Environment = "env-1"
            Workspace = setup.Workspace
        }

    s, fst (play (App.init setup 80 24 start) ([ Msg.Started info; Msg.Env env ] @ msgs))

let private screen (model: Model) = Frame.text (View.view model)

[<Tests>]
let tests =
    testList
        "TUI app"
        [
            testList
                "golden renders"
                [
                    for name, make in Screens.all do
                        for width, height in sizes do
                            test $"{name} at {width}x{height}" {
                                Golden.expect $"{name}-{width}x{height}.txt" (Screens.render (make width height))
                            }

                    test "verbose shows every cell's code and what each action returned" {
                        let model =
                            { Screens.finished 80 40 with
                                Verbosity = Verbosity.Verbose
                            }

                        Golden.expect "finished-verbose-80x40.txt" (Screens.render model)
                    }

                    test "verbose shows the summary a compaction recorded" {
                        let model =
                            { Screens.compacted 80 30 with
                                Verbosity = Verbosity.Verbose
                            }

                        Golden.expect "compacted-verbose-80x30.txt" (Screens.render model)
                    }

                    test "verbose shows a reply the text-only nudge superseded" {
                        let model =
                            { Screens.question 80 24 with
                                Verbosity = Verbosity.Verbose
                            }

                        Golden.expect "question-verbose-80x24.txt" (Screens.render model)
                    }
                ]

            testList
                "keys"
                [
                    test "Enter steers what was typed; Tab queues it" {
                        let s, model = ready ()
                        let model, cmds = play model (typing "fix it" @ [ key KeyCode.Enter ])

                        Expect.equal
                            cmds
                            [ Cmd.Prompt(s.Id, "tui/run1/prompt/1", "fix it", PromptMode.Steer) ]
                            "steered, with this run's key"

                        Expect.isTrue (Editor.isEmpty model.Editor) "cleared"
                        let _, cmds = play model (typing "later" @ [ key KeyCode.Tab ])

                        Expect.equal
                            cmds
                            [ Cmd.Prompt(s.Id, "tui/run1/prompt/2", "later", PromptMode.Queue) ]
                            "queued, with the next key"
                    }

                    test "Alt+Enter and Ctrl+J add lines; an empty prompt is not sent" {
                        let _, model = ready ()

                        let model, cmds =
                            play
                                model
                                (typing "a"
                                 @ [ Msg.Key(Keys.alt (Keys.key KeyCode.Enter)) ]
                                 @ typing "b"
                                 @ [ ctrl 'j' ]
                                 @ typing "c")

                        Expect.isEmpty cmds "nothing sent"
                        Expect.equal model.Editor.Text "a\nb\nc" "three lines"

                        let _, cmds =
                            play
                                { model with
                                    Editor = Editor.ofText "   "
                                }
                                [ key KeyCode.Enter ]

                        Expect.isEmpty cmds "blank"
                    }

                    test "Esc interrupts the running turn; idle, it does nothing" {
                        let s, model = ready ()
                        let _, cmds = play model [ key KeyCode.Escape ]
                        Expect.isEmpty cmds "idle"
                        let model, _ = play model (s.Prompt "go on")
                        let _, cmds = play model [ key KeyCode.Escape ]

                        Expect.equal
                            cmds
                            [ Cmd.Interrupt(s.Id, "tui/run1/interrupt/2") ]
                            "interrupted, keyed by the turn"
                    }

                    test "Ctrl+C clears, then interrupts, and twice exits; Ctrl+D exits on an empty prompt" {
                        let s, model = ready ()
                        let model, _ = play model (s.Prompt "go on" @ typing "draft")
                        let model, cmds = play model [ ctrl 'c' ]
                        Expect.isTrue (Editor.isEmpty model.Editor) "cleared"
                        Expect.isEmpty cmds "nothing sent"
                        let model, cmds = play model [ ctrl 'c' ]
                        Expect.equal cmds [ Cmd.Quit ] "the second press exits"
                        Expect.isTrue model.Quitting "quitting"
                        let s, model = ready ()
                        let model, cmds = play model (s.Prompt "again" @ [ ctrl 'c' ])
                        Expect.equal cmds [ Cmd.Interrupt(s.Id, "tui/run1/interrupt/2") ] "interrupts first"

                        let model, _ =
                            play model [ Msg.Tick(model.Now + 5_000_000L); key (KeyCode.Char "x") ]

                        let _, cmds = play model [ ctrl 'c' ]
                        Expect.isEmpty cmds "too late: armed again, not exiting"
                        let _, model = ready ()
                        let _, cmds = play model [ ctrl 'd' ]
                        Expect.equal cmds [ Cmd.Quit ] "Ctrl+D"
                    }

                    test "Up and Down go through the prompts sent" {
                        let s, model = ready ()
                        let model, _ = play model (s.Prompt("second prompt", PromptMode.Queue))
                        let model, _ = play model (typing "new")
                        let model, _ = play model [ key KeyCode.Up ]
                        Expect.equal model.Editor.Text "second prompt" "the newest"
                        let model, _ = play model [ key KeyCode.Up ]

                        Expect.equal
                            model.Editor.Text
                            "replace deprecated Http.retry with RetryPolicy in billing"
                            "older"

                        let model, _ = play model [ key KeyCode.Down; key KeyCode.Down ]
                        Expect.equal model.Editor.Text "new" "back to what was typed"
                    }

                    test "PageUp scrolls the transcript, PageDown back; Ctrl+O changes the detail" {
                        let _, model = ready ()
                        let model, _ = play model [ key KeyCode.PageUp ]
                        Expect.equal model.Scroll 12 "half a screen"
                        let model, _ = play model [ key KeyCode.PageDown; key KeyCode.PageDown ]
                        Expect.equal model.Scroll 0 "never below the end"
                        let model, _ = play model [ ctrl 'o' ]
                        Expect.equal model.Verbosity Verbosity.Verbose "more"
                        let model, _ = play model [ ctrl 'o' ]
                        Expect.equal model.Verbosity Verbosity.Quiet "around"
                    }
                ]

            testList
                "cards"
                [
                    test "a permission: once, this session, always, or deny; batched, oldest first" {
                        let model = Screens.grant 80 24
                        let session = model.Session.Value.Id

                        match App.cards model with
                        | [ Card.Grant first; Card.Grant second ] ->
                            Expect.equal first.Id (RequestId "g-1") "oldest first"
                            Expect.equal second.Id (RequestId "g-2") "then the host's"
                        | other -> failtest $"{other}"

                        let answered, cmds = play model [ key (KeyCode.Char "s") ]

                        match cmds with
                        | [ Cmd.AnswerGrant(RequestId "g-1", answer) ] ->
                            Expect.equal answer.Effect GrantEffect.Allow "allowed"
                            Expect.equal answer.Scope ScopeChoice.Session "for the session"
                        | other -> failtest $"{other}"

                        let _, again = play answered [ key (KeyCode.Char "s") ]
                        Expect.isEmpty again "an answer being sent is not sent twice"
                        Expect.stringContains (screen answered) "sending…" "shown as sending"

                        let granted: GrantNotice =
                            {
                                Seq = 9L
                                Event =
                                    GrantEvent.Granted
                                        {
                                            Id = GrantId "gr-1"
                                            Action = ActionName.create "shell.exec"
                                            Resource = "make deploy"
                                            Effect = GrantEffect.Allow
                                            Scope = GrantScope.Session session
                                            By = "user:local-os-user:alice"
                                            At = start
                                            Expires = None
                                            Reason = None
                                            Origin = GrantOrigin.Answer(RequestId "g-1")
                                        }
                            }

                        let model, _ = play answered [ Msg.Grant granted ]

                        match App.cards model with
                        | [ Card.Grant next ] ->
                            Expect.equal next.Id (RequestId "g-2") "the next one"
                            let choices = App.choices model (Card.Grant next) |> List.map (fun (k, _, _) -> k)
                            Expect.equal choices [ "y"; "a"; "n" ] "no session scope for a host-level request"
                        | other -> failtest $"{other}"

                        let _, cmds = play model [ key (KeyCode.Char "n") ]

                        match cmds with
                        | [ Cmd.AnswerGrant(RequestId "g-2", answer) ] ->
                            Expect.equal answer.Effect GrantEffect.Deny "denied"
                            Expect.equal answer.Scope ScopeChoice.Once "once"
                        | other -> failtest $"{other}"
                    }

                    test "a permission of another session is not shown" {
                        let model = Screens.finished 80 24
                        let model, _ = play model [ grantRequest 3L "g-9" (Some "other") "Run rm -rf" ]
                        Expect.isEmpty (App.cards model) "not this session's"
                    }

                    test "a permission of the session pending before the TUI knew the session shows once it does" {
                        let early, _ =
                            play (App.init setup 80 24 start) [ grantRequest 4L "g-5" (Some "s9") "Run make" ]

                        Expect.isEmpty (App.cards early) "not yet"

                        let info: SessionInfo =
                            {
                                Id = "s9"
                                Title = None
                                Environment = "env-1"
                                Workspace = setup.Workspace
                            }

                        let started, _ = play early [ Msg.Started info ]
                        Expect.equal (App.cards started |> List.map _.Id) [ "grant/g-5" ] "now"
                    }

                    test "a refused answer can be given again" {
                        let model = Screens.grant 80 24
                        let model, _ = play model [ key (KeyCode.Char "y") ]
                        let model, _ = play model [ Msg.AnswerFailed("grant/g-1", "unavailable") ]
                        let _, cmds = play model [ key (KeyCode.Char "y") ]
                        Expect.equal cmds.Length 1 "sent again"
                    }

                    test "a decision about a call in doubt: a letter or the selection answers it" {
                        let model = Screens.decision 80 30
                        let session = model.Session.Value.Id
                        let _, cmds = play model [ key (KeyCode.Char "r") ]

                        Expect.equal
                            cmds
                            [
                                Cmd.Answer(
                                    session,
                                    "tui/run1/answer/unknown-t1-s2-1",
                                    "unknown-t1-s2-1",
                                    Json.Object [ "option", Json.String "rerun" ]
                                )
                            ]
                            "rerun"

                        let _, cmds = play model [ key KeyCode.Right; key KeyCode.Enter ]

                        match cmds with
                        | [ Cmd.Answer(_, _, _, answer) ] ->
                            Expect.equal answer (Json.Object [ "option", Json.String "failed" ]) "the second option"
                        | other -> failtest $"{other}"
                    }

                    test "the agent's question is answered with the text typed" {
                        let model = Screens.question 80 24
                        let session = model.Session.Value.Id
                        let model, cmds = play model (typing "staging" @ [ key KeyCode.Enter ])

                        Expect.equal
                            cmds
                            [ Cmd.Answer(session, "tui/run1/answer/q-1-2", "q-1-2", Json.String "staging") ]
                            "answered"

                        Expect.isTrue (Editor.isEmpty model.Editor) "cleared"
                    }

                    test "a key that answers nothing on a card does nothing" {
                        let model = Screens.grant 80 24
                        let next, cmds = play model (typing "zq")
                        Expect.isEmpty cmds "nothing"
                        Expect.isTrue (Editor.isEmpty next.Editor) "not typed into the prompt"
                    }
                ]

            testList
                "streams"
                [
                    test "what the model writes shows until its reply is recorded" {
                        let s, model = ready ()
                        let model, _ = play model (s.Prompt "summarize")
                        let turn = s.Running

                        let model, _ =
                            play
                                model
                                [
                                    Msg.Streaming(turn, true, "The change ", None)
                                    Msg.Streaming(turn, false, "touches 14 files.", None)
                                ]

                        Expect.stringContains (screen model) "The change touches 14 files." "streamed"

                        let model, _ =
                            play model [ s.Reply(1, [ text "The change touches 14 files, all in billing." ]) ]

                        Expect.isNone model.Streaming "replaced by the reply"
                        Expect.stringContains (screen model) "all in billing." "the recorded reply"
                    }

                    test "a compaction's streamed summary gives way to its activity line" {
                        let s, model = ready ()
                        let model, _ = play model (s.Prompt "summarize")
                        let turn = s.Running

                        let model, _ =
                            play model [ Msg.Streaming(turn, true, "Summary: the change ", None) ]

                        Expect.isSome model.Streaming "streamed"
                        let model, _ = play model [ s.Collapse(s.Seq, "Summary: the change is small.") ]
                        Expect.isNone model.Streaming "replaced by the collapse"
                        Expect.stringContains (screen model) "Compact" "the activity line"
                    }

                    test "a snapshot replaces earlier events" {
                        let s, model = ready ()

                        let model, _ =
                            play model [ Msg.Session(Fx.Store.Feed.Reset { Seq = s.Seq; State = s.State }) ]

                        Expect.equal model.Timeline.ResetAt (Some s.Seq) "reset"
                        Expect.stringContains (screen model) "earlier events" "said so"
                    }

                    test "events arrive once: a replay after a reconnect changes nothing" {
                        let s, msgs = finishedTurn ()
                        let once, _ = play (App.init setup 80 24 start) msgs
                        let twice, _ = play once msgs
                        Expect.equal twice.Timeline once.Timeline "the same"
                        ignore s
                    }

                    test "the screen follows the terminal's size" {
                        let _, model = ready ()
                        let model, _ = play model [ Msg.Resized(50, 10) ]
                        let frame = View.view model
                        Expect.equal (frame.Width, frame.Height) (50, 10) "resized"
                        Expect.equal frame.Rows.Length 10 "every row"

                        Expect.isTrue
                            (frame.Rows |> List.forall (fun row -> Row.width row <= 50))
                            "no row wider than the screen"
                    }
                ]

            testList
                "questions"
                [
                    let items (model: Model) =
                        Timeline.blocks model.Timeline
                        |> List.rev
                        |> List.pick (fun block ->
                            match block with
                            | Block.Turn turn -> Some turn.Items
                            | _ -> None)

                    let count (needle: string) (haystack: string) = haystack.Split(needle).Length - 1

                    test "a question that repeats the reply shows once" {
                        let model = Screens.question 120 30
                        let screen = Frame.text (View.view model)
                        Expect.equal (count "Deploy to staging or production?" screen) 1 "once"
                        Expect.isFalse (screen.Contains "The agent asks") "no generic title"
                    }

                    test "a reply without an action that another reply followed is superseded" {
                        let model = Screens.question 80 24

                        Expect.equal
                            (items model
                             |> List.choose (fun item ->
                                 match item with
                                 | TurnItem.Said text -> Some("said " + text)
                                 | TurnItem.Superseded text -> Some("superseded " + text)
                                 | _ -> None))
                            [
                                "superseded Sure, which environment should I deploy to?"
                                "said I can deploy now. Deploy to staging or production?"
                            ]
                            "the draft, then the reply"
                    }

                    test "a reply whose action returned is never superseded" {
                        let s, model = ready ()
                        let model, _ = play model (s.Prompt "check it")
                        let model, _ = play model (s.Boundary 1)

                        let model, _ =
                            play
                                model
                                [
                                    s.Reply(1, [ text "Looking."; cell "toolu_1" "await check()" ])
                                    s.Receipt(1, "toolu_1", "cell", "ok")
                                    s.Reply(2, [ text "It works." ])
                                ]

                        let model, _ = play model (s.Boundary 3)
                        let model, _ = play model [ s.Reply(3, [ text "Anything else?" ]) ]

                        Expect.equal
                            (items model
                             |> List.choose (fun item ->
                                 match item with
                                 | TurnItem.Said text -> Some("said " + text)
                                 | TurnItem.Superseded text -> Some("superseded " + text)
                                 | _ -> None))
                            [ "said Looking."; "superseded It works."; "said Anything else?" ]
                            "only the reply without an action"
                    }

                    test "quiet still shows the question where it was asked" {
                        let model =
                            { Screens.question 120 30 with
                                Verbosity = Verbosity.Quiet
                            }

                        let screen = Frame.text (View.view model)
                        Expect.equal (count "Deploy to staging or production?" screen) 1 "once"
                    }
                ]
        ]
