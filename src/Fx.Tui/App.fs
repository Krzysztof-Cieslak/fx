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
        /// What a person must be told while it exists: that it runs unsandboxed although the host
        /// asked for a sandbox, …
        Warnings: string list
        /// The sandbox driver of its processes, once it is ready (`none`: off, the default).
        Sandbox: string option
        /// The branch its workspace has checked out.
        Branch: string option
        /// The names of the variables its commands got (never their values).
        Variables: string list
    }

    override this.ToString() = $"{this.Name} ({this.Provider})"

/// How the grants guard decides the session's commands (`session/approvals`; docs/modules/codeact.md,
/// "Approval modes").
type ApprovalsInfo =
    {
        /// `ask`, `auto` (auto approvals run commands written plainly in the workspace), `deny` or
        /// `off`.
        Mode: string
        /// The host's `codeact.grants.mode` for the session.
        Configured: string
        /// The sandbox driver that confines the session's commands; `None`: they run with the user's
        /// rights (the default).
        Sandbox: string option
        /// Why auto approvals are on or off.
        Reason: string
    }

    override this.ToString() = "approvals " + this.Mode

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
    /// The host's sessions (`session/list`), for the session picker.
    | Sessions of SessionInfo list
    /// How the session's commands are decided now (`session/approvals`), after a switch if `switched`.
    | Approvals of ApprovalsInfo * switched: bool

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
        | Sessions sessions -> $"sessions ({sessions.Length})"
        | Approvals(info, _) -> string info

/// What the TUI asks of the host, as data: the shell sends it over the client protocol.
[<RequireQualifiedAccess>]
type Cmd =
    | Prompt of session: string * key: string * text: string * mode: PromptMode
    | Interrupt of session: string * key: string
    | Answer of session: string * key: string * request: string * answer: Json
    | AnswerGrant of RequestId * GrantAnswer
    /// Variant overrides of the session (`session/overrides`): slots and their values.
    | Override of session: string * key: string * set: (string * Json) list
    /// Leave the session shown for a new one in this workspace.
    | NewSession
    /// Leave the session shown for this one.
    | OpenSession of id: string
    /// Ask for the host's sessions (they arrive as `Msg.Sessions`).
    | ListSessions
    /// Ask how the session's commands are decided (it arrives as `Msg.Approvals`).
    | Approvals of session: string
    /// A person turns auto approvals on or off for the session (`session/setApprovals`).
    | SetApprovals of session: string * auto: bool
    /// Draw everything again (the screen may have been changed by someone else).
    | Redraw
    | Quit

    override this.ToString() =
        match this with
        | Prompt(_, key, _, mode) -> $"{mode} ({key})"
        | Override(_, key, set) -> $"override {set.Length} ({key})"
        | NewSession -> "new session"
        | OpenSession id -> "open " + id
        | ListSessions -> "list sessions"
        | Approvals session -> "approvals of " + session
        | SetApprovals(session, auto) -> $"auto approvals {auto} for {session}"
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

/// What the palette offers.
[<RequireQualifiedAccess>]
type PaletteItem =
    | Command of TuiCommand
    | Session of SessionInfo
    /// A value for a command that takes one (`/theme dark`).
    | Value of command: TuiCommand * value: string

    member this.Label: string =
        match this with
        | Command command -> "/" + command.Name
        | Session session -> session.Title |> Option.defaultValue session.Id
        | Value(_, value) -> value

    member this.Detail: string =
        match this with
        | Command command -> command.Title
        | Session session -> session.Id
        | Value(command, _) -> "/" + command.Name

    override this.ToString() = this.Label

/// The command palette (`Ctrl+P`), or a picker one of its commands opened (sessions, themes).
type Palette =
    {
        Title: string
        Query: string
        /// The item selected among those the query shows.
        Selected: int
        Items: PaletteItem list
        /// Still waiting for its items (the host's sessions).
        Loading: bool
    }

    override this.ToString() =
        $"palette {this.Title} ({this.Items.Length})"

