namespace Fx.Grants.Host

open System
open System.Threading
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Sdk
open Fx.Grants

/// What a grants log runs with.
[<NoEquality; NoComparison>]
type GrantsSettings =
    {
        /// The vocabulary: the core's actions and those extensions contribute (`GrantActions`).
        Actions: Actions
        /// Rules from host config and policy extensions (`GrantPolicy.rules`): they only tighten.
        Policy: PolicyRule list
        /// Gates over every check and request (`GrantPolicy.gates`): they only tighten.
        Gates: Contributed<Gate<GrantQuery>> list
        /// Where the log lives: the host-local store (`GrantStoreLog.hostLocal`) or a contributed one.
        Store: GrantStore
    }

    override this.ToString() =
        "GrantsSettings(" + this.Store.Id + ", " + string this.Policy.Length + " rules)"

module GrantsSettings =
    /// The core's actions, no policy, on a store.
    let create (store: GrantStore) : GrantsSettings =
        {
            Actions = Actions.builtIn
            Policy = []
            Gates = []
            Store = store
        }

/// The grants log of a host (spec §13): one actor, the single writer of the store's stream, that runs
/// the grants machine, its timers (expiries and request timeouts), and the effective rules. Agents and
/// extensions get only its `Grants` capability (`GrantsService.grants`): they check and request. People
/// answer, grant and revoke through the client protocol (`GrantsProtocol`). Use the functions of
/// `GrantsService`.
[<Sealed>]
type GrantsService<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
    internal
    (
        env: 'R,
        settings: GrantsSettings,
        stream: StreamId,
        log: EventLog<GrantEvent>,
        actor: Actor<GrantCommand, GrantEvent, GrantsState>
    ) =
    member _.Env = env
    member _.Settings = settings
    member _.Stream = stream
    member _.Log = log
    member _.Actor = actor

    override _.ToString() = "GrantsService(" + stream.Value + ")"

