namespace Fx.Grants

/// The name of an action a grant permits or forbids: dotted segments, each a lowercase letter then
/// letters and digits, at least two (`shell.exec`, `model.paidFallback`). The vocabulary is open:
/// the built-ins are `Actions.builtIn`, extensions contribute more (docs/modules/grants.md).
[<Struct>]
type ActionName =
    | ActionName of string

    member this.Value =
        let (ActionName name) = this
        name

    override this.ToString() = this.Value

module ActionName =
    let private segment (text: string) =
        text.Length > 0
        && text[0] >= 'a'
        && text[0] <= 'z'
        && text
           |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))

    /// Whether the text is an action name.
    let isValid (text: string) : bool =
        text.Length <= 128
        && (let parts = Text.split '.' text
            parts.Length >= 2 && parts |> List.forall segment)

    let tryCreate (text: string) : ActionName option =
        if isValid text then Some(ActionName text) else None

    /// The action name; a defect if the text is not one.
    let create (text: string) : ActionName =
        if isValid text then
            ActionName text
        else
            invalidArg (nameof text) ("Not an action name: " + text)

/// Identifies a grant.
[<Struct>]
type GrantId =
    | GrantId of string

    member this.Value =
        let (GrantId id) = this
        id

    override this.ToString() = this.Value

/// Identifies a grant request.
[<Struct>]
type RequestId =
    | RequestId of string

    member this.Value =
        let (RequestId id) = this
        id

    override this.ToString() = this.Value

/// Whether a grant permits or forbids.
[<RequireQualifiedAccess>]
type GrantEffect =
    | Allow
    | Deny

    override this.ToString() =
        match this with
        | Allow -> "allow"
        | Deny -> "deny"

/// The scopes a person can choose when answering a request (spec §19.4: once / session / repo / host).
[<RequireQualifiedAccess>]
type ScopeKind =
    | Once
    | Session
    | Repo
    | Host

    override this.ToString() =
        match this with
        | Once -> "once"
        | Session -> "session"
        | Repo -> "repo"
        | Host -> "host"

/// The scope a person chooses in an answer (spec §13.2): the request's context fills in which session
/// or repository.
[<RequireQualifiedAccess>]
type ScopeChoice =
    /// This request only.
    | Once
    /// The request's session, until it ends.
    | Session
    /// The request's repository, or only a path prefix in it (`services/billing`; repo-relative).
    | Repo of path: string option
    /// Everywhere on this host.
    | Host

    member this.Kind =
        match this with
        | Once -> ScopeKind.Once
        | Session -> ScopeKind.Session
        | Repo _ -> ScopeKind.Repo
        | Host -> ScopeKind.Host

    override this.ToString() =
        match this with
        | Once -> "once"
        | Session -> "session"
        | Repo None -> "repo"
        | Repo(Some path) -> "repo:" + path
        | Host -> "host"

/// A repository, or a path prefix in it.
type RepoScope =
    {
        /// The repository's id, as the host names it (a workspace's repository).
        Repo: string
        /// A repo-relative, `/`-separated path prefix; `None` for the whole repository.
        Path: string option
    }

    override this.ToString() =
        match this with
        | { Path = None } -> this.Repo
        | { Path = Some path } -> this.Repo + ":" + path

/// Where a grant applies.
[<RequireQualifiedAccess>]
type GrantScope =
    /// One request only: spent on it when granted, so it never applies to anything else.
    | Once of request: RequestId
    /// One session.
    | Session of session: string
    | Repo of RepoScope
    | Host

    member this.Kind =
        match this with
        | Once _ -> ScopeKind.Once
        | Session _ -> ScopeKind.Session
        | Repo _ -> ScopeKind.Repo
        | Host -> ScopeKind.Host

    override this.ToString() =
        match this with
        | Once request -> "once:" + request.Value
        | Session session -> "session:" + session
        | Repo repo -> "repo:" + string repo
        | Host -> "host"

/// How a grant came about.
[<RequireQualifiedAccess>]
type GrantOrigin =
    /// A person answered this request.
    | Answer of request: RequestId
    /// A person made it directly (the grants view, the CLI), through this client.
    | Direct of via: string

    override this.ToString() =
        match this with
        | Answer request -> "answer to " + request.Value
        | Direct via -> "direct via " + via

