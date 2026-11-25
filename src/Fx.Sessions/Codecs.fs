namespace Fx.Sessions

open Fx.Contracts
open Fx.Machines

/// The JSON of the session's commands, events, state and intents: what the event store keeps (the
/// session's event schema, version 1), what clients send and receive (`session/*`, spec §17), and the
/// TypeScript types generated for them. Events are kept forever: change their JSON only through a new
/// schema version with an upcaster (docs/modules/store.md).
[<RequireQualifiedAccess>]
module Codecs =
    let private time: Codec<int64> =
        Codec.int64 |> Codec.describe "Microseconds since the Unix epoch, UTC."

    let private position: Codec<int64> =
        Codec.int64
        |> Codec.atLeast 1L
        |> Codec.describe "A sequence number in the session's stream."

    let private positive: Codec<int> = Codec.int |> Codec.atLeast 1

    let private text: Codec<string> = Codec.string |> Codec.nonEmpty

    /// The same JSON and schema as `Fx.Store.Codecs.streamId`.
    let streamId: Codec<StreamId> =
        Codec.string
        |> Codec.nonEmpty
        |> Codec.map StreamId (fun stream -> stream.Value)
        |> Codec.describe "An event stream, `kind/id`."

    /// The same JSON and schema as `Fx.Store.Codecs.attribution`: `{"principal", "via"}`.
    let attribution: Codec<Attribution> =
        Codec.record {
            let! principal =
                Codec.field "principal" (fun (a: Attribution) -> a.Principal) Codec.string

            and! via = Codec.field "via" (fun a -> a.Via) Codec.string
            return Attribution.create principal via
        }
        |> Codec.named "Attribution"
        |> Codec.describe "Who sent a command (the principal) and what delivered it."

    /// The same JSON and schema as `Fx.Store.Codecs.eventRef`: `{"stream", "seq"}`.
    let eventRef: Codec<EventRef> =
        Codec.record {
            let! stream = Codec.field "stream" (fun (r: EventRef) -> r.Stream) streamId
            and! seq = Codec.field "seq" (fun r -> r.Seq) (Codec.int64 |> Codec.atLeast 1L)
            return EventRef.create stream seq
        }
        |> Codec.named "EventRef"
        |> Codec.describe "The position of an event: its stream and sequence number."

    /// Variant overrides by slot id: `{"edit.guard": "require"}`.
    let overrides: Codec<(string * Json) list> =
        Codec.keyValues Codec.json
        |> Codec.describe "Variant overrides, by slot id (docs/modules/variants.md)."

    let promptMode: Codec<PromptMode> =
        Codec.enum [ PromptMode.Steer, "steer"; PromptMode.Queue, "queue" ]
        |> Codec.describe "steer: into the running turn at its next step; queue: a turn of its own after it."
        |> Codec.named "PromptMode"

    let evidence: Codec<Evidence> =
        Codec.record {
            let! kind = Codec.field "kind" (fun (e: Evidence) -> e.Kind) text
            and! summary = Codec.field "summary" (fun e -> e.Summary) Codec.string
            and! detail = Codec.defaultField "detail" (fun e -> e.Detail) Json.Null Codec.json

            return
                {
                    Kind = kind
                    Summary = summary
                    Detail = detail
                }
        }
        |> Codec.describe "Evidence for a result: what was checked and what it showed."
        |> Codec.named "Evidence"

    let turnResult: Codec<TurnResult> =
        Codec.union "kind" {
            let! finished =
                Codec.case3
                    "done"
                    (fun summary evidence verify -> TurnResult.Done(summary, evidence, verify))
                    (Field.required "summary" Codec.string)
                    (Field.withDefault "evidence" [] (Codec.list evidence))
                    (Field.optional "verify" Codec.string)

            and! needInput =
                Codec.case1
                    "needInput"
                    TurnResult.NeedInput
                    (Field.required "requests" (Codec.list text |> Codec.minItems 1))

            and! waiting =
                Codec.case2
                    "waiting"
                    (fun jobs note -> TurnResult.Waiting(jobs, note))
                    (Field.required "jobs" (Codec.list text |> Codec.minItems 1))
                    (Field.required "note" Codec.string)

            return
                fun value ->
                    match value with
                    | TurnResult.Done(summary, evidence, verify) -> finished summary evidence verify
                    | TurnResult.NeedInput requests -> needInput requests
                    | TurnResult.Waiting(jobs, note) -> waiting jobs note
        }
        |> Codec.describe "How a turn finishes its work (spec §4.5)."
        |> Codec.named "TurnResult"

    let turnEnd: Codec<TurnEnd> =
        Codec.union "kind" {
            let! completed =
                Codec.case1 "completed" TurnEnd.Completed (Field.required "result" turnResult)

            and! interrupted = Codec.case0 "interrupted" TurnEnd.Interrupted

            and! failed =
                Codec.case1 "failed" TurnEnd.Failed (Field.required "reason" Codec.string)

            and! abandoned =
                Codec.case1 "abandoned" TurnEnd.Abandoned (Field.required "reason" Codec.string)

            return
                fun value ->
                    match value with
                    | TurnEnd.Completed result -> completed result
                    | TurnEnd.Interrupted -> interrupted
                    | TurnEnd.Failed reason -> failed reason
                    | TurnEnd.Abandoned reason -> abandoned reason
        }
        |> Codec.describe "How a turn ended."
        |> Codec.named "TurnEnd"

    let resolution: Codec<Resolution> =
        Codec.union "kind" {
            let! answered =
                Codec.case1 "answered" Resolution.Answered (Field.required "response" Codec.json)

            and! cancelled =
                Codec.case1 "cancelled" Resolution.Cancelled (Field.required "reason" Codec.string)

            and! expired = Codec.case0 "expired" (Resolution.Expired None)

            and! defaulted =
                Codec.case1
                    "expiredWithAnswer"
                    (fun response -> Resolution.Expired(Some response))
                    (Field.required "response" Codec.json)

            return
                fun value ->
                    match value with
                    | Resolution.Answered response -> answered response
                    | Resolution.Cancelled reason -> cancelled reason
                    | Resolution.Expired None -> expired
                    | Resolution.Expired(Some response) -> defaulted response
        }
        |> Codec.describe "How a pending request was settled."
        |> Codec.named "Resolution"

    let inputContent: Codec<InputContent> =
        Codec.union "kind" {
            let! prompt =
                Codec.case1 "text" InputContent.Text (Field.required "text" Codec.string)

            and! resolved =
                Codec.case2
                    "resolved"
                    (fun request resolution -> InputContent.Resolved(request, resolution))
                    (Field.required "request" text)
                    (Field.required "resolution" resolution)

            and! finished =
                Codec.case2
                    "jobFinished"
                    (fun job note -> InputContent.JobFinished(job, note))
                    (Field.required "job" text)
                    (Field.required "note" Codec.string)

            return
                fun value ->
                    match value with
                    | InputContent.Text value -> prompt value
                    | InputContent.Resolved(request, resolution) -> resolved request resolution
                    | InputContent.JobFinished(job, note) -> finished job note
        }
        |> Codec.describe "What an input says: a prompt, a settled request, a finished job."
        |> Codec.named "InputContent"

    let input: Codec<Input> =
        Codec.record {
            let! id = Codec.field "id" (fun (i: Input) -> i.Id) position
            and! by = Codec.field "by" (fun i -> i.By) attribution
            and! at = Codec.field "at" (fun i -> i.At) time
            and! content = Codec.field "content" (fun i -> i.Content) inputContent

            return
                {
                    Id = id
                    By = by
                    At = at
                    Content = content
                }
        }
        |> Codec.describe "An input for the agent; its id is the sequence number of the event that received it."
        |> Codec.named "Input"

    let requestOrigin: Codec<RequestOrigin> =
        Codec.union "kind" {
            let! turn = Codec.case1 "turn" RequestOrigin.Turn (Field.required "turn" positive)
            and! session = Codec.case0 "session" RequestOrigin.Session

            and! stream =
                Codec.case1 "stream" RequestOrigin.Stream (Field.required "event" eventRef)

            return
                fun value ->
                    match value with
                    | RequestOrigin.Turn number -> turn number
                    | RequestOrigin.Session -> session
                    | RequestOrigin.Stream origin -> stream origin
        }
        |> Codec.describe "Who opened a request: a turn, the session, or a follower of the log (a run step)."
        |> Codec.named "RequestOrigin"

    let reply: Codec<Reply> =
        Codec.enum [ Reply.ToOpener, "toOpener"; Reply.AsInput, "asInput" ]
        |> Codec.describe "toOpener: the opener waits for it in the log; asInput: it becomes an input for the agent."
        |> Codec.named "Reply"

    let lifetime: Codec<Lifetime> =
        Codec.enum [ Lifetime.Turn, "turn"; Lifetime.Session, "session" ]
        |> Codec.describe "turn: cancelled when its turn ends; session: open until settled."
        |> Codec.named "Lifetime"

    let expiryPolicy: Codec<ExpiryPolicy> =
        Codec.union "kind" {
            let! cancel = Codec.case0 "cancel" ExpiryPolicy.Cancel

            and! answer =
                Codec.case1 "answer" ExpiryPolicy.Answer (Field.required "response" Codec.json)

            return
                fun value ->
                    match value with
                    | ExpiryPolicy.Cancel -> cancel
                    | ExpiryPolicy.Answer response -> answer response
        }
        |> Codec.describe "What happens when a request's time runs out."
        |> Codec.named "ExpiryPolicy"

    let expiry: Codec<Expiry> =
        Codec.record {
            let! at = Codec.field "at" (fun (e: Expiry) -> e.At) time
            and! andThen = Codec.field "then" (fun e -> e.Then) expiryPolicy
            return { At = at; Then = andThen }
        }
        |> Codec.describe "A request's deadline and what happens then."
        |> Codec.named "Expiry"

    let requestSpec: Codec<RequestSpec> =
        Codec.record {
            let! id = Codec.field "id" (fun (r: RequestSpec) -> r.Id) text
            and! kind = Codec.field "kind" (fun r -> r.Kind) text
            and! title = Codec.field "title" (fun r -> r.Title) Codec.string
            and! payload = Codec.field "payload" (fun r -> r.Payload) Codec.json
            and! origin = Codec.field "origin" (fun r -> r.Origin) requestOrigin
            and! reply = Codec.field "reply" (fun r -> r.Reply) reply
            and! lifetime = Codec.field "lifetime" (fun r -> r.Lifetime) lifetime
            and! expiry = Codec.optionalField "expiry" (fun r -> r.Expiry) expiry

            return
                {
                    Id = id
                    Kind = kind
                    Title = title
                    Payload = payload
                    Origin = origin
                    Reply = reply
                    Lifetime = lifetime
                    Expiry = expiry
                }
        }
        |> Codec.describe "A pending request: a decision card, grant request, gate, form, auth flow or install."
        |> Codec.named "RequestSpec"

    let pendingRequest: Codec<PendingRequest> =
        Codec.record {
            let! request =
                Codec.field "request" (fun (p: PendingRequest) -> p.Request) requestSpec

            and! capabilities =
                Codec.field "capabilities" (fun p -> p.Capabilities) (Codec.list Codec.string)

            and! by = Codec.field "by" (fun p -> p.By) attribution
            and! at = Codec.field "at" (fun p -> p.At) time
            and! seq = Codec.field "seq" (fun p -> p.Seq) position

            return
                {
                    Request = request
                    Capabilities = capabilities
                    By = by
                    At = at
                    Seq = seq
                }
        }
        |> Codec.describe "An open request, the client capabilities that can answer it, and who opened it when."
        |> Codec.named "PendingRequest"

    let forkStatus: Codec<ForkStatus> =
        Codec.union "kind" {
            let! starting = Codec.case0 "starting" ForkStatus.Starting
            and! ready = Codec.case0 "ready" ForkStatus.Ready

            and! failed =
                Codec.case1 "failed" ForkStatus.Failed (Field.required "reason" Codec.string)

            return
                fun value ->
                    match value with
                    | ForkStatus.Starting -> starting
                    | ForkStatus.Ready -> ready
                    | ForkStatus.Failed reason -> failed reason
        }
        |> Codec.named "ForkStatus"

    let fork: Codec<Fork> =
        Codec.record {
            let! child = Codec.field "child" (fun (f: Fork) -> f.Child) text
            and! upTo = Codec.field "upTo" (fun f -> f.UpTo) position
            and! seq = Codec.field "seq" (fun f -> f.Seq) position
            and! profile = Codec.field "profile" (fun f -> f.Profile) Codec.string
            and! overrides = Codec.defaultField "overrides" (fun f -> f.Overrides) [] overrides
            and! status = Codec.field "status" (fun f -> f.Status) forkStatus

            return
                {
                    Child = child
                    UpTo = upTo
                    Seq = seq
                    Profile = profile
                    Overrides = overrides
                    Status = status
                }
        }
        |> Codec.describe "A fork: a session whose history is this one's up to an event."
        |> Codec.named "Fork"

    let runningTurn: Codec<RunningTurn> =
        Codec.record {
            let! turn = Codec.field "turn" (fun (r: RunningTurn) -> r.Turn) positive
            and! profile = Codec.field "profile" (fun r -> r.Profile) Codec.string
            and! inputs = Codec.field "inputs" (fun r -> r.Inputs) (Codec.list input)
            and! step = Codec.field "step" (fun r -> r.Step) (Codec.int |> Codec.atLeast 0)
            and! at = Codec.field "at" (fun r -> r.At) time
            and! seq = Codec.field "seq" (fun r -> r.Seq) position

            return
                {
                    Turn = turn
                    Profile = profile
                    Inputs = inputs
                    Step = step
                    At = at
                    Seq = seq
                }
        }
        |> Codec.describe "The turn in progress and the inputs delivered to it."
        |> Codec.named "RunningTurn"

    let lastTurn: Codec<LastTurn> =
        Codec.record {
            let! turn = Codec.field "turn" (fun (l: LastTurn) -> l.Turn) positive
            and! ended = Codec.field "end" (fun l -> l.End) turnEnd
            and! at = Codec.field "at" (fun l -> l.At) time
            return { Turn = turn; End = ended; At = at }
        }
        |> Codec.named "LastTurn"

    let status: Codec<Status> =
        Codec.enum
            [
                Status.New, "new"
                Status.Open, "open"
                Status.Closed, "closed"
                Status.Archived, "archived"
            ]
        |> Codec.named "SessionStatus"

    let state: Codec<SessionState> =
        Codec.record {
            let! id = Codec.field "id" (fun (s: SessionState) -> s.Id) Codec.string
            and! seq = Codec.field "seq" (fun s -> s.Seq) (Codec.int64 |> Codec.atLeast 0L)
            and! status = Codec.field "status" (fun s -> s.Status) status
            and! title = Codec.optionalField "title" (fun s -> s.Title) Codec.string
            and! profile = Codec.field "profile" (fun s -> s.Profile) Codec.string
            and! overrides = Codec.defaultField "overrides" (fun s -> s.Overrides) [] overrides
            and! parent = Codec.optionalField "parent" (fun s -> s.Parent) eventRef
            and! forks = Codec.defaultField "forks" (fun s -> s.Forks) [] (Codec.list fork)
            and! turns = Codec.field "turns" (fun s -> s.Turns) (Codec.int |> Codec.atLeast 0)
            and! running = Codec.optionalField "running" (fun s -> s.Running) runningTurn
            and! last = Codec.optionalField "last" (fun s -> s.Last) lastTurn
            and! held = Codec.defaultField "held" (fun s -> s.Held) false Codec.bool
            and! queue = Codec.defaultField "queue" (fun s -> s.Queue) [] (Codec.list input)
            and! inbox = Codec.defaultField "inbox" (fun s -> s.Inbox) [] (Codec.list input)

            and! awaitingRequests =
                Codec.defaultField "awaitingRequests" (fun s -> s.AwaitingRequests) [] (Codec.list Codec.string)

            and! awaitingJobs =
                Codec.defaultField "awaitingJobs" (fun s -> s.AwaitingJobs) [] (Codec.list Codec.string)

            and! requests =
                Codec.defaultField "requests" (fun s -> s.Requests) [] (Codec.list pendingRequest)

            return
                {
                    Id = id
                    Seq = seq
                    Status = status
                    Title = title
                    Profile = profile
                    Overrides = overrides
                    Parent = parent
                    Forks = forks
                    Turns = turns
                    Running = running
                    Last = last
                    Held = held
                    Queue = queue
                    Inbox = inbox
                    AwaitingRequests = awaitingRequests
                    AwaitingJobs = awaitingJobs
                    Requests = requests
                }
        }
        |> Codec.describe "A session's state: the fold of its log, and every client's view of it."
        |> Codec.named "SessionState"

    let sessionStart: Codec<SessionStart> =
        Codec.record {
            let! id = Codec.field "id" (fun (s: SessionStart) -> s.Id) text
            and! profile = Codec.field "profile" (fun s -> s.Profile) text
            and! overrides = Codec.defaultField "overrides" (fun s -> s.Overrides) [] overrides
            and! parent = Codec.optionalField "parent" (fun s -> s.Parent) eventRef
            and! title = Codec.optionalField "title" (fun s -> s.Title) Codec.string

            return
                {
                    Id = id
                    Profile = profile
                    Overrides = overrides
                    Parent = parent
                    Title = title
                }
        }
        |> Codec.describe "A session's start: its id, profile, overrides and, for a fork, its parent."
        |> Codec.named "SessionStart"

    let profileSelection: Codec<ProfileSelection> =
        Codec.record {
            let! profile = Codec.field "profile" (fun (p: ProfileSelection) -> p.Profile) text
            and! overrides = Codec.defaultField "overrides" (fun p -> p.Overrides) [] overrides

            and! changed =
                Codec.defaultField "changed" (fun p -> p.Changed) [] (Codec.list Codec.string)

            and! basedOn = Codec.field "basedOn" (fun p -> p.BasedOn) Codec.string

            return
                {
                    Profile = profile
                    Overrides = overrides
                    Changed = changed
                    BasedOn = basedOn
                }
        }
        |> Codec.named "ProfileSelection"

    let command: Codec<Command> =
        Codec.union "kind" {
            let! start =
                Codec.case1 "start" Command.Start (Field.required "session" sessionStart)

            and! prompt =
                Codec.case2
                    "prompt"
                    (fun text mode -> Command.Prompt(text, mode))
                    (Field.required "text" Codec.string)
                    (Field.withDefault "mode" PromptMode.Steer promptMode)

            and! withdraw =
                Codec.case1 "withdraw" Command.Withdraw (Field.required "input" position)

            and! interrupt = Codec.case0 "interrupt" Command.Interrupt

            and! openRequest =
                Codec.case1 "openRequest" Command.OpenRequest (Field.required "request" requestSpec)

            and! answer =
                Codec.case2
                    "answer"
                    (fun request response -> Command.Answer(request, response))
                    (Field.required "request" text)
                    (Field.required "response" Codec.json)

            and! cancelRequest =
                Codec.case2
                    "cancelRequest"
                    (fun request reason -> Command.CancelRequest(request, reason))
                    (Field.required "request" text)
                    (Field.required "reason" Codec.string)

            and! expireRequest =
                Codec.case1 "expireRequest" Command.ExpireRequest (Field.required "request" text)

            and! notifyJob =
                Codec.case2
                    "notifyJob"
                    (fun job note -> Command.NotifyJob(job, note))
                    (Field.required "job" text)
                    (Field.required "note" Codec.string)

            and! selectProfile =
                Codec.case1 "selectProfile" Command.SelectProfile (Field.required "selection" profileSelection)

            and! fork =
                Codec.case2
                    "fork"
                    (fun child upTo -> Command.Fork(child, upTo))
                    (Field.required "child" text)
                    (Field.optional "upTo" position)

            and! close = Codec.case0 "close" Command.Close
            and! reopen = Codec.case0 "reopen" Command.Reopen
            and! archive = Codec.case0 "archive" Command.Archive

            and! takeInputs =
                Codec.case2
                    "takeInputs"
                    (fun turn step -> Command.TakeInputs(turn, step))
                    (Field.required "turn" positive)
                    (Field.required "step" positive)

            and! endTurn =
                Codec.case2
                    "endTurn"
                    (fun turn result -> Command.EndTurn(turn, result))
                    (Field.required "turn" positive)
                    (Field.required "result" turnResult)

            and! failTurn =
                Codec.case2
                    "failTurn"
                    (fun turn reason -> Command.FailTurn(turn, reason))
                    (Field.required "turn" positive)
                    (Field.required "reason" Codec.string)

            and! abandonTurn =
                Codec.case2
                    "abandonTurn"
                    (fun turn reason -> Command.AbandonTurn(turn, reason))
                    (Field.required "turn" positive)
                    (Field.required "reason" Codec.string)

            and! childStarted =
                Codec.case1 "childStarted" Command.ChildStarted (Field.required "child" text)

            and! childFailed =
                Codec.case2
                    "childFailed"
                    (fun child reason -> Command.ChildFailed(child, reason))
                    (Field.required "child" text)
                    (Field.required "reason" Codec.string)

            return
                fun value ->
                    match value with
                    | Command.Start session -> start session
                    | Command.Prompt(text, mode) -> prompt text mode
                    | Command.Withdraw input -> withdraw input
                    | Command.Interrupt -> interrupt
                    | Command.OpenRequest request -> openRequest request
                    | Command.Answer(request, response) -> answer request response
                    | Command.CancelRequest(request, reason) -> cancelRequest request reason
                    | Command.ExpireRequest request -> expireRequest request
                    | Command.NotifyJob(job, note) -> notifyJob job note
                    | Command.SelectProfile selection -> selectProfile selection
                    | Command.Fork(child, upTo) -> fork child upTo
                    | Command.Close -> close
                    | Command.Reopen -> reopen
                    | Command.Archive -> archive
                    | Command.TakeInputs(turn, step) -> takeInputs turn step
                    | Command.EndTurn(turn, result) -> endTurn turn result
                    | Command.FailTurn(turn, reason) -> failTurn turn reason
                    | Command.AbandonTurn(turn, reason) -> abandonTurn turn reason
                    | Command.ChildStarted child -> childStarted child
                    | Command.ChildFailed(child, reason) -> childFailed child reason
        }
        |> Codec.describe "What clients and the host ask a session to do (spec §5)."
        |> Codec.named "SessionCommand"

    let sessionCommand: Codec<SessionCommand> =
        Codec.record {
            let! command = Codec.field "command" (fun (c: SessionCommand) -> c.Command) command
            and! by = Codec.field "by" (fun c -> c.By) attribution
            and! at = Codec.field "at" (fun c -> c.At) time
            return { Command = command; By = by; At = at }
        }
        |> Codec.describe "A command with its sender and time, as the session machine handles it."
        |> Codec.named "StampedSessionCommand"

    let requestResolved: Codec<RequestResolved> =
        Codec.record {
            let! request = Codec.field "request" (fun (r: RequestResolved) -> r.Request) text
            and! resolution = Codec.field "resolution" (fun r -> r.Resolution) resolution
            and! by = Codec.field "by" (fun r -> r.By) attribution
            and! at = Codec.field "at" (fun r -> r.At) time
            and! input = Codec.optionalField "input" (fun r -> r.Input) position

            return
                {
                    Request = request
                    Resolution = resolution
                    By = by
                    At = at
                    Input = input
                }
        }
        |> Codec.describe "A request settled; `input` is set when it became an input for the agent."
        |> Codec.named "RequestResolved"

    let event: Codec<Event> =
        Codec.union "kind" {
            let! started =
                Codec.case3
                    "started"
                    (fun start by at -> Event.Started(start, by, at))
                    (Field.required "session" sessionStart)
                    (Field.required "by" attribution)
                    (Field.required "at" time)

            and! profileSelected =
                Codec.case4
                    "profileSelected"
                    (fun profile overrides changed by -> Event.ProfileSelected(profile, overrides, changed, by))
                    (Field.required "profile" text)
                    (Field.withDefault "overrides" [] overrides)
                    (Field.withDefault "changed" [] (Codec.list Codec.string))
                    (Field.required "by" attribution)

            and! promptQueued =
                Codec.case1 "promptQueued" Event.PromptQueued (Field.required "input" input)

            and! promptSteered =
                Codec.case1 "promptSteered" Event.PromptSteered (Field.required "input" input)

            and! promptWithdrawn =
                Codec.case2
                    "promptWithdrawn"
                    (fun input by -> Event.PromptWithdrawn(input, by))
                    (Field.required "input" position)
                    (Field.required "by" attribution)

            and! turnStarted =
                Codec.case4
                    "turnStarted"
                    (fun turn inputs profile at -> Event.TurnStarted(turn, inputs, profile, at))
                    (Field.required "turn" positive)
                    (Field.required "inputs" (Codec.list position))
                    (Field.required "profile" Codec.string)
                    (Field.required "at" time)

            and! inputsDelivered =
                Codec.case3
                    "inputsDelivered"
                    (fun turn step inputs -> Event.InputsDelivered(turn, step, inputs))
                    (Field.required "turn" positive)
                    (Field.required "step" positive)
                    (Field.required "inputs" (Codec.list position))

            and! turnEnded =
                Codec.case3
                    "turnEnded"
                    (fun turn ended at -> Event.TurnEnded(turn, ended, at))
                    (Field.required "turn" positive)
                    (Field.required "end" turnEnd)
                    (Field.required "at" time)

            and! interrupted =
                Codec.case2
                    "interrupted"
                    (fun turn by -> Event.Interrupted(turn, by))
                    (Field.optional "turn" positive)
                    (Field.required "by" attribution)

            and! requestOpened =
                Codec.case1 "requestOpened" Event.RequestOpened (Field.required "pending" pendingRequest)

            and! requestResolved =
                Codec.caseOf "requestResolved" Event.RequestResolved requestResolved

            and! jobNotified =
                Codec.case1 "jobNotified" Event.JobNotified (Field.required "input" input)

            and! forked =
                Codec.case3
                    "forked"
                    (fun child upTo by -> Event.Forked(child, upTo, by))
                    (Field.required "child" text)
                    (Field.required "upTo" position)
                    (Field.required "by" attribution)

            and! forkReady =
                Codec.case1 "forkReady" Event.ForkReady (Field.required "child" text)

            and! forkFailed =
                Codec.case2
                    "forkFailed"
                    (fun child reason -> Event.ForkFailed(child, reason))
                    (Field.required "child" text)
                    (Field.required "reason" Codec.string)

            and! closed = Codec.case1 "closed" Event.Closed (Field.required "by" attribution)

            and! reopened =
                Codec.case1 "reopened" Event.Reopened (Field.required "by" attribution)

            and! archived =
                Codec.case1 "archived" Event.Archived (Field.required "by" attribution)

            return
                fun value ->
                    match value with
                    | Event.Started(start, by, at) -> started start by at
                    | Event.ProfileSelected(profile, overrides, changed, by) ->
                        profileSelected profile overrides changed by
                    | Event.PromptQueued input -> promptQueued input
                    | Event.PromptSteered input -> promptSteered input
                    | Event.PromptWithdrawn(input, by) -> promptWithdrawn input by
                    | Event.TurnStarted(turn, inputs, profile, at) -> turnStarted turn inputs profile at
                    | Event.InputsDelivered(turn, step, inputs) -> inputsDelivered turn step inputs
                    | Event.TurnEnded(turn, ended, at) -> turnEnded turn ended at
                    | Event.Interrupted(turn, by) -> interrupted turn by
                    | Event.RequestOpened pending -> requestOpened pending
                    | Event.RequestResolved resolved -> requestResolved resolved
                    | Event.JobNotified input -> jobNotified input
                    | Event.Forked(child, upTo, by) -> forked child upTo by
                    | Event.ForkReady child -> forkReady child
                    | Event.ForkFailed(child, reason) -> forkFailed child reason
                    | Event.Closed by -> closed by
                    | Event.Reopened by -> reopened by
                    | Event.Archived by -> archived by
        }
        |> Codec.describe "What happened in a session (spec §5); kept forever."
        |> Codec.named "SessionEvent"

    let turnStart: Codec<TurnStart> =
        Codec.record {
            let! session = Codec.field "session" (fun (t: TurnStart) -> t.Session) text
            and! turn = Codec.field "turn" (fun t -> t.Turn) positive
            and! profile = Codec.field "profile" (fun t -> t.Profile) Codec.string
            and! inputs = Codec.field "inputs" (fun t -> t.Inputs) (Codec.list input)
            and! seq = Codec.field "seq" (fun t -> t.Seq) position

            return
                {
                    Session = session
                    Turn = turn
                    Profile = profile
                    Inputs = inputs
                    Seq = seq
                }
        }
        |> Codec.named "TurnStart"

    let forkStart: Codec<ForkStart> =
        Codec.record {
            let! parent = Codec.field "parent" (fun (f: ForkStart) -> f.Parent) eventRef
            and! start = Codec.field "start" (fun f -> f.Start) sessionStart
            and! seq = Codec.field "seq" (fun f -> f.Seq) position

            return
                {
                    Parent = parent
                    Start = start
                    Seq = seq
                }
        }
        |> Codec.named "ForkStart"

    let intent: Codec<Intent> =
        Codec.union "kind" {
            let! runTurn = Codec.case1 "runTurn" Intent.RunTurn (Field.required "turn" turnStart)

            and! expire =
                Codec.case2
                    "expire"
                    (fun request at -> Intent.Expire(request, at))
                    (Field.required "request" text)
                    (Field.required "at" time)

            and! startFork =
                Codec.case1 "startFork" Intent.StartFork (Field.required "fork" forkStart)

            and! abandon = Codec.case1 "abandon" Intent.Abandon (Field.required "turn" positive)

            return
                fun value ->
                    match value with
                    | Intent.RunTurn turn -> runTurn turn
                    | Intent.Expire(request, at) -> expire request at
                    | Intent.StartFork fork -> startFork fork
                    | Intent.Abandon turn -> abandon turn
        }
        |> Codec.describe "What a session asks its host to do after an event."
        |> Codec.named "SessionIntent"
