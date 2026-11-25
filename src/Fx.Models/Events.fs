namespace Fx.Models

open System
open Fx.Contracts
open Fx.Effects

/// Tokens a response used so far. Input counts every input token, cached or not; `CacheRead` and
/// `CacheWrite` are the parts of it read from and written to the cache. Output counts every output
/// token; `Reasoning` is the part of it spent reasoning.
type Usage =
    {
        Input: int
        Output: int
        CacheRead: int
        CacheWrite: int
        Reasoning: int
    }

    override this.ToString() =
        $"usage(in {this.Input}, out {this.Output}, cache read {this.CacheRead}, cache write {this.CacheWrite}, reasoning {this.Reasoning})"

/// Why a response ended normally.
[<RequireQualifiedAccess>]
type StopReason =
    /// The model finished its turn.
    | EndTurn
    /// The model wants its tool calls run.
    | ToolUse
    /// The output limit (`MaxOutputTokens` or the model's own) cut the response.
    | MaxTokens
    /// A stop sequence matched (which one, if the provider says).
    | StopSequence of sequence: string option
    /// The model refused; the response may carry a refusal part.
    | Refusal
    /// The provider filtered the output.
    | ContentFilter
    /// The context window filled up while generating.
    | ContextWindow
    /// The provider paused a long turn; send the conversation again to continue it.
    | Pause
    /// A reason this version does not know, as the provider named it.
    | Other of reason: string

    override this.ToString() =
        match this with
        | EndTurn -> "end turn"
        | ToolUse -> "tool use"
        | MaxTokens -> "max tokens"
        | StopSequence(Some s) -> $"stop sequence {s}"
        | StopSequence None -> "stop sequence"
        | Refusal -> "refusal"
        | ContentFilter -> "content filter"
        | ContextWindow -> "context window"
        | Pause -> "pause"
        | Other reason -> "other: " + reason

/// A request the provider will not take now; try again later.
type RetryLater =
    {
        /// How long the provider asked to wait (`Retry-After`), if it said.
        RetryAfter: TimeSpan option
        Message: string
    }

    override this.ToString() =
        match this.RetryAfter with
        | Some wait -> $"{this.Message} (retry after {wait.TotalSeconds} s)"
        | None -> this.Message

/// The request does not fit the model's context window: compact the history and try again.
type ContextOverflow =
    {
        /// How many tokens the request had, if the provider said.
        Tokens: int option
        /// The limit, if the provider said.
        Limit: int option
        Message: string
    }

    override this.ToString() =
        let show =
            function
            | Some(n: int) -> string n
            | None -> "?"

        $"{this.Message} ({show this.Tokens} > {show this.Limit} tokens)"

/// The model-egress gates stopped a request before it left (spec §18).
type EgressBlock =
    {
        /// The gates want a human to decide (Ask) rather than refusing (Deny).
        Ask: bool
        /// Their reasons, each naming the gate.
        Reasons: string list
    }

    override this.ToString() =
        let verdict = if this.Ask then "needs approval" else "denied"
        let reasons = String.concat "; " this.Reasons
        $"{verdict}: {reasons}"