/// A permission (spec §13.2): an action on the resources a pattern matches is allowed or denied in a
/// scope, by a person, until it is revoked or expires.
type Grant =
    {
        Id: GrantId
        Action: ActionName
        /// A pattern over the action's resources (`Pattern`).
        Resource: string
        Effect: GrantEffect
        Scope: GrantScope
        /// The principal who granted it: always a person (spec §13.2: agents only request).
        By: string
        /// Microseconds since the Unix epoch, UTC.
        At: int64
        Expires: int64 option
        Reason: string option
        Origin: GrantOrigin
    }

    override this.ToString() =
        string this.Effect
        + " "
        + this.Action.Value
        + " "
        + this.Resource
        + " ("
        + string this.Scope
        + ", by "
        + this.By
        + ")"

/// Where in a repository an action happens.
type RepoPlace =
    {
        Repo: string
        /// Repo-relative and `/`-separated: the file or directory concerned, "" for the root.
        Path: string
    }

    override this.ToString() = this.Repo + ":" + this.Path

/// The context of a check or request: what the scopes of grants are compared with.
type GrantContext =
    {
        Session: string option
        Run: string option
        Repo: RepoPlace option
    }

    override this.ToString() =
        let part (name: string) (value: string option) =
            match value with
            | Some value -> [ name + "=" + value ]
            | None -> []

        "["
        + String.concat
            ", "
            (part "session" this.Session
             @ part "run" this.Run
             @ part "repo" (this.Repo |> Option.map string))
        + "]"

module GrantContext =
    /// No session, run or repository: only host-wide grants apply.
    let none: GrantContext =
        {
            Session = None
            Run = None
            Repo = None
        }

/// Who asks for a permission (spec §13.2: agents can only request).
type Requester =
    {
        /// On whose behalf: the session's principal, an extension's, or `system`.
        Principal: string
        /// The agent that asks, if one does.
        Agent: string option
        /// The extension that asks, if one does.
        Extension: string option
    }

    override this.ToString() =
        this.Principal
        + (this.Agent |> Option.map (fun a -> " agent " + a) |> Option.defaultValue "")
        + (this.Extension
           |> Option.map (fun e -> " extension " + e)
           |> Option.defaultValue "")

/// What a check asks: may this action happen on this resource in this context?
type GrantQuery =
    {
        Action: ActionName
        /// The concrete resource (`Resource.validate` for the action's kind).
        Resource: string
        Context: GrantContext
    }

    override this.ToString() =
        this.Action.Value + " " + this.Resource + " " + string this.Context

/// Something a request shows the person deciding: a label and text (a diff, a command's purpose, a
/// cost estimate).
type Evidence =
    {
        Label: string
        Text: string
    }

    override this.ToString() = this.Label + ": " + this.Text

/// What a requester asks (spec §13.2 `GrantRequest`), before the grants log records it.
type GrantAsk =
    {
        /// The requester's idempotency key: asking again with it is the same request (derive it from
        /// what asks, e.g. a tool call's id).
        Key: string
        Action: ActionName
        /// The concrete resource (`Resource.validate` for the action's kind).
        Resource: string
        Context: GrantContext
        Requester: Requester
        /// One line for the prompt, e.g. "Run `npm test`".
        Summary: string
        Evidence: Evidence list
        /// Broader patterns the requester proposes for a persistent answer (`npm run *`).
        Suggestions: string list
        /// The scopes the requester offers; empty for every scope the action and context allow.
        Scopes: ScopeKind list
        /// When the request times out unanswered, if it does (microseconds since the Unix epoch).
        Expires: int64 option
    }

    override this.ToString() =
        "ask " + this.Action.Value + " " + this.Resource + " [" + this.Key + "]"

/// A durable pending request (spec §5, §13.2): what the person deciding sees.
type GrantRequest =
    {
        Id: RequestId
        Key: string
        Action: ActionName
        Resource: string
        Context: GrantContext
        Requester: Requester
        Summary: string
        Evidence: Evidence list
        Suggestions: string list
        /// The scopes an answer may choose; always includes `Once`.
        Offer: ScopeKind list
        /// When it was made (microseconds since the Unix epoch).
        At: int64
        Expires: int64 option
    }

    member this.Query: GrantQuery =
        {
            Action = this.Action
            Resource = this.Resource
            Context = this.Context
        }

    override this.ToString() =
        "request " + this.Id.Value + ": " + this.Action.Value + " " + this.Resource

