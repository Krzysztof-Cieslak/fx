namespace Fx.Host

open Fx.Contracts
open Fx.Rpc
open Fx.Sdk
open Fx.Identity
open Fx.Sessions
open Fx.Sessions.Host
open Fx.Store

/// A session as clients list it: the index's entry.
type SessionRef =
    {
        Session: string
    }

    override this.ToString() = this.Session

/// `session/create`: a session in the host's workspace. With an id the client chose, creating again is
/// harmless (the same session).
type CreateSession =
    {
        Session: string option
        Title: string option
    }

    override this.ToString() =
        "create " + (this.Session |> Option.defaultValue "<new>")

/// A session's state and the sequence number of its last event.
type SessionView =
    {
        Seq: int64
        State: SessionState
    }

    override this.ToString() = $"#{this.Seq} {this.State}"

/// `session/prompt`: a prompt, steered into the running turn or queued.
type PromptSession =
    {
        Session: string
        /// The client's idempotency key: sending again with the same key changes nothing.
        Key: string
        Text: string
        Mode: PromptMode
    }

    override this.ToString() = $"{this.Mode} into {this.Session}"

/// A command that needs only the session and a key (`interrupt`, `close`, `reopen`).
type KeyedSession =
    {
        Session: string
        Key: string
    }

    override this.ToString() = $"{this.Session} ({this.Key})"

/// `session/answer`: an answer to an open request the client can answer.
type AnswerSession =
    {
        Session: string
        Key: string
        Request: string
        Answer: Json
    }

    override this.ToString() =
        $"answer {this.Request} in {this.Session}"

/// `session/fork`: a new session continuing this one's history up to a sequence number (the head by
/// default).
type ForkSession =
    {
        Session: string
        Key: string
        Child: string option
        UpTo: int64 option
    }

    override this.ToString() = $"fork {this.Session}"

/// `session/overrides`: slots to set and slots whose override to remove (the session layer).
type OverrideSession =
    {
        Session: string
        Key: string
        Set: (string * Json) list
        Unset: string list
    }

    override this.ToString() =
        $"overrides of {this.Session} ({this.Set.Length} set, {this.Unset.Length} unset)"

/// `session/subscribe`: the events after a sequence number, then live, as `session/event`
/// notifications, until the request is cancelled.
type SubscribeSession =
    {
        Session: string
        After: int64
    }

    override this.ToString() = $"{this.Session} after #{this.After}"

/// What an accepted command appended to the session's log.
type Committed =
    {
        /// The head before the command.
        After: int64
        Count: int
    }

    override this.ToString() = $"+{this.Count} after #{this.After}"

/// `session/event`: one item of a subscription.
type SessionNotice =
    {
        Session: string
        Item: Feed<SessionState, Event>
    }

    override this.ToString() = $"{this.Session}: {this.Item}"

/// `session/contextEvent`: one event a session's turns recorded in its context log (spec §5: frames,
/// the model's replies, receipts of actions, pins), with its envelope (its cause places it after a
/// session event).
type ContextNotice =
    {
        Session: string
        Item: Fx.Machines.Host.Recorded<Fx.Prompt.ContextEvent>
    }

    override this.ToString() =
        $"{this.Session}: context #{this.Item.Seq}"

/// An environment as clients see it.
type EnvSummary =
    {
        Id: string
        Name: string
        Provider: string
        Phase: string
        Transport: string option
        Workspace: string option
        Reason: string option
        /// What a person must be told while it exists (no sandbox, …).
        Warnings: string list
    }

    override this.ToString() =
        $"{this.Id} ({this.Provider}, {this.Phase})"

/// An id, e.g. of an environment or a session token.
type IdRef =
    {
        Id: string
    }

    override this.ToString() = this.Id