[<RequireQualifiedAccess>]
module GrantsService =
    /// What the host's own commands are attributed to.
    let private host (via: string) = Attribution.system via

    let private now<'R when 'R :> IClock> : Eff<'R, int64> =
        Clock.now |> Eff.map (fun instant -> instant.UnixMicroseconds)

    /// Sleeps until a wall-clock time: delays run on the monotonic clock, which may drift from it.
    let private until<'R when 'R :> IClock and 'R :> IScope> (at: int64) : Eff<'R, int64> =
        eff {
            let! started = now
            let mutable current = started

            while current < at do
                do! Clock.sleep (TimeSpan.FromTicks((at - current) * 10L))
                let! later = now
                current <- later

            return current
        }

    let private interpret<'R when 'R :> IClock and 'R :> IScope> (intent: GrantIntent) : Eff<'R, GrantCommand list> =
        eff {
            match intent with
            | GrantIntent.ExpireAt(grant, at) ->
                let! current = until at

                return
                    [
                        {
                            Command = GrantOp.Expire grant
                            By = GrantLog.SystemPrincipal
                            At = current
                        }
                    ]
            | GrantIntent.TimeOutAt(request, at) ->
                let! current = until at

                return
                    [
                        {
                            Command = GrantOp.Close(request, CloseReason.TimedOut)
                            By = GrantLog.SystemPrincipal
                            At = current
                        }
                    ]
        }

    /// Starts the grants log on its store: rebuilds the state, restarts the timers of grants and
    /// requests that expire, then serves. Stop it with `stop`.
    let start<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (settings: GrantsSettings)
        : Eff<'R, GrantsService<'R>> =
        eff {
            let stream = GrantMachine.stream settings.Store.Id
            let log = GrantStoreLog.toEventLog settings.Store stream

            let spec =
                { ActorSpec.create stream (GrantMachine.machine settings.Actions) log interpret with
                    Recover = GrantLog.recover
                }

            let! actor = Actor.start spec
            let! env = Eff.ask
            return GrantsService(env, settings, stream, log, actor)
        }

    let stop<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        : Eff<'R, unit> =
        Actor.stop service.Actor

    /// The state now and the sequence number of its last event.
    let snapshot<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        : Snapshot<GrantsState> =
        Actor.snapshot service.Actor

    /// The state and sequence number the grants log has at least reached: the actor's snapshot, then
    /// the events the log has beyond it.
    let private current<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        (after: int64)
        (state: GrantsState)
        =
        eff {
            let! page = EventLog.read service.Log service.Stream after EventLog.PageSize
            return List.fold (fun s (r: Recorded<GrantEvent>) -> GrantLog.evolve s r.Event) state page, page.Length
        }

    /// The state as of the log's head. The actor's snapshot can lag the log for a moment after an
    /// append, and a waiter that read an answer from the log must not then be judged without it.
    let private latest<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        : Eff<'R, GrantsState> =
        eff {
            let started = snapshot service
            let! state, _ = current service started.Seq started.State
            return state
        }

    /// The effective rules and the policy gates on a query: the maximum of their decisions.
    let private decide<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        (state: GrantsState)
        (query: GrantQuery)
        : Eff<'R, GrantCheck> =
        eff {
            let settings = service.Settings
            let! at = now

            let rules =
                Rules.decide settings.Actions settings.Policy at state query |> Rules.toCheck

            if settings.Gates.IsEmpty then
                return rules
            else
                let! verdict = Hooks.decide settings.Gates query
                let reasons (rs: Reason list) = rs |> List.map string

                match rules, verdict with
                | _, Verdict.Allow -> return rules
                | GrantCheck.Denied mine, Verdict.Deny theirs -> return GrantCheck.Denied(mine @ reasons theirs)
                | _, Verdict.Deny theirs -> return GrantCheck.Denied(reasons theirs)
                | GrantCheck.Denied _, Verdict.Ask _ -> return rules
                | GrantCheck.MustAsk mine, Verdict.Ask theirs -> return GrantCheck.MustAsk(mine @ reasons theirs)
                | GrantCheck.Allowed _, Verdict.Ask theirs -> return GrantCheck.MustAsk(reasons theirs)
        }

    let private valid (settings: GrantsSettings) (query: GrantQuery) : Result<unit, GrantError> =
        match Actions.kindOf query.Action settings.Actions with
        | None -> Error(GrantError.UnknownAction query.Action.Value)
        | Some kind ->
            match Resource.validate kind query.Resource with
            | Error reason -> Error(GrantError.InvalidResource(query.Resource, reason))
            | Ok() ->
                GrantLog.validateContext query.Context
                |> Result.mapError GrantError.InvalidRequest

    /// What the effective rules say about a query now.
    let check<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        (query: GrantQuery)
        : Eff<'R, Result<GrantCheck, GrantError>> =
        eff {
            match valid service.Settings query with
            | Error e -> return Error e
            | Ok() ->
                let! state = latest service
                let! decision = decide service state query
                return Ok decision
        }

    /// Sends a command to the log, stamped with `at`. A refusal is read again from the state as a
    /// typed error; if the state now accepts the command, the refusal was a race (`Conflict`).
    let private sendAt<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        (by: Attribution)
        (key: string)
        (at: int64)
        (op: GrantOp)
        : Eff<'R, Result<unit, GrantError>> =
        eff {
            let command =
                {
                    Command = op
                    By = by.Principal
                    At = at
                }

            match! Actor.send service.Actor (Envelope.create (IdempotencyKey key) by command) with
            | Accepted _ -> return Ok()
            | Stopped -> return Error(GrantError.Unavailable "the grants log stopped")
            | Rejected rejection ->
                match GrantLog.decide service.Settings.Actions (snapshot service).State command with
                | Error e -> return Error e
                | Ok _ -> return Error(GrantError.Conflict(rejection.Code, rejection.Message))
        }

    /// Sends a command to the log, stamped with the clock's time now.
    let private send<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        (by: Attribution)
        (key: string)
        (op: GrantOp)
        : Eff<'R, Result<unit, GrantError>> =
        eff {
            let! at = now
            return! sendAt service by key at op
        }

    let private outcomeOf (state: GrantsState) (id: RequestId) : AskOutcome =
        match GrantLog.resolution state id with
        | Some resolution -> AskOutcome.Settled resolution
        | None -> AskOutcome.Pending id

    /// Settles an ask from the rules, or records a durable pending request (spec §5). A key is one
    /// request: asking again while it is pending gets it, once it is allowed gets that. Once it was
    /// denied or cancelled, the rules settle the ask if they allow or deny now (a grant made since), and
    /// otherwise it keeps its resolution: prompting again takes a new key.
    let request<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        (ask: GrantAsk)
        : Eff<'R, Result<AskOutcome, GrantError>> =
        eff {
            let settings = service.Settings
            let! state = latest service
            let! at = now

            match state.Keys.TryFind ask.Key with
            | Some existing ->
                match GrantLog.resolution state existing with
                | None
                | Some(Resolution.Allowed _) -> return Ok(outcomeOf state existing)
                | Some earlier ->
                    match! decide service state state.Requests[existing].Request.Query with
                    | GrantCheck.Allowed grant -> return Ok(AskOutcome.Settled(Resolution.Allowed grant))
                    | GrantCheck.Denied reasons -> return Ok(AskOutcome.Settled(Resolution.Denied reasons))
                    | GrantCheck.MustAsk _ -> return Ok(AskOutcome.Settled earlier)
            | None ->
                // Validate before deciding: an invalid ask is an error, not a denial.
                match GrantLog.request settings.Actions ask (RequestId "-") at with
                | Error e -> return Error e
                | Ok draft ->
                    match! decide service state draft.Query with
                    | GrantCheck.Allowed grant -> return Ok(AskOutcome.Settled(Resolution.Allowed grant))
                    | GrantCheck.Denied reasons -> return Ok(AskOutcome.Settled(Resolution.Denied reasons))
                    | GrantCheck.MustAsk _ ->
                        let! id = Ids.newId
                        let! at = now

                        match GrantLog.request settings.Actions ask (RequestId(string id)) at with
                        | Error e -> return Error e
                        | Ok made ->
                            let via =
                                match ask.Requester.Agent, ask.Requester.Extension with
                                | Some agent, _ -> "agent:" + agent
                                | None, Some extension -> "extension:" + extension
                                | None, None -> "grants"

                            let by = Attribution.create ask.Requester.Principal via

                            // The command carries the request's own time (the machine checks it).
                            let! sent = sendAt service by ("request/" + ask.Key) at (GrantOp.Request made)

                            match sent with
                            | Error e -> return Error e
                            | Ok() ->
                                let state = (snapshot service).State

                                match state.Keys.TryFind ask.Key with
                                | Some id -> return Ok(outcomeOf state id)
                                | None -> return Error(GrantError.Unavailable "the request was not recorded")
        }

    /// Waits until a request is resolved. While it waits, a grant made since that allows it closes it
    /// as covered, and rules that now deny it close it as refused (so answering one request can settle
    /// others it covers: batched prompts, spec §19.4).
    let await<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        (id: RequestId)
        : Eff<'R, Result<Resolution, GrantError>> =
        eff {
            let started = snapshot service
            let mutable seq = started.Seq
            let mutable state = started.State
            let mutable result = None

            while result.IsNone do
                let! caught, count = current service seq state
                state <- caught
                seq <- seq + int64 count

                match state.Requests.TryFind id with
                | None -> result <- Some(Error(GrantError.UnknownRequest id))
                | Some entry ->
                    match entry.Resolution with
                    | Some resolution -> result <- Some(Ok resolution)
                    | None ->
                        let! rules = decide service state entry.Request.Query

                        let why =
                            match rules with
                            | GrantCheck.Allowed grant -> Some(CloseReason.Covered grant)
                            | GrantCheck.Denied reasons -> Some(CloseReason.Refused reasons)
                            | GrantCheck.MustAsk _ -> None

                        match why with
                        | Some why ->
                            // Keyed by the position it was decided at, so a retry deduplicates and a
                            // later decision is a new command.
                            let key = "close/" + id.Value + "/" + string seq

                            match! send service (host "await") key (GrantOp.Close(id, why)) with
                            | Ok() -> ()
                            | Error(GrantError.Unavailable _ as e) -> result <- Some(Error e)
                            | Error _ ->
                                // Answered meanwhile: the log shows it soon.
                                let! _ = EventLog.waitPast service.Log service.Stream seq
                                ()
                        | None ->
                            if count = 0 then
                                let! _ = EventLog.waitPast service.Log service.Stream seq
                                ()

            return result.Value
        }

    /// Withdraws a pending request.
    let withdraw<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        (id: RequestId)
        (reason: string)
        : Eff<'R, Result<unit, GrantError>> =
        send service (host "withdraw") ("withdraw/" + id.Value) (GrantOp.Close(id, CloseReason.Withdrawn reason))

    /// Runs one of the service's effects in its environment, with the caller's cancellation.
    let within<'R, 'S, 'T when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R> and 'S :> IScope>
        (service: GrantsService<'R>)
        (effect: Eff<'R, 'T>)
        : Eff<'S, 'T> =
        fun caller ->
            let env = service.Env

            effect (
                env.WithScope
                    { env.Scope with
                        Cancellation = caller.Scope.Cancellation
                    }
            )

    /// The grants capability for agents, strategies and extensions: check, request, wait, withdraw.
    /// Calls run in the service's environment with the caller's cancellation.
    let grants<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        : Grants =
        let run (effect: Eff<'R, 'T>) (cancellation: CancellationToken) =
            let env = service.Env

            effect (
                env.WithScope
                    { env.Scope with
                        Cancellation = cancellation
                    }
            )

        {
            Check = fun query -> run (check service query)
            Request = fun ask -> run (request service ask)
            Await = fun id -> run (await service id)
            Withdraw = fun id reason -> run (withdraw service id reason)
        }

    // ---- People: the client protocol --------------------------------------------------------------

    /// A person answers a pending request (spec §19.4: once, session, repo, host). Answering again
    /// gets the first outcome. The resolution, as the requester sees it.
    let answer<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        (by: Attribution)
        (request: RequestId)
        (answer: GrantAnswer)
        : Eff<'R, Result<Resolution, GrantError>> =
        eff {
            let! id = Ids.newId
            // The same answer again gets the first outcome; a different one is a new command.
            let key =
                "answer/"
                + request.Value
                + "/"
                + Fx.Contracts.Json.serializeCanonical (Codecs.answer.Encode answer)

            match! send service by key (GrantOp.Answer(request, answer, GrantId(string id))) with
            | Error e -> return Error e
            | Ok() ->
                match GrantLog.resolution (snapshot service).State request with
                | Some resolution -> return Ok resolution
                | None -> return Error(GrantError.NotPending request)
        }

    /// A person grants directly (the grants view): session, repository or host scope.
    let grant<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        (by: Attribution)
        (draft: GrantDraft)
        : Eff<'R, Result<Grant, GrantError>> =
        eff {
            let! id = Ids.newId
            let! at = now

            let made =
                {
                    Id = GrantId(string id)
                    Action = draft.Action
                    Resource = draft.Resource
                    Effect = draft.Effect
                    Scope = draft.Scope
                    By = by.Principal
                    At = at
                    Expires = draft.Expires
                    Reason = draft.Reason
                    Origin = GrantOrigin.Direct by.Via
                }

            // The command is stamped with its own time: the grant's must match.
            let command =
                {
                    Command = GrantOp.Grant made
                    By = by.Principal
                    At = at
                }

            match! Actor.send service.Actor (Envelope.create (IdempotencyKey("grant/" + made.Id.Value)) by command) with
            | Accepted _ -> return Ok made
            | Stopped -> return Error(GrantError.Unavailable "the grants log stopped")
            | Rejected rejection ->
                match GrantLog.decide service.Settings.Actions (snapshot service).State command with
                | Error e -> return Error e
                | Ok _ -> return Error(GrantError.Conflict(rejection.Code, rejection.Message))
        }

    /// A person revokes a grant.
    let revoke<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        (by: Attribution)
        (grant: GrantId)
        (reason: string option)
        : Eff<'R, Result<unit, GrantError>> =
        send service by ("revoke/" + grant.Value + "/" + by.Principal) (GrantOp.Revoke(grant, reason))

    /// A session ended (T125/T129 call this): its grants expire and its pending requests close.
    let endSession<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        (session: string)
        : Eff<'R, Result<unit, GrantError>> =
        send service (host "session") ("end-session/" + session) (GrantOp.EndSession session)

    /// The grants in force (or all of them), as of a sequence number.
    let list<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        (all: bool)
        : GrantList =
        let current = snapshot service

        {
            Seq = current.Seq
            Grants =
                current.State.Grants
                |> Map.toList
                |> List.map snd
                |> List.filter (fun entry -> all || entry.Status = GrantStatus.Active)
        }

    /// The pending requests, oldest first, as of a sequence number.
    let pending<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        : PendingList =
        let current = snapshot service

        {
            Seq = current.Seq
            Requests = GrantLog.pending current.State
        }

    /// Follows the log after a sequence number: replays, then goes live, until `handle` returns false
    /// or the scope is cancelled. Clients fold the notices with `GrantLog.evolve` (spec §5).
    let subscribe<'R when 'R :> IClock and 'R :> IIds and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (service: GrantsService<'R>)
        (after: int64)
        (handle: GrantNotice -> Eff<'R, bool>)
        : Eff<'R, int64> =
        EventLog.subscribe service.Log service.Stream after (fun recorded ->
            handle
                {
                    Seq = recorded.Seq
                    Event = recorded.Event
                })
