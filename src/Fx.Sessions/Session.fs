namespace Fx.Sessions

open Fx.Contracts
open Fx.Machines

/// The codes of the session's rejections (`Rejection.Code`), for clients to match on.
[<RequireQualifiedAccess>]
module Rejections =
    /// The session has not started.
    [<Literal>]
    let New = "session.new"

    [<Literal>]
    let Started = "session.started"

    [<Literal>]
    let Closed = "session.closed"

    /// A turn is running: interrupt it first.
    [<Literal>]
    let Busy = "session.busy"

    /// A command that is malformed whatever the state (an empty id, a prompt too long, …).
    [<Literal>]
    let Invalid = "session.invalid"

    /// The input is not queued or waiting any more.
    [<Literal>]
    let NotPending = "session.not-pending"

    /// The turn named is not the running turn (it ended, or never started).
    [<Literal>]
    let StaleTurn = "session.stale-turn"

    /// The profile changed since the host resolved the selection: resolve again.
    [<Literal>]
    let StaleProfile = "session.stale-profile"

    [<Literal>]
    let UnknownKind = "session.unknown-kind"

    [<Literal>]
    let InvalidPayload = "session.invalid-payload"

    [<Literal>]
    let InvalidAnswer = "session.invalid-answer"

    /// An open request has the id.
    [<Literal>]
    let RequestExists = "session.request-exists"

    /// The request is not open: settled already, or never opened.
    [<Literal>]
    let NotOpen = "session.not-open"

    /// The request's time ran out; it is about to expire.
    [<Literal>]
    let Expired = "session.expired"

    /// An expiry before the request's deadline (or of a request without one).
    [<Literal>]
    let NotExpired = "session.not-expired"

    [<Literal>]
    let ForkExists = "session.fork-exists"

