/// The Messages API's event stream as canonical events: a pure fold over server-sent events
/// (`SseAdapter.Decode`), tested on the documented streams without a network.
///
/// | Anthropic | Canonical |
/// |---|---|
/// | `message_start` | `started {id, model}`, `usage` |
/// | `content_block_start` `text` / `thinking` / `redacted_thinking` / `tool_use` | `partStarted` text / reasoning / reasoning / toolCall |
/// | `text_delta`, `thinking_delta`, `input_json_delta` | `delta` |
/// | `signature_delta` (gathered), a redacted block's `data` | `partState` (`signature`, `redacted`) before `partEnded` |
/// | `content_block_stop` | `partEnded` |
/// | `message_delta` | `usage` (cumulative), the stop reason |
/// | `message_stop` | `finished` |
/// | `error` | `failed` (`Errors.ofEvent`) |
/// | `ping`, unknown events, blocks and deltas | nothing (the API may add them) |
///
/// Anthropic numbers content blocks itself; parts are numbered in the order they start, so blocks
/// that have no canonical part (server tools, fallback markers) leave no gap.
module Fx.Ext.ModelAnthropic.Decoder

open Fx.Contracts
open Fx.Models
open Fx.Sdk

/// What a content block becomes.
[<RequireQualifiedAccess>]
type BlockKind =
    | Text
    /// Thinking, with its signature so far (it arrives in `signature_delta`s just before the end).
    | Thinking of signature: string
    /// Redacted thinking: its data arrived whole with the block.
    | Redacted of data: string
    /// A tool call: its input at the start (`{}` when streamed), and whether arguments streamed.
    | ToolUse of input: Json * streamed: bool
    /// A block with no canonical part: its events are ignored.
    | Skipped

    override this.ToString() =
        match this with
        | Text -> "text"
        | Thinking signature -> $"thinking (signature {signature.Length} chars)"
        | Redacted data -> $"redacted thinking ({data.Length} chars)"
        | ToolUse(_, streamed) -> $"tool use (streamed {streamed})"
        | Skipped -> "skipped"

/// An open content block: its canonical part and kind.
type Block =
    {
        Part: int
        Kind: BlockKind
    }

    override this.ToString() =
        $"block (part {this.Part}, {this.Kind})"

/// Anthropic's token counts as reported so far (each `message_delta` repeats or updates them).
type RawUsage =
    {
        Input: int
        CacheWrite: int
        CacheRead: int
        Output: int
        Thinking: int
    }

    override this.ToString() =
        $"raw usage (in {this.Input}, cache write {this.CacheWrite}, cache read {this.CacheRead}, out {this.Output}, thinking {this.Thinking})"

/// The decoder's state between events.
type State =
    {
        /// Stamps provider state: the call's family and model.
        Family: string
        Model: ModelRef
        /// Open blocks by Anthropic's index.
        Blocks: Map<int, Block>
        /// Parts started so far (the next part's index).
        Parts: int
        Raw: RawUsage
        /// The last usage reported (counts never decrease).
        Usage: Usage
        Stop: StopReason option
    }

    override this.ToString() =
        $"anthropic decoder ({this.Parts} parts, {this.Blocks.Count} open)"

let private zero: RawUsage =
    {
        Input = 0
        CacheWrite = 0
        CacheRead = 0
        Output = 0
        Thinking = 0
    }

/// The state before the first event of a call.
let initial (ctx: WireCtx) : State =
    {
        Family = ctx.Family
        Model = ctx.Model
        Blocks = Map.empty
        Parts = 0
        Raw = zero
        Usage = Usage.zero
        Stop = None
    }

let private text (name: string) (json: Json) : string option =
    match Json.tryField name json with
    | Some(Json.String s) -> Some s
    | _ -> None

let private count (name: string) (json: Json) : int option =
    match Json.tryField name json with
    | Some(Json.Number n) ->
        match JsonNumber.tryToDecimal n with
        | Some d when d >= 0m && d <= 2_000_000_000m -> Some(int d)
        | _ -> None
    | _ -> None

