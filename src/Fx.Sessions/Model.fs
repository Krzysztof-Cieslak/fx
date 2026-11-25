namespace Fx.Sessions

// The session's commands, events, state and intents (spec §5). Plain data shared with clients: they
// fold the same events with the same `Session.evolve` (Fable), so this file uses FSharp.Core,
// Fx.Machines and Fx.Contracts' `Json` only. Times are microseconds since the Unix epoch (`int64`), as
// in the event store's envelopes.

open Fx.Contracts
open Fx.Machines

/// How a prompt sent while a turn runs reaches the agent (spec §5, §19.2).
[<RequireQualifiedAccess>]
type PromptMode =
    /// Into the running turn, at its next step boundary. With no turn running, it starts one at once.
    | Steer
    /// After the work in progress: it starts a turn of its own once the session is free.
    | Queue

    override this.ToString() =
        match this with
        | Steer -> "steer"
        | Queue -> "queue"

/// A piece of evidence a result points to (spec §4.1, §4.5): what was checked and what it showed.
/// The strategy that runs turns (T127) decides what it collects.
type Evidence =
    {
        /// What kind of evidence, e.g. `command`, `review`, `check`.
        Kind: string
        /// One line for people, e.g. `dotnet test tests/Billing → 214 passed`.
        Summary: string
        /// Details for tools; `null` for none.
        Detail: Json
    }

    override this.ToString() = this.Kind + ": " + this.Summary

/// How a turn ends when it finishes its work (spec §4.5): a valid result, checked by the harness.
[<RequireQualifiedAccess>]
type TurnResult =
    /// The work is done.
    | Done of summary: string * evidence: Evidence list * verify: string option
    /// The agent needs answers to these pending requests (decisions, forms) before it can go on.
    | NeedInput of requests: string list
    /// The agent waits for these background jobs.
    | Waiting of jobs: string list * note: string

    override this.ToString() =
        match this with
        | Done(summary, evidence, _) -> "done (" + string evidence.Length + " evidence): " + summary
        | NeedInput requests -> "need input " + String.concat ", " requests
        | Waiting(jobs, note) -> "waiting on " + String.concat ", " jobs + ": " + note

/// How a turn ended.
[<RequireQualifiedAccess>]
type TurnEnd =
    /// It finished with a result.
    | Completed of TurnResult
    /// Someone interrupted it (the control path, spec §5).
    | Interrupted
    /// The strategy that ran it failed (an expected failure such as a model error, or a defect).
    | Failed of reason: string
    /// The host stopped while it ran; nothing auto-resumes (spec §5, crash recovery is T128).
    | Abandoned of reason: string

    override this.ToString() =
        match this with
        | Completed result -> string result
        | Interrupted -> "interrupted"
        | Failed reason -> "failed: " + reason
        | Abandoned reason -> "abandoned: " + reason

/// How a pending request was settled.
[<RequireQualifiedAccess>]
type Resolution =
    /// Someone answered it; the response is what the request's kind reads as an answer.
    | Answered of response: Json
    /// It was withdrawn: by someone, or because its turn ended or the session closed.
    | Cancelled of reason: string
    /// Its time ran out; the response is the default its expiry policy gave, if any.
    | Expired of response: Json option

    override this.ToString() =
        match this with
        | Answered response -> "answered " + Json.serialize response
        | Cancelled reason -> "cancelled (" + reason + ")"
        | Expired None -> "expired"
        | Expired(Some response) -> "expired, defaulted to " + Json.serialize response

/// What an input to the agent says.
[<RequireQualifiedAccess>]
type InputContent =
    /// A prompt someone typed.
    | Text of string
    /// A request the agent asked to hear about was settled.
    | Resolved of request: string * resolution: Resolution
    /// A background job finished.
    | JobFinished of job: string * note: string

    override this.ToString() =
        match this with
        | Text text -> Json.quote text
        | Resolved(request, resolution) -> request + " " + string resolution
        | JobFinished(job, note) -> "job " + job + " finished: " + note