/// Why a model call failed (spec §10.3): expected failures, mapped from each provider's responses by
/// its adapter. Messages are the provider's, for people; code matches on the case.
type ChatError =
    /// Too many requests or tokens per time: retry later.
    | RateLimited of RetryLater
    /// Credits, billing or plan quota are exhausted (also mid-stream): retrying will not help.
    | QuotaExhausted of message: string
    /// The request is too long for the context window: compact, then retry.
    | ContextOverflow of ContextOverflow
    /// The provider refused the request on safety or policy grounds.
    | Refused of message: string
    /// The credentials were rejected (401).
    | AuthFailed of message: string
    /// The credentials do not allow this (403).
    | PermissionDenied of message: string
    /// The model or endpoint does not exist (404).
    | NotFound of message: string
    /// The provider rejected the request as malformed.
    | InvalidRequest of message: string
    /// The provider is overloaded (503, Anthropic's 529): retry later.
    | Overloaded of RetryLater
    /// The provider failed (5xx).
    | ServerError of status: int * message: string
    /// The connection failed, broke off or timed out.
    | Transport of HttpError
    /// The provider cancelled the response. (The caller's own cancellation is an
    /// `OperationCanceledException`, not an error.)
    | Cancelled of message: string
    /// The provider sent something the adapter cannot read, or a stream that breaks the event rules.
    | InvalidResponse of message: string
    /// The model-egress gates stopped the request; nothing was sent.
    | EgressBlocked of EgressBlock
    /// No wire, provider definition, credentials or capability for the request.
    | Unavailable of message: string

    override this.ToString() =
        match this with
        | RateLimited r -> "rate limited: " + string r
        | QuotaExhausted m -> "quota exhausted: " + m
        | ContextOverflow o -> "context overflow: " + string o
        | Refused m -> "refused: " + m
        | AuthFailed m -> "authentication failed: " + m
        | PermissionDenied m -> "permission denied: " + m
        | NotFound m -> "not found: " + m
        | InvalidRequest m -> "invalid request: " + m
        | Overloaded r -> "overloaded: " + string r
        | ServerError(status, m) -> $"server error {status}: {m}"
        | Transport e -> "transport: " + string e
        | Cancelled m -> "cancelled: " + m
        | InvalidResponse m -> "invalid response: " + m
        | EgressBlocked b -> "egress blocked: " + string b
        | Unavailable m -> "unavailable: " + m

/// The kind of a part a response starts.
[<RequireQualifiedAccess>]
type PartStart =
    | Text
    | Reasoning
    | ToolCall of id: string * name: string
    | Refusal

    override this.ToString() =
        match this with
        | Text -> "text"
        | Reasoning -> "reasoning"
        | ToolCall(id, name) -> $"tool call {id} {name}"
        | Refusal -> "refusal"

/// What the provider says about a response when it starts.
type ResponseStart =
    {
        /// The provider's id for the response.
        Id: string option
        /// The model that answers, as the provider names it.
        Model: string option
    }

    override this.ToString() =
        let id = this.Id |> Option.defaultValue "-"
        let model = this.Model |> Option.defaultValue "-"
        $"start(id {id}, model {model})"

/// One event of a streamed response (spec §10.2 `ChatWire.Stream`). Parts are numbered 0, 1, 2, … in
/// the order they start; `ChatEvents.check` states the rules a stream follows, and
/// `ChatAggregate.apply` folds a stream into a `ChatResponse`.
[<RequireQualifiedAccess>]
type ChatEvent =
    /// The response started (first, at most once).
    | Started of ResponseStart
    /// Part `index` starts.
    | PartStarted of index: int * start: PartStart
    /// Text for part `index`: its text, reasoning, tool-call arguments or refusal.
    | Delta of index: int * text: string
    /// Opaque state for part `index`, whole (at most once per part).
    | PartState of index: int * state: ProviderState
    /// Part `index` is complete.
    | PartEnded of index: int
    /// The usage so far (each report replaces the previous one; counts never decrease).
    | UsageUpdated of Usage
    /// The response is complete (last).
    | Finished of StopReason
    /// The response failed (last). Parts so far stay as they are.
    | Failed of ChatError

    override this.ToString() =
        match this with
        | Started start -> string start
        | PartStarted(index, start) -> $"part {index} started: {start}"
        | Delta(index, text) -> $"part {index} delta ({text.Length} chars)"
        | PartState(index, state) -> $"part {index} {state}"
        | PartEnded index -> $"part {index} ended"
        | UsageUpdated usage -> string usage
        | Finished stop -> $"finished ({stop})"
        | Failed error -> $"failed ({error})"