/// Anthropic's `usage` over what was reported before: counts it leaves out keep their values.
let private raw (previous: RawUsage) (usage: Json) : RawUsage =
    let get name (fallback: int) =
        count name usage |> Option.defaultValue fallback

    {
        Input = get "input_tokens" previous.Input
        CacheWrite = get "cache_creation_input_tokens" previous.CacheWrite
        CacheRead = get "cache_read_input_tokens" previous.CacheRead
        Output = get "output_tokens" previous.Output
        Thinking =
            Json.tryField "output_tokens_details" usage
            |> Option.bind (count "thinking_tokens")
            |> Option.defaultValue previous.Thinking
    }

/// The canonical usage: Anthropic's `input_tokens` excludes cache reads and writes, the canonical
/// input includes them. No count goes below the last report.
let usage (previous: Usage) (raw: RawUsage) : Usage =
    {
        Input = max previous.Input (raw.Input + raw.CacheWrite + raw.CacheRead)
        Output = max previous.Output raw.Output
        CacheRead = max previous.CacheRead raw.CacheRead
        CacheWrite = max previous.CacheWrite raw.CacheWrite
        Reasoning = max previous.Reasoning (min raw.Thinking raw.Output)
    }

/// A stop reason as the API names it.
let stopReason (reason: string) (sequence: string option) : StopReason =
    match reason with
    | "end_turn" -> StopReason.EndTurn
    | "tool_use" -> StopReason.ToolUse
    | "max_tokens" -> StopReason.MaxTokens
    | "stop_sequence" -> StopReason.StopSequence sequence
    | "refusal" -> StopReason.Refusal
    | "pause_turn" -> StopReason.Pause
    | "model_context_window_exceeded" -> StopReason.ContextWindow
    | other -> StopReason.Other other

let private decoded (state: State) (events: ChatEvent list) : Decoded<State> = { State = state; Events = events }

let private fail (state: State) (message: string) : Decoded<State> =
    decoded state [ ChatEvent.Failed(InvalidResponse message) ]

let private withUsage (state: State) (json: Json) : State * ChatEvent list =
    match Json.tryField "usage" json with
    | Some(Json.Object _ as reported) ->
        let raw = raw state.Raw reported
        let usage = usage state.Usage raw
        { state with Raw = raw; Usage = usage }, [ ChatEvent.UsageUpdated usage ]
    | _ -> state, []

let private messageStart (state: State) (data: Json) : Decoded<State> =
    match Json.tryField "message" data with
    | Some(Json.Object _ as message) ->
        let state, usage = withUsage state message

        decoded
            state
            (ChatEvent.Started
                {
                    Id = text "id" message
                    Model = text "model" message
                }
             :: usage)
    | _ -> fail state "message_start without a message"

let private blockStart (state: State) (index: int) (content: Json) : Decoded<State> =
    let part = state.Parts

    let opened (kind: BlockKind) (start: PartStart option) (events: ChatEvent list) =
        let parts = if start.IsSome then state.Parts + 1 else state.Parts

        decoded
            { state with
                Blocks = state.Blocks.Add(index, { Part = part; Kind = kind })
                Parts = parts
            }
            [
                match start with
                | Some start -> ChatEvent.PartStarted(part, start)
                | None -> ()
                yield! events
            ]

    let initialText (name: string) =
        match text name content with
        | Some "" -> []
        | Some initial -> [ ChatEvent.Delta(part, initial) ]
        | None -> []

    if state.Blocks.ContainsKey index then
        fail state $"content block {index} started twice"
    else
        match text "type" content with
        | Some "text" -> opened BlockKind.Text (Some PartStart.Text) (initialText "text")
        | Some "thinking" ->
            opened
                (BlockKind.Thinking(text "signature" content |> Option.defaultValue ""))
                (Some PartStart.Reasoning)
                (initialText "thinking")
        | Some "redacted_thinking" ->
            opened (BlockKind.Redacted(text "data" content |> Option.defaultValue "")) (Some PartStart.Reasoning) []
        | Some "tool_use" ->
            match text "id" content, text "name" content with
            | Some id, Some name ->
                let input = Json.tryField "input" content |> Option.defaultValue (Json.Object [])
                opened (BlockKind.ToolUse(input, false)) (Some(PartStart.ToolCall(id, name))) []
            | _ -> fail state $"tool_use block {index} without an id or a name"
        | _ -> opened BlockKind.Skipped None []