/// An input for the agent: a prompt, a settled request, a finished job. Its id is the sequence number of
/// the event that received it, so it is unique in its session and every later event can refer to it.
type Input =
    {
        Id: int64
        By: Attribution
        At: int64
        Content: InputContent
    }

    override this.ToString() =
        "#" + string this.Id + " " + this.By.Principal + ": " + string this.Content

/// Who opened a pending request, and so where its resolution goes (spec §12.2: delivered exactly once
/// to the originating session or waiting run step).
[<RequireQualifiedAccess>]
type RequestOrigin =
    /// A turn of this session.
    | Turn of turn: int
    /// The session itself: the host or an extension acting for it (an install, an auth flow).
    | Session
    /// Something that follows this session's log from another stream, e.g. a run step.
    | Stream of EventRef

    override this.ToString() =
        match this with
        | Turn turn -> "turn " + string turn
        | Session -> "session"
        | Stream origin -> string origin

/// How a resolution reaches its opener.
[<RequireQualifiedAccess>]
type Reply =
    /// The opener waits for it in the log (a tool call waiting for a grant, a run step at a gate).
    | ToOpener
    /// It becomes an input for the agent (a decision card it will read later).
    | AsInput

    override this.ToString() =
        match this with
        | ToOpener -> "to opener"
        | AsInput -> "as input"

/// How long a request stays open, at most.
[<RequireQualifiedAccess>]
type Lifetime =
    /// Until the turn that opened it ends: then it is cancelled.
    | Turn
    /// Until it is settled, across turns and restarts.
    | Session

    override this.ToString() =
        match this with
        | Turn -> "turn"
        | Session -> "session"

/// What happens when a request's time runs out.
[<RequireQualifiedAccess>]
type ExpiryPolicy =
    /// It expires with no answer.
    | Cancel
    /// It expires with this answer, e.g. "deny".
    | Answer of Json

    override this.ToString() =
        match this with
        | Cancel -> "cancel"
        | Answer response -> "answer " + Json.serialize response

/// A request's deadline and what happens then.
type Expiry =
    {
        /// Microseconds since the Unix epoch.
        At: int64
        Then: ExpiryPolicy
    }

    override this.ToString() =
        "at " + string this.At + " then " + string this.Then

/// A pending request as its opener asks for it (spec §5: decision cards, grant requests, workflow gates,
/// forms, auth flows, installs). Its kind (`RequestKinds`) says what its payload and answers look like.
type RequestSpec =
    {
        /// Unique among the session's open requests. Openers derive it from what they do (a decision
        /// id, a tool call), so a retry opens the same request.
        Id: string
        Kind: string
        /// One line for people.
        Title: string
        Payload: Json
        Origin: RequestOrigin
        Reply: Reply
        Lifetime: Lifetime
        Expiry: Expiry option
    }

    override this.ToString() =
        this.Id + " (" + this.Kind + ") " + Json.quote this.Title

/// A request that is open: what was asked, who may answer it, and when and by whom it was opened.
type PendingRequest =
    {
        Request: RequestSpec
        /// The client capabilities that can answer it, from its kind when it was opened: a client
        /// offering any of them is offered the request.
        Capabilities: string list
        By: Attribution
        At: int64
        /// The sequence number of the event that opened it.
        Seq: int64
    }

    override this.ToString() =
        string this.Request
        + " opened at #"
        + string this.Seq
        + " by "
        + this.By.Principal

/// A fork of this session (spec §5: session tree). Forks are sessions of their own whose first event
/// refers to this session's stream at a sequence number.
[<RequireQualifiedAccess>]
type ForkStatus =
    /// Being started.
    | Starting
    | Ready
    | Failed of reason: string

    override this.ToString() =
        match this with
        | Starting -> "starting"
        | Ready -> "ready"
        | Failed reason -> "failed: " + reason

type Fork =
    {
        /// The child session's id.
        Child: string
        /// The last event of this session the child's history includes.
        UpTo: int64
        /// The sequence number of the `Forked` event.
        Seq: int64
        /// The profile and overrides the child starts with: this session's when it forked.
        Profile: string
        Overrides: (string * Json) list
        Status: ForkStatus
    }

    override this.ToString() =
        this.Child + " at #" + string this.UpTo + " (" + string this.Status + ")"

