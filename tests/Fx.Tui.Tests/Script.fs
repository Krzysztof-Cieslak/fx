/// Scripted streams of what a TUI receives: session events made by the session machine itself (so
/// they are what a host records), context events after them, grant notices, on a clock the script
/// moves. Shared with the JIT-vs-native scenarios.
module Fx.Tui.Tests.Script

open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Models
open Fx.Prompt
open Fx.Sessions
open Fx.Store
open Fx.Grants
open Fx.CodeAct
open Fx.Tui

/// 2026-11-25T10:00:00Z.
let start = 1_795_600_800_000_000L

let person = Attribution.create "user:local-os-user:alice" "tui"
let host = Attribution.system "host"

/// A session as a host would record it: commands handled by the session machine, events numbered
/// and stamped; the turn runner's context events placed after the last session event.
[<Sealed>]
type Session(id: string) =
    let stream = Session.stream id
    let mutable state = Session.initial
    let mutable seq = 0L
    let mutable contextSeq = 0L
    let mutable now = start
    let mutable keys = 0

    member _.Id = id
    member _.State = state
    member _.Seq = seq
    member _.Now = now

    /// Moves the clock on.
    member _.Advance(seconds: float) =
        now <- now + int64 (seconds * 1_000_000.0)

    /// Handles a command as the session machine does; the messages a subscriber receives.
    member _.Send(command: Command, ?by: Attribution) : Msg list =
        let by = defaultArg by person

        match Session.handle Questions.kinds state { Command = command; By = by; At = now } with
        | Error rejection -> failwith $"rejected: {rejection}"
        | Ok events ->
            keys <- keys + 1

            events
            |> List.map (fun event ->
                seq <- seq + 1L
                state <- Session.evolve state event

                Msg.Session(
                    Feed.Next
                        {
                            Stream = stream
                            Seq = seq
                            Event = event
                            Meta =
                                {
                                    At = Instant now
                                    By = by
                                    Key = IdempotencyKey $"k{keys}"
                                    Cause = None
                                }
                        }
                ))

    /// A context event the turn records now.
    member _.Context(event: ContextEvent) : Msg =
        contextSeq <- contextSeq + 1L

        Msg.Context
            {
                Stream = ContextLog.stream id
                Seq = contextSeq
                Event = event
                Meta =
                    {
                        At = Instant now
                        By = host
                        Key = IdempotencyKey $"c{contextSeq}"
                        Cause = Some(EventRef.create stream seq)
                    }
            }

    member this.Start(title: string option) =
        this.Send(
            Command.Start
                {
                    Id = id
                    Profile = "p1"
                    Overrides = []
                    Parent = None
                    Title = title
                },
            person
        )

    member this.Prompt(text: string, ?mode: PromptMode) =
        this.Send(Command.Prompt(text, defaultArg mode PromptMode.Steer))

    member this.Running: int =
        match state.Running with
        | Some turn -> turn.Turn
        | None -> failwith "no turn runs"

    member this.Boundary(step: int) =
        this.Send(Command.TakeInputs(this.Running, step), host)

    member this.Reply(step: int, parts: Part list, ?used: ReplyUsage) =
        this.Context(
            ContextEvent.Replied
                {
                    Turn = this.Running
                    Step = step
                    Shown = []
                    Parts = parts
                    Used = used
                }
        )

    member this.Receipt
        (
            step: int,
            call: string,
            name: string,
            text: string,
            ?error: bool,
            ?restarted: KernelRestart,
            ?approvals: Approval list
        ) =
        this.Context(
            ContextEvent.Returned
                {
                    Turn = this.Running
                    Step = step
                    Call = Some call
                    Name = name
                    Pieces =
                        [
                            // A cell in a new kernel: its receipt starts with the note for the model.
                            match restarted with
                            | Some restart -> Piece.Text $"[A new kernel started for this cell ({restart.Reason}).]"
                            | None -> ()
                            Piece.Text text
                        ]
                    IsError = defaultArg error false
                    Restarted = restarted
                    Approvals = defaultArg approvals []
                }
        )

    /// A compaction: the session's history from its first event to `upTo` shown as a summary.
    member this.Collapse(upTo: int64, summary: string) =
        this.Context(
            ContextEvent.Collapsed
                {
                    From = EventRef.create stream 1L
                    To = EventRef.create stream upTo
                    Summary = [ Piece.Quote { Text = summary; Sources = [] } ]
                }
        )

    member this.End(result: TurnResult) =
        this.Send(Command.EndTurn(this.Running, result), host)

    member this.Fail(reason: string) =
        this.Send(Command.FailTurn(this.Running, reason), host)

    member this.Open(request: RequestSpec) =
        this.Send(Command.OpenRequest request, host)

    override _.ToString() = $"Session({id})"

