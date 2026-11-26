namespace Fx.Tui

open System
open Fx.Contracts
open Fx.Machines
open Fx.Machines.Host
open Fx.Prompt
open Fx.Sessions
open Fx.Store
open Fx.Grants

/// What the TUI knows before it connects: where it runs and what it may show.
type Setup =
    {
        /// The product's name (`Fx.Product.Name`), for the header.
        Product: string
        /// The binary's name, for hints such as `fx trust`.
        Binary: string
        Workspace: string
        /// The main model, when the client knows it (embedded mode reads the host's resolution).
        Model: string option
        /// Whether the project is trusted: if not, its config is not read (spec §18).
        Trusted: bool
        /// What a person should know about the host's setup (an extension that failed to load, …).
        Warnings: string list
        /// The prefix of this run's idempotency keys: unique per run, so a key is one intent.
        Keys: string
        /// What the terminal draws (its colour depth, its background).
        Terminal: Terminal
        /// The theme asked for (`tui.theme`).
        Theme: ThemeChoice
    }

    override this.ToString() = $"setup {this.Workspace}"

/// The session the TUI shows.
type SessionInfo =
    {
        Id: string
        Title: string option
        Environment: string
        /// Where it works.
        Workspace: string
    }

    override this.ToString() = this.Id

/// The session's environment, as `env/get` reports it.
type EnvInfo =
    {
        Id: string
        Name: string
        Provider: string
        Phase: string
        /// What a person must be told while it exists: that it runs unsandboxed, …
        Warnings: string list
        /// The sandbox driver that confines its processes, once it is ready.
        Sandbox: string option
    }

    override this.ToString() = $"{this.Name} ({this.Provider})"

/// How much of each turn the transcript shows.
[<RequireQualifiedAccess>]
type Verbosity =
    /// The prompts, activity lines and result cards.
    | Quiet
    /// Also what the model wrote, and errors under the actions that failed.
    | Normal
    /// Also every cell's code and what each action returned.
    | Verbose

    override this.ToString() =
        match this with
        | Quiet -> "quiet"
        | Normal -> "normal"
        | Verbose -> "verbose"

[<RequireQualifiedAccess>]
type Phase =
    /// Connecting, creating or resuming the session (creating its environment can ask for grants).
    | Starting of what: string
    | Ready
    /// The session could not be started, or the connection ended.
    | Stopped of reason: string

    override this.ToString() =
        match this with
        | Starting what -> "starting: " + what
        | Ready -> "ready"
        | Stopped reason -> "stopped: " + reason

/// A transient line under the transcript.
type Notice =
    {
        Text: string
        IsError: bool
        /// When it goes away (microseconds since the epoch).
        Until: int64
    }

    override this.ToString() = this.Text

/// What the person is asked: a permission, a decision, a question. The first open one has the
/// keyboard (a question shares it with the editor, whose text is the answer).
[<RequireQualifiedAccess>]
type Card =
    | Grant of GrantRequest
    /// What happened to a call that was running when the host stopped (`decision.outcomeUnknown`).
    | Decision of
        PendingRequest *
        question: string *
        action: string *
        output: string *
        options: (string * string * string) list
    | Confirm of PendingRequest * message: string
    | Choice of PendingRequest * question: string * options: string list
    /// The agent's question (`agent.question`), answered with text.
    | Question of PendingRequest * question: string

    member this.Id: string =
        match this with
        | Grant request -> "grant/" + request.Id.Value
        | Decision(request, _, _, _, _)
        | Confirm(request, _)
        | Choice(request, _, _)
        | Question(request, _) -> request.Request.Id

    override this.ToString() = this.Id