/// The turn in progress.
type RunningTurn =
    {
        Turn: int
        /// The profile it runs under: overrides made during the turn apply from the next one.
        Profile: string
        /// Every input delivered to it so far: those it started with, then those delivered at step
        /// boundaries, in order.
        Inputs: Input list
        /// The last step boundary at which inputs were delivered (0 before any).
        Step: int
        At: int64
        /// The sequence number of the event that started it.
        Seq: int64
    }

    override this.ToString() =
        "turn "
        + string this.Turn
        + " ("
        + string this.Inputs.Length
        + " inputs, step "
        + string this.Step
        + ")"

/// How the last turn ended.
type LastTurn =
    {
        Turn: int
        End: TurnEnd
        At: int64
    }

    override this.ToString() =
        "turn " + string this.Turn + " " + string this.End

[<RequireQualifiedAccess>]
type Status =
    /// No events yet.
    | New
    | Open
    /// No new inputs until it is reopened.
    | Closed
    /// Closed and put away.
    | Archived

    override this.ToString() =
        match this with
        | New -> "new"
        | Open -> "open"
        | Closed -> "closed"
        | Archived -> "archived"

/// A session's state: the fold of its log (spec §5). Clients keep the same fold, so it is also their
/// view of the session: what runs, what waits, and what is asked of them.
type SessionState =
    {
        Id: string
        /// The sequence number of the last event folded in: the stream's head.
        Seq: int64
        Status: Status
        Title: string option
        /// The id of the profile new turns run under (docs/modules/variants.md).
        Profile: string
        /// The session's variant overrides (the `Session` layer), by slot id, sorted.
        Overrides: (string * Json) list
        /// The session this one was forked from, at the last event its history includes.
        Parent: EventRef option
        Forks: Fork list
        /// How many turns have started.
        Turns: int
        Running: RunningTurn option
        Last: LastTurn option
        /// Set when a turn was interrupted, failed or abandoned: nothing starts until someone sends a
        /// prompt (spec §5: nothing auto-resumes).
        Held: bool
        /// Queued prompts, oldest first. Each starts a turn of its own.
        Queue: Input list
        /// Inputs waiting for the next step boundary or turn: steered prompts, settled requests that
        /// reply as input, finished jobs.
        Inbox: Input list
        /// The requests the last turn needs answered (`NeedInput`) that are still open: the next turn
        /// starts when all are settled.
        AwaitingRequests: string list
        /// The jobs the last turn waits for (`Waiting`).
        AwaitingJobs: string list
        /// Open requests, in the order they were opened.
        Requests: PendingRequest list
    }

    override this.ToString() =
        let running =
            match this.Running with
            | Some turn -> string turn
            | None -> "idle"

        "session "
        + this.Id
        + " #"
        + string this.Seq
        + " "
        + string this.Status
        + ", "
        + running
        + (if this.Held then ", held" else "")
        + ", queue "
        + string this.Queue.Length
        + ", inbox "
        + string this.Inbox.Length
        + ", requests "
        + string this.Requests.Length

/// What starting a session (or a fork) records.
type SessionStart =
    {
        Id: string
        Profile: string
        Overrides: (string * Json) list
        Parent: EventRef option
        Title: string option
    }

    override this.ToString() =
        let parent =
            match this.Parent with
            | Some parent -> " forked from " + string parent
            | None -> ""

        this.Id + " under " + this.Profile + parent

/// A new profile for the session, resolved and stored by the host from the session's overrides.
type ProfileSelection =
    {
        Profile: string
        /// All of the session's overrides, by slot id, sorted.
        Overrides: (string * Json) list
        /// The slots whose values changed.
        Changed: string list
        /// The profile the host resolved from: if the session's profile has changed since, the
        /// selection is refused and the host resolves again (two clients overriding at once).
        BasedOn: string
    }

    override this.ToString() =
        this.Profile + " (changed " + String.concat ", " this.Changed + ")"