let text (value: string) : Part =
    Part.Text
        {
            Text = value
            State = None
            Sources = []
        }

let call (id: string) (name: string) (arguments: (string * Json) list) : Part =
    Part.ToolCall
        {
            Id = id
            Name = name
            Arguments = Json.serialize (Json.Object arguments)
            State = None
        }

let cell (id: string) (code: string) : Part =
    call id "cell" [ "code", Json.String code ]

/// The agent's question, as CodeAct opens it.
let question (turn: int) (step: int) (text: string) : RequestSpec =
    RequestKind.request
        Questions.kind
        $"q-{turn}-{step}"
        "The agent asks"
        (RequestOrigin.Turn turn)
        Reply.AsInput
        Lifetime.Session
        { Question = text; Reason = "reply" }

/// A decision about a call in doubt, as crash recovery opens it.
let decision (turn: int) : RequestSpec =
    let card: OutcomeUnknown =
        {
            Question = "The host stopped while this cell ran. What happened?"
            Scope = "session"
            Call =
                {
                    Id = "toolu_9"
                    Tool = "cell"
                    Turn = turn
                    Step = 2
                    Index = 1
                    Attempt = 1
                    Cell = Some "t1-s2-1"
                    Intent = Some "run the migration"
                    Action = "// intent: run the migration\nawait fx.shell.run(\"make migrate\")"
                }
            Evidence =
                {
                    Output = "applying 0001\napplying 0002\n"
                    HostCalls = []
                    Changes = None
                    ChangesNote = Some "no snapshots"
                }
            Options = Recovery.options
        }

    RequestKind.request
        Recovery.kind
        "unknown-t1-s2-1"
        "The host stopped during a cell"
        (RequestOrigin.Turn turn)
        Reply.ToOpener
        Lifetime.Turn
        card

/// A grant request of the session, as the grants log announces it.
let grantRequest (seq: int64) (id: string) (session: string option) (summary: string) : Msg =
    Msg.Grant
        {
            Seq = seq
            Event =
                GrantEvent.Requested
                    {
                        Id = RequestId id
                        Key = "k/" + id
                        Action = ActionName.create "shell.exec"
                        Resource = "make deploy"
                        Context =
                            {
                                Session = session
                                Run = None
                                Repo = None
                            }
                        Requester =
                            {
                                Principal = "system"
                                Agent = None
                                Extension = None
                            }
                        Summary = summary
                        // As T127b's guard asks: which turn and call, and the command line.
                        Evidence =
                            [
                                {
                                    Label = "asked by"
                                    Text = "turn 1, call toolu_1 (bash)"
                                }
                                {
                                    Label = "command"
                                    Text = "make deploy"
                                }
                            ]
                        Suggestions = []
                        // The grants log offers the session's scope only for a session's request.
                        Offer =
                            match session with
                            | Some _ -> [ ScopeKind.Once; ScopeKind.Session; ScopeKind.Host ]
                            | None -> [ ScopeKind.Once; ScopeKind.Host ]
                        At = start
                        Expires = None
                    }
        }

let setup: Setup =
    {
        Product = "fx"
        Binary = "fx"
        Workspace = "/home/alice/src/billing"
        Model = Some "anthropic/claude-sonnet-5-5"
        Trusted = true
        Warnings = []
        Keys = "tui/run1"
        Terminal = Terminal.basic
        Theme = ThemeChoice.Auto
    }