/// A complete response: the assistant's parts, why it stopped, and what it used.
type ChatResponse =
    {
        Id: string option
        Model: string option
        Parts: Part list
        Stop: StopReason
        Usage: Usage
    }

    override this.ToString() =
        $"response ({this.Parts.Length} parts, {this.Stop}, {this.Usage})"

/// A failed response: the error, and what had arrived before it.
type ChatFailure =
    {
        Error: ChatError
        Id: string option
        Model: string option
        Parts: Part list
        Usage: Usage
    }

    override this.ToString() =
        $"failure ({this.Error}; {this.Parts.Length} parts before it)"

module Usage =
    let zero: Usage =
        {
            Input = 0
            Output = 0
            CacheRead = 0
            CacheWrite = 0
            Reasoning = 0
        }

    let codec: Codec<Usage> =
        Codec.record {
            let! input = Codec.defaultField "input" (fun (u: Usage) -> u.Input) 0 Codec.int
            and! output = Codec.defaultField "output" (fun u -> u.Output) 0 Codec.int
            and! cacheRead = Codec.defaultField "cacheRead" (fun u -> u.CacheRead) 0 Codec.int
            and! cacheWrite = Codec.defaultField "cacheWrite" (fun u -> u.CacheWrite) 0 Codec.int
            and! reasoning = Codec.defaultField "reasoning" (fun u -> u.Reasoning) 0 Codec.int

            return
                {
                    Input = input
                    Output = output
                    CacheRead = cacheRead
                    CacheWrite = cacheWrite
                    Reasoning = reasoning
                }
        }
        |> Codec.describe "Tokens used: input (cached included), output (reasoning included), and their parts."
        |> Codec.named "Usage"

    /// Whether every count of `later` is at least that of `earlier`.
    let isMonotonic (earlier: Usage) (later: Usage) : bool =
        later.Input >= earlier.Input
        && later.Output >= earlier.Output
        && later.CacheRead >= earlier.CacheRead
        && later.CacheWrite >= earlier.CacheWrite
        && later.Reasoning >= earlier.Reasoning

    /// The sum of two usages (e.g. over the calls of a turn).
    let add (a: Usage) (b: Usage) : Usage =
        {
            Input = a.Input + b.Input
            Output = a.Output + b.Output
            CacheRead = a.CacheRead + b.CacheRead
            CacheWrite = a.CacheWrite + b.CacheWrite
            Reasoning = a.Reasoning + b.Reasoning
        }

    /// What the usage cost, in US dollars, at these prices.
    let cost (prices: Cost) (usage: Usage) : decimal =
        let perToken (price: decimal) (tokens: int) = price * decimal tokens / 1_000_000m
        let uncached = max 0 (usage.Input - usage.CacheRead - usage.CacheWrite)

        perToken prices.Input uncached
        + perToken (prices.CacheRead |> Option.defaultValue prices.Input) usage.CacheRead
        + perToken (prices.CacheWrite |> Option.defaultValue prices.Input) usage.CacheWrite
        + perToken prices.Output usage.Output

module StopReason =
    let codec: Codec<StopReason> =
        Codec.union "kind" {
            let! endTurn = Codec.case0 "endTurn" StopReason.EndTurn
            and! toolUse = Codec.case0 "toolUse" StopReason.ToolUse
            and! maxTokens = Codec.case0 "maxTokens" StopReason.MaxTokens

            and! stopSequence =
                Codec.case1 "stopSequence" StopReason.StopSequence (Field.optional "sequence" Codec.string)

            and! refusal = Codec.case0 "refusal" StopReason.Refusal
            and! contentFilter = Codec.case0 "contentFilter" StopReason.ContentFilter
            and! contextWindow = Codec.case0 "contextWindow" StopReason.ContextWindow
            and! pause = Codec.case0 "pause" StopReason.Pause

            and! other =
                Codec.case1 "other" StopReason.Other (Field.required "reason" Codec.string)

            return
                fun value ->
                    match value with
                    | StopReason.EndTurn -> endTurn
                    | StopReason.ToolUse -> toolUse
                    | StopReason.MaxTokens -> maxTokens
                    | StopReason.StopSequence s -> stopSequence s
                    | StopReason.Refusal -> refusal
                    | StopReason.ContentFilter -> contentFilter
                    | StopReason.ContextWindow -> contextWindow
                    | StopReason.Pause -> pause
                    | StopReason.Other reason -> other reason
        }
        |> Codec.describe "Why a response ended normally."
        |> Codec.named "StopReason"