/// What clients and the host ask a session to do (spec §5: commands, not mutations). Every command
/// travels with an idempotency key in its envelope.
[<RequireQualifiedAccess>]
type Command =
    /// Starts the session: the first event binds its profile (docs/modules/variants.md).
    | Start of SessionStart
    | Prompt of text: string * mode: PromptMode
    /// Withdraws a queued prompt or a steered one not delivered yet.
    | Withdraw of input: int64
    /// The control path: stops the running turn (send it with `Actor.interrupt`).
    | Interrupt
    | OpenRequest of RequestSpec
    | Answer of request: string * response: Json
    | CancelRequest of request: string * reason: string
    /// From the request's timer.
    | ExpireRequest of request: string
    /// A background job finished.
    | NotifyJob of job: string * note: string
    | SelectProfile of ProfileSelection
    /// Forks the session at a sequence number (the head if none) into a new session.
    | Fork of child: string * upTo: int64 option
    | Close
    | Reopen
    | Archive
    /// From the turn runner at a step boundary: delivers what waits in the inbox.
    | TakeInputs of turn: int * step: int
    /// From the turn runner: the turn's result.
    | EndTurn of turn: int * result: TurnResult
    /// From the turn runner: the turn failed.
    | FailTurn of turn: int * reason: string
    /// From recovery: the host stopped during the turn.
    | AbandonTurn of turn: int * reason: string
    /// From the fork's interpreter: the child session started.
    | ChildStarted of child: string
    /// From the fork's interpreter: the child session refused to start.
    | ChildFailed of child: string * reason: string

    override this.ToString() =
        match this with
        | Start start -> "start " + string start
        | Prompt(text, mode) -> string mode + " " + Json.quote text
        | Withdraw input -> "withdraw #" + string input
        | Interrupt -> "interrupt"
        | OpenRequest request -> "open " + string request
        | Answer(request, response) -> "answer " + request + " " + Json.serialize response
        | CancelRequest(request, reason) -> "cancel " + request + " (" + reason + ")"
        | ExpireRequest request -> "expire " + request
        | NotifyJob(job, note) -> "job " + job + " finished: " + note
        | SelectProfile selection -> "select profile " + string selection
        | Fork(child, Some upTo) -> "fork " + child + " at #" + string upTo
        | Fork(child, None) -> "fork " + child
        | Close -> "close"
        | Reopen -> "reopen"
        | Archive -> "archive"
        | TakeInputs(turn, step) -> "take inputs, turn " + string turn + " step " + string step
        | EndTurn(turn, result) -> "end turn " + string turn + ": " + string result
        | FailTurn(turn, reason) -> "fail turn " + string turn + ": " + reason
        | AbandonTurn(turn, reason) -> "abandon turn " + string turn + ": " + reason
        | ChildStarted child -> "child " + child + " started"
        | ChildFailed(child, reason) -> "child " + child + " failed: " + reason

/// A command as the session machine handles it: what, who and when. `Handle` is pure, so the sender
/// and the time come in the command; the session host builds it from the envelope's attribution and
/// the clock, so events record the same sender as their metadata.
type SessionCommand =
    {
        Command: Command
        By: Attribution
        At: int64
    }

    override this.ToString() =
        string this.Command + " by " + string this.By

/// The settling of a request.
type RequestResolved =
    {
        Request: string
        Resolution: Resolution
        By: Attribution
        At: int64
        /// The input it became (the event's own sequence number), for requests that reply as input.
        Input: int64 option
    }

    override this.ToString() =
        this.Request + " " + string this.Resolution + " by " + this.By.Principal