/// A person's answer to a request (spec §19.4).
type GrantAnswer =
    {
        Effect: GrantEffect
        /// `Once` answers only this request; any other scope also records a grant that applies later.
        Scope: ScopeChoice
        /// The grant's pattern; `None` for exactly the requested resource. It must match the request's
        /// resource.
        Resource: string option
        Expires: int64 option
        Reason: string option
    }

    override this.ToString() =
        string this.Effect
        + " "
        + string this.Scope
        + (this.Resource |> Option.map (fun r -> " " + r) |> Option.defaultValue "")

/// A request answered "deny, just this once".
type Denial =
    {
        Request: RequestId
        By: string
        At: int64
        Reason: string option
    }

    override this.ToString() =
        "denied " + this.Request.Value + " by " + this.By

type Revocation =
    {
        Grant: GrantId
        By: string
        At: int64
        Reason: string option
    }

    override this.ToString() =
        "revoked " + this.Grant.Value + " by " + this.By

/// A grant that stopped applying on its own: its expiry passed, or its session ended.
type Expiry =
    {
        Grant: GrantId
        At: int64
    }

    override this.ToString() =
        "expired " + this.Grant.Value + " at " + string this.At

/// Why a pending request closed without an answer.
[<RequireQualifiedAccess>]
type CloseReason =
    /// The requester no longer needs it (its turn ended, it was interrupted).
    | Withdrawn of reason: string
    /// A grant made since allows it.
    | Covered of grant: GrantId
    /// The rules deny it now (a deny grant, a policy).
    | Refused of reasons: string list
    /// Its expiry passed unanswered.
    | TimedOut
    /// Its session ended.
    | SessionEnded

    override this.ToString() =
        match this with
        | Withdrawn reason -> "withdrawn: " + reason
        | Covered grant -> "covered by " + grant.Value
        | Refused reasons -> "refused: " + String.concat "; " reasons
        | TimedOut -> "timed out"
        | SessionEnded -> "session ended"

type Closure =
    {
        Request: RequestId
        At: int64
        Why: CloseReason
    }

    override this.ToString() =
        "closed " + this.Request.Value + " (" + string this.Why + ")"

/// The grants log's events (spec §13.2). Facts kept forever: add cases, never change what one means.
[<RequireQualifiedAccess>]
type GrantEvent =
    /// A durable pending request.
    | Requested of GrantRequest
    /// A grant; with `Origin = Answer r` it also answers request `r` (allowed or denied by its effect).
    | Granted of Grant
    /// A request answered "deny, just this once".
    | Denied of Denial
    | Revoked of Revocation
    | Expired of Expiry
    /// A pending request closed without an answer.
    | Closed of Closure

    override this.ToString() =
        match this with
        | Requested request -> string request
        | Granted grant -> "granted " + grant.Id.Value + ": " + string grant
        | Denied denial -> string denial
        | Revoked revocation -> string revocation
        | Expired expiry -> string expiry
        | Closed closure -> string closure

/// Where a grant stands.
[<RequireQualifiedAccess>]
type GrantStatus =
    | Active
    /// A `Once` grant: spent on its request.
    | Spent
    | Revoked of Revocation
    | Expired of at: int64

    override this.ToString() =
        match this with
        | Active -> "active"
        | Spent -> "spent"
        | Revoked revocation -> string revocation
        | Expired at -> "expired at " + string at

type GrantEntry =
    {
        Grant: Grant
        Status: GrantStatus
    }

    override this.ToString() =
        string this.Grant + ": " + string this.Status

/// How a request ended, for its requester.
[<RequireQualifiedAccess>]
type Resolution =
    /// Permitted, by this grant.
    | Allowed of grant: GrantId
    /// Forbidden, for these reasons.
    | Denied of reasons: string list
    /// Never answered: withdrawn, timed out, its session ended.
    | Cancelled of reason: string

    override this.ToString() =
        match this with
        | Allowed grant -> "allowed by " + grant.Value
        | Denied reasons -> "denied: " + String.concat "; " reasons
        | Cancelled reason -> "cancelled: " + reason