let private blockDelta (state: State) (index: int) (delta: Json) : Decoded<State> =
    match state.Blocks.TryFind index with
    | None -> fail state $"a delta for content block {index}, which is not open"
    | Some block ->
        let update (kind: BlockKind) =
            { state with
                Blocks = state.Blocks.Add(index, { block with Kind = kind })
            }

        let piece (name: string) =
            match text name delta with
            | Some "" -> decoded state []
            | Some piece -> decoded state [ ChatEvent.Delta(block.Part, piece) ]
            | None -> fail state $"a delta for content block {index} without {name}"

        match block.Kind, text "type" delta with
        | BlockKind.Skipped, _ -> decoded state []
        | BlockKind.Text, Some "text_delta" -> piece "text"
        | BlockKind.Thinking _, Some "thinking_delta" -> piece "thinking"
        | BlockKind.Thinking signature, Some "signature_delta" ->
            decoded (update (BlockKind.Thinking(signature + (text "signature" delta |> Option.defaultValue "")))) []
        | BlockKind.ToolUse(input, _), Some "input_json_delta" ->
            match text "partial_json" delta with
            | Some "" -> decoded state []
            | Some json -> decoded (update (BlockKind.ToolUse(input, true))) [ ChatEvent.Delta(block.Part, json) ]
            | None -> fail state $"a delta for content block {index} without partial_json"
        // Citations, and deltas this version does not know.
        | _ -> decoded state []

let private blockStop (state: State) (index: int) : Decoded<State> =
    match state.Blocks.TryFind index with
    | None -> fail state $"content block {index} stopped, but it is not open"
    | Some block ->
        let state =
            { state with
                Blocks = state.Blocks.Remove index
            }

        let stamp (kind: string) (data: string) : ProviderState =
            {
                Family = state.Family
                Model = state.Model
                Kind = kind
                Data = data
            }

        let ended (events: ChatEvent list) =
            decoded state (events @ [ ChatEvent.PartEnded block.Part ])

        match block.Kind with
        | BlockKind.Skipped -> decoded state []
        | BlockKind.Text -> ended []
        | BlockKind.Thinking "" -> ended []
        | BlockKind.Thinking signature ->
            ended [ ChatEvent.PartState(block.Part, stamp Request.SignatureKind signature) ]
        | BlockKind.Redacted "" -> ended []
        | BlockKind.Redacted data -> ended [ ChatEvent.PartState(block.Part, stamp Request.RedactedKind data) ]
        | BlockKind.ToolUse(_, true) -> ended []
        // A call whose input did not stream (no arguments): its input as it started.
        | BlockKind.ToolUse(input, false) -> ended [ ChatEvent.Delta(block.Part, Json.serialize input) ]

let private messageDelta (state: State) (data: Json) : Decoded<State> =
    let stop =
        Json.tryField "delta" data
        |> Option.bind (fun delta ->
            text "stop_reason" delta
            |> Option.map (fun reason -> stopReason reason (text "stop_sequence" delta)))

    let state, usage = withUsage state data

    decoded
        { state with
            Stop = Option.orElse state.Stop stop
        }
        usage

let private messageStop (state: State) : Decoded<State> =
    match state.Stop with
    | Some stop -> decoded state [ ChatEvent.Finished stop ]
    | None -> fail state "the message stopped without a stop reason"

let private index (data: Json) : int option = count "index" data

/// One server-sent event: the new state and the canonical events it completes.
let decode (state: State, event: SseEvent) : Decoded<State> =
    match Json.parse event.Data with
    | Error e -> fail state $"an event is not JSON: {e}"
    | Ok data ->
        let kind = text "type" data |> Option.defaultValue event.Event

        match kind with
        | "message_start" -> messageStart state data
        | "content_block_start" ->
            match index data, Json.tryField "content_block" data with
            | Some i, Some(Json.Object _ as content) -> blockStart state i content
            | _ -> fail state "content_block_start without an index or a content block"
        | "content_block_delta" ->
            match index data, Json.tryField "delta" data with
            | Some i, Some(Json.Object _ as delta) -> blockDelta state i delta
            | _ -> fail state "content_block_delta without an index or a delta"
        | "content_block_stop" ->
            match index data with
            | Some i -> blockStop state i
            | None -> fail state "content_block_stop without an index"
        | "message_delta" -> messageDelta state data
        | "message_stop" -> messageStop state
        | "error" -> decoded state [ ChatEvent.Failed(Errors.ofEvent data) ]
        // `ping`, and events this version does not know.
        | _ -> decoded state []