/// The session machine (spec §5; docs/modules/sessions.md): pure, and shared with clients, which fold
/// the same events with `evolve`.
module Session =
    /// The kind of session streams: a session's stream is `session/<id>`.
    [<Literal>]
    let StreamKind = "session"

    /// The longest prompt, in UTF-16 code units, so that every event stays far below the event store's
    /// 1 MiB. Larger content belongs in attachments (blobs).
    [<Literal>]
    let MaxPromptChars = 131072

    let stream (id: string) : StreamId = StreamId.create StreamKind id

    /// The state of an empty log.
    let initial: SessionState =
        {
            Id = ""
            Seq = 0L
            Status = Status.New
            Title = None
            Profile = ""
            Overrides = []
            Parent = None
            Forks = []
            Turns = 0
            Running = None
            Last = None
            Held = false
            Queue = []
            Inbox = []
            AwaitingRequests = []
            AwaitingJobs = []
            Requests = []
        }

    let private without (ids: int64 list) (inputs: Input list) =
        inputs |> List.filter (fun input -> not (List.contains input.Id ids))

    let private pick (ids: int64 list) (inputs: Input list) =
        ids
        |> List.choose (fun id -> inputs |> List.tryFind (fun input -> input.Id = id))

    /// The open request with this id.
    let tryRequest (id: string) (state: SessionState) : PendingRequest option =
        state.Requests |> List.tryFind (fun pending -> pending.Request.Id = id)

    /// The input an event received, if it received one: a prompt, a request settled as input, a job's
    /// notice. Its id is the event's sequence number.
    let inputOf (event: Event) : Input option =
        match event with
        | Event.PromptQueued input
        | Event.PromptSteered input
        | Event.JobNotified input -> Some input
        | Event.RequestResolved resolved ->
            resolved.Input
            |> Option.map (fun id ->
                ({
                    Id = id
                    By = resolved.By
                    At = resolved.At
                    Content = InputContent.Resolved(resolved.Request, resolved.Resolution)
                }
                : Input))
        | _ -> None

    /// Applies one event. Total: every event `handle` ever decided is accepted.
    let evolve (previous: SessionState) (event: Event) : SessionState =
        let state =
            { previous with
                Seq = previous.Seq + 1L
            }

        match event with
        | Event.Started(start, _, _) ->
            { state with
                Id = start.Id
                Status = Status.Open
                Title = start.Title
                Profile = start.Profile
                Overrides = start.Overrides
                Parent = start.Parent
            }
        | Event.ProfileSelected(profile, overrides, _, _) ->
            { state with
                Profile = profile
                Overrides = overrides
            }
        | Event.PromptQueued input ->
            { state with
                Queue = state.Queue @ [ input ]
                Held = false
            }
        | Event.PromptSteered input ->
            // Steering takes over from whatever the session waited for.
            { state with
                Inbox = state.Inbox @ [ input ]
                Held = false
                AwaitingRequests = []
                AwaitingJobs = []
            }
        | Event.PromptWithdrawn(id, _) ->
            { state with
                Queue = without [ id ] state.Queue
                Inbox = without [ id ] state.Inbox
            }
        | Event.TurnStarted(turn, ids, profile, at) ->
            { state with
                Turns = turn
                Running =
                    Some(
                        {
                            Turn = turn
                            Profile = profile
                            Inputs = pick ids (state.Inbox @ state.Queue)
                            Step = 0
                            At = at
                            Seq = state.Seq
                        }
                        : RunningTurn
                    )
                Inbox = without ids state.Inbox
                Queue = without ids state.Queue
                Held = false
                AwaitingRequests = []
                AwaitingJobs = []
            }
        | Event.InputsDelivered(turn, step, ids) ->
            match state.Running with
            | Some running when running.Turn = turn ->
                { state with
                    Running =
                        Some
                            { running with
                                Inputs = running.Inputs @ pick ids state.Inbox
                                Step = max running.Step step
                            }
                    Inbox = without ids state.Inbox
                }
            | _ -> state
        | Event.TurnEnded(turn, ended, at) ->
            let awaitingRequests, awaitingJobs =
                match ended with
                | TurnEnd.Completed(TurnResult.NeedInput requests) ->
                    let awaited (id: string) =
                        state.Requests
                        |> List.exists (fun pending -> pending.Request.Id = id && pending.Request.Reply = Reply.AsInput)

                    List.distinct requests |> List.filter awaited, []
                | TurnEnd.Completed(TurnResult.Waiting(jobs, _)) -> [], List.distinct jobs
                | _ -> [], []

            { state with
                Running = None
                Last = Some({ Turn = turn; End = ended; At = at }: LastTurn)
                Held =
                    (match ended with
                     | TurnEnd.Completed _ -> false
                     | _ -> true)
                AwaitingRequests = awaitingRequests
                AwaitingJobs = awaitingJobs
            }
        | Event.Interrupted(None, _) ->
            // Stopping an idle session stops it waiting, too.
            { state with
                Held = true
                AwaitingRequests = []
                AwaitingJobs = []
            }
        | Event.Interrupted(Some _, _) -> state
        | Event.RequestOpened pending ->
            { state with
                Requests = state.Requests @ [ pending ]
            }
        | Event.RequestResolved resolved ->
            { state with
                Requests =
                    state.Requests
                    |> List.filter (fun pending -> pending.Request.Id <> resolved.Request)
                Inbox =
                    match inputOf event with
                    | Some input -> state.Inbox @ [ input ]
                    | None -> state.Inbox
                AwaitingRequests = state.AwaitingRequests |> List.filter (fun id -> id <> resolved.Request)
            }
        | Event.JobNotified input ->
            let finished (job: string) =
                match input.Content with
                | InputContent.JobFinished(finishedJob, _) -> finishedJob = job
                | _ -> false

            { state with
                Inbox = state.Inbox @ [ input ]
                AwaitingJobs = state.AwaitingJobs |> List.filter (fun job -> not (finished job))
            }
        | Event.Forked(child, upTo, _) ->
            { state with
                Forks =
                    state.Forks
                    @ [
                        ({
                            Child = child
                            UpTo = upTo
                            Seq = state.Seq
                            Profile = state.Profile
                            Overrides = state.Overrides
                            Status = ForkStatus.Starting
                        }
                        : Fork)
                    ]
            }
        | Event.ForkReady child ->
            { state with
                Forks =
                    state.Forks
                    |> List.map (fun fork ->
                        if fork.Child = child then
                            { fork with Status = ForkStatus.Ready }
                        else
                            fork)
            }
        | Event.ForkFailed(child, reason) ->
            { state with
                Forks =
                    state.Forks
                    |> List.map (fun fork ->
                        if fork.Child = child then
                            { fork with
                                Status = ForkStatus.Failed reason
                            }
                        else
                            fork)
            }
        | Event.Closed _ ->
            // Closing drops what waited for a turn.
            { state with
                Status = Status.Closed
                Queue = []
                Inbox = []
                AwaitingRequests = []
                AwaitingJobs = []
                Held = false
            }
        | Event.Reopened _ -> { state with Status = Status.Open }
        | Event.Archived _ -> { state with Status = Status.Archived }

    /// The inputs the next turn starts with, if the session would start one now: the inbox if it has
    /// any, else the oldest queued prompt. None while a turn runs, while the session is held, closed,
    /// or waits for requests or jobs.
    let nextInputs (state: SessionState) : Input list option =
        if
            state.Status <> Status.Open
            || state.Running.IsSome
            || state.Held
            || not state.AwaitingRequests.IsEmpty
            || not state.AwaitingJobs.IsEmpty
        then
            None
        elif not state.Inbox.IsEmpty then
            Some state.Inbox
        else
            match state.Queue with
            | first :: _ -> Some [ first ]
            | [] -> None

    /// The open requests a client with these capabilities can answer (spec §5: re-offered to capable
    /// clients), oldest first.
    let offers (capabilities: string list) (state: SessionState) : PendingRequest list =
        state.Requests
        |> List.filter (fun pending ->
            pending.Capabilities
            |> List.exists (fun capability -> List.contains capability capabilities))

    // ---- Handle -----------------------------------------------------------------------------------

    /// The events decided so far (newest first) and the state after them, so that a command can add
    /// events that depend on the ones before (sequence numbers, the next turn).
    type private Draft =
        {
            State: SessionState
            Events: Event list
        }

        override this.ToString() =
            "Draft(" + string this.Events.Length + " events)"

    let private emit (event: Event) (draft: Draft) : Draft =
        {
            State = evolve draft.State event
            Events = event :: draft.Events
        }

    /// The sequence number the next event gets.
    let private next (draft: Draft) = draft.State.Seq + 1L

    let private finish (draft: Draft) : Result<Event list, Rejection> = Ok(List.rev draft.Events)

    let private reject (code: string) (message: string) : Result<Event list, Rejection> =
        Error(Rejection.create code message)

    /// Starts the next turn if the session would start one now.
    let private settle (at: int64) (draft: Draft) : Draft =
        match nextInputs draft.State with
        | Some inputs ->
            emit (Event.TurnStarted(draft.State.Turns + 1, inputs |> List.map _.Id, draft.State.Profile, at)) draft
        | None -> draft

    /// Settles an open request. It becomes an input if it replies as input, unless the session settles
    /// it itself because its turn ended or the session closed.
    let private resolve
        (by: Attribution)
        (at: int64)
        (asInput: bool)
        (resolution: Resolution)
        (pending: PendingRequest)
        (draft: Draft)
        : Draft =
        let input =
            if asInput && pending.Request.Reply = Reply.AsInput then
                Some(next draft)
            else
                None

        emit
            (Event.RequestResolved(
                {
                    Request = pending.Request.Id
                    Resolution = resolution
                    By = by
                    At = at
                    Input = input
                }
                : RequestResolved
            ))
            draft

    let private cancelAll
        (reason: string)
        (by: Attribution)
        (at: int64)
        (pendings: PendingRequest list)
        (draft: Draft)
        =
        pendings
        |> List.fold (fun draft pending -> resolve by at false (Resolution.Cancelled reason) pending draft) draft

    /// Ends the running turn: the requests that live as long as it are cancelled, then it ends.
    let private endTurn (turn: int) (ended: TurnEnd) (by: Attribution) (at: int64) (draft: Draft) : Draft =
        let ofTurn =
            draft.State.Requests
            |> List.filter (fun pending ->
                pending.Request.Lifetime = Lifetime.Turn
                && pending.Request.Origin = RequestOrigin.Turn turn)

        draft
        |> cancelAll "turn ended" by at ofTurn
        |> emit (Event.TurnEnded(turn, ended, at))

    let private runningTurn (turn: int) (state: SessionState) : Result<RunningTurn, Rejection> =
        match state.Running with
        | Some running when running.Turn = turn -> Ok running
        | _ -> Error(Rejection.create Rejections.StaleTurn ("Turn " + string turn + " is not running."))

    let private renderIssues (issues: Issue list) = Issue.renderAtMost 5 issues

    let private openRequest
        (kinds: RequestKinds)
        (by: Attribution)
        (at: int64)
        (spec: RequestSpec)
        (draft: Draft)
        : Result<Event list, Rejection> =
        let state = draft.State

        match RequestKinds.tryFind spec.Kind kinds with
        | _ when spec.Id.Trim() = "" -> reject Rejections.Invalid "A request needs an id."
        | _ when (tryRequest spec.Id state).IsSome ->
            reject Rejections.RequestExists ("A request " + Json.quote spec.Id + " is open.")
        | None -> reject Rejections.UnknownKind ("No request kind is called " + Json.quote spec.Kind + ".")
        | Some kind ->
            let payloadIssues = kind.CheckPayload spec.Payload

            let originTurn =
                match spec.Origin with
                | RequestOrigin.Turn turn -> Some turn
                | _ -> None

            let defaultIssues =
                match spec.Expiry with
                | Some { Then = ExpiryPolicy.Answer response } when payloadIssues.IsEmpty ->
                    kind.CheckAnswer spec.Payload response
                | _ -> []

            match originTurn, spec.Expiry with
            | _ when not payloadIssues.IsEmpty -> reject Rejections.InvalidPayload (renderIssues payloadIssues)
            | _ when not defaultIssues.IsEmpty ->
                reject Rejections.InvalidAnswer ("The expiry's answer: " + renderIssues defaultIssues)
            | None, _ when spec.Lifetime = Lifetime.Turn ->
                reject Rejections.Invalid "Only a turn's requests can live as long as the turn."
            | Some turn, _ when (runningTurn turn state |> Result.isError) ->
                reject Rejections.StaleTurn ("Turn " + string turn + " is not running.")
            | _, Some expiry when expiry.At <= at ->
                reject Rejections.Invalid "The request would expire before it opens."
            | _ ->
                draft
                |> emit (
                    Event.RequestOpened(
                        {
                            Request = spec
                            Capabilities = kind.Capabilities
                            By = by
                            At = at
                            Seq = next draft
                        }
                        : PendingRequest
                    )
                )
                |> finish

    let private answer
        (kinds: RequestKinds)
        (by: Attribution)
        (at: int64)
        (id: string)
        (response: Json)
        (draft: Draft)
        : Result<Event list, Rejection> =
        match tryRequest id draft.State with
        | None -> reject Rejections.NotOpen ("No request " + Json.quote id + " is open.")
        | Some pending ->
            match pending.Request.Expiry, RequestKinds.tryFind pending.Request.Kind kinds with
            | Some expiry, _ when expiry.At <= at ->
                reject Rejections.Expired ("Request " + Json.quote id + " has expired.")
            | _, None ->
                reject Rejections.UnknownKind ("No request kind is called " + Json.quote pending.Request.Kind + ".")
            | _, Some kind ->
                match kind.CheckAnswer pending.Request.Payload response with
                | [] ->
                    draft
                    |> resolve by at true (Resolution.Answered response) pending
                    |> settle at
                    |> finish
                | issues -> reject Rejections.InvalidAnswer (renderIssues issues)

    /// What the commands that need an open session do.
    let private handleOpen
        (kinds: RequestKinds)
        (by: Attribution)
        (at: int64)
        (command: Command)
        (draft: Draft)
        : Result<Event list, Rejection> =
        let state = draft.State

        match command with
        | Command.Prompt(text, _) when text.Trim() = "" -> reject Rejections.Invalid "The prompt is empty."
        | Command.Prompt(text, _) when text.Length > MaxPromptChars ->
            reject
                Rejections.Invalid
                ("The prompt has "
                 + string text.Length
                 + " characters; at most "
                 + string MaxPromptChars
                 + " fit. Attach large content instead.")
        | Command.Prompt(text, mode) ->
            let input: Input =
                {
                    Id = next draft
                    By = by
                    At = at
                    Content = InputContent.Text text
                }

            let received =
                match mode with
                | PromptMode.Steer -> Event.PromptSteered input
                | PromptMode.Queue -> Event.PromptQueued input

            draft |> emit received |> settle at |> finish
        | Command.Withdraw id ->
            let isPrompt (input: Input) =
                input.Id = id
                && (match input.Content with
                    | InputContent.Text _ -> true
                    | _ -> false)

            if List.exists isPrompt state.Queue || List.exists isPrompt state.Inbox then
                draft |> emit (Event.PromptWithdrawn(id, by)) |> finish
            else
                reject Rejections.NotPending ("Prompt #" + string id + " is not waiting.")
        | Command.OpenRequest spec -> openRequest kinds by at spec draft
        | Command.Answer(id, response) -> answer kinds by at id response draft
        | Command.CancelRequest(id, reason) ->
            match tryRequest id state with
            | None -> reject Rejections.NotOpen ("No request " + Json.quote id + " is open.")
            | Some pending ->
                draft
                |> resolve by at true (Resolution.Cancelled reason) pending
                |> settle at
                |> finish
        | Command.NotifyJob(job, note) ->
            draft
            |> emit (
                Event.JobNotified(
                    {
                        Id = next draft
                        By = by
                        At = at
                        Content = InputContent.JobFinished(job, note)
                    }
                    : Input
                )
            )
            |> settle at
            |> finish
        | Command.SelectProfile selection ->
            let overrides = List.sortBy fst selection.Overrides

            if selection.Profile.Trim() = "" then
                reject Rejections.Invalid "A profile needs an id."
            elif selection.BasedOn <> state.Profile then
                reject
                    Rejections.StaleProfile
                    ("The session's profile is "
                     + state.Profile
                     + " now, not "
                     + selection.BasedOn
                     + ".")
            elif selection.Profile = state.Profile && overrides = state.Overrides then
                Ok []
            else
                draft
                |> emit (Event.ProfileSelected(selection.Profile, overrides, selection.Changed, by))
                |> finish
        | _ -> reject Rejections.Invalid ("Not a command for an open session: " + string command + ".")

    /// The command as its JSON reads, canonical: the JSON values it carries (payloads, answers,
    /// overrides, evidence) go into events as the store keeps them, so the state of a live actor equals
    /// the fold of the stored log. A command its codec refuses is refused. (JSON with duplicate keys has
    /// no canonical form: a defect of whoever built it, which throws.)
    let private canonical (command: Command) : Result<Command, Rejection> =
        match Fx.Sessions.Codecs.command.Decode(Json.canonicalize (Fx.Sessions.Codecs.command.Encode command)) with
        | Ok command -> Ok command
        | Error issues -> Error(Rejection.create Rejections.Invalid (Issue.renderAtMost 5 issues))

    /// What a canonical command means in a state.
    let private decide
        (kinds: RequestKinds)
        (state: SessionState)
        (sent: SessionCommand)
        : Result<Event list, Rejection> =
        let by = sent.By
        let at = sent.At
        let draft = { State = state; Events = [] }

        match state.Status, sent.Command with
        | Status.New, Command.Start start ->
            if start.Id.Trim() = "" then
                reject Rejections.Invalid "A session needs an id."
            elif start.Profile.Trim() = "" then
                reject Rejections.Invalid "A session needs a profile."
            else
                draft
                |> emit (
                    Event.Started(
                        { start with
                            Overrides = List.sortBy fst start.Overrides
                        },
                        by,
                        at
                    )
                )
                |> finish
        | Status.New, _ -> reject Rejections.New "The session has not started."
        | _, Command.Start _ -> reject Rejections.Started "The session has already started."
        | _, Command.Interrupt ->
            match state.Running with
            | Some running ->
                draft
                |> emit (Event.Interrupted(Some running.Turn, by))
                |> endTurn running.Turn TurnEnd.Interrupted by at
                |> finish
            | None -> draft |> emit (Event.Interrupted(None, by)) |> finish
        | _, Command.TakeInputs(turn, step) ->
            match runningTurn turn state with
            | Error rejection -> Error rejection
            | Ok _ when step < 1 -> reject Rejections.Invalid "Steps count from 1."
            | Ok _ when state.Inbox.IsEmpty -> Ok []
            | Ok _ ->
                draft
                |> emit (Event.InputsDelivered(turn, step, state.Inbox |> List.map _.Id))
                |> finish
        | _, Command.EndTurn(turn, result) ->
            runningTurn turn state
            |> Result.bind (fun _ -> draft |> endTurn turn (TurnEnd.Completed result) by at |> settle at |> finish)
        | _, Command.FailTurn(turn, reason) ->
            runningTurn turn state
            |> Result.bind (fun _ -> draft |> endTurn turn (TurnEnd.Failed reason) by at |> finish)
        | _, Command.AbandonTurn(turn, reason) ->
            runningTurn turn state
            |> Result.bind (fun _ -> draft |> endTurn turn (TurnEnd.Abandoned reason) by at |> finish)
        | _, Command.ExpireRequest id ->
            match tryRequest id state with
            | None -> reject Rejections.NotOpen ("No request " + Json.quote id + " is open.")
            | Some pending ->
                match pending.Request.Expiry with
                | Some expiry when expiry.At <= at ->
                    let response =
                        match expiry.Then with
                        | ExpiryPolicy.Cancel -> None
                        | ExpiryPolicy.Answer response -> Some response

                    draft
                    |> resolve by at true (Resolution.Expired response) pending
                    |> settle at
                    |> finish
                | _ -> reject Rejections.NotExpired ("Request " + Json.quote id + " has not expired.")
        | _, Command.Fork(child, position) ->
            let at' = defaultArg position state.Seq

            if child.Trim() = "" || child = state.Id then
                reject Rejections.Invalid "A fork needs an id of its own."
            elif state.Forks |> List.exists (fun fork -> fork.Child = child) then
                reject Rejections.ForkExists ("Fork " + Json.quote child + " exists.")
            elif at' < 1L || at' > state.Seq then
                reject
                    Rejections.Invalid
                    ("A fork starts after an event of the session, #1 to #" + string state.Seq + ".")
            else
                draft |> emit (Event.Forked(child, at', by)) |> finish
        | _, Command.ChildStarted child ->
            match state.Forks |> List.tryFind (fun fork -> fork.Child = child) with
            | Some fork when fork.Status = ForkStatus.Starting -> draft |> emit (Event.ForkReady child) |> finish
            | Some _ -> Ok []
            | None -> reject Rejections.Invalid ("No fork is called " + Json.quote child + ".")
        | _, Command.ChildFailed(child, reason) ->
            match state.Forks |> List.tryFind (fun fork -> fork.Child = child) with
            | Some fork when fork.Status = ForkStatus.Starting ->
                draft |> emit (Event.ForkFailed(child, reason)) |> finish
            | Some _ -> Ok []
            | None -> reject Rejections.Invalid ("No fork is called " + Json.quote child + ".")
        | Status.Open, Command.Close ->
            if state.Running.IsSome then
                reject Rejections.Busy "A turn is running; interrupt it first."
            else
                draft
                |> cancelAll "session closed" by at state.Requests
                |> emit (Event.Closed by)
                |> finish
        | _, Command.Close -> Ok []
        | Status.Open, Command.Archive ->
            if state.Running.IsSome then
                reject Rejections.Busy "A turn is running; interrupt it first."
            else
                draft
                |> cancelAll "session closed" by at state.Requests
                |> emit (Event.Closed by)
                |> emit (Event.Archived by)
                |> finish
        | Status.Closed, Command.Archive -> draft |> emit (Event.Archived by) |> finish
        | _, Command.Archive -> Ok []
        | Status.Open, Command.Reopen -> Ok []
        | _, Command.Reopen -> draft |> emit (Event.Reopened by) |> finish
        | Status.Open, command -> handleOpen kinds by at command draft
        | _, _ -> reject Rejections.Closed "The session is closed."

    /// Decides what a command means in a state (pure): the events to append, or a rejection.
    let handle (kinds: RequestKinds) (state: SessionState) (sent: SessionCommand) : Result<Event list, Rejection> =
        match canonical sent.Command with
        | Error rejection -> Error rejection
        | Ok command -> decide kinds state { sent with Command = command }

    /// Work the state says is in flight besides a turn: request timers, forks being started. An
    /// interrupt cancels every intent of the actor (the control path), so the session asks for these
    /// again after one, as `recover` does when an actor starts.
    let background (state: SessionState) : Intent list =
        [
            for pending in state.Requests do
                match pending.Request.Expiry with
                | Some expiry -> Intent.Expire(pending.Request.Id, expiry.At)
                | None -> ()
            for fork in state.Forks do
                if fork.Status = ForkStatus.Starting then
                    Intent.StartFork(
                        {
                            Parent = EventRef.create (stream state.Id) fork.UpTo
                            Start =
                                ({
                                    Id = fork.Child
                                    Profile = fork.Profile
                                    Overrides = fork.Overrides
                                    Parent = Some(EventRef.create (stream state.Id) fork.UpTo)
                                    Title = state.Title
                                }
                                : SessionStart)
                            Seq = fork.Seq
                        }
                        : ForkStart
                    )
        ]

    /// What should happen after an event, given the state after it.
    let react (state: SessionState) (event: Event) : Intent list =
        match event with
        | Event.TurnStarted _ ->
            match state.Running with
            | Some running ->
                [
                    Intent.RunTurn(
                        {
                            Session = state.Id
                            Turn = running.Turn
                            Profile = running.Profile
                            Inputs = running.Inputs
                            Seq = running.Seq
                        }
                        : TurnStart
                    )
                ]
            | None -> []
        | Event.RequestOpened pending ->
            match pending.Request.Expiry with
            | Some expiry -> [ Intent.Expire(pending.Request.Id, expiry.At) ]
            | None -> []
        | Event.Forked(child, _, _) ->
            background state
            |> List.filter (fun intent ->
                match intent with
                | Intent.StartFork fork -> fork.Start.Id = child
                | _ -> false)
        | Event.Interrupted(None, _)
        | Event.TurnEnded(_, TurnEnd.Interrupted, _) -> background state
        | _ -> []

    /// What an actor picks up when it starts on a log: a turn that was running when the host stopped
    /// is abandoned (nothing auto-resumes, spec §5), and the background work restarts.
    let recover (state: SessionState) : Intent list =
        let abandoned =
            match state.Running with
            | Some running -> [ Intent.Abandon running.Turn ]
            | None -> []

        abandoned @ background state

    /// The session machine. `kinds` are the request kinds it accepts.
    let machine (kinds: RequestKinds) : Machine<SessionCommand, Event, SessionState, Intent> =
        {
            Initial = initial
            Evolve = evolve
            Handle = handle kinds
            React = react
        }