/// What the effective rules say about a query now.
[<RequireQualifiedAccess>]
type GrantCheck =
    /// Permitted by this grant.
    | Allowed of grant: GrantId
    /// A person must decide.
    | MustAsk of reasons: string list
    | Denied of reasons: string list

    override this.ToString() =
        match this with
        | Allowed grant -> "allowed by " + grant.Value
        | MustAsk reasons -> "ask: " + String.concat "; " reasons
        | Denied reasons -> "denied: " + String.concat "; " reasons

/// What became of an ask: settled at once by the rules, or a pending request a person answers.
[<RequireQualifiedAccess>]
type AskOutcome =
    | Settled of Resolution
    | Pending of request: RequestId

    override this.ToString() =
        match this with
        | Settled resolution -> string resolution
        | Pending request -> "pending " + request.Value

[<RequireQualifiedAccess>]
type RequestStatus =
    | Pending
    /// Answered with a grant (its effect says allowed or denied).
    | Answered of Grant
    /// Answered "deny, just this once".
    | Rejected of Denial
    | Closed of Closure

    override this.ToString() =
        match this with
        | Pending -> "pending"
        | Answered grant -> "answered: " + string grant
        | Rejected denial -> string denial
        | Closed closure -> string closure

type RequestEntry =
    {
        Request: GrantRequest
        Status: RequestStatus
    }

    member this.Resolution: Resolution option =
        match this.Status with
        | RequestStatus.Pending -> None
        | RequestStatus.Answered grant ->
            match grant.Effect with
            | GrantEffect.Allow -> Some(Resolution.Allowed grant.Id)
            | GrantEffect.Deny ->
                Some(
                    Resolution.Denied
                        [
                            "denied by "
                            + grant.By
                            + (grant.Reason |> Option.map (fun r -> ": " + r) |> Option.defaultValue "")
                        ]
                )
        | RequestStatus.Rejected denial ->
            Some(
                Resolution.Denied
                    [
                        "denied by "
                        + denial.By
                        + (denial.Reason |> Option.map (fun r -> ": " + r) |> Option.defaultValue "")
                    ]
            )
        | RequestStatus.Closed closure ->
            match closure.Why with
            | CloseReason.Covered grant -> Some(Resolution.Allowed grant)
            | CloseReason.Refused reasons -> Some(Resolution.Denied reasons)
            | why -> Some(Resolution.Cancelled(string why))

    override this.ToString() =
        string this.Request + ": " + string this.Status

/// The grants log's state: the fold of its events.
type GrantsState =
    {
        /// Every grant, by id (ids sort by creation).
        Grants: Map<GrantId, GrantEntry>
        /// Every request, by id.
        Requests: Map<RequestId, RequestEntry>
        /// Requests by their requester's key.
        Keys: Map<string, RequestId>
        /// How many events were folded: the sequence number of the last one.
        Seq: int64
    }

    override this.ToString() =
        "GrantsState("
        + string this.Grants.Count
        + " grants, "
        + string this.Requests.Count
        + " requests, seq "
        + string this.Seq
        + ")"

/// What happens to the grants log. The host stamps every command with its sender and time, so events
/// record them and clients see who did what by folding events alone.
[<RequireQualifiedAccess>]
type GrantOp =
    /// Records a pending request (the host checked the rules first: they said ask).
    | Request of GrantRequest
    /// A person answers a pending request; `grant` is the id of the grant the answer makes, if any.
    | Answer of request: RequestId * answer: GrantAnswer * grant: GrantId
    /// A person grants directly (not `Once`); `By` and `At` must be the command's.
    | Grant of Grant
    | Revoke of grant: GrantId * reason: string option
    /// Ends a grant whose expiry has passed (the host's timer).
    | Expire of grant: GrantId
    /// Closes a pending request without an answer.
    | Close of request: RequestId * why: CloseReason
    /// A session ended: its grants expire and its pending requests close.
    | EndSession of session: string

    override this.ToString() =
        match this with
        | Request request -> string request
        | Answer(request, answer, _) -> "answer " + request.Value + ": " + string answer
        | Grant grant -> "grant " + string grant
        | Revoke(grant, _) -> "revoke " + grant.Value
        | Expire grant -> "expire " + grant.Value
        | Close(request, why) -> "close " + request.Value + " (" + string why + ")"
        | EndSession session -> "end session " + session