/// An environment the host left unsandboxed (the default, T137): `sandbox: off`, no banner.
let env: EnvInfo =
    {
        Id = "env-1"
        Name = "default"
        Provider = "local"
        Phase = "ready"
        Warnings = []
        Sandbox = Some "none"
        Branch = Some "main"
        Variables = []
    }

/// An environment bubblewrap confines: no banner.
let confined: EnvInfo = { env with Sandbox = Some "bubblewrap" }

/// An environment the host asked to sandbox (`auto`) where no driver can run: its warning is the
/// banner.
let unconfined: EnvInfo =
    { env with
        Warnings =
            [
                "runs unsandboxed: the sandbox the host asked for cannot run here (Windows has no sandbox driver yet (AppContainer: T511)); commands can read and change everything the user can"
            ]
    }

/// Folds messages into a model, keeping the commands it asked for.
let play (model: Model) (msgs: Msg list) : Model * Cmd list =
    msgs
    |> List.fold
        (fun (model, cmds) msg ->
            let next, more = App.update msg model
            next, cmds @ more)
        (model, [])

let key (code: KeyCode) : Msg = Msg.Key(Keys.key code)
let ctrl (c: char) : Msg = Msg.Key(Keys.ctrl c)

/// Typing text, a key per character.
let typing (text: string) : Msg list =
    Text.clusters text |> List.map (fun c -> Msg.Key(Keys.char c))

/// A session with one finished turn: a prompt, a read, an edit, a failing and a passing command, a
/// cell, and a result with evidence.
let finishedTurn () : Session * Msg list =
    let s = Session "01JD2W7Z9Q0000000000000000"

    let msgs =
        [
            yield! s.Start(Some "billing")
            yield! s.Prompt "replace deprecated Http.retry with RetryPolicy in billing"
            yield! s.Boundary 1
            s.Advance 2.0

            yield
                s.Reply(
                    1,
                    [
                        text "I'll look at the call sites first."
                        call "toolu_1" "read" [ "path", Json.String "src/Billing/Client.fs" ]
                        call
                            "toolu_2"
                            "edit"
                            [
                                "path", Json.String "src/Billing/Client.fs"
                                "old", Json.String "a"
                                "new", Json.String "b"
                            ]
                        call "toolu_3" "bash" [ "command", Json.String "dotnet test tests/Billing" ]
                    ]
                )
            s.Advance 0.2
            yield s.Receipt(1, "toolu_1", "read", "1 open Billing")
            s.Advance 0.3
            yield s.Receipt(1, "toolu_2", "edit", "changed 3 lines")
            s.Advance 48.0
            yield s.Receipt(1, "toolu_3", "bash", "exit 1\nFailed: 2, Passed: 212", true)
            yield! s.Boundary 2
            s.Advance 3.0
            yield
                s.Reply(
                    2,
                    [
                        cell
                            "toolu_4"
                            "// intent: rerun the tests\nconst r = await fx.shell.run(\"dotnet test tests/Billing\")\nr.exitCode"
                    ]
                )
            s.Advance 46.0
            yield s.Receipt(2, "toolu_4", "cell", "0")

            yield!
                s.End(
                    TurnResult.Done(
                        "Migrated 37 call sites to RetryPolicy.",
                        [
                            {
                                Kind = "command"
                                Summary = "dotnet test tests/Billing → 214 passed"
                                Detail = Json.Null
                            }
                        ],
                        Some "dotnet test tests/Billing"
                    )
                )
        ]

    s, msgs

/// What a model call used, as CodeAct records it.
let used (input: int) (output: int) (cost: decimal) : ReplyUsage =
    {
        Model =
            {
                Provider = "anthropic"
                Model = "claude-opus-5-5"
            }
        Tokens =
            { Usage.zero with
                Input = input
                Output = output
            }
        Context = Some 1_000_000
        Cost = Some cost
    }