/// What happened in a session: facts kept forever (spec §5). Add cases; never change what one means.
[<RequireQualifiedAccess>]
type Event =
    /// The first event of every session stream: its id, profile and, for a fork, its parent.
    | Started of SessionStart * by: Attribution * at: int64
    | ProfileSelected of profile: string * overrides: (string * Json) list * changed: string list * by: Attribution
    | PromptQueued of Input
    | PromptSteered of Input
    | PromptWithdrawn of input: int64 * by: Attribution
    | TurnStarted of turn: int * inputs: int64 list * profile: string * at: int64
    | InputsDelivered of turn: int * step: int * inputs: int64 list
    | TurnEnded of turn: int * ended: TurnEnd * at: int64
    /// An interrupt took effect: the control path cancelled the work in flight before the command was
    /// handled, so requesting and applying it are one event (with the running turn's end after it).
    | Interrupted of turn: int option * by: Attribution
    | RequestOpened of PendingRequest
    | RequestResolved of RequestResolved
    | JobNotified of Input
    | Forked of child: string * upTo: int64 * by: Attribution
    | ForkReady of child: string
    | ForkFailed of child: string * reason: string
    | Closed of by: Attribution
    | Reopened of by: Attribution
    | Archived of by: Attribution

    override this.ToString() =
        let ids (inputs: int64 list) =
            inputs |> List.map (fun id -> "#" + string id) |> String.concat " "

        match this with
        | Started(start, by, _) -> "started " + string start + " by " + by.Principal
        | ProfileSelected(profile, _, changed, by) ->
            "profile "
            + profile
            + " ("
            + String.concat ", " changed
            + ") by "
            + by.Principal
        | PromptQueued input -> "queued " + string input
        | PromptSteered input -> "steered " + string input
        | PromptWithdrawn(input, by) -> "withdrawn #" + string input + " by " + by.Principal
        | TurnStarted(turn, inputs, _, _) -> "turn " + string turn + " started with " + ids inputs
        | InputsDelivered(turn, step, inputs) -> "turn " + string turn + " step " + string step + " took " + ids inputs
        | TurnEnded(turn, ended, _) -> "turn " + string turn + " ended: " + string ended
        | Interrupted(Some turn, by) -> "interrupted turn " + string turn + " by " + by.Principal
        | Interrupted(None, by) -> "interrupted (idle) by " + by.Principal
        | RequestOpened request -> "opened " + string request.Request
        | RequestResolved resolved -> "resolved " + string resolved
        | JobNotified input -> "job " + string input
        | Forked(child, upTo, by) -> "forked " + child + " at #" + string upTo + " by " + by.Principal
        | ForkReady child -> "fork " + child + " ready"
        | ForkFailed(child, reason) -> "fork " + child + " failed: " + reason
        | Closed by -> "closed by " + by.Principal
        | Reopened by -> "reopened by " + by.Principal
        | Archived by -> "archived by " + by.Principal

/// A turn to run (the boundary to the strategy that runs turns, T127).
type TurnStart =
    {
        Session: string
        Turn: int
        Profile: string
        Inputs: Input list
        /// The sequence number of the event that started it: the turn's commands derive their keys from
        /// it.
        Seq: int64
    }

    override this.ToString() =
        "turn "
        + string this.Turn
        + " of "
        + this.Session
        + " ("
        + string this.Inputs.Length
        + " inputs)"

/// A fork's child session to start.
type ForkStart =
    {
        Parent: EventRef
        Start: SessionStart
        /// The sequence number of the `Forked` event: the child's start derives its key from it.
        Seq: int64
    }

    override this.ToString() =
        "start " + this.Start.Id + " from " + string this.Parent

/// What the session asks its host to do after an event (spec §4.4). Intents are not durable: the
/// state records the work, and `Session.recover` picks it up when an actor starts.
[<RequireQualifiedAccess>]
type Intent =
    /// Run a turn; its result comes back as `EndTurn` or `FailTurn`.
    | RunTurn of TurnStart
    /// Expire a request at a time (microseconds since the Unix epoch): `ExpireRequest` then.
    | Expire of request: string * at: int64
    /// Start a fork's child session: `ChildStarted` or `ChildFailed`.
    | StartFork of ForkStart
    /// Recover a turn the host stopped during (spec §5, crash recovery): its strategy says whether
    /// something it did is in doubt. If not, `AbandonTurn`; if so, the strategy asks people what
    /// happened and the turn's result comes back as `EndTurn` or `FailTurn`, as from `RunTurn`.
    | RecoverTurn of TurnStart

    override this.ToString() =
        match this with
        | RunTurn turn -> "run " + string turn
        | Expire(request, at) -> "expire " + request + " at " + string at
        | StartFork fork -> string fork
        | RecoverTurn turn -> "recover " + string turn