/// Why the host refused a client's request: the expected failures of every method of the client
/// protocol besides `grants/*` (which has its own).
[<RequireQualifiedAccess>]
type ClientError =
    /// Not signed in, a revoked token, or not the role the method needs.
    | Forbidden of IdentityFailure
    | UnknownSession of session: string
    | UnknownEnvironment of id: string
    /// The session refused the command (`Rejection.Code`: `session.busy`, `session.closed`, …).
    | Rejected of code: string * message: string
    /// The request is open but this client did not offer a capability that answers it.
    | CannotAnswer of request: string
    /// The params are well-formed but cannot be done (overrides that do not resolve, …).
    | Invalid of reasons: string list
    /// The host cannot do it now (it is stopping, an environment failed, …).
    | Unavailable of reason: string

    override this.ToString() =
        match this with
        | Forbidden failure -> "forbidden: " + string failure
        | UnknownSession session -> "no session " + session
        | UnknownEnvironment id -> "no environment " + id
        | Rejected(code, message) -> code + ": " + message
        | CannotAnswer request -> $"this client cannot answer {request}"
        | Invalid reasons -> "invalid: " + String.concat "; " reasons
        | Unavailable reason -> "unavailable: " + reason

/// Why an extension's method failed, as `ext/<id>/<name>` reports it.
[<RequireQualifiedAccess>]
type ExtCallError =
    | Forbidden of IdentityFailure
    | Failed of MethodFailure

    override this.ToString() =
        match this with
        | Forbidden failure -> "forbidden: " + string failure
        | Failed failure -> string failure