/// What arrives in the TUI's loop.
[<RequireQualifiedAccess>]
type Msg =
    | Key of Key
    | Resized of width: int * height: int
    /// The time now (microseconds since the epoch): durations and the spinner move on.
    | Tick of now: int64
    | Starting of what: string
    | Started of SessionInfo
    | StartFailed of reason: string
    | Env of EnvInfo
    | Session of Feed<SessionState, Event>
    | Context of Recorded<ContextEvent>
    /// What the model is writing in a running turn (not recorded yet): text its reply adds, or a
    /// tool call it started; `fresh` starts a new model call.
    | Streaming of turn: int * fresh: bool * text: string * tool: string option
    | GrantsPending of PendingList
    | Grant of GrantNotice
    /// A command finished: what it was, and why it failed if it did.
    | Sent of what: string * Result<unit, string>
    /// An answer was refused or could not be sent: the card can be answered again.
    | AnswerFailed of card: string * reason: string
    | Disconnected of reason: string

    override this.ToString() =
        match this with
        | Key key -> "key " + string key
        | Resized(w, h) -> $"resized {w}x{h}"
        | Tick now -> "tick " + string now
        | Starting what -> "starting " + what
        | Started info -> "started " + string info
        | StartFailed reason -> "start failed: " + reason
        | Env env -> "env " + string env
        | Session item -> "session " + string item
        | Context recorded -> "context " + string recorded.Seq
        | Streaming(turn, _, text, _) -> $"streaming turn {turn} ({text.Length} chars)"
        | GrantsPending list -> string list
        | Grant notice -> "grant " + string notice
        | Sent(what, Ok()) -> what + " sent"
        | Sent(what, Error e) -> what + " failed: " + e
        | AnswerFailed(card, reason) -> card + " not answered: " + reason
        | Disconnected reason -> "disconnected: " + reason

/// What the TUI asks of the host, as data: the shell sends it over the client protocol.
[<RequireQualifiedAccess>]
type Cmd =
    | Prompt of session: string * key: string * text: string * mode: PromptMode
    | Interrupt of session: string * key: string
    | Answer of session: string * key: string * request: string * answer: Json
    | AnswerGrant of RequestId * GrantAnswer
    /// Draw everything again (the screen may have been changed by someone else).
    | Redraw
    | Quit

    override this.ToString() =
        match this with
        | Prompt(_, key, _, mode) -> $"{mode} ({key})"
        | Interrupt(_, key) -> $"interrupt ({key})"
        | Answer(_, key, request, _) -> $"answer {request} ({key})"
        | AnswerGrant(request, answer) -> $"grant {request.Value}: {answer}"
        | Redraw -> "redraw"
        | Quit -> "quit"

/// An answer the person chose on a card.
[<RequireQualifiedAccess>]
type CardAnswer =
    | Allow of ScopeChoice
    | Deny
    | Option of id: string
    | Yes
    | No
    | Pick of option: string
    | Text of string

    override this.ToString() =
        match this with
        | Allow scope -> "allow " + string scope
        | Deny -> "deny"
        | Option id -> "option " + id
        | Yes -> "yes"
        | No -> "no"
        | Pick option -> "pick " + option
        | Text text -> $"text ({text.Length} chars)"

/// A reply being written: its text so far, and the last tool call it started.
type Writing =
    {
        Turn: int
        Text: string
        Tool: string option
    }

    override this.ToString() =
        $"writing turn {this.Turn} ({this.Text.Length} chars)"

type Model =
    {
        Setup: Setup
        Width: int
        Height: int
        Now: int64
        Phase: Phase
        Session: SessionInfo option
        Env: EnvInfo option
        Timeline: Timeline
        /// Pending grant requests, oldest first (cards show this session's and the host's).
        Grants: GrantRequest list
        GrantsSeq: int64
        Streaming: Writing option
        Editor: Editor
        /// While going through earlier prompts: which one (from the newest) and the text being typed.
        History: (int * string) option
        Verbosity: Verbosity
        /// Rows the transcript is scrolled up from its end.
        Scroll: int
        Notice: Notice option
        /// When Ctrl+C was pressed with nothing to clear or interrupt: a second press soon exits.
        ExitArmed: int64 option
        /// How many commands this run has sent: the next key.
        Count: int
        /// Cards whose answers are being sent.
        Answering: Set<string>
        /// The option selected in the first card.
        Selected: int
        Quitting: bool
    }

    override this.ToString() =
        $"model ({this.Phase}, {this.Timeline})"