module HttpErrors =
    let private phase: Codec<HttpPhase> =
        Codec.enum [ HttpPhase.Response, "response"; HttpPhase.Body, "body" ]

    /// The JSON form of an `HttpError` (in `ChatError.Transport`, cassettes and journals).
    let codec: Codec<HttpError> =
        Codec.union "kind" {
            let! invalid =
                Codec.case1 "invalidRequest" HttpError.InvalidRequest (Field.required "reason" Codec.string)

            and! refused =
                Codec.case1 "refused" HttpError.Refused (Field.required "reason" Codec.string)

            and! connect =
                Codec.case1 "connectFailed" HttpError.ConnectFailed (Field.required "reason" Codec.string)

            and! reset =
                Codec.case1 "reset" HttpError.Reset (Field.required "reason" Codec.string)

            and! timedOut =
                Codec.case1 "timedOut" HttpError.TimedOut (Field.required "phase" phase)

            return
                fun value ->
                    match value with
                    | HttpError.InvalidRequest reason -> invalid reason
                    | HttpError.Refused reason -> refused reason
                    | HttpError.ConnectFailed reason -> connect reason
                    | HttpError.Reset reason -> reset reason
                    | HttpError.TimedOut p -> timedOut p
        }
        |> Codec.named "HttpError"

