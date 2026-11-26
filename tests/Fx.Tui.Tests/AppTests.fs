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

                    for name, terminal, choice in
                        [
                            "truecolor-dark", ColorDepth.TrueColor, ThemeChoice.Dark
                            "truecolor-light", ColorDepth.TrueColor, ThemeChoice.Light
                            "indexed-dark", ColorDepth.Indexed, ThemeChoice.Auto
                            "basic", ColorDepth.Basic, ThemeChoice.Auto
                            "nocolor", ColorDepth.NoColor, ThemeChoice.Dark
                        ] do
                        test $"styles at {name}" {
                            let model = Screens.finished 60 16

                            let model =
                                { model with
                                    Setup =
                                        { model.Setup with
                                            Terminal =
                                                {
                                                    Depth = terminal
                                                    LightBackground = None
                                                }
                                            Theme = choice
                                        }
                                }

                            let drawn =
                                (View.view model).Rows
                                |> List.mapi (fun i row ->
                                    $"{i, 2} "
                                    + (row
                                       |> List.map (fun span -> "{" + string span.Style + "}" + span.Text)
                                       |> String.concat ""))
                                |> String.concat "\n"

                            Golden.expect $"styles-{name}.txt" (drawn + "\n")
                        }

                    test "a wide terminal has the sidebar" {
                        Golden.expect "usage-130x24.txt" (Screens.render (Screens.usage 130 24))
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

                    test "the wheel scrolls three rows a notch, never below the end" {
                        let _, model = ready ()
                        let model, _ = play model [ key KeyCode.WheelUp; key KeyCode.WheelUp ]
                        Expect.equal model.Scroll 6 "three rows a notch"
                        let model, _ = play model [ key KeyCode.WheelDown; key KeyCode.WheelDown; key KeyCode.WheelDown ]
                        Expect.equal model.Scroll 0 "never below the end"
                    }

                    test "settling keeps the scroll within the transcript, and on its rows as it grows" {
                        let _, model = ready ()

                        let small =
                            { model with
                                Width = 24
                                Height = 14
                            }

                        let top = View.settle { small with Scroll = 10_000 }
                        Expect.isGreaterThan top.Scroll 0 "the transcript is longer than the screen"
                        Expect.isLessThan top.Scroll 10_000 "no further than its first row"
                        Expect.equal (screen { top with Scroll = top.Scroll + 1 }) (screen top) "the first row at the top"
                        let down, _ = play top [ key KeyCode.PageDown ]
                        Expect.notEqual (screen (View.settle down)) (screen top) "scrolling down starts at once"
                        let at = min 2 top.Scroll

                        let grown =
                            View.settle
                                { top with
                                    Scroll = at
                                    TranscriptRows = top.TranscriptRows - 2
                                }

                        Expect.equal grown.Scroll (min (at + 2) top.Scroll) "kept on the same rows"
                        let pinned = View.settle { top with Scroll = 0; TranscriptRows = top.TranscriptRows - 2 }
                        Expect.equal pinned.Scroll 0 "at the end, it follows the end"
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
                "start screen"
                [
                    test "a session without turns shows the name in blocks; the first prompt the transcript" {
                        let model = Screens.fresh 80 24
                        Expect.isTrue (View.isStart model) "the start screen"
                        Expect.stringContains (screen model) "█" "the wordmark"
                        let s = Session "01JD2W7Z9Q0000000000000009"
                        let model, _ = play model (s.Start None @ s.Prompt "hello")
                        Expect.isFalse (View.isStart model) "a turn"
                        Expect.stringContains (screen model) "┃ hello" "the transcript"
                    }

                    test "a permission waiting shows the usual screen" {
                        Expect.isFalse (View.isStart (Screens.grant 80 24)) "the card"
                    }

                    test "the wordmark: five rows as wide as it says" {
                        let rows = Wordmark.draw Style.plain Style.bold "fx"
                        Expect.equal rows.Length Wordmark.Height "rows"
                        Expect.equal (rows |> List.map Row.width |> List.max) (Wordmark.width "fx") "width"
                        Expect.equal (Wordmark.width "ab") 9 "two glyphs and a gap"
                    }
                ]

            testList
                "commands"
                [
                    test "a `/` prompt names a command, with what follows it" {
                        match Commands.parse Commands.all "/model anthropic/claude-opus-5-5" with
                        | Some(Ok(command, argument)) ->
                            Expect.equal command.Action CommandAction.Model "the command"
                            Expect.equal argument "anthropic/claude-opus-5-5" "its argument"
                        | other -> failtest $"{other}"

                        Expect.equal (Commands.parse Commands.all "hello") None "not a command"
                        Expect.equal (Commands.parse Commands.all "//path") None "an escaped slash"
                        Expect.equal (Commands.parse Commands.all "/nope") (Some(Error "/nope")) "unknown"
                    }

                    test "the palette's search: letters in order, names first" {
                        Expect.equal
                            (Commands.search "ses" Commands.all |> List.map _.Name |> List.head)
                            "sessions"
                            "sessions"

                        Expect.equal (Commands.search "xyz" Commands.all) [] "nothing"
                        Expect.equal (Commands.score "nw" "new") (Some 2) "apart"
                    }

                    test "Ctrl+P opens the palette; typing filters; Enter runs; Esc closes" {
                        let _, model = ready ()
                        let model, _ = play model [ ctrl 'p' ]
                        Expect.isSome model.Palette "open"
                        let model, _ = play model (typing "det")

                        Expect.equal
                            (Palette.shown model.Palette.Value |> List.map _.Label |> List.head)
                            "/detail"
                            "filtered"

                        let model, cmds = play model [ key KeyCode.Enter ]
                        Expect.isNone model.Palette "closed"
                        Expect.equal model.Verbosity Verbosity.Verbose "ran /detail"
                        Expect.equal cmds [] "nothing sent"
                        let model, _ = play model [ ctrl 'p'; key KeyCode.Escape ]
                        Expect.isNone model.Palette "Esc closes"
                    }

                    test "/model sends an override and shows the model" {
                        let s, model = ready ()
                        let model, cmds = play model (typing "/model openai/gpt-6" @ [ key KeyCode.Enter ])

                        Expect.equal
                            cmds
                            [
                                Cmd.Override(
                                    s.Id,
                                    "tui/run1/model/1",
                                    [ "model.role.main", Fx.Contracts.Json.String "openai/gpt-6" ]
                                )
                            ]
                            "the override"

                        Expect.equal model.Setup.Model (Some "openai/gpt-6") "shown"
                        Expect.isTrue (Editor.isEmpty model.Editor) "the prompt is cleared"
                    }

                    test "/theme with and without a name" {
                        let _, model = ready ()
                        let model, _ = play model (typing "/theme light" @ [ key KeyCode.Enter ])
                        Expect.equal model.Setup.Theme ThemeChoice.Light "set"
                        let model, _ = play model (typing "/theme" @ [ key KeyCode.Enter ])
                        Expect.equal (model.Palette |> Option.map _.Title) (Some "Theme") "a picker"
                        let model, _ = play model (typing "ansi" @ [ key KeyCode.Enter ])
                        Expect.equal model.Setup.Theme ThemeChoice.Ansi16 "picked"
                    }

                    test "/new leaves the session; /sessions picks another" {
                        let _, model = ready ()
                        let model, cmds = play model (typing "/new" @ [ key KeyCode.Enter ])
                        Expect.equal cmds [ Cmd.NewSession ] "a new session"
                        Expect.isNone model.Session "left"
                        Expect.equal model.Timeline Timeline.empty "nothing of the old one"

                        let model, cmds = play model (typing "/sessions" @ [ key KeyCode.Enter ])
                        Expect.equal cmds [ Cmd.ListSessions ] "asked for the list"

                        let sessions: SessionInfo list =
                            [
                                {
                                    Id = "s-old"
                                    Title = Some "billing"
                                    Environment = "env-1"
                                    Workspace = setup.Workspace
                                }
                                {
                                    Id = "s-new"
                                    Title = None
                                    Environment = "env-2"
                                    Workspace = setup.Workspace
                                }
                            ]

                        let model, _ = play model [ Msg.Sessions sessions ]

                        Expect.equal
                            (Palette.shown model.Palette.Value |> List.map _.Label)
                            [ "s-new"; "billing" ]
                            "newest first"

                        let _, cmds = play model (typing "bill" @ [ key KeyCode.Enter ])
                        Expect.equal cmds [ Cmd.OpenSession "s-old" ] "opened"
                    }

                    test "Tab completes a command; an unknown one says so" {
                        let _, model = ready ()
                        let model, _ = play model (typing "/se" @ [ key KeyCode.Tab ])
                        Expect.equal model.Editor.Text "/sessions " "completed"
                        let _, model = ready ()
                        let model, cmds = play model (typing "/zzz" @ [ key KeyCode.Enter ])
                        Expect.equal cmds [] "nothing sent to the agent"
                        Expect.stringContains (screen model) "no command /zzz" "said so"
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