[<RequireQualifiedAccess>]
module App =
    /// The request kinds the TUI answers (`initialize`'s `answers`).
    let answers: string list =
        [ "confirm"; "choice"; "agent.question"; "decision.outcomeUnknown" ]

    let private second = 1_000_000L

    let init (setup: Setup) (width: int) (height: int) (now: int64) : Model =
        {
            Setup = setup
            Width = max 1 width
            Height = max 1 height
            Now = now
            Phase = Phase.Starting "connecting"
            Session = None
            Env = None
            Timeline = Timeline.empty
            Grants = []
            GrantsSeq = 0L
            Streaming = None
            Editor = Editor.empty
            History = None
            Verbosity = Verbosity.Normal
            Scroll = 0
            Notice = None
            ExitArmed = None
            Count = 0
            Answering = Set.empty
            Selected = 0
            Quitting = false
        }

    let private notice (text: string) (model: Model) =
        { model with
            Notice =
                Some
                    {
                        Text = text
                        IsError = false
                        Until = model.Now + 4L * second
                    }
        }

    let private error (text: string) (model: Model) =
        { model with
            Notice =
                Some
                    {
                        Text = text
                        IsError = true
                        Until = model.Now + 8L * second
                    }
        }

    // ---- cards ----------------------------------------------------------------------------------

    let private field (name: string) (json: Json) : string =
        match Json.tryField name json with
        | Some(Json.String text) -> text
        | _ -> ""

    /// The card of an open request the TUI can answer.
    let private requestCard (request: PendingRequest) : Card option =
        let payload = request.Request.Payload

        match request.Request.Kind with
        | "agent.question" ->
            let question = field "question" payload
            Some(Card.Question(request, (if question = "" then request.Request.Title else question)))
        | "decision.outcomeUnknown" ->
            let options =
                match Json.tryField "options" payload with
                | Some(Json.Array items) ->
                    items
                    |> List.map (fun item -> field "id" item, field "label" item, field "detail" item)
                    |> List.filter (fun (id, _, _) -> id <> "")
                | _ -> []

            let action =
                match Json.tryField "call" payload with
                | Some call ->
                    let action = field "action" call
                    if action = "" then field "intent" call else action
                | None -> ""

            let output =
                match Json.tryField "evidence" payload with
                | Some evidence -> field "output" evidence
                | None -> ""

            Some(Card.Decision(request, field "question" payload, action, output, options))
        | "confirm" ->
            match Codec.decode RequestKinds.confirm.Payload payload with
            | Ok confirmation -> Some(Card.Confirm(request, confirmation.Message))
            | Error _ -> None
        | "choice" ->
            match Codec.decode RequestKinds.choice.Payload payload with
            | Ok choice -> Some(Card.Choice(request, choice.Question, choice.Options))
            | Error _ -> None
        | _ -> None

    /// Whether a pending grant request is this TUI's to show: the session's, or the host's (a
    /// session's environment asks to install before the session exists).
    let relevant (model: Model) (request: GrantRequest) : bool =
        match request.Context.Session, model.Session with
        | None, _ -> true
        | Some session, Some current -> session = current.Id
        | Some _, None -> false

    /// What the person is asked now, in order: permissions first (a tool call waits on them), then
    /// the session's open requests the TUI can answer.
    let cards (model: Model) : Card list =
        (model.Grants |> List.filter (relevant model) |> List.map Card.Grant)
        @ (model.Timeline.State.Requests
           |> List.filter (fun r -> r.Capabilities |> List.exists (fun c -> List.contains c answers))
           |> List.choose requestCard)

    /// The open requests nobody here can answer (another client's kinds).
    let others (model: Model) : PendingRequest list =
        model.Timeline.State.Requests
        |> List.filter (fun r -> not (r.Capabilities |> List.exists (fun c -> List.contains c answers)))

    /// The keys that answer a card with choices, and what each answers.
    let choices (model: Model) (card: Card) : (string * string * CardAnswer) list =
        ignore model

        match card with
        | Card.Grant request ->
            // What the request offers (always once; the grants log narrowed the rest to its action
            // and context), with the keys `fx run` uses at the terminal (`GrantPrompts`).
            let offered (kind: ScopeKind) = List.contains kind request.Offer

            [
                "y", "once", CardAnswer.Allow ScopeChoice.Once
                if offered ScopeKind.Session then
                    "s", "this session", CardAnswer.Allow ScopeChoice.Session
                if offered ScopeKind.Repo then
                    "r", "this repo", CardAnswer.Allow(ScopeChoice.Repo None)
                if offered ScopeKind.Host then
                    "a", "always on this host", CardAnswer.Allow ScopeChoice.Host
                "n", "deny", CardAnswer.Deny
            ]
        | Card.Decision(_, _, _, _, options) ->
            let firsts =
                options
                |> List.map (fun (id, _, _) ->
                    if id = "" then
                        ""
                    else
                        id.Substring(0, 1).ToLowerInvariant())

            options
            |> List.mapi (fun i (id, label, _) ->
                let first = firsts[i]

                let key =
                    if first <> "" && (firsts |> List.filter ((=) first) |> List.length) = 1 then
                        first
                    else
                        string (i + 1)

                key, (if label = "" then id else label), CardAnswer.Option id)
        | Card.Confirm _ -> [ "y", "yes", CardAnswer.Yes; "n", "no", CardAnswer.No ]
        | Card.Choice(_, _, options) ->
            options
            |> List.mapi (fun i option -> string (i + 1), option, CardAnswer.Pick option)
        | Card.Question _ -> []

    // ---- sending --------------------------------------------------------------------------------

    let private nextKey (what: string) (model: Model) : string * Model =
        let key = $"{model.Setup.Keys}/{what}/{model.Count + 1}"
        key, { model with Count = model.Count + 1 }

    let private answer (card: Card) (choice: CardAnswer) (model: Model) : Model * Cmd list =
        if model.Answering.Contains card.Id then
            model, []
        else
            let model =
                { model with
                    Answering = model.Answering.Add card.Id
                    Selected = 0
                }

            match card, choice, model.Session with
            | Card.Grant request, _, _ ->
                let grant: GrantAnswer =
                    match choice with
                    | CardAnswer.Allow scope ->
                        {
                            Effect = GrantEffect.Allow
                            Scope = scope
                            Resource = None
                            Expires = None
                            Reason = None
                        }
                    | _ ->
                        {
                            Effect = GrantEffect.Deny
                            Scope = ScopeChoice.Once
                            Resource = None
                            Expires = None
                            Reason = Some "denied in the TUI"
                        }

                model, [ Cmd.AnswerGrant(request.Id, grant) ]
            | Card.Decision(request, _, _, _, _), CardAnswer.Option id, Some session ->
                let json = Json.Object [ "option", Json.String id ]

                model,
                [
                    Cmd.Answer(session.Id, $"{model.Setup.Keys}/answer/{request.Request.Id}", request.Request.Id, json)
                ]
            | Card.Confirm(request, _), (CardAnswer.Yes | CardAnswer.No), Some session ->
                let json = Json.Bool(choice = CardAnswer.Yes)

                model,
                [
                    Cmd.Answer(session.Id, $"{model.Setup.Keys}/answer/{request.Request.Id}", request.Request.Id, json)
                ]
            | Card.Choice(request, _, _), CardAnswer.Pick option, Some session ->
                model,
                [
                    Cmd.Answer(
                        session.Id,
                        $"{model.Setup.Keys}/answer/{request.Request.Id}",
                        request.Request.Id,
                        Json.String option
                    )
                ]
            | Card.Question(request, _), CardAnswer.Text text, Some session ->
                model,
                [
                    Cmd.Answer(
                        session.Id,
                        $"{model.Setup.Keys}/answer/{request.Request.Id}",
                        request.Request.Id,
                        Json.String text
                    )
                ]
            | _ ->
                { model with
                    Answering = model.Answering.Remove card.Id
                },
                []

    let private running (model: Model) = model.Timeline.State.Running.IsSome

    let private interrupt (model: Model) : Model * Cmd list =
        match model.Session, model.Timeline.State.Running with
        | Some session, Some turn ->
            notice "interrupting…" model, [ Cmd.Interrupt(session.Id, $"{model.Setup.Keys}/interrupt/{turn.Turn}") ]
        | _ -> model, []

    let private send (mode: PromptMode) (model: Model) : Model * Cmd list =
        let text = model.Editor.Text.Trim()

        match model.Session with
        | _ when text = "" -> model, []
        | None -> error "the session has not started yet" model, []
        | Some session ->
            let key, model = nextKey "prompt" model

            { model with
                Editor = Editor.empty
                History = None
                Scroll = 0
            },
            [ Cmd.Prompt(session.Id, key, text, mode) ]

    // ---- keys -----------------------------------------------------------------------------------

    let private history (delta: int) (model: Model) : Model =
        let prompts = Timeline.prompts model.Timeline |> List.rev

        match model.History, delta with
        | None, d when d < 0 && not prompts.IsEmpty ->
            { model with
                History = Some(0, model.Editor.Text)
                Editor = Editor.ofText prompts[0]
            }
        | Some(index, draft), d ->
            let next = index - d

            if next < 0 then
                { model with
                    History = None
                    Editor = Editor.ofText draft
                }
            elif next < prompts.Length then
                { model with
                    History = Some(next, draft)
                    Editor = Editor.ofText prompts[next]
                }
            else
                model
        | _ -> model

    let private transcriptPage (model: Model) = max 1 (model.Height / 2)

    let private edit (key: Key) (model: Model) : Model * Cmd list =
        let editor = model.Editor
        let set (editor: Editor) = { model with Editor = editor }, []

        match key.Code, key.Ctrl, key.Alt with
        | KeyCode.Enter, false, true
        | KeyCode.Enter, true, false -> set (Editor.newline editor)
        | KeyCode.Enter, false, false when key.Shift -> set (Editor.newline editor)
        | KeyCode.Char "j", true, false -> set (Editor.newline editor)
        | KeyCode.Paste text, _, _ -> set (Editor.insert text editor)
        | KeyCode.Backspace, false, false
        | KeyCode.Char "h", true, false -> set (Editor.backspace editor)
        | KeyCode.Backspace, _, true
        | KeyCode.Char "w", true, false -> set (Editor.deleteWord editor)
        | KeyCode.Delete, false, false -> set (Editor.delete editor)
        | KeyCode.Left, false, false
        | KeyCode.Char "b", true, false -> set (Editor.left editor)
        | KeyCode.Right, false, false
        | KeyCode.Char "f", true, false -> set (Editor.right editor)
        | KeyCode.Left, true, _
        | KeyCode.Left, _, true
        | KeyCode.Char "b", false, true -> set (Editor.wordLeft editor)
        | KeyCode.Right, true, _
        | KeyCode.Right, _, true
        | KeyCode.Char "f", false, true -> set (Editor.wordRight editor)
        | KeyCode.Home, _, _
        | KeyCode.Char "a", true, false -> set (Editor.home editor)
        | KeyCode.End, _, _
        | KeyCode.Char "e", true, false -> set (Editor.``end`` editor)
        | KeyCode.Char "u", true, false -> set (Editor.killToStart editor)
        | KeyCode.Char "k", true, false -> set (Editor.killToEnd editor)
        | KeyCode.Up, false, false ->
            match Editor.line editor with
            | 0, _ -> history -1 model, []
            | _ -> set (Editor.up editor)
        | KeyCode.Down, false, false ->
            match Editor.line editor with
            | line, count when line = count - 1 -> history 1 model, []
            | _ -> set (Editor.down editor)
        | KeyCode.Char c, false, false -> set (Editor.insert c editor)
        | _ -> model, []

    /// Keys every screen has: exit, interrupt, scrolling, detail, redraw.
    let private common (key: Key) (model: Model) : (Model * Cmd list) option =
        match key.Code, key.Ctrl, key.Alt with
        | KeyCode.Char "c", true, false ->
            match model.ExitArmed with
            | Some at when model.Now - at <= 3L * second -> Some({ model with Quitting = true }, [ Cmd.Quit ])
            | _ ->
                let armed =
                    { model with
                        ExitArmed = Some model.Now
                    }

                if not (Editor.isEmpty model.Editor) then
                    Some(
                        notice
                            "cleared · Ctrl+C again to exit"
                            { armed with
                                Editor = Editor.empty
                                History = None
                            },
                        []
                    )
                elif running model then
                    let model, cmds = interrupt armed
                    Some(notice "interrupting… · Ctrl+C again to exit" model, cmds)
                else
                    Some(notice "Ctrl+C again to exit" armed, [])
        | KeyCode.Char "d", true, false when Editor.isEmpty model.Editor ->
            Some({ model with Quitting = true }, [ Cmd.Quit ])
        | KeyCode.Escape, false, false ->
            if running model then
                Some(interrupt model)
            else
                Some({ model with Notice = None; Scroll = 0 }, [])
        | KeyCode.PageUp, _, _ ->
            Some(
                { model with
                    Scroll = model.Scroll + transcriptPage model
                },
                []
            )
        | KeyCode.PageDown, _, _ ->
            Some(
                { model with
                    Scroll = max 0 (model.Scroll - transcriptPage model)
                },
                []
            )
        | KeyCode.Char "o", true, false ->
            let verbosity =
                match model.Verbosity with
                | Verbosity.Quiet -> Verbosity.Normal
                | Verbosity.Normal -> Verbosity.Verbose
                | Verbosity.Verbose -> Verbosity.Quiet

            Some(notice $"detail: {verbosity}" { model with Verbosity = verbosity }, [])
        | KeyCode.Char "l", true, false -> Some(model, [ Cmd.Redraw ])
        | _ -> None

    let private onKey (key: Key) (model: Model) : Model * Cmd list =
        match common key model with
        | Some result -> result
        | None ->
            let model = { model with ExitArmed = None }

            match cards model with
            | (Card.Question _ as card) :: _ ->
                match key.Code, key.Ctrl, key.Alt, key.Shift with
                | KeyCode.Enter, false, false, false ->
                    let text = model.Editor.Text.Trim()

                    if text = "" then
                        model, []
                    else
                        let model, cmds = answer card (CardAnswer.Text text) model
                        { model with Editor = Editor.empty }, cmds
                | _ -> edit key model
            | card :: _ ->
                let options = choices model card
                let count = options.Length

                let previous () =
                    { model with
                        Selected = (model.Selected + count - 1) % max 1 count
                    },
                    []

                match key.Code, key.Ctrl, key.Alt with
                | KeyCode.Tab, false, false when key.Shift -> previous ()
                | KeyCode.Left, false, false
                | KeyCode.Up, false, false -> previous ()
                | KeyCode.Right, false, false
                | KeyCode.Down, false, false
                | KeyCode.Tab, false, false ->
                    { model with
                        Selected = (model.Selected + 1) % max 1 count
                    },
                    []
                | KeyCode.Enter, false, false when count > 0 ->
                    let _, _, choice = options[min model.Selected (count - 1)]
                    answer card choice model
                | KeyCode.Char c, false, false ->
                    match options |> List.tryFind (fun (k, _, _) -> k = c.ToLowerInvariant()) with
                    | Some(_, _, choice) -> answer card choice model
                    | None -> model, []
                | _ -> model, []
            | [] ->
                match key.Code, key.Ctrl, key.Alt, key.Shift with
                | KeyCode.Enter, false, false, false -> send PromptMode.Steer model
                | KeyCode.Tab, false, false, false -> send PromptMode.Queue model
                | _ -> edit key model

    // ---- update ---------------------------------------------------------------------------------

    let private grant (notice: GrantNotice) (model: Model) : Model =
        if notice.Seq <= model.GrantsSeq then
            model
        else
            let without (id: RequestId) =
                model.Grants |> List.filter (fun r -> r.Id <> id)

            let grants, settled =
                match notice.Event with
                | GrantEvent.Requested request -> model.Grants @ [ request ], None
                | GrantEvent.Granted granted ->
                    match granted.Origin with
                    | GrantOrigin.Answer id -> without id, Some id
                    | GrantOrigin.Direct _ -> model.Grants, None
                | GrantEvent.Denied denial -> without denial.Request, Some denial.Request
                | GrantEvent.Closed closure -> without closure.Request, Some closure.Request
                | _ -> model.Grants, None

            { model with
                Grants = grants
                GrantsSeq = notice.Seq
                Answering =
                    match settled with
                    | Some id -> model.Answering.Remove("grant/" + id.Value)
                    | None -> model.Answering
            }

    let update (msg: Msg) (model: Model) : Model * Cmd list =
        match msg with
        | Msg.Key key -> onKey key model
        | Msg.Resized(width, height) ->
            { model with
                Width = max 1 width
                Height = max 1 height
            },
            []
        | Msg.Tick now ->
            let model = { model with Now = max model.Now now }

            let model =
                match model.Notice with
                | Some n when n.Until <= model.Now -> { model with Notice = None }
                | _ -> model

            let model =
                match model.ExitArmed with
                | Some at when model.Now - at > 3L * second -> { model with ExitArmed = None }
                | _ -> model

            model, []
        | Msg.Starting what ->
            { model with
                Phase = Phase.Starting what
            },
            []
        | Msg.Started info ->
            { model with
                Session = Some info
                Phase = Phase.Ready
            },
            []
        | Msg.StartFailed reason ->
            { model with
                Phase = Phase.Stopped reason
            },
            []
        | Msg.Env env -> { model with Env = Some env }, []
        | Msg.Session item ->
            let timeline = Timeline.session item model.Timeline

            let streaming =
                match model.Streaming, timeline.State.Running with
                | Some writing, Some running when running.Turn = writing.Turn -> model.Streaming
                | _ -> None

            let open' = timeline.State.Requests |> List.map _.Request.Id |> Set.ofList

            { model with
                Timeline = timeline
                Streaming = streaming
                Answering =
                    model.Answering
                    |> Set.filter (fun id -> id.StartsWith "grant/" || open'.Contains id)
            },
            []
        | Msg.Context recorded ->
            let streaming =
                match recorded.Event with
                // What streamed was the reply, or the summary a compaction recorded.
                | ContextEvent.Replied _
                | ContextEvent.Collapsed _ -> None
                | _ -> model.Streaming

            { model with
                Timeline = Timeline.context recorded model.Timeline
                Streaming = streaming
            },
            []
        | Msg.Streaming(turn, fresh, text, tool) ->
            let writing =
                match model.Streaming with
                | Some writing when writing.Turn = turn && not fresh ->
                    { writing with
                        Text = writing.Text + text
                        Tool = tool |> Option.orElse writing.Tool
                    }
                | _ ->
                    {
                        Turn = turn
                        Text = text
                        Tool = tool
                    }

            { model with Streaming = Some writing }, []
        | Msg.GrantsPending pending ->
            { model with
                Grants = pending.Requests
                GrantsSeq = pending.Seq
            },
            []
        | Msg.Grant n -> grant n model, []
        | Msg.Sent(_, Ok()) -> model, []
        | Msg.Sent(what, Error reason) -> error $"{what}: {reason}" model, []
        | Msg.AnswerFailed(card, reason) ->
            error
                $"the answer was not taken: {reason}"
                { model with
                    Answering = model.Answering.Remove card
                },
            []
        | Msg.Disconnected reason ->
            error
                $"disconnected: {reason}"
                { model with
                    Phase = Phase.Stopped reason
                },
            []