type GrantCommand =
    {
        Command: GrantOp
        /// The sender's principal (`system` for the host's own commands).
        By: string
        /// When the host handled it (microseconds since the Unix epoch).
        At: int64
    }

    override this.ToString() =
        string this.Command + " by " + this.By + " at " + string this.At

/// What the grants log asks the host to do.
[<RequireQualifiedAccess>]
type GrantIntent =
    /// Send `Expire grant` once the time has come.
    | ExpireAt of grant: GrantId * at: int64
    /// Send `Close(request, TimedOut)` once the time has come.
    | TimeOutAt of request: RequestId * at: int64

    override this.ToString() =
        match this with
        | ExpireAt(grant, at) -> "expire " + grant.Value + " at " + string at
        | TimeOutAt(request, at) -> "time out " + request.Value + " at " + string at

/// The expected failures of the grants log and its capability: a command the log refuses, a check or
/// request it cannot make. `Code` is stable (`grants.*`); clients match on it.
[<RequireQualifiedAccess>]
type GrantError =
    | UnknownAction of action: string
    | InvalidResource of resource: string * reason: string
    | InvalidPattern of pattern: string * reason: string
    | InvalidRequest of reason: string
    | InvalidAnswer of reason: string
    | InvalidGrant of reason: string
    | UnknownRequest of request: RequestId
    /// The request was answered or closed already.
    | NotPending of request: RequestId
    | UnknownGrant of grant: GrantId
    /// The grant was revoked, expired or spent already.
    | NotActive of grant: GrantId
    /// Only people grant, answer and revoke (spec §13.2): not agents, not the host itself.
    | NotAPerson of principal: string
    /// An expiry or a timeout whose time has not come.
    | NotDue of what: string
    | DuplicateId of id: string
    /// The log refused a command after the state changed under it; try again.
    | Conflict of code: string * message: string
    /// The grants log is not running.
    | Unavailable of reason: string

    member this.Code =
        match this with
        | UnknownAction _ -> "grants.unknown-action"
        | InvalidResource _ -> "grants.invalid-resource"
        | InvalidPattern _ -> "grants.invalid-pattern"
        | InvalidRequest _ -> "grants.invalid-request"
        | InvalidAnswer _ -> "grants.invalid-answer"
        | InvalidGrant _ -> "grants.invalid-grant"
        | UnknownRequest _ -> "grants.unknown-request"
        | NotPending _ -> "grants.not-pending"
        | UnknownGrant _ -> "grants.unknown-grant"
        | NotActive _ -> "grants.not-active"
        | NotAPerson _ -> "grants.not-a-person"
        | NotDue _ -> "grants.not-due"
        | DuplicateId _ -> "grants.duplicate-id"
        | Conflict _ -> "grants.conflict"
        | Unavailable _ -> "grants.unavailable"

    member this.Message =
        match this with
        | UnknownAction action -> "No action is named " + action + "."
        | InvalidResource(resource, reason) -> "Invalid resource " + resource + ": " + reason + "."
        | InvalidPattern(pattern, reason) -> "Invalid pattern " + pattern + ": " + reason + "."
        | InvalidRequest reason -> "Invalid request: " + reason + "."
        | InvalidAnswer reason -> "Invalid answer: " + reason + "."
        | InvalidGrant reason -> "Invalid grant: " + reason + "."
        | UnknownRequest request -> "No request has the id " + request.Value + "."
        | NotPending request -> "Request " + request.Value + " is not pending."
        | UnknownGrant grant -> "No grant has the id " + grant.Value + "."
        | NotActive grant -> "Grant " + grant.Value + " is not active."
        | NotAPerson principal -> "Only people grant, answer and revoke; " + principal + " is not a person."
        | NotDue what -> "The time for " + what + " has not come."
        | DuplicateId id -> "The id " + id + " is taken."
        | Conflict(code, message) -> code + ": " + message
        | Unavailable reason -> "The grants log is unavailable: " + reason + "."

    override this.ToString() = this.Code + ": " + this.Message
