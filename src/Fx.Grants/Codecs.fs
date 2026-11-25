namespace Fx.Grants

open Fx.Contracts

/// The JSON of the grants log: its events (the event schema's version 1: events are kept forever, so
/// change their JSON only through a new schema version with an upcaster, docs/modules/store.md), its
/// commands and state, the capability's values and the `grants/*` client protocol, and the
/// TypeScript types generated from them.
[<RequireQualifiedAccess>]
module Codecs =
    let private time: Codec<int64> =
        Codec.int64 |> Codec.describe "Microseconds since the Unix epoch, UTC."

    let private text: Codec<string> = Codec.string |> Codec.nonEmpty

    let actionName: Codec<ActionName> =
        Codec.string
        |> Codec.tryMap
            (fun name ->
                match ActionName.tryCreate name with
                | Some action -> Ok action
                | None -> Error "expected an action name: dotted segments such as `shell.exec`")
            (fun action -> action.Value)
        |> Codec.describe
            "An action, e.g. `shell.exec`: dotted segments, each a lowercase letter then letters and digits."

    let grantId: Codec<GrantId> =
        text
        |> Codec.maxLength GrantLog.MaxId
        |> Codec.map GrantId (fun id -> id.Value)
        |> Codec.describe "A grant's id."

    let requestId: Codec<RequestId> =
        text
        |> Codec.maxLength GrantLog.MaxId
        |> Codec.map RequestId (fun id -> id.Value)
        |> Codec.describe "A grant request's id."

    let resourceKind: Codec<ResourceKind> =
        Codec.enum
            [
                ResourceKind.Path, "path"
                ResourceKind.Command, "command"
                ResourceKind.Name, "name"
            ]
        |> Codec.describe "How an action's resources look and how patterns match them."
        |> Codec.named "ResourceKind"

    let effect: Codec<GrantEffect> =
        Codec.enum [ GrantEffect.Allow, "allow"; GrantEffect.Deny, "deny" ]
        |> Codec.describe "Whether a grant permits or forbids."
        |> Codec.named "GrantEffect"

    let scopeKind: Codec<ScopeKind> =
        Codec.enum
            [
                ScopeKind.Once, "once"
                ScopeKind.Session, "session"
                ScopeKind.Repo, "repo"
                ScopeKind.Host, "host"
            ]
        |> Codec.describe "A scope a person can choose: once, session, repo or host."
        |> Codec.named "ScopeKind"

    let scopeChoice: Codec<ScopeChoice> =
        Codec.union "kind" {
            let! once = Codec.case0 "once" ScopeChoice.Once
            and! session = Codec.case0 "session" ScopeChoice.Session
            and! repo = Codec.case1 "repo" ScopeChoice.Repo (Field.optional "path" text)
            and! host = Codec.case0 "host" ScopeChoice.Host

            return
                fun value ->
                    match value with
                    | ScopeChoice.Once -> once
                    | ScopeChoice.Session -> session
                    | ScopeChoice.Repo path -> repo path
                    | ScopeChoice.Host -> host
        }
        |> Codec.describe "The scope of an answer: the request's session or repository (or a path in it), or the host."
        |> Codec.named "ScopeChoice"

    let grantScope: Codec<GrantScope> =
        Codec.union "kind" {
            let! once = Codec.case1 "once" GrantScope.Once (Field.required "request" requestId)

            and! session =
                Codec.case1 "session" GrantScope.Session (Field.required "session" text)

            and! repo =
                Codec.case2
                    "repo"
                    (fun repo path -> GrantScope.Repo { Repo = repo; Path = path })
                    (Field.required "repo" text)
                    (Field.optional "path" text)

            and! host = Codec.case0 "host" GrantScope.Host

            return
                fun value ->
                    match value with
                    | GrantScope.Once request -> once request
                    | GrantScope.Session id -> session id
                    | GrantScope.Repo scope -> repo scope.Repo scope.Path
                    | GrantScope.Host -> host
        }
        |> Codec.describe "Where a grant applies."
        |> Codec.named "GrantScope"

    let origin: Codec<GrantOrigin> =
        Codec.union "kind" {
            let! answer =
                Codec.case1 "answer" GrantOrigin.Answer (Field.required "request" requestId)

            and! direct = Codec.case1 "direct" GrantOrigin.Direct (Field.required "via" text)

            return
                fun value ->
                    match value with
                    | GrantOrigin.Answer request -> answer request
                    | GrantOrigin.Direct via -> direct via
        }
        |> Codec.describe "How a grant came about: an answer to a request, or made directly."
        |> Codec.named "GrantOrigin"

    let grant: Codec<Grant> =
        Codec.record {
            let! id = Codec.field "id" (fun (g: Grant) -> g.Id) grantId
            and! action = Codec.field "action" (fun g -> g.Action) actionName
            and! resource = Codec.field "resource" (fun g -> g.Resource) text
            and! effect = Codec.field "effect" (fun g -> g.Effect) effect
            and! scope = Codec.field "scope" (fun g -> g.Scope) grantScope
            and! by = Codec.field "by" (fun g -> g.By) text
            and! at = Codec.field "at" (fun g -> g.At) time
            and! expires = Codec.optionalField "expires" (fun g -> g.Expires) time
            and! reason = Codec.optionalField "reason" (fun g -> g.Reason) Codec.string
            and! origin = Codec.field "origin" (fun g -> g.Origin) origin

            return
                {
                    Id = id
                    Action = action
                    Resource = resource
                    Effect = effect
                    Scope = scope
                    By = by
                    At = at
                    Expires = expires
                    Reason = reason
                    Origin = origin
                }
        }
        |> Codec.describe
            "A permission (spec §13.2): an action on the resources a pattern matches, allowed or denied in a scope by a person."
        |> Codec.named "Grant"

    let repoPlace: Codec<RepoPlace> =
        Codec.record {
            let! repo = Codec.field "repo" (fun (p: RepoPlace) -> p.Repo) text
            and! path = Codec.defaultField "path" (fun p -> p.Path) "" Codec.string
            return { Repo = repo; Path = path }
        }
        |> Codec.describe
            "Where in a repository an action happens: a repo-relative, `/`-separated path (\"\" for the root)."
        |> Codec.named "RepoPlace"

    let context: Codec<GrantContext> =
        Codec.record {
            let! session =
                Codec.optionalField "session" (fun (c: GrantContext) -> c.Session) text

            and! run = Codec.optionalField "run" (fun c -> c.Run) text
            and! repo = Codec.optionalField "repo" (fun c -> c.Repo) repoPlace

            return
                {
                    Session = session
                    Run = run
                    Repo = repo
                }
        }
        |> Codec.describe "The context of a check or request, which the scopes of grants are compared with."
        |> Codec.named "GrantContext"

    let requester: Codec<Requester> =
        Codec.record {
            let! principal = Codec.field "principal" (fun (r: Requester) -> r.Principal) text
            and! agent = Codec.optionalField "agent" (fun r -> r.Agent) text
            and! extension = Codec.optionalField "extension" (fun r -> r.Extension) text

            return
                {
                    Principal = principal
                    Agent = agent
                    Extension = extension
                }
        }
        |> Codec.describe "Who asks: on whose behalf, and the agent or extension that asks."
        |> Codec.named "Requester"

    let evidence: Codec<Evidence> =
        Codec.record {
            let! label = Codec.field "label" (fun (e: Evidence) -> e.Label) text
            and! body = Codec.field "text" (fun e -> e.Text) Codec.string
            return { Label = label; Text = body }
        }
        |> Codec.describe "Something the person deciding sees."
        |> Codec.named "GrantEvidence"

    let query: Codec<GrantQuery> =
        Codec.record {
            let! action = Codec.field "action" (fun (q: GrantQuery) -> q.Action) actionName
            and! resource = Codec.field "resource" (fun q -> q.Resource) text

            and! context =
                Codec.defaultField "context" (fun q -> q.Context) GrantContext.none context

            return
                {
                    Action = action
                    Resource = resource
                    Context = context
                }
        }
        |> Codec.describe "May this action happen on this resource in this context?"
        |> Codec.named "GrantQuery"

    let ask: Codec<GrantAsk> =
        Codec.record {
            let! key = Codec.field "key" (fun (a: GrantAsk) -> a.Key) text
            and! action = Codec.field "action" (fun a -> a.Action) actionName
            and! resource = Codec.field "resource" (fun a -> a.Resource) text

            and! context =
                Codec.defaultField "context" (fun a -> a.Context) GrantContext.none context

            and! requester = Codec.field "requester" (fun a -> a.Requester) requester
            and! summary = Codec.field "summary" (fun a -> a.Summary) text

            and! evidence =
                Codec.defaultField "evidence" (fun a -> a.Evidence) [] (Codec.list evidence)

            and! suggestions =
                Codec.defaultField "suggestions" (fun a -> a.Suggestions) [] (Codec.list text)

            and! scopes =
                Codec.defaultField "scopes" (fun a -> a.Scopes) [] (Codec.list scopeKind)

            and! expires = Codec.optionalField "expires" (fun a -> a.Expires) time

            return
                {
                    Key = key
                    Action = action
                    Resource = resource
                    Context = context
                    Requester = requester
                    Summary = summary
                    Evidence = evidence
                    Suggestions = suggestions
                    Scopes = scopes
                    Expires = expires
                }
        }
        |> Codec.describe "What a requester asks for; the key makes asking again the same request."
        |> Codec.named "GrantAsk"

    let request: Codec<GrantRequest> =
        Codec.record {
            let! id = Codec.field "id" (fun (r: GrantRequest) -> r.Id) requestId
            and! key = Codec.field "key" (fun r -> r.Key) text
            and! action = Codec.field "action" (fun r -> r.Action) actionName
            and! resource = Codec.field "resource" (fun r -> r.Resource) text
            and! context = Codec.field "context" (fun r -> r.Context) context
            and! requester = Codec.field "requester" (fun r -> r.Requester) requester
            and! summary = Codec.field "summary" (fun r -> r.Summary) text

            and! evidence =
                Codec.defaultField "evidence" (fun r -> r.Evidence) [] (Codec.list evidence)

            and! suggestions =
                Codec.defaultField "suggestions" (fun r -> r.Suggestions) [] (Codec.list text)

            and! offer =
                Codec.field "offer" (fun r -> r.Offer) (Codec.list scopeKind |> Codec.minItems 1)

            and! at = Codec.field "at" (fun r -> r.At) time
            and! expires = Codec.optionalField "expires" (fun r -> r.Expires) time

            return
                {
                    Id = id
                    Key = key
                    Action = action
                    Resource = resource
                    Context = context
                    Requester = requester
                    Summary = summary
                    Evidence = evidence
                    Suggestions = suggestions
                    Offer = offer
                    At = at
                    Expires = expires
                }
        }
        |> Codec.describe "A durable pending request for a permission: what the person deciding sees."
        |> Codec.named "GrantRequest"

    let answer: Codec<GrantAnswer> =
        Codec.record {
            let! effect = Codec.field "effect" (fun (a: GrantAnswer) -> a.Effect) effect
            and! scope = Codec.field "scope" (fun a -> a.Scope) scopeChoice
            and! resource = Codec.optionalField "resource" (fun a -> a.Resource) text
            and! expires = Codec.optionalField "expires" (fun a -> a.Expires) time
            and! reason = Codec.optionalField "reason" (fun a -> a.Reason) Codec.string

            return
                {
                    Effect = effect
                    Scope = scope
                    Resource = resource
                    Expires = expires
                    Reason = reason
                }
        }
        |> Codec.describe
            "A person's answer to a request: allow or deny, once or in a scope, for the resource or a pattern."
        |> Codec.named "GrantAnswer"

    let denial: Codec<Denial> =
        Codec.record {
            let! request = Codec.field "request" (fun (d: Denial) -> d.Request) requestId
            and! by = Codec.field "by" (fun d -> d.By) text
            and! at = Codec.field "at" (fun d -> d.At) time
            and! reason = Codec.optionalField "reason" (fun d -> d.Reason) Codec.string

            return
                {
                    Request = request
                    By = by
                    At = at
                    Reason = reason
                }
        }
        |> Codec.describe "A request answered \"deny, just this once\"."
        |> Codec.named "GrantDenial"

    let revocation: Codec<Revocation> =
        Codec.record {
            let! id = Codec.field "grant" (fun (r: Revocation) -> r.Grant) grantId
            and! by = Codec.field "by" (fun r -> r.By) text
            and! at = Codec.field "at" (fun r -> r.At) time
            and! reason = Codec.optionalField "reason" (fun r -> r.Reason) Codec.string

            return
                {
                    Grant = id
                    By = by
                    At = at
                    Reason = reason
                }
        }
        |> Codec.describe "A grant revoked by a person."
        |> Codec.named "GrantRevocation"

    let expiry: Codec<Expiry> =
        Codec.record {
            let! id = Codec.field "grant" (fun (e: Expiry) -> e.Grant) grantId
            and! at = Codec.field "at" (fun e -> e.At) time
            return { Grant = id; At = at }
        }
        |> Codec.describe "A grant that stopped applying: its expiry passed or its session ended."
        |> Codec.named "GrantExpiry"

    let closeReason: Codec<CloseReason> =
        Codec.union "kind" {
            let! withdrawn =
                Codec.case1 "withdrawn" CloseReason.Withdrawn (Field.required "reason" text)

            and! covered =
                Codec.case1 "covered" CloseReason.Covered (Field.required "grant" grantId)

            and! refused =
                Codec.case1 "refused" CloseReason.Refused (Field.required "reasons" (Codec.list Codec.string))

            and! timedOut = Codec.case0 "timedOut" CloseReason.TimedOut
            and! sessionEnded = Codec.case0 "sessionEnded" CloseReason.SessionEnded

            return
                fun value ->
                    match value with
                    | CloseReason.Withdrawn reason -> withdrawn reason
                    | CloseReason.Covered id -> covered id
                    | CloseReason.Refused reasons -> refused reasons
                    | CloseReason.TimedOut -> timedOut
                    | CloseReason.SessionEnded -> sessionEnded
        }
        |> Codec.describe "Why a pending request closed without an answer."
        |> Codec.named "CloseReason"

    let closure: Codec<Closure> =
        Codec.record {
            let! request = Codec.field "request" (fun (c: Closure) -> c.Request) requestId
            and! at = Codec.field "at" (fun c -> c.At) time
            and! why = Codec.field "why" (fun c -> c.Why) closeReason

            return
                {
                    Request = request
                    At = at
                    Why = why
                }
        }
        |> Codec.describe "A pending request closed without an answer."
        |> Codec.named "GrantClosure"

    /// The grants log's events: version 1 of its event schema.
    let event: Codec<GrantEvent> =
        Codec.union "kind" {
            let! requested = Codec.caseOf "requested" GrantEvent.Requested request
            and! granted = Codec.caseOf "granted" GrantEvent.Granted grant
            and! denied = Codec.caseOf "denied" GrantEvent.Denied denial
            and! revoked = Codec.caseOf "revoked" GrantEvent.Revoked revocation
            and! expired = Codec.caseOf "expired" GrantEvent.Expired expiry
            and! closed = Codec.caseOf "closed" GrantEvent.Closed closure

            return
                fun value ->
                    match value with
                    | GrantEvent.Requested r -> requested r
                    | GrantEvent.Granted g -> granted g
                    | GrantEvent.Denied d -> denied d
                    | GrantEvent.Revoked r -> revoked r
                    | GrantEvent.Expired e -> expired e
                    | GrantEvent.Closed c -> closed c
        }
        |> Codec.describe "An event of the grants log (spec §13.2)."
        |> Codec.named "GrantEvent"

    let status: Codec<GrantStatus> =
        Codec.union "kind" {
            let! active = Codec.case0 "active" GrantStatus.Active
            and! spent = Codec.case0 "spent" GrantStatus.Spent
            and! revoked = Codec.caseOf "revoked" GrantStatus.Revoked revocation
            and! expired = Codec.case1 "expired" GrantStatus.Expired (Field.required "at" time)

            return
                fun value ->
                    match value with
                    | GrantStatus.Active -> active
                    | GrantStatus.Spent -> spent
                    | GrantStatus.Revoked r -> revoked r
                    | GrantStatus.Expired at -> expired at
        }
        |> Codec.describe "Where a grant stands."
        |> Codec.named "GrantStatus"

    let entry: Codec<GrantEntry> =
        Codec.record {
            let! g = Codec.field "grant" (fun (e: GrantEntry) -> e.Grant) grant
            and! s = Codec.field "status" (fun e -> e.Status) status
            return { Grant = g; Status = s }
        }
        |> Codec.describe "A grant and where it stands."
        |> Codec.named "GrantEntry"

    let requestStatus: Codec<RequestStatus> =
        Codec.union "kind" {
            let! pending = Codec.case0 "pending" RequestStatus.Pending

            and! answered =
                Codec.case1 "answered" RequestStatus.Answered (Field.required "grant" grant)

            and! rejected = Codec.caseOf "rejected" RequestStatus.Rejected denial
            and! closed = Codec.caseOf "closed" RequestStatus.Closed closure

            return
                fun value ->
                    match value with
                    | RequestStatus.Pending -> pending
                    | RequestStatus.Answered g -> answered g
                    | RequestStatus.Rejected d -> rejected d
                    | RequestStatus.Closed c -> closed c
        }
        |> Codec.describe "Where a request stands."
        |> Codec.named "RequestStatus"

    let requestEntry: Codec<RequestEntry> =
        Codec.record {
            let! r = Codec.field "request" (fun (e: RequestEntry) -> e.Request) request
            and! s = Codec.field "status" (fun e -> e.Status) requestStatus
            return { Request = r; Status = s }
        }
        |> Codec.describe "A request and where it stands."
        |> Codec.named "RequestEntry"

    /// The state, with grants and requests as lists ordered by id (the keys index is rebuilt).
    let state: Codec<GrantsState> =
        Codec.record {
            let! grants =
                Codec.field "grants" (fun (s: GrantsState) -> s.Grants |> Map.toList |> List.map snd) (Codec.list entry)

            and! requests =
                Codec.field "requests" (fun s -> s.Requests |> Map.toList |> List.map snd) (Codec.list requestEntry)

            and! seq = Codec.field "seq" (fun s -> s.Seq) (Codec.int64 |> Codec.atLeast 0L)

            return
                {
                    Grants = grants |> List.map (fun e -> e.Grant.Id, e) |> Map.ofList
                    Requests = requests |> List.map (fun e -> e.Request.Id, e) |> Map.ofList
                    Keys = requests |> List.map (fun e -> e.Request.Key, e.Request.Id) |> Map.ofList
                    Seq = seq
                }
        }
        |> Codec.describe "The grants log's state: the fold of its events."
        |> Codec.named "GrantsState"

    let resolution: Codec<Resolution> =
        Codec.union "kind" {
            let! allowed =
                Codec.case1 "allowed" Resolution.Allowed (Field.required "grant" grantId)

            and! denied =
                Codec.case1 "denied" Resolution.Denied (Field.required "reasons" (Codec.list Codec.string))

            and! cancelled =
                Codec.case1 "cancelled" Resolution.Cancelled (Field.required "reason" Codec.string)

            return
                fun value ->
                    match value with
                    | Resolution.Allowed g -> allowed g
                    | Resolution.Denied reasons -> denied reasons
                    | Resolution.Cancelled reason -> cancelled reason
        }
        |> Codec.describe "How a request ended: allowed by a grant, denied, or cancelled unanswered."
        |> Codec.named "GrantResolution"

    let check: Codec<GrantCheck> =
        Codec.union "kind" {
            let! allowed =
                Codec.case1 "allowed" GrantCheck.Allowed (Field.required "grant" grantId)

            and! ask =
                Codec.case1 "ask" GrantCheck.MustAsk (Field.required "reasons" (Codec.list Codec.string))

            and! denied =
                Codec.case1 "denied" GrantCheck.Denied (Field.required "reasons" (Codec.list Codec.string))

            return
                fun value ->
                    match value with
                    | GrantCheck.Allowed g -> allowed g
                    | GrantCheck.MustAsk reasons -> ask reasons
                    | GrantCheck.Denied reasons -> denied reasons
        }
        |> Codec.describe "What the effective rules say now: allowed by a grant, ask a person, or denied."
        |> Codec.named "GrantCheck"

    let askOutcome: Codec<AskOutcome> =
        Codec.union "kind" {
            let! settled =
                Codec.case1 "settled" AskOutcome.Settled (Field.required "resolution" resolution)

            and! pending =
                Codec.case1 "pending" AskOutcome.Pending (Field.required "request" requestId)

            return
                fun value ->
                    match value with
                    | AskOutcome.Settled r -> settled r
                    | AskOutcome.Pending r -> pending r
        }
        |> Codec.describe "What became of an ask: settled by the rules, or a pending request."
        |> Codec.named "AskOutcome"

    let actionDef: Codec<ActionDef> =
        Codec.record {
            let! name = Codec.field "name" (fun (d: ActionDef) -> d.Name) actionName
            and! resource = Codec.field "resource" (fun d -> d.Resource) resourceKind
            and! description = Codec.field "description" (fun d -> d.Description) text

            and! scopes =
                Codec.field
                    "scopes"
                    (fun d -> d.Scopes)
                    (Codec.list scopeKind
                     |> Codec.refine (fun scopes ->
                         if List.contains ScopeKind.Once scopes then
                             Ok()
                         else
                             Error "the scopes must include once"))

            return
                {
                    Name = name
                    Resource = resource
                    Description = description
                    Scopes = scopes
                }
        }
        |> Codec.describe
            "An action grants are about: its name, the kind of its resources, the scopes answers may choose."
        |> Codec.named "GrantActionDef"

    let policyRule: Codec<PolicyRule> =
        Codec.record {
            let! id = Codec.field "id" (fun (r: PolicyRule) -> r.Id) text
            and! action = Codec.field "action" (fun r -> r.Action) actionName
            and! resource = Codec.field "resource" (fun r -> r.Resource) text

            and! effect =
                Codec.field
                    "effect"
                    (fun r -> r.Effect)
                    (Codec.enum [ PolicyEffect.Ask, "ask"; PolicyEffect.Deny, "deny" ])

            and! reason = Codec.field "reason" (fun r -> r.Reason) text

            return
                {
                    Id = id
                    Action = action
                    Resource = resource
                    Effect = effect
                    Reason = reason
                }
        }
        |> Codec.describe "A policy rule: it asks or denies, and outranks every allow grant (policy only tightens)."
        |> Codec.named "GrantPolicyRule"

    let op: Codec<GrantOp> =
        Codec.union "kind" {
            let! req = Codec.caseOf "request" GrantOp.Request request

            and! answerOp =
                Codec.case3
                    "answer"
                    (fun r a g -> GrantOp.Answer(r, a, g))
                    (Field.required "request" requestId)
                    (Field.required "answer" answer)
                    (Field.required "grant" grantId)

            and! grantOp = Codec.case1 "grant" GrantOp.Grant (Field.required "grant" grant)

            and! revoke =
                Codec.case2
                    "revoke"
                    (fun g r -> GrantOp.Revoke(g, r))
                    (Field.required "grant" grantId)
                    (Field.optional "reason" Codec.string)

            and! expire = Codec.case1 "expire" GrantOp.Expire (Field.required "grant" grantId)

            and! close =
                Codec.case2
                    "close"
                    (fun r w -> GrantOp.Close(r, w))
                    (Field.required "request" requestId)
                    (Field.required "why" closeReason)

            and! endSession =
                Codec.case1 "endSession" GrantOp.EndSession (Field.required "session" text)

            return
                fun value ->
                    match value with
                    | GrantOp.Request r -> req r
                    | GrantOp.Answer(r, a, g) -> answerOp r a g
                    | GrantOp.Grant g -> grantOp g
                    | GrantOp.Revoke(g, r) -> revoke g r
                    | GrantOp.Expire g -> expire g
                    | GrantOp.Close(r, w) -> close r w
                    | GrantOp.EndSession s -> endSession s
        }
        |> Codec.describe "What happens to the grants log."
        |> Codec.named "GrantOp"

    let command: Codec<GrantCommand> =
        Codec.record {
            let! c = Codec.field "command" (fun (c: GrantCommand) -> c.Command) op
            and! by = Codec.field "by" (fun c -> c.By) text
            and! at = Codec.field "at" (fun c -> c.At) time
            return { Command = c; By = by; At = at }
        }
        |> Codec.describe "A command to the grants log, with its sender and time."
        |> Codec.named "GrantCommand"

    let intent: Codec<GrantIntent> =
        Codec.union "kind" {
            let! expireAt =
                Codec.case2
                    "expireAt"
                    (fun g at -> GrantIntent.ExpireAt(g, at))
                    (Field.required "grant" grantId)
                    (Field.required "at" time)

            and! timeOutAt =
                Codec.case2
                    "timeOutAt"
                    (fun r at -> GrantIntent.TimeOutAt(r, at))
                    (Field.required "request" requestId)
                    (Field.required "at" time)

            return
                fun value ->
                    match value with
                    | GrantIntent.ExpireAt(g, at) -> expireAt g at
                    | GrantIntent.TimeOutAt(r, at) -> timeOutAt r at
        }
        |> Codec.describe "What the grants log asks the host to do."
        |> Codec.named "GrantIntent"

    let error: Codec<GrantError> =
        Codec.union "kind" {
            let! unknownAction =
                Codec.case1 "unknownAction" GrantError.UnknownAction (Field.required "action" Codec.string)

            and! invalidResource =
                Codec.case2
                    "invalidResource"
                    (fun r why -> GrantError.InvalidResource(r, why))
                    (Field.required "resource" Codec.string)
                    (Field.required "reason" Codec.string)

            and! invalidPattern =
                Codec.case2
                    "invalidPattern"
                    (fun p why -> GrantError.InvalidPattern(p, why))
                    (Field.required "pattern" Codec.string)
                    (Field.required "reason" Codec.string)

            and! invalidRequest =
                Codec.case1 "invalidRequest" GrantError.InvalidRequest (Field.required "reason" Codec.string)

            and! invalidAnswer =
                Codec.case1 "invalidAnswer" GrantError.InvalidAnswer (Field.required "reason" Codec.string)

            and! invalidGrant =
                Codec.case1 "invalidGrant" GrantError.InvalidGrant (Field.required "reason" Codec.string)

            and! unknownRequest =
                Codec.case1 "unknownRequest" GrantError.UnknownRequest (Field.required "request" requestId)

            and! notPending =
                Codec.case1 "notPending" GrantError.NotPending (Field.required "request" requestId)

            and! unknownGrant =
                Codec.case1 "unknownGrant" GrantError.UnknownGrant (Field.required "grant" grantId)

            and! notActive =
                Codec.case1 "notActive" GrantError.NotActive (Field.required "grant" grantId)

            and! notAPerson =
                Codec.case1 "notAPerson" GrantError.NotAPerson (Field.required "principal" Codec.string)

            and! notDue =
                Codec.case1 "notDue" GrantError.NotDue (Field.required "what" Codec.string)

            and! duplicateId =
                Codec.case1 "duplicateId" GrantError.DuplicateId (Field.required "id" Codec.string)

            and! conflict =
                Codec.case2
                    "conflict"
                    (fun c m -> GrantError.Conflict(c, m))
                    (Field.required "code" Codec.string)
                    (Field.required "message" Codec.string)

            and! unavailable =
                Codec.case1 "unavailable" GrantError.Unavailable (Field.required "reason" Codec.string)

            return
                fun value ->
                    match value with
                    | GrantError.UnknownAction a -> unknownAction a
                    | GrantError.InvalidResource(r, why) -> invalidResource r why
                    | GrantError.InvalidPattern(p, why) -> invalidPattern p why
                    | GrantError.InvalidRequest why -> invalidRequest why
                    | GrantError.InvalidAnswer why -> invalidAnswer why
                    | GrantError.InvalidGrant why -> invalidGrant why
                    | GrantError.UnknownRequest r -> unknownRequest r
                    | GrantError.NotPending r -> notPending r
                    | GrantError.UnknownGrant g -> unknownGrant g
                    | GrantError.NotActive g -> notActive g
                    | GrantError.NotAPerson p -> notAPerson p
                    | GrantError.NotDue what -> notDue what
                    | GrantError.DuplicateId id -> duplicateId id
                    | GrantError.Conflict(c, m) -> conflict c m
                    | GrantError.Unavailable why -> unavailable why
        }
        |> Codec.describe "Why the grants log refused a command, or a check or request could not be made."
        |> Codec.named "GrantError"