module ChatError =
    let private milliseconds: Codec<TimeSpan> =
        Codec.int64
        |> Codec.map (fun ms -> TimeSpan.FromMilliseconds(float ms)) (fun t -> int64 t.TotalMilliseconds)

    let private retryLater: Codec<RetryLater> =
        Codec.record {
            let! retryAfter =
                Codec.optionalField "retryAfterMs" (fun (r: RetryLater) -> r.RetryAfter) milliseconds

            and! message = Codec.defaultField "message" (fun r -> r.Message) "" Codec.string

            return
                {
                    RetryAfter = retryAfter
                    Message = message
                }
        }

    let private overflow: Codec<ContextOverflow> =
        Codec.record {
            let! tokens =
                Codec.optionalField "tokens" (fun (o: ContextOverflow) -> o.Tokens) Codec.int

            and! limit = Codec.optionalField "limit" (fun o -> o.Limit) Codec.int
            and! message = Codec.defaultField "message" (fun o -> o.Message) "" Codec.string

            return
                {
                    Tokens = tokens
                    Limit = limit
                    Message = message
                }
        }

    let private block: Codec<EgressBlock> =
        Codec.record {
            let! ask = Codec.field "ask" (fun (b: EgressBlock) -> b.Ask) Codec.bool

            and! reasons =
                Codec.defaultField "reasons" (fun b -> b.Reasons) [] (Codec.list Codec.string)

            return { Ask = ask; Reasons = reasons }
        }

    let private message = Field.withDefault "message" "" Codec.string

    let codec: Codec<ChatError> =
        Codec.union "kind" {
            let! rateLimited = Codec.caseOf "rateLimited" RateLimited retryLater
            and! quota = Codec.case1 "quotaExhausted" QuotaExhausted message
            and! overflowed = Codec.caseOf "contextOverflow" ContextOverflow overflow
            and! refused = Codec.case1 "refused" Refused message
            and! auth = Codec.case1 "authFailed" AuthFailed message
            and! permission = Codec.case1 "permissionDenied" PermissionDenied message
            and! notFound = Codec.case1 "notFound" NotFound message
            and! invalid = Codec.case1 "invalidRequest" InvalidRequest message
            and! overloaded = Codec.caseOf "overloaded" Overloaded retryLater

            and! server =
                Codec.case2
                    "serverError"
                    (fun status m -> ServerError(status, m))
                    (Field.required "status" Codec.int)
                    message

            and! transport =
                Codec.case1 "transport" Transport (Field.required "error" HttpErrors.codec)

            and! cancelled = Codec.case1 "cancelled" Cancelled message
            and! invalidResponse = Codec.case1 "invalidResponse" InvalidResponse message
            and! egress = Codec.caseOf "egressBlocked" EgressBlocked block
            and! unavailable = Codec.case1 "unavailable" Unavailable message

            return
                fun value ->
                    match value with
                    | RateLimited r -> rateLimited r
                    | QuotaExhausted m -> quota m
                    | ContextOverflow o -> overflowed o
                    | Refused m -> refused m
                    | AuthFailed m -> auth m
                    | PermissionDenied m -> permission m
                    | NotFound m -> notFound m
                    | InvalidRequest m -> invalid m
                    | Overloaded r -> overloaded r
                    | ServerError(status, m) -> server status m
                    | Transport e -> transport e
                    | Cancelled m -> cancelled m
                    | InvalidResponse m -> invalidResponse m
                    | EgressBlocked b -> egress b
                    | Unavailable m -> unavailable m
        }
        |> Codec.describe "Why a model call failed."
        |> Codec.named "ChatError"

    /// Whether the same request may succeed later: rate limits, overload, server errors, transport
    /// failures and unreadable responses.
    let isRetryable (error: ChatError) : bool =
        match error with
        | RateLimited _
        | Overloaded _
        | ServerError _
        | InvalidResponse _ -> true
        | Transport e ->
            match e with
            | HttpError.ConnectFailed _
            | HttpError.Reset _
            | HttpError.TimedOut _ -> true
            | HttpError.InvalidRequest _
            | HttpError.Refused _ -> false
        | QuotaExhausted _
        | ContextOverflow _
        | ChatError.Refused _
        | AuthFailed _
        | PermissionDenied _
        | NotFound _
        | ChatError.InvalidRequest _
        | Cancelled _
        | EgressBlocked _
        | Unavailable _ -> false

    /// How long to wait before retrying, if the provider said.
    let retryAfter (error: ChatError) : TimeSpan option =
        match error with
        | RateLimited r
        | Overloaded r -> r.RetryAfter
        | _ -> None

    /// Whether the history should be compacted before trying again (spec §5).
    let needsCompaction (error: ChatError) : bool =
        match error with
        | ContextOverflow _ -> true
        | _ -> false

module PartStart =
    let codec: Codec<PartStart> =
        Codec.union "kind" {
            let! text = Codec.case0 "text" PartStart.Text
            and! reasoning = Codec.case0 "reasoning" PartStart.Reasoning

            and! call =
                Codec.case2
                    "toolCall"
                    (fun id name -> PartStart.ToolCall(id, name))
                    (Field.required "id" Codec.string)
                    (Field.required "name" Codec.string)

            and! refusal = Codec.case0 "refusal" PartStart.Refusal

            return
                fun value ->
                    match value with
                    | PartStart.Text -> text
                    | PartStart.Reasoning -> reasoning
                    | PartStart.ToolCall(id, name) -> call id name
                    | PartStart.Refusal -> refusal
        }
        |> Codec.named "PartStart"