[<RequireQualifiedAccess>]
module Palette =
    let create (title: string) (items: PaletteItem list) : Palette =
        {
            Title = title
            Query = ""
            Selected = 0
            Items = items
            Loading = false
        }

    /// The items the query shows, best first.
    let shown (palette: Palette) : PaletteItem list =
        palette.Items
        |> List.choose (fun item ->
            match Commands.score palette.Query item.Label, Commands.score palette.Query item.Detail with
            | Some label, Some detail -> Some(item, max (label * 2) detail)
            | Some label, None -> Some(item, label * 2)
            | None, Some detail -> Some(item, detail)
            | None, None -> None)
        |> List.sortByDescending snd
        |> List.map fst

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
        /// How the person chose to show single items, over the detail (kept until the session shown
        /// changes).
        Folds: Map<ItemId, Fold>
        /// The item selected while the person selects items (`Shift+↑`).
        Selection: ItemId option
        /// The item to bring into view when the model is next settled (`View.settle`): the selection
        /// moved, or an item was expanded or collapsed.
        Reveal: ItemId option
        /// The item each row of the screen showed when the model was last settled (rows from the
        /// screen's top; the transcript's alone), for clicks.
        ItemsAt: Map<int, ItemId>
        /// The columns those rows had (the main column's: right of it is the sidebar).
        ItemsWidth: int
        /// Rows the transcript is scrolled up from its end (0: at the end, following new output).
        Scroll: int
        /// The transcript's rows when the model was last settled (`View.settle`): a scrolled view
        /// stays on its rows as the transcript grows.
        TranscriptRows: int
        /// The rows the transcript had on the screen when the model was last settled: a page.
        TranscriptRoom: int
        Notice: Notice option
        /// When Ctrl+C was pressed with nothing to clear or interrupt: a second press soon exits.
        ExitArmed: int64 option
        /// How many commands this run has sent: the next key.
        Count: int
        /// Cards whose answers are being sent.
        Answering: Set<string>
        /// The command palette or a picker, while it is open.
        Palette: Palette option
        /// The option selected in the first card.
        Selected: int
        /// How the session's commands are decided, once the host said.
        Approvals: ApprovalsInfo option
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
            Folds = Map.empty
            Selection = None
            Reveal = None
            ItemsAt = Map.empty
            ItemsWidth = 0
            Scroll = 0
            TranscriptRows = 0
            TranscriptRoom = 0
            Notice = None
            ExitArmed = None
            Count = 0
            Answering = Set.empty
            Selected = 0
            Approvals = None
            Quitting = false
            Palette = None
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

    // ---- commands -------------------------------------------------------------------------------

    /// The model without the session it showed: what the next one needs (grants stay; they are the
    /// host's).
    let private leave (what: string) (model: Model) : Model =
        { model with
            Session = None
            Env = None
            Timeline = Timeline.empty
            Streaming = None
            Phase = Phase.Starting what
            Scroll = 0
            TranscriptRows = 0
            History = None
            Editor = Editor.empty
            Answering = model.Answering |> Set.filter (fun id -> id.StartsWith "grant/")
            Selected = 0
            Palette = None
            Folds = Map.empty
            Selection = None
            Reveal = None
            ItemsAt = Map.empty
            Approvals = None
        }

    let private cycleDetail (model: Model) : Model =
        let verbosity =
            match model.Verbosity with
            | Verbosity.Quiet -> Verbosity.Normal
            | Verbosity.Normal -> Verbosity.Verbose
            | Verbosity.Verbose -> Verbosity.Quiet

        notice $"detail: {verbosity}" { model with Verbosity = verbosity }

    let private themes: (ThemeChoice * string) list =
        [
            ThemeChoice.Auto, "auto"
            ThemeChoice.Dark, "dark"
            ThemeChoice.Light, "light"
            ThemeChoice.Ansi16, "ansi16"
        ]

    /// Asks the host to turn auto approvals on or off for the session: a person's grant.
    let private setApprovals (auto: bool) (model: Model) : Model * Cmd list =
        match model.Session with
        | Some session ->
            notice
                (if auto then
                     "approvals: switching to auto…"
                 else
                     "approvals: switching to ask…")
                model,
            [ Cmd.SetApprovals(session.Id, auto) ]
        | None -> error "the session has not started yet" model, []

    /// Switches between asking and auto approvals (`Shift+Tab`, `/approvals`).
    let private switchApprovals (model: Model) : Model * Cmd list =
        let auto =
            match model.Approvals with
            | Some info -> info.Mode = "auto"
            | None -> false

        setApprovals (not auto) model

    /// Runs a command with what was typed after its name.
    let private run (command: TuiCommand) (argument: string) (model: Model) : Model * Cmd list =
        let model = { model with Palette = None }

        match command.Action with
        | CommandAction.NewSession -> leave "creating a new session" model, [ Cmd.NewSession ]
        | CommandAction.Sessions ->
            { model with
                Palette =
                    Some
                        { Palette.create "Sessions" [] with
                            Loading = true
                        }
            },
            [ Cmd.ListSessions ]
        | CommandAction.Model when argument = "" ->
            notice "type the model after the command: /model provider/model" model, []
        | CommandAction.Model ->
            match model.Session with
            | Some session ->
                let key = $"{model.Setup.Keys}/model/{model.Count + 1}"

                notice
                    $"model: {argument} from the next turn"
                    { model with
                        Count = model.Count + 1
                        Setup =
                            { model.Setup with
                                Model = Some argument
                            }
                    },
                [ Cmd.Override(session.Id, key, [ "model.role.main", Json.String argument ]) ]
            | None -> error "the session has not started yet" model, []
        | CommandAction.Detail -> cycleDetail model, []
        | CommandAction.Theme ->
            match themes |> List.tryFind (fun (_, name) -> name = argument.ToLowerInvariant()) with
            | Some(choice, name) ->
                notice
                    $"theme: {name}"
                    { model with
                        Setup = { model.Setup with Theme = choice }
                    },
                []
            | None when argument = "" ->
                { model with
                    Palette =
                        Some(
                            Palette.create
                                "Theme"
                                (themes |> List.map (fun (_, name) -> PaletteItem.Value(command, name)))
                        )
                },
                []
            | None -> error $"no theme {argument}: auto, dark, light or ansi16" model, []
        | CommandAction.Approvals ->
            match argument.ToLowerInvariant() with
            | "" -> switchApprovals model
            | "auto" -> setApprovals true model
            | "ask" -> setApprovals false model
            | other -> error $"no approval mode {other}: ask or auto" model, []
        | CommandAction.Exit -> { model with Quitting = true }, [ Cmd.Quit ]

    /// The commands a `/` prompt being typed may name, best first.
    let slashes (model: Model) : TuiCommand list =
        let text = model.Editor.Text

        if text.StartsWith('/') && not (text.Contains '\n') && not (text.Contains ' ') then
            Commands.search (text.Substring 1) Commands.all
        else
            []

    /// A `/` prompt sent: its command (or the best one for what was typed), never the agent.
    let private slash (model: Model) : (Model * Cmd list) option =
        match Commands.parse Commands.all model.Editor.Text with
        | None -> None
        | Some(Ok(command, argument)) -> Some(run command argument { model with Editor = Editor.empty })
        | Some(Error name) ->
            match slashes model with
            | command :: _ -> Some(run command "" { model with Editor = Editor.empty })
            | [] -> Some(error $"no command {name}: Ctrl+P lists them" model, [])

    let private paletteKey (palette: Palette) (key: Key) (model: Model) : Model * Cmd list =
        let shown = Palette.shown palette

        let set (palette: Palette) =
            { model with Palette = Some palette }, []

        match key.Code, key.Ctrl, key.Alt with
        | KeyCode.Escape, false, false
        | KeyCode.Char "p", true, false -> { model with Palette = None }, []
        | KeyCode.Up, false, false ->
            set
                { palette with
                    Selected = max 0 (palette.Selected - 1)
                }
        | KeyCode.Down, false, false
        | KeyCode.Tab, false, false ->
            set
                { palette with
                    Selected = min (max 0 (shown.Length - 1)) (palette.Selected + 1)
                }
        | KeyCode.Enter, false, false ->
            match List.tryItem palette.Selected shown with
            | Some(PaletteItem.Command command) -> run command "" model
            | Some(PaletteItem.Value(command, value)) -> run command value model
            | Some(PaletteItem.Session session) ->
                match model.Session with
                | Some current when current.Id = session.Id -> { model with Palette = None }, []
                | _ -> leave ("opening " + session.Id) model, [ Cmd.OpenSession session.Id ]
            | None -> model, []
        | KeyCode.Backspace, false, false when palette.Query <> "" ->
            let clusters = Text.clusters palette.Query

            set
                { palette with
                    Query = clusters |> List.truncate (clusters.Length - 1) |> String.concat ""
                    Selected = 0
                }
        | KeyCode.Char c, false, false ->
            set
                { palette with
                    Query = palette.Query + c
                    Selected = 0
                }
        | KeyCode.Paste text, _, _ ->
            set
                { palette with
                    Query = palette.Query + Text.firstLine text
                    Selected = 0
                }
        | _ -> model, []

    /// The command palette over everything.
    let openPalette (model: Model) : Model =
        { model with
            Palette = Some(Palette.create "Commands" (Commands.all |> List.map PaletteItem.Command))
        }

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

    /// Rows `PgUp` and `PgDn` scroll: the transcript's rows on the screen, but two kept for context
    /// (half the screen before a frame was drawn).
    let private transcriptPage (model: Model) =
        if model.TranscriptRoom > 0 then
            max 1 (model.TranscriptRoom - 2)
        else
            max 1 (model.Height / 2)

    /// Rows one notch of the mouse wheel scrolls.
    let private wheelRows = 3

    /// Scrolls the transcript up (`rows` > 0) or down, never past its end; the view keeps it within
    /// its first row (`View.settle`).
    let private scroll (rows: int) (model: Model) : (Model * Cmd list) option =
        Some(
            { model with
                Scroll = max 0 (model.Scroll + rows)
            },
            []
        )

    // ---- items ----------------------------------------------------------------------------------

    /// The transcript's items at the model's detail, in order.
    let items (model: Model) : Item list =
        Items.all model.Verbosity (Timeline.blocks model.Timeline)

    /// The item selected, while the person selects items.
    let selected (model: Model) : Item option =
        match model.Selection with
        | Some id -> items model |> List.tryFind (fun item -> item.Id = id)
        | None -> None

    let private selectItem (item: Item) (model: Model) : Model =
        { model with
            Selection = Some item.Id
            Reveal = Some item.Id
        }

    /// Selects the item above the one selected (the newest when none is, or it is gone), or below
    /// it: past the newest, selecting ends. At the oldest it stays.
    let private moveSelection (up: bool) (model: Model) : Model =
        let all = items model
        let shown = Items.shown model.Folds

        let before, after =
            match model.Selection with
            | Some id ->
                match List.tryFindIndex (fun (item: Item) -> item.Id = id) all with
                | Some i -> List.take i all, List.skip (i + 1) all
                | None -> all, []
            | None -> all, []

        let next =
            if up then
                before |> List.filter shown |> List.tryLast
            else
                after |> List.filter shown |> List.tryHead

        match next, up with
        | Some item, _ -> selectItem item model
        | None, true when model.Selection.IsSome -> { model with Reveal = model.Selection }
        | None, true -> notice "nothing to select yet" model
        | None, false ->
            { model with
                Selection = None
                Reveal = None
            }

    /// Selects the oldest item, or the newest.
    let private selectEnd (oldest: bool) (model: Model) : Model =
        let shown = items model |> List.filter (Items.shown model.Folds)

        match (if oldest then List.tryHead shown else List.tryLast shown) with
        | Some item -> selectItem item model
        | None -> model

    /// The item selected shown as the person chose.
    let private foldSelected (fold: Fold) (model: Model) : Model =
        match selected model with
        | Some item ->
            { model with
                Folds = Map.add item.Id fold model.Folds
                Reveal = Some item.Id
            }
        | None -> model

    /// The item selected expanded, or collapsed if it shows more than its line.
    let private toggleSelected (model: Model) : Model =
        match selected model with
        | Some item ->
            { model with
                Folds = Items.toggle model.Folds item
                Reveal = Some item.Id
            }
        | None -> model

    /// A click on the transcript: the item under it expanded, or collapsed if it shows more than its
    /// line (and selected, while selecting).
    let private click (column: int) (row: int) (model: Model) : Model =
        match Map.tryFind row model.ItemsAt with
        | Some id when column < model.ItemsWidth ->
            match items model |> List.tryFind (fun item -> item.Id = id) with
            | Some item ->
                { model with
                    Folds = Items.toggle model.Folds item
                    Selection = model.Selection |> Option.map (fun _ -> id)
                    Reveal = Some id
                }
            | None -> model
        | _ -> model

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
        | KeyCode.PageUp, _, _ -> scroll (transcriptPage model) model
        | KeyCode.PageDown, _, _ -> scroll (-(transcriptPage model)) model
        | KeyCode.WheelUp, _, _ -> scroll wheelRows model
        | KeyCode.WheelDown, _, _ -> scroll (-wheelRows) model
        // The transcript's top (no further than all its rows; the view stops at its first) and end.
        | KeyCode.Home, true, false ->
            Some(
                { model with
                    Scroll = max model.Scroll model.TranscriptRows
                },
                []
            )
        | KeyCode.End, true, false -> Some({ model with Scroll = 0 }, [])
        | KeyCode.Char "o", true, false -> Some(cycleDetail model, [])
        | KeyCode.Char "l", true, false -> Some(model, [ Cmd.Redraw ])
        | _ -> None

    /// Keys while the person selects items: `↑` `↓` move (with any modifier), `Enter` or `Space`
    /// expands or collapses, `→` expands and `←` collapses, `Home` `End` go to the oldest and the
    /// newest, `Esc` ends it; scrolling, the detail and redrawing keep it. Any other key ends it
    /// (`None`) and does what it does.
    let private selecting (key: Key) (model: Model) : (Model * Cmd list) option =
        match key.Code, key.Ctrl, key.Alt with
        | KeyCode.Up, _, _ -> Some(moveSelection true model, [])
        | KeyCode.Down, _, _ -> Some(moveSelection false model, [])
        | KeyCode.Enter, false, false
        | KeyCode.Char " ", false, false -> Some(toggleSelected model, [])
        | KeyCode.Right, false, false -> Some(foldSelected Fold.Expanded model, [])
        | KeyCode.Left, false, false -> Some(foldSelected Fold.Collapsed model, [])
        | KeyCode.Click(column, row), _, _ -> Some(click column row model, [])
        | KeyCode.Home, false, false -> Some(selectEnd true model, [])
        | KeyCode.End, false, false -> Some(selectEnd false model, [])
        | KeyCode.Escape, false, false ->
            Some(
                { model with
                    Selection = None
                    Reveal = None
                },
                []
            )
        | KeyCode.PageUp, _, _
        | KeyCode.PageDown, _, _
        | KeyCode.WheelUp, _, _
        | KeyCode.WheelDown, _, _
        | KeyCode.Home, true, false
        | KeyCode.End, true, false
        | KeyCode.Char "o", true, false
        | KeyCode.Char "l", true, false -> common key model
        | _ -> None

    /// A key, when nothing is selected.
    let private keyed (key: Key) (model: Model) : Model * Cmd list =
        match model.Palette, key.Code, key.Ctrl with
        | Some palette, _, _ when not (key.Code = KeyCode.Char "c" && key.Ctrl) -> paletteKey palette key model
        | None, KeyCode.Char "p", true -> openPalette { model with ExitArmed = None }, []
        // `Shift+↑` (or `Ctrl+↑`, `Alt+↑`, where the terminal passes them on) selects the newest item.
        | None, KeyCode.Up, _ when key.Shift || key.Ctrl || key.Alt ->
            moveSelection true { model with ExitArmed = None }, []
        | None, KeyCode.Click(column, row), _ -> click column row model, []
        | _ ->

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
                    | KeyCode.Enter, false, false, false ->
                        match slash model with
                        | Some result -> result
                        | None -> send PromptMode.Steer model
                    | KeyCode.Tab, false, false, true -> switchApprovals model
                    | KeyCode.Tab, false, false, false ->
                        // On a `/` prompt, Tab completes the command's name.
                        match slashes model with
                        | command :: _ ->
                            { model with
                                Editor = Editor.ofText ("/" + command.Name + " ")
                            },
                            []
                        | [] -> send PromptMode.Queue model
                    | _ -> edit key model

    let private onKey (key: Key) (model: Model) : Model * Cmd list =
        let selection =
            match model.Palette, model.Selection with
            | None, Some _ -> selecting key model
            | _ -> None

        match selection with
        | Some result -> result
        | None when model.Selection.IsSome ->
            // Any other key ends the selection, then does what it does.
            keyed
                key
                { model with
                    Selection = None
                    Reveal = None
                }
        | None -> keyed key model

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
        | Msg.Env env ->
            // Its sandbox decides whether auto approvals can hold.
            { model with Env = Some env },
            (match model.Session with
             | Some session -> [ Cmd.Approvals session.Id ]
             | None -> [])
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
        | Msg.Grant n ->
            // A grant of auto approvals made, revoked or expired (here, by `fx grants` or another
            // client): ask how the session's commands are decided now.
            let refresh =
                match n.Event, model.Session with
                | GrantEvent.Granted granted, Some session when
                    granted.Action.Value = "approvals.auto" && n.Seq > model.GrantsSeq
                    ->
                    [ Cmd.Approvals session.Id ]
                | (GrantEvent.Revoked _ | GrantEvent.Expired _), Some session when n.Seq > model.GrantsSeq ->
                    [ Cmd.Approvals session.Id ]
                | _ -> []

            grant n model, refresh
        | Msg.Approvals(info, switched) ->
            let model = { model with Approvals = Some info }

            match switched, info.Mode, info.Sandbox with
            // What auto approvals mean without a sandbox, in one line.
            | true, "auto", None ->
                notice "approvals: auto · unsandboxed: commands run unasked with your rights" model, []
            | true, "auto", Some driver ->
                notice $"approvals: auto · commands run unasked, confined by {driver}" model, []
            | true, mode, _ -> notice $"approvals: {mode}" model, []
            | false, _, _ -> model, []
        | Msg.Sent(_, Ok()) -> model, []
        | Msg.Sent(what, Error reason) -> error $"{what}: {reason}" model, []
        | Msg.AnswerFailed(card, reason) ->
            error
                $"the answer was not taken: {reason}"
                { model with
                    Answering = model.Answering.Remove card
                },
            []
        | Msg.Sessions sessions ->
            match model.Palette with
            | Some palette when palette.Loading ->
                { model with
                    Palette =
                        Some
                            { palette with
                                Loading = false
                                Items = sessions |> List.rev |> List.map PaletteItem.Session
                            }
                },
                []
            | _ -> model, []
        | Msg.Disconnected reason ->
            error
                $"disconnected: {reason}"
                { model with
                    Phase = Phase.Stopped reason
                },
            []