/// The client protocol (spec §17): JSON-RPC 2.0 over WebSocket, or in process in embedded mode. Every
/// method is a value with codecs, for the host's handlers (`ClientServer`) and clients alike. Method
/// groups: `session/*`, `grants/*` (`GrantsProtocol`), `identity/*`, `env/*` and `ext/<id>/<name>`.
[<RequireQualifiedAccess>]
module ClientProtocol =
    /// The protocol's version, announced in `initialize` (`clientProtocol`).
    [<Literal>]
    let Version = "1"

    /// The `initialize` member with the request kinds' capabilities a client can answer (spec §5:
    /// requests are offered to capable clients): `{"answers": ["confirm", "agent.question", …]}`.
    [<Literal>]
    let AnswersMember = "answers"

    let private key = Codec.string |> Codec.nonEmpty |> Codec.maxLength 256
    let private sessionId = Codec.string |> Codec.nonEmpty |> Codec.maxLength 128

    let sessionRef: Codec<SessionRef> =
        Codec.record {
            let! session = Codec.field "session" (fun (p: SessionRef) -> p.Session) sessionId
            return { Session = session }
        }
        |> Codec.named "SessionRef"

    let createSession: Codec<CreateSession> =
        Codec.record {
            let! session =
                Codec.optionalField "session" (fun (p: CreateSession) -> p.Session) sessionId

            and! title =
                Codec.optionalField "title" (fun (p: CreateSession) -> p.Title) Codec.string

            return { Session = session; Title = title }
        }
        |> Codec.describe "A session in the host's workspace; with an id the client chose, creating again is harmless."
        |> Codec.named "CreateSession"

    let sessionEntry: Codec<SessionEntry> =
        Codec.record {
            let! session =
                Codec.field "session" (fun (e: SessionEntry) -> e.Session) Codec.string

            and! workspace =
                Codec.field "workspace" (fun (e: SessionEntry) -> e.Workspace) Codec.string

            and! environment =
                Codec.field "environment" (fun (e: SessionEntry) -> e.Environment) Codec.string

            and! title =
                Codec.optionalField "title" (fun (e: SessionEntry) -> e.Title) Codec.string

            return
                {
                    Session = session
                    Workspace = workspace
                    Environment = environment
                    Title = title
                }
        }
        |> Codec.describe "A session the host created, and where it works."
        |> Codec.named "SessionEntry"

    let sessionView: Codec<SessionView> =
        Codec.record {
            let! seq = Codec.field "seq" (fun (v: SessionView) -> v.Seq) Codec.int64

            and! state =
                Codec.field "state" (fun (v: SessionView) -> v.State) Fx.Sessions.Codecs.state

            return { Seq = seq; State = state }
        }
        |> Codec.named "SessionView"

    let promptSession: Codec<PromptSession> =
        Codec.record {
            let! session = Codec.field "session" (fun (p: PromptSession) -> p.Session) sessionId
            and! key = Codec.field "key" (fun (p: PromptSession) -> p.Key) key
            and! text = Codec.field "text" (fun (p: PromptSession) -> p.Text) Codec.string

            and! mode =
                Codec.field "mode" (fun (p: PromptSession) -> p.Mode) Fx.Sessions.Codecs.promptMode

            return
                {
                    Session = session
                    Key = key
                    Text = text
                    Mode = mode
                }
        }
        |> Codec.named "PromptSession"

    let keyedSession: Codec<KeyedSession> =
        Codec.record {
            let! session = Codec.field "session" (fun (p: KeyedSession) -> p.Session) sessionId
            and! key = Codec.field "key" (fun (p: KeyedSession) -> p.Key) key
            return { Session = session; Key = key }
        }
        |> Codec.named "KeyedSession"

    let answerSession: Codec<AnswerSession> =
        Codec.record {
            let! session = Codec.field "session" (fun (p: AnswerSession) -> p.Session) sessionId
            and! key = Codec.field "key" (fun (p: AnswerSession) -> p.Key) key

            and! request =
                Codec.field "request" (fun (p: AnswerSession) -> p.Request) Codec.string

            and! answer = Codec.field "answer" (fun (p: AnswerSession) -> p.Answer) Codec.json

            return
                {
                    Session = session
                    Key = key
                    Request = request
                    Answer = answer
                }
        }
        |> Codec.named "AnswerSession"

    let forkSession: Codec<ForkSession> =
        Codec.record {
            let! session = Codec.field "session" (fun (p: ForkSession) -> p.Session) sessionId
            and! key = Codec.field "key" (fun (p: ForkSession) -> p.Key) key
            and! child = Codec.optionalField "child" (fun (p: ForkSession) -> p.Child) sessionId
            and! upTo = Codec.optionalField "upTo" (fun (p: ForkSession) -> p.UpTo) Codec.int64

            return
                {
                    Session = session
                    Key = key
                    Child = child
                    UpTo = upTo
                }
        }
        |> Codec.named "ForkSession"

    let overrideSession: Codec<OverrideSession> =
        Codec.record {
            let! session =
                Codec.field "session" (fun (p: OverrideSession) -> p.Session) sessionId

            and! key = Codec.field "key" (fun (p: OverrideSession) -> p.Key) key

            and! set =
                Codec.defaultField "set" (fun (p: OverrideSession) -> p.Set) [] (Codec.keyValues Codec.json)

            and! unset =
                Codec.defaultField "unset" (fun (p: OverrideSession) -> p.Unset) [] (Codec.list Codec.string)

            return
                {
                    Session = session
                    Key = key
                    Set = set
                    Unset = unset
                }
        }
        |> Codec.describe "Variant overrides of a session: slots to set, and slots whose override to remove."
        |> Codec.named "OverrideSession"

    let subscribeSession: Codec<SubscribeSession> =
        Codec.record {
            let! session =
                Codec.field "session" (fun (p: SubscribeSession) -> p.Session) sessionId

            and! after =
                Codec.defaultField "after" (fun (p: SubscribeSession) -> p.After) 0L (Codec.int64 |> Codec.atLeast 0L)

            return { Session = session; After = after }
        }
        |> Codec.named "SubscribeSession"

    let committed: Codec<Committed> =
        Codec.record {
            let! after = Codec.field "after" (fun (a: Committed) -> a.After) Codec.int64
            and! count = Codec.field "count" (fun (a: Committed) -> a.Count) Codec.int
            return { After = after; Count = count }
        }
        |> Codec.describe "What an accepted command appended: the head before it and how many events."
        |> Codec.named "Committed"

    let sessionEvent: Codec<SessionNotice> =
        Codec.record {
            let! session =
                Codec.field "session" (fun (e: SessionNotice) -> e.Session) Codec.string

            and! item = Codec.field "item" (fun (e: SessionNotice) -> e.Item) SessionLog.feed
            return { Session = session; Item = item }
        }
        |> Codec.named "SessionNotice"

    let contextEvent: Codec<ContextNotice> =
        let recorded =
            Fx.Store.Codecs.recorded Fx.Prompt.ContextCodecs.event
            |> Codec.describe "A context event with its position, time, sender, key and cause."
            |> Codec.named "RecordedContextEvent"

        Codec.record {
            let! session =
                Codec.field "session" (fun (e: ContextNotice) -> e.Session) Codec.string

            and! item = Codec.field "item" (fun (e: ContextNotice) -> e.Item) recorded
            return { Session = session; Item = item }
        }
        |> Codec.named "ContextNotice"

    let streaming: Codec<StreamingNotice> =
        Codec.record {
            let! session =
                Codec.field "session" (fun (n: StreamingNotice) -> n.Session) Codec.string

            and! turn = Codec.field "turn" (fun (n: StreamingNotice) -> n.Turn) Codec.int

            and! fresh =
                Codec.defaultField "fresh" (fun (n: StreamingNotice) -> n.Fresh) false Codec.bool

            and! text =
                Codec.defaultField "text" (fun (n: StreamingNotice) -> n.Text) "" Codec.string

            and! tool =
                Codec.optionalField "tool" (fun (n: StreamingNotice) -> n.Tool) Codec.string

            return
                {
                    Session = session
                    Turn = turn
                    Fresh = fresh
                    Text = text
                    Tool = tool
                }
        }
        |> Codec.describe
            "What the model is writing in a running turn, as it writes it: text the reply adds, or a tool call it started. Not recorded: the complete reply arrives as a context event."
        |> Codec.named "StreamingNotice"

    let envSummary: Codec<EnvSummary> =
        Codec.record {
            let! id = Codec.field "id" (fun (e: EnvSummary) -> e.Id) Codec.string
            and! name = Codec.field "name" (fun (e: EnvSummary) -> e.Name) Codec.string

            and! provider =
                Codec.field "provider" (fun (e: EnvSummary) -> e.Provider) Codec.string

            and! phase = Codec.field "phase" (fun (e: EnvSummary) -> e.Phase) Codec.string

            and! transport =
                Codec.optionalField "transport" (fun (e: EnvSummary) -> e.Transport) Codec.string

            and! workspace =
                Codec.optionalField "workspace" (fun (e: EnvSummary) -> e.Workspace) Codec.string

            and! reason =
                Codec.optionalField "reason" (fun (e: EnvSummary) -> e.Reason) Codec.string

            and! warnings =
                Codec.field "warnings" (fun (e: EnvSummary) -> e.Warnings) (Codec.list Codec.string)

            return
                {
                    Id = id
                    Name = name
                    Provider = provider
                    Phase = phase
                    Transport = transport
                    Workspace = workspace
                    Reason = reason
                    Warnings = warnings
                }
        }
        |> Codec.describe "An environment: where sessions' hands are."
        |> Codec.named "EnvSummary"

    let idRef: Codec<IdRef> =
        Codec.record {
            let! id = Codec.field "id" (fun (r: IdRef) -> r.Id) (Codec.string |> Codec.nonEmpty)
            return { Id = id }
        }
        |> Codec.named "IdRef"

    let clientError: Codec<ClientError> =
        Codec.union "kind" {
            let! forbidden =
                Codec.case1
                    "forbidden"
                    ClientError.Forbidden
                    (Field.required "failure" Fx.Identity.Codecs.identityFailure)

            and! unknownSession =
                Codec.case1 "unknownSession" ClientError.UnknownSession (Field.required "session" Codec.string)

            and! unknownEnvironment =
                Codec.case1 "unknownEnvironment" ClientError.UnknownEnvironment (Field.required "id" Codec.string)

            and! rejected =
                Codec.case2
                    "rejected"
                    (fun code message -> ClientError.Rejected(code, message))
                    (Field.required "code" Codec.string)
                    (Field.required "message" Codec.string)

            and! cannotAnswer =
                Codec.case1 "cannotAnswer" ClientError.CannotAnswer (Field.required "request" Codec.string)

            and! invalid =
                Codec.case1 "invalid" ClientError.Invalid (Field.required "reasons" (Codec.list Codec.string))

            and! unavailable =
                Codec.case1 "unavailable" ClientError.Unavailable (Field.required "reason" Codec.string)

            return
                fun value ->
                    match value with
                    | ClientError.Forbidden failure -> forbidden failure
                    | ClientError.UnknownSession session -> unknownSession session
                    | ClientError.UnknownEnvironment id -> unknownEnvironment id
                    | ClientError.Rejected(code, message) -> rejected code message
                    | ClientError.CannotAnswer request -> cannotAnswer request
                    | ClientError.Invalid reasons -> invalid reasons
                    | ClientError.Unavailable reason -> unavailable reason
        }
        |> Codec.describe "Why the host refused a request of the client protocol."
        |> Codec.named "ClientError"

    let private methodFailure: Codec<MethodFailure> =
        Codec.union "kind" {
            let! unknownMethod =
                Codec.case1 "unknownMethod" MethodFailure.UnknownMethod (Field.required "name" Codec.string)

            and! invalidParams =
                Codec.case1
                    "invalidParams"
                    MethodFailure.InvalidParams
                    (Field.required "issues" (Codec.list Fx.Contracts.Codecs.issue))

            and! failed =
                Codec.case2
                    "failed"
                    (fun message data -> MethodFailure.Failed(message, data))
                    (Field.required "message" Codec.string)
                    (Field.required "data" Codec.json)

            and! crashed =
                Codec.case1 "crashed" MethodFailure.Crashed (Field.required "detail" Codec.string)

            and! unreachable =
                Codec.case1 "unreachable" MethodFailure.Unreachable (Field.required "reason" Codec.string)

            return
                fun value ->
                    match value with
                    | MethodFailure.UnknownMethod name -> unknownMethod name
                    | MethodFailure.InvalidParams issues -> invalidParams issues
                    | MethodFailure.Failed(message, data) -> failed message data
                    | MethodFailure.Crashed detail -> crashed detail
                    | MethodFailure.Unreachable reason -> unreachable reason
        }

    let extCallError: Codec<ExtCallError> =
        Codec.union "kind" {
            let! forbidden =
                Codec.case1
                    "forbidden"
                    ExtCallError.Forbidden
                    (Field.required "failure" Fx.Identity.Codecs.identityFailure)

            and! failed =
                Codec.case1 "failed" ExtCallError.Failed (Field.required "failure" methodFailure)

            return
                fun value ->
                    match value with
                    | ExtCallError.Forbidden failure -> forbidden failure
                    | ExtCallError.Failed failure -> failed failure
        }
        |> Codec.describe "Why an extension's method failed."
        |> Codec.named "ExtCallError"

    // ---- session/* ---------------------------------------------------------------------------------

    let create =
        Method.define "session/create" createSession sessionEntry
        |> Method.withErrors clientError

    let list =
        Method.define "session/list" Codec.unit (Codec.list sessionEntry)
        |> Method.withErrors clientError

    let get =
        Method.define "session/get" sessionRef sessionView
        |> Method.withErrors clientError

    let prompt =
        Method.define "session/prompt" promptSession committed
        |> Method.withErrors clientError

    let interrupt =
        Method.define "session/interrupt" keyedSession committed
        |> Method.withErrors clientError

    let answer =
        Method.define "session/answer" answerSession committed
        |> Method.withErrors clientError

    /// The child's id.
    let fork =
        Method.define "session/fork" forkSession sessionRef
        |> Method.withErrors clientError

    let overrides =
        Method.define "session/overrides" overrideSession committed
        |> Method.withErrors clientError

    let close =
        Method.define "session/close" keyedSession committed
        |> Method.withErrors clientError

    let reopen =
        Method.define "session/reopen" keyedSession committed
        |> Method.withErrors clientError

    /// The open requests this client can answer (spec §5: offered to capable clients).
    let offers =
        Method.define "session/offers" sessionRef (Codec.list Fx.Sessions.Codecs.pendingRequest)
        |> Method.withErrors clientError

    /// Runs until cancelled (`$/cancelRequest`); answers the last sequence number sent if it ends.
    let subscribe =
        Method.define "session/subscribe" subscribeSession Codec.int64
        |> Method.withErrors clientError

    /// One item of a subscription.
    let event = Notification.define "session/event" sessionEvent

    /// Follows a session's context log (what its turns recorded: replies, receipts, frames) after a
    /// sequence number of that log, then live, as `session/contextEvent` notifications, with what the
    /// model writes meanwhile as `session/streaming`, until cancelled; answers the last sequence
    /// number sent if it ends.
    let context =
        Method.define "session/context" subscribeSession Codec.int64
        |> Method.withErrors clientError

    /// One event of a `session/context` subscription.
    let contextNotice = Notification.define "session/contextEvent" contextEvent

    /// What the model writes in the session's running turn, sent while a `session/context`
    /// subscription runs.
    let streamingNotice = Notification.define "session/streaming" streaming

    // ---- identity/* --------------------------------------------------------------------------------

    /// Who this connection is (no token).
    let whoami =
        Method.define "identity/whoami" Codec.unit Fx.Identity.Codecs.sessionInfo
        |> Method.withErrors clientError

    /// The host's live client sessions (owner).
    let sessions =
        Method.define "identity/sessions" Codec.unit (Codec.list Fx.Identity.Codecs.sessionInfo)
        |> Method.withErrors clientError

    /// Revokes a session token by id (owner, or the caller's own): its connections end.
    let revoke =
        Method.define "identity/revoke" idRef Codec.bool
        |> Method.withErrors clientError

    /// Revokes this connection's own token: this connection ends.
    let signOut =
        Method.define "identity/signOut" Codec.unit Codec.bool
        |> Method.withErrors clientError

    // ---- env/* -------------------------------------------------------------------------------------

    let envList =
        Method.define "env/list" Codec.unit (Codec.list envSummary)
        |> Method.withErrors clientError

    let envGet =
        Method.define "env/get" idRef envSummary |> Method.withErrors clientError

    /// Destroys an environment (owner): envd stops, the provider releases what it holds.
    let envDestroy =
        Method.define "env/destroy" idRef envSummary |> Method.withErrors clientError

    // ---- ext/<id>/<name> ---------------------------------------------------------------------------

    /// The method name of an extension's method on the client protocol.
    let extName (extension: string) (name: string) = "ext/" + extension + "/" + name

    /// An extension's method, untyped (its params and result are the method's JSON).
    let ext (extension: string) (name: string) : Method<Json, Json, ExtCallError> =
        Method.define (extName extension name) Codec.json Codec.json
        |> Method.withErrors extCallError

    /// The least role of each method of the protocol (identity.md#roles): viewers read, collaborators
    /// drive sessions and call extensions, owners manage the host. `grants/*` checks its own: people
    /// answer, grant and revoke (owner).
    let leastRole (method: string) : Role =
        match method with
        | "session/list"
        | "session/get"
        | "session/offers"
        | "session/subscribe"
        | "session/context"
        | "identity/whoami"
        | "identity/signOut"
        | "env/list"
        | "env/get" -> Role.Viewer
        | "identity/sessions"
        | "identity/revoke"
        | "env/destroy" -> Role.Owner
        | _ -> Role.Collaborator

    /// The protocol's types, for clients' declarations (`client.d.ts`).
    let schemas: Schema list =
        [
            sessionRef.Schema
            createSession.Schema
            sessionEntry.Schema
            sessionView.Schema
            promptSession.Schema
            keyedSession.Schema
            answerSession.Schema
            forkSession.Schema
            overrideSession.Schema
            subscribeSession.Schema
            committed.Schema
            sessionEvent.Schema
            contextEvent.Schema
            streaming.Schema
            envSummary.Schema
            idRef.Schema
            clientError.Schema
            extCallError.Schema
        ]