module ChatEvent =
    let private start: Codec<ResponseStart> =
        Codec.record {
            let! id = Codec.optionalField "id" (fun (s: ResponseStart) -> s.Id) Codec.string
            and! model = Codec.optionalField "model" (fun s -> s.Model) Codec.string
            return { Id = id; Model = model }
        }

    let private index = Field.required "index" (Codec.int |> Codec.atLeast 0)

    let codec: Codec<ChatEvent> =
        Codec.union "kind" {
            let! started = Codec.caseOf "started" ChatEvent.Started start

            and! partStarted =
                Codec.case2
                    "partStarted"
                    (fun i s -> ChatEvent.PartStarted(i, s))
                    index
                    (Field.required "part" PartStart.codec)

            and! delta =
                Codec.case2 "delta" (fun i t -> ChatEvent.Delta(i, t)) index (Field.required "text" Codec.string)

            and! state =
                Codec.case2
                    "partState"
                    (fun i s -> ChatEvent.PartState(i, s))
                    index
                    (Field.required "state" ProviderState.codec)

            and! ended = Codec.case1 "partEnded" ChatEvent.PartEnded index

            and! usage =
                Codec.case1 "usage" ChatEvent.UsageUpdated (Field.required "usage" Usage.codec)

            and! finished =
                Codec.case1 "finished" ChatEvent.Finished (Field.required "stop" StopReason.codec)

            and! failed =
                Codec.case1 "failed" ChatEvent.Failed (Field.required "error" ChatError.codec)

            return
                fun value ->
                    match value with
                    | ChatEvent.Started s -> started s
                    | ChatEvent.PartStarted(i, s) -> partStarted i s
                    | ChatEvent.Delta(i, t) -> delta i t
                    | ChatEvent.PartState(i, s) -> state i s
                    | ChatEvent.PartEnded i -> ended i
                    | ChatEvent.UsageUpdated u -> usage u
                    | ChatEvent.Finished stop -> finished stop
                    | ChatEvent.Failed e -> failed e
        }
        |> Codec.describe "One event of a streamed model response."
        |> Codec.named "ChatEvent"

    /// Whether the event ends a stream.
    let isTerminal (event: ChatEvent) : bool =
        match event with
        | ChatEvent.Finished _
        | ChatEvent.Failed _ -> true
        | _ -> false

module ChatResponse =
    let codec: Codec<ChatResponse> =
        Codec.record {
            let! id = Codec.optionalField "id" (fun (r: ChatResponse) -> r.Id) Codec.string
            and! model = Codec.optionalField "model" (fun r -> r.Model) Codec.string
            and! parts = Codec.field "parts" (fun r -> r.Parts) (Codec.list Part.codec)
            and! stop = Codec.field "stop" (fun r -> r.Stop) StopReason.codec
            and! usage = Codec.defaultField "usage" (fun r -> r.Usage) Usage.zero Usage.codec

            return
                {
                    Id = id
                    Model = model
                    Parts = parts
                    Stop = stop
                    Usage = usage
                }
        }
        |> Codec.describe "A complete model response."
        |> Codec.named "ChatResponse"

    /// The response as the assistant message that continues the conversation.
    let message (response: ChatResponse) : Message = Message.assistant response.Parts

    /// The tool calls the response makes, in order.
    let toolCalls (response: ChatResponse) : ToolCall list =
        response.Parts
        |> List.choose (fun p ->
            match p with
            | Part.ToolCall c -> Some c
            | _ -> None)

    /// The text of its text parts, concatenated.
    let text (response: ChatResponse) : string =
        response.Parts
        |> List.choose (fun p ->
            match p with
            | Part.Text t -> Some t.Text
            | _ -> None)
        |> String.concat ""

module ChatFailure =
    let codec: Codec<ChatFailure> =
        Codec.record {
            let! error = Codec.field "error" (fun (f: ChatFailure) -> f.Error) ChatError.codec
            and! id = Codec.optionalField "id" (fun f -> f.Id) Codec.string
            and! model = Codec.optionalField "model" (fun f -> f.Model) Codec.string
            and! parts = Codec.defaultField "parts" (fun f -> f.Parts) [] (Codec.list Part.codec)
            and! usage = Codec.defaultField "usage" (fun f -> f.Usage) Usage.zero Usage.codec

            return
                {
                    Error = error
                    Id = id
                    Model = model
                    Parts = parts
                    Usage = usage
                }
        }
        |> Codec.describe "A failed model response and what arrived before the failure."
        |> Codec.named "ChatFailure"
