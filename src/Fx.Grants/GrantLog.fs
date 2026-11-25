namespace Fx.Grants

open Fx.Identity

/// The grants log as a machine (spec §4.4, §13.2): `initial`, `evolve`, `decide` (the machine's
/// `Handle`, with typed errors), `react` and `recover`. Pure; the host assembles the `Machine` value
/// (Fx.Grants.Host) and clients fold the same events with `evolve`.
[<RequireQualifiedAccess>]
module GrantLog =
    /// The longest summary, reason or evidence label, in UTF-16 code units.
    [<Literal>]
    let MaxText = 2000

    /// The longest evidence text: events stay far below the store's 1 MiB.
    [<Literal>]
    let MaxEvidenceText = 16384

    [<Literal>]
    let MaxEvidence = 32

    [<Literal>]
    let MaxSuggestions = 16

    /// The longest id or key.
    [<Literal>]
    let MaxId = 512

    let initial: GrantsState =
        {
            Grants = Map.empty
            Requests = Map.empty
            Keys = Map.empty
            Seq = 0L
        }

    /// The principal of the host's own commands.
    [<Literal>]
    let SystemPrincipal = "system"

    /// Whether a principal may grant, answer and revoke: a person, as its id says
    /// (`Fx.Identity.PrincipalId.isPerson`: `user:…`), never a device, a service or the host itself.
    /// The capability split is the first line: agents act through `IGrants`, which can only request.
    let isPerson (principal: string) : bool = PrincipalId.isPerson principal

    let private scopeOrder =
        [ ScopeKind.Once; ScopeKind.Session; ScopeKind.Repo; ScopeKind.Host ]

    let private isRepoPath (path: string) : bool = Resource.isRepoPath path

    let private text (what: string) (max: int) (value: string) : Result<unit, string> =
        if System.String.IsNullOrWhiteSpace value then
            Error(what + " cannot be empty")
        elif value.Length > max then
            Error(what + " has at most " + string max + " characters")
        else
            Ok()

    let private all (checks: Result<unit, string> list) : Result<unit, string> =
        checks
        |> List.tryPick (fun check ->
            match check with
            | Error message -> Some message
            | Ok() -> None)
        |> Option.map Error
        |> Option.defaultValue (Ok())

    let private optionalText (what: string) (value: string option) =
        match value with
        | Some value -> text what MaxText value
        | None -> Ok()

    /// Problems with a context, if any: ids and a canonical repository path.
    let validateContext (context: GrantContext) : Result<unit, string> =
        all
            [
                optionalText "a session id" context.Session
                optionalText "a run id" context.Run
                match context.Repo with
                | None -> Ok()
                | Some place ->
                    all
                        [
                            text "a repository id" MaxText place.Repo
                            (if isRepoPath place.Path then
                                 Ok()
                             else
                                 Error("the repository path " + place.Path + " is not canonical and repo-relative"))
                        ]
            ]

    /// The scopes a request offers: the action's, narrowed to what the requester offers (if anything),
    /// and to what the context makes possible (a session, a repository). Always `Once` first.
    let offer (def: ActionDef) (requested: ScopeKind list) (context: GrantContext) : ScopeKind list =
        scopeOrder
        |> List.filter (fun kind ->
            kind = ScopeKind.Once
            || (List.contains kind def.Scopes
                && (requested.IsEmpty || List.contains kind requested)
                && (match kind with
                    | ScopeKind.Session -> context.Session.IsSome
                    | ScopeKind.Repo -> context.Repo.IsSome
                    | _ -> true)))

    let private validateAsk (def: ActionDef) (ask: GrantAsk) (at: int64) : Result<unit, GrantError> =
        match Resource.validate def.Resource ask.Resource with
        | Error reason -> Error(GrantError.InvalidResource(ask.Resource, reason))
        | Ok() ->
            let invalid =
                all
                    [
                        text "the key" MaxId ask.Key
                        text "the summary" MaxText ask.Summary
                        text "the principal" MaxText ask.Requester.Principal
                        optionalText "the agent" ask.Requester.Agent
                        optionalText "the extension" ask.Requester.Extension
                        validateContext ask.Context
                        (if ask.Evidence.Length > MaxEvidence then
                             Error("at most " + string MaxEvidence + " pieces of evidence")
                         else
                             Ok())
                        yield!
                            ask.Evidence
                            |> List.map (fun e ->
                                all
                                    [
                                        text "an evidence label" MaxText e.Label
                                        text "evidence" MaxEvidenceText e.Text
                                    ])
                        (if ask.Suggestions.Length > MaxSuggestions then
                             Error("at most " + string MaxSuggestions + " suggestions")
                         else
                             Ok())
                        yield!
                            ask.Suggestions
                            |> List.map (fun pattern ->
                                match Pattern.validate def.Resource pattern with
                                | Error reason -> Error("the suggestion " + pattern + " is not a pattern: " + reason)
                                | Ok() when not (Pattern.matches def.Resource pattern ask.Resource) ->
                                    Error("the suggestion " + pattern + " does not match the resource")
                                | Ok() -> Ok())
                        (match ask.Expires with
                         | Some expires when expires <= at -> Error "the request would expire before it is made"
                         | _ -> Ok())
                    ]

            invalid |> Result.mapError GrantError.InvalidRequest

    /// The request an ask makes, with its id and time, or why it cannot be made.
    let request (actions: Actions) (ask: GrantAsk) (id: RequestId) (at: int64) : Result<GrantRequest, GrantError> =
        match Actions.tryFind ask.Action actions with
        | None -> Error(GrantError.UnknownAction ask.Action.Value)
        | Some def ->
            validateAsk def ask at
            |> Result.map (fun () ->
                {
                    Id = id
                    Key = ask.Key
                    Action = ask.Action
                    Resource = ask.Resource
                    Context = ask.Context
                    Requester = ask.Requester
                    Summary = ask.Summary
                    Evidence = ask.Evidence
                    Suggestions = ask.Suggestions
                    Offer = offer def ask.Scopes ask.Context
                    At = at
                    Expires = ask.Expires
                })

    /// Problems with a request a command records, at the command's time.
    let private validateRequest (actions: Actions) (request: GrantRequest) (at: int64) : Result<unit, GrantError> =
        match Actions.tryFind request.Action actions with
        | None -> Error(GrantError.UnknownAction request.Action.Value)
        | Some def ->
            let ask =
                {
                    Key = request.Key
                    Action = request.Action
                    Resource = request.Resource
                    Context = request.Context
                    Requester = request.Requester
                    Summary = request.Summary
                    Evidence = request.Evidence
                    Suggestions = request.Suggestions
                    Scopes = request.Offer
                    Expires = request.Expires
                }

            validateAsk def ask at
            |> Result.bind (fun () ->
                let possible = offer def [] request.Context

                all
                    [
                        text "the request's id" MaxId request.Id.Value
                        (if request.At = at then
                             Ok()
                         else
                             Error "a request is made at the command's time")
                        (if
                             List.contains ScopeKind.Once request.Offer
                             && request.Offer |> List.forall (fun kind -> List.contains kind possible)
                             && List.distinct request.Offer = request.Offer
                         then
                             Ok()
                         else
                             Error "a request offers once, and only scopes its action and context allow")
                    ]
                |> Result.mapError GrantError.InvalidRequest)

    /// The ask a request was made from.
    let askOf (request: GrantRequest) : GrantAsk =
        {
            Key = request.Key
            Action = request.Action
            Resource = request.Resource
            Context = request.Context
            Requester = request.Requester
            Summary = request.Summary
            Evidence = request.Evidence
            Suggestions = request.Suggestions
            Scopes = request.Offer
            Expires = request.Expires
        }

    /// Whether an answer fits a request, at a time: a scope the request offers, a pattern of the
    /// action's kind that matches the requested resource, a repository path that covers the
    /// request's, an expiry in the future. The same check a session's request kind uses (T125).
    let checkAnswer
        (actions: Actions)
        (request: GrantRequest)
        (answer: GrantAnswer)
        (at: int64)
        : Result<unit, string> =
        match Actions.kindOf request.Action actions with
        | None -> Error("no action is named " + request.Action.Value)
        | Some kind ->
            let ignoreCase = answer.Effect = GrantEffect.Deny

            all
                [
                    (if List.contains answer.Scope.Kind request.Offer then
                         Ok()
                     else
                         Error("the request does not offer the scope " + string answer.Scope.Kind))
                    (match answer.Scope, request.Context.Repo with
                     | ScopeChoice.Repo(Some path), Some place ->
                         if path = "" || not (isRepoPath path) then
                             Error(
                                 "the path "
                                 + path
                                 + " is not a canonical repo-relative path (use no path for the root)"
                             )
                         elif not (Resource.isUnder ignoreCase path place.Path) then
                             Error("the path " + path + " does not cover the request's " + place.Path)
                         else
                             Ok()
                     | _ -> Ok())
                    (match answer.Resource with
                     | None -> Ok()
                     | Some pattern ->
                         match Pattern.validate kind pattern with
                         | Error reason -> Error("the pattern " + pattern + " is invalid: " + reason)
                         | Ok() ->
                             let matched =
                                 if ignoreCase then
                                     Pattern.matchesIgnoringCase kind pattern request.Resource
                                 else
                                     Pattern.matches kind pattern request.Resource

                             if matched then
                                 Ok()
                             else
                                 Error("the pattern " + pattern + " does not match the requested resource"))
                    (match answer.Expires with
                     | Some expires when expires <= at -> Error "the grant would expire before it is made"
                     | _ -> Ok())
                    optionalText "the reason" answer.Reason
                ]

    /// The grant an answer makes (not for "deny once", which makes none).
    let grantOf (request: GrantRequest) (answer: GrantAnswer) (id: GrantId) (by: string) (at: int64) : Grant =
        let scope =
            match answer.Scope, request.Context with
            | ScopeChoice.Session, { Session = Some session } -> GrantScope.Session session
            | ScopeChoice.Repo path, { Repo = Some place } -> GrantScope.Repo { Repo = place.Repo; Path = path }
            | ScopeChoice.Host, _ -> GrantScope.Host
            | _ -> GrantScope.Once request.Id

        {
            Id = id
            Action = request.Action
            Resource = answer.Resource |> Option.defaultValue (Pattern.exact request.Resource)
            Effect = answer.Effect
            Scope = scope
            By = by
            At = at
            Expires = answer.Expires
            Reason = answer.Reason
            Origin = GrantOrigin.Answer request.Id
        }

    let private validateGrant (actions: Actions) (grant: Grant) (command: GrantCommand) : Result<unit, GrantError> =
        match Actions.kindOf grant.Action actions with
        | None -> Error(GrantError.UnknownAction grant.Action.Value)
        | Some kind ->
            match Pattern.validate kind grant.Resource with
            | Error reason -> Error(GrantError.InvalidPattern(grant.Resource, reason))
            | Ok() ->
                all
                    [
                        text "the grant's id" MaxId grant.Id.Value
                        (if grant.By = command.By && grant.At = command.At then
                             Ok()
                         else
                             Error "a grant is made by the command's sender, at the command's time")
                        (match grant.Origin with
                         | GrantOrigin.Direct via -> text "the client" MaxText via
                         | GrantOrigin.Answer _ -> Error "only an answer makes a grant that answers a request")
                        (match grant.Scope with
                         | GrantScope.Once _ -> Error "a grant for once answers a request"
                         | GrantScope.Session session -> text "the session" MaxText session
                         | GrantScope.Repo scope ->
                             all
                                 [
                                     text "the repository" MaxText scope.Repo
                                     (match scope.Path with
                                      | Some path when path = "" || not (isRepoPath path) ->
                                          Error("the path " + path + " is not a canonical repo-relative path")
                                      | _ -> Ok())
                                 ]
                         | GrantScope.Host -> Ok())
                        (match grant.Expires with
                         | Some expires when expires <= grant.At -> Error "the grant would expire before it is made"
                         | _ -> Ok())
                        optionalText "the reason" grant.Reason
                    ]
                |> Result.mapError GrantError.InvalidGrant

    let private person (command: GrantCommand) : Result<unit, GrantError> =
        if isPerson command.By then
            Ok()
        else
            Error(GrantError.NotAPerson command.By)

    let private pendingRequest (state: GrantsState) (id: RequestId) : Result<GrantRequest, GrantError> =
        match Map.tryFind id state.Requests with
        | None -> Error(GrantError.UnknownRequest id)
        | Some {
                   Request = request
                   Status = RequestStatus.Pending
               } -> Ok request
        | Some _ -> Error(GrantError.NotPending id)

    let private activeGrant (state: GrantsState) (id: GrantId) : Result<Grant, GrantError> =
        match Map.tryFind id state.Grants with
        | None -> Error(GrantError.UnknownGrant id)
        | Some {
                   Grant = grant
                   Status = GrantStatus.Active
               } -> Ok grant
        | Some _ -> Error(GrantError.NotActive id)

    let private freshGrantId (state: GrantsState) (id: GrantId) : Result<unit, GrantError> =
        if state.Grants.ContainsKey id then
            Error(GrantError.DuplicateId id.Value)
        else
            Ok()

    /// The machine's `Handle`: the events a command appends in a state, or why it is refused.
    let decide (actions: Actions) (state: GrantsState) (command: GrantCommand) : Result<GrantEvent list, GrantError> =
        let at = command.At

        match command.Command with
        | GrantOp.Request request ->
            if state.Keys.ContainsKey request.Key then
                // The same ask again: the request exists.
                Ok []
            elif state.Requests.ContainsKey request.Id then
                Error(GrantError.DuplicateId request.Id.Value)
            else
                validateRequest actions request at
                |> Result.map (fun () -> [ GrantEvent.Requested request ])
        | GrantOp.Answer(id, answer, grantId) ->
            person command
            |> Result.bind (fun () -> pendingRequest state id)
            |> Result.bind (fun request ->
                match checkAnswer actions request answer at with
                | Error reason -> Error(GrantError.InvalidAnswer reason)
                | Ok() ->
                    match answer.Effect, answer.Scope with
                    | GrantEffect.Deny, ScopeChoice.Once ->
                        Ok
                            [
                                GrantEvent.Denied
                                    {
                                        Request = id
                                        By = command.By
                                        At = at
                                        Reason = answer.Reason
                                    }
                            ]
                    | _ ->
                        freshGrantId state grantId
                        |> Result.map (fun () -> [ GrantEvent.Granted(grantOf request answer grantId command.By at) ]))
        | GrantOp.Grant grant ->
            person command
            |> Result.bind (fun () -> freshGrantId state grant.Id)
            |> Result.bind (fun () -> validateGrant actions grant command)
            |> Result.map (fun () -> [ GrantEvent.Granted grant ])
        | GrantOp.Revoke(id, reason) ->
            person command
            |> Result.bind (fun () -> activeGrant state id)
            |> Result.bind (fun _ ->
                match optionalText "the reason" reason with
                | Error message -> Error(GrantError.InvalidGrant message)
                | Ok() ->
                    Ok
                        [
                            GrantEvent.Revoked
                                {
                                    Grant = id
                                    By = command.By
                                    At = at
                                    Reason = reason
                                }
                        ])
        | GrantOp.Expire id ->
            activeGrant state id
            |> Result.bind (fun grant ->
                match grant.Expires with
                | Some expires when expires <= at -> Ok [ GrantEvent.Expired { Grant = id; At = at } ]
                | _ -> Error(GrantError.NotDue("the expiry of " + id.Value)))
        | GrantOp.Close(id, why) ->
            pendingRequest state id
            |> Result.bind (fun request ->
                let valid =
                    match why with
                    | CloseReason.TimedOut ->
                        match request.Expires with
                        | Some expires when expires <= at -> Ok()
                        | _ -> Error(GrantError.NotDue("the timeout of " + id.Value))
                    | CloseReason.Covered grant ->
                        match Map.tryFind grant state.Grants with
                        | Some entry when entry.Grant.Effect = GrantEffect.Allow -> Ok()
                        | Some _ -> Error(GrantError.InvalidRequest("grant " + grant.Value + " does not allow"))
                        | None -> Error(GrantError.UnknownGrant grant)
                    | CloseReason.Refused [] -> Error(GrantError.InvalidRequest "a refusal needs a reason")
                    | CloseReason.Withdrawn reason ->
                        text "the reason" MaxText reason |> Result.mapError GrantError.InvalidRequest
                    | CloseReason.Refused _
                    | CloseReason.SessionEnded -> Ok()

                valid
                |> Result.map (fun () -> [ GrantEvent.Closed { Request = id; At = at; Why = why } ]))
        | GrantOp.EndSession session ->
            let expired =
                state.Grants
                |> Map.toList
                |> List.choose (fun (id, entry) ->
                    match entry with
                    | {
                          Status = GrantStatus.Active
                          Grant = { Scope = GrantScope.Session s }
                      } when s = session -> Some(GrantEvent.Expired { Grant = id; At = at })
                    | _ -> None)

            let closed =
                state.Requests
                |> Map.toList
                |> List.choose (fun (id, entry) ->
                    match entry.Status with
                    | RequestStatus.Pending when entry.Request.Context.Session = Some session ->
                        Some(
                            GrantEvent.Closed
                                {
                                    Request = id
                                    At = at
                                    Why = CloseReason.SessionEnded
                                }
                        )
                    | _ -> None)

            Ok(expired @ closed)

    /// The machine's `Evolve`: total, so it accepts every event any version decided (events about
    /// unknown grants or requests change nothing but the sequence number).
    let evolve (state: GrantsState) (event: GrantEvent) : GrantsState =
        let state = { state with Seq = state.Seq + 1L }

        let setGrant (id: GrantId) (status: GrantStatus) =
            match Map.tryFind id state.Grants with
            | Some entry ->
                { state with
                    Grants = Map.add id { entry with Status = status } state.Grants
                }
            | None -> state

        let setRequest (state: GrantsState) (id: RequestId) (status: RequestStatus) =
            match Map.tryFind id state.Requests with
            | Some entry ->
                { state with
                    Requests = Map.add id { entry with Status = status } state.Requests
                }
            | None -> state

        match event with
        | GrantEvent.Requested request ->
            { state with
                Requests =
                    Map.add
                        request.Id
                        {
                            Request = request
                            Status = RequestStatus.Pending
                        }
                        state.Requests
                Keys = Map.add request.Key request.Id state.Keys
            }
        | GrantEvent.Granted grant ->
            let status =
                match grant.Scope with
                | GrantScope.Once _ -> GrantStatus.Spent
                | _ -> GrantStatus.Active

            let state =
                { state with
                    Grants = Map.add grant.Id { Grant = grant; Status = status } state.Grants
                }

            match grant.Origin with
            | GrantOrigin.Answer request -> setRequest state request (RequestStatus.Answered grant)
            | GrantOrigin.Direct _ -> state
        | GrantEvent.Denied denial -> setRequest state denial.Request (RequestStatus.Rejected denial)
        | GrantEvent.Revoked revocation -> setGrant revocation.Grant (GrantStatus.Revoked revocation)
        | GrantEvent.Expired expiry -> setGrant expiry.Grant (GrantStatus.Expired expiry.At)
        | GrantEvent.Closed closure -> setRequest state closure.Request (RequestStatus.Closed closure)

    /// The machine's `React`: timers for expiries and timeouts.
    let react (state: GrantsState) (event: GrantEvent) : GrantIntent list =
        match event with
        | GrantEvent.Granted { Id = id; Expires = Some at } ->
            match Map.tryFind id state.Grants with
            | Some { Status = GrantStatus.Active } -> [ GrantIntent.ExpireAt(id, at) ]
            | _ -> []
        | GrantEvent.Requested { Id = id; Expires = Some at } -> [ GrantIntent.TimeOutAt(id, at) ]
        | _ -> []

    /// The machine's `Recover`: the timers of a rebuilt state.
    let recover (state: GrantsState) : GrantIntent list =
        let expiries =
            state.Grants
            |> Map.toList
            |> List.choose (fun (id, entry) ->
                match entry with
                | {
                      Status = GrantStatus.Active
                      Grant = { Expires = Some at }
                  } -> Some(GrantIntent.ExpireAt(id, at))
                | _ -> None)

        let timeouts =
            state.Requests
            |> Map.toList
            |> List.choose (fun (id, entry) ->
                match entry with
                | {
                      Status = RequestStatus.Pending
                      Request = { Expires = Some at }
                  } -> Some(GrantIntent.TimeOutAt(id, at))
                | _ -> None)

        expiries @ timeouts

    /// The pending requests, oldest first.
    let pending (state: GrantsState) : GrantRequest list =
        state.Requests
        |> Map.toList
        |> List.choose (fun (_, entry) ->
            match entry.Status with
            | RequestStatus.Pending -> Some entry.Request
            | _ -> None)
        |> List.sortBy (fun request -> request.At, request.Id.Value)

    /// How a request ended, if it did.
    let resolution (state: GrantsState) (id: RequestId) : Resolution option =
        Map.tryFind id state.Requests |> Option.bind (fun entry -> entry.Resolution)

    /// The state of a log: the fold of its events.
    let replay (events: GrantEvent list) : GrantsState = List.fold evolve initial events
