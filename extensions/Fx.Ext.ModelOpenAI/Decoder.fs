/// The Responses API's event stream as canonical events: a pure fold over server-sent events
/// (`SseAdapter.Decode`), tested on the documented streams without a network.
///
/// | OpenAI | Canonical |
/// |---|---|
/// | `response.created` | `started {id, model}` |
/// | `response.output_item.added` `reasoning` / `function_call` | `partStarted` reasoning / toolCall |
/// | `response.content_part.added` `output_text` / `refusal` (or their first delta) | `partStarted` text / refusal |
/// | `response.output_text.delta`, `response.refusal.delta`, `response.function_call_arguments.delta`, `response.reasoning_summary_text.delta`, `response.reasoning_text.delta` | `delta` (summary parts joined by a blank line) |
/// | `response.output_item.done` | the item's text if none streamed; `partState` (a reasoning item with its `encrypted_content`: `reasoning`; an assistant message's `phase`: `phase`); `partEnded` |
/// | `response.completed` | open parts ended, `usage`, `finished` (`toolUse` with a function call, `refusal` with a refusal, else `endTurn`) |
/// | `response.incomplete` | open parts ended, `usage`, `finished` (`max_output_tokens` → `maxTokens`, `content_filter` → `contentFilter`, else `other`) |
/// | `response.failed`, `error` | `usage` (if any), `failed` (`Errors.ofFailed`, `Errors.ofEvent`) |
/// | `response.in_progress`, `response.queued`, `*.done` of texts, annotations, hosted tools' items and events this version does not know | nothing |
///
/// OpenAI numbers output items (and the content of messages) itself; parts are numbered in the order
/// they start, so items without a canonical part (hosted tools) leave no gap.
module Fx.Ext.ModelOpenAI.Decoder

open Fx.Contracts
open Fx.Models
open Fx.Sdk

/// What a content part of a message becomes.
[<RequireQualifiedAccess>]
type ContentKind =
    | Text
    | Refusal

    override this.ToString() =
        match this with
        | Text -> "text"
        | Refusal -> "refusal"

/// A content part of a message: its canonical part, and whether its text arrived.
type Content =
    {
        Part: int
        Kind: ContentKind
        Streamed: bool
    }

    override this.ToString() =
        $"content (part {this.Part}, {this.Kind}, streamed {this.Streamed})"

/// What an output item becomes.
[<RequireQualifiedAccess>]
type ItemKind =
    /// Reasoning: its part, the summary part (or reasoning content) its text last came from, and
    /// whether any text arrived.
    | Reasoning of part: int * section: string * written: bool
    /// An assistant message: its phase, and its content parts by content index.
    | Message of phase: string option * contents: Map<int, Content>
    /// A function call: its part, and whether its arguments streamed.
    | FunctionCall of part: int * streamed: bool
    /// An item with no canonical part (hosted tools): its events are ignored.
    | Skipped

    override this.ToString() =
        match this with
        | Reasoning(part, _, _) -> $"reasoning (part {part})"
        | Message(phase, contents) ->
            let phase = phase |> Option.defaultValue "-"
            $"message (phase {phase}, {contents.Count} contents)"
        | FunctionCall(part, streamed) -> $"function call (part {part}, streamed {streamed})"
        | Skipped -> "skipped"

/// An output item: what it becomes, and whether it is done.
type Item =
    {
        Kind: ItemKind
        Done: bool
    }

    override this.ToString() = $"item ({this.Kind}, done {this.Done})"

/// The decoder's state between events.
type State =
    {
        /// Stamps provider state: the call's family and model.
        Family: string
        Model: ModelRef
        /// The output items so far, by OpenAI's output index.
        Items: Map<int, Item>
        /// Parts started so far (the next part's index).
        Parts: int
        /// Parts started and not ended.
        Open: Set<int>
        /// Whether a function call started (the stop is `toolUse`).
        Calls: bool
        /// Whether a refusal started (the stop is `refusal`).
        Refusals: bool
        /// Whether `started` was emitted.
        Started: bool
    }

    override this.ToString() =
        $"openai decoder ({this.Parts} parts, {this.Items.Count} items)"

/// The state before the first event of a call.
let initial (ctx: WireCtx) : State =
    {
        Family = ctx.Family
        Model = ctx.Model
        Items = Map.empty
        Parts = 0
        Open = Set.empty
        Calls = false
        Refusals = false
        Started = false
    }

let private text (name: string) (json: Json) : string option =
    match Json.tryField name json with
    | Some(Json.String s) -> Some s
    | _ -> None

let private count (name: string) (json: Json) : int option =
    match Json.tryField name json with
    | Some(Json.Number n) ->
        match JsonNumber.tryToDecimal n with
        | Some d when d >= 0m && d <= 2_000_000_000m && d = System.Math.Floor d -> Some(int d)
        | _ -> None
    | _ -> None

/// The canonical usage of a response's `usage`: OpenAI's `input_tokens` includes cached tokens and
/// cache writes, as the canonical input does; `reasoning_tokens` are part of `output_tokens`.
let usage (json: Json) : Usage option =
    match json with
    | Json.Object _ ->
        let input = count "input_tokens" json |> Option.defaultValue 0
        let output = count "output_tokens" json |> Option.defaultValue 0

        let detail (field: string) (name: string) =
            Json.tryField field json |> Option.bind (count name) |> Option.defaultValue 0

        Some
            {
                Input = input
                Output = output
                CacheRead = detail "input_tokens_details" "cached_tokens"
                CacheWrite = detail "input_tokens_details" "cache_write_tokens"
                Reasoning = min output (detail "output_tokens_details" "reasoning_tokens")
            }
    | _ -> None

/// The stop of an incomplete response, by `incomplete_details.reason`.
let incompleteReason (reason: string) : StopReason =
    match reason with
    | "max_output_tokens" -> StopReason.MaxTokens
    | "content_filter" -> StopReason.ContentFilter
    | other -> StopReason.Other other

let private decoded (state: State) (events: ChatEvent list) : Decoded<State> = { State = state; Events = events }

let private fail (state: State) (message: string) : Decoded<State> =
    decoded state [ ChatEvent.Failed(InvalidResponse message) ]

/// Starts a part: its index, and the new state.
let private start (state: State) (kind: PartStart) : State * int * ChatEvent =
    let part = state.Parts

    { state with
        Parts = part + 1
        Open = state.Open.Add part
        Calls =
            state.Calls
            || (match kind with
                | PartStart.ToolCall _ -> true
                | _ -> false)
        Refusals = state.Refusals || kind = PartStart.Refusal
    },
    part,
    ChatEvent.PartStarted(part, kind)

let private ended (state: State) (part: int) : State * ChatEvent =
    { state with
        Open = state.Open.Remove part
    },
    ChatEvent.PartEnded part

let private setItem (index: int) (item: Item) (state: State) : State =
    { state with
        Items = state.Items.Add(index, item)
    }

let private stamp (state: State) (kind: string) (data: string) : ProviderState =
    {
        Family = state.Family
        Model = state.Model
        Kind = kind
        Data = data
    }

let private created (state: State) (data: Json) : Decoded<State> =
    match state.Started, Json.tryField "response" data with
    | false, Some(Json.Object _ as response) ->
        decoded
            { state with Started = true }
            [
                ChatEvent.Started
                    {
                        Id = text "id" response
                        Model = text "model" response
                    }
            ]
    | _ -> decoded state []

/// A function call's start: its part, and any arguments that came with it.
let private callStart (state: State) (index: int) (item: Json) : Decoded<State> =
    match text "call_id" item, text "name" item with
    | Some id, Some name ->
        let state, part, started = start state (PartStart.ToolCall(id, name))

        let arguments = text "arguments" item |> Option.defaultValue ""

        decoded
            (setItem
                index
                {
                    Kind = ItemKind.FunctionCall(part, arguments <> "")
                    Done = false
                }
                state)
            [
                started
                if arguments <> "" then
                    ChatEvent.Delta(part, arguments)
            ]
    | _ -> fail state $"function call {index} without a call_id or a name"

let private itemAdded (state: State) (index: int) (item: Json) : Decoded<State> =
    if state.Items.ContainsKey index then
        fail state $"output item {index} was added twice"
    else
        match text "type" item with
        | Some "reasoning" ->
            let state, part, started = start state PartStart.Reasoning

            decoded
                (setItem
                    index
                    {
                        Kind = ItemKind.Reasoning(part, "", false)
                        Done = false
                    }
                    state)
                [ started ]
        | Some "message" ->
            decoded
                (setItem
                    index
                    {
                        Kind = ItemKind.Message(text "phase" item, Map.empty)
                        Done = false
                    }
                    state)
                []
        | Some "function_call" -> callStart state index item
        | _ ->
            decoded
                (setItem
                    index
                    {
                        Kind = ItemKind.Skipped
                        Done = false
                    }
                    state)
                []

/// A content part of a message, started if it was not: the state, the content, and the events.
let private content
    (state: State)
    (index: int)
    (contentIndex: int)
    (kind: ContentKind)
    : Result<State * Content * ChatEvent list, string> =
    let item =
        state.Items.TryFind index
        |> Option.defaultValue
            {
                Kind = ItemKind.Message(None, Map.empty)
                Done = false
            }

    match item with
    | { Done = true } -> Error $"output item {index} is already done"
    | {
          Kind = ItemKind.Message(phase, contents)
      } ->
        match contents.TryFind contentIndex with
        | Some content when content.Kind = kind -> Ok(state, content, [])
        | Some _ -> Error $"content {contentIndex} of output item {index} changed its kind"
        | None ->
            let partKind =
                match kind with
                | ContentKind.Text -> PartStart.Text
                | ContentKind.Refusal -> PartStart.Refusal

            let state, part, started = start state partKind

            let content =
                {
                    Part = part
                    Kind = kind
                    Streamed = false
                }

            let state =
                setItem
                    index
                    {
                        Kind = ItemKind.Message(phase, contents.Add(contentIndex, content))
                        Done = false
                    }
                    state

            Ok(state, content, [ started ])
    | { Kind = ItemKind.Skipped } -> Error $"output item {index} has no message content"
    | item -> Error $"output item {index} is a {item.Kind}, not a message"

let private withContent (state: State) (index: int) (contentIndex: int) (content: Content) : State =
    match state.Items.TryFind index with
    | Some({
               Kind = ItemKind.Message(phase, contents)
           } as item) ->
        setItem
            index
            { item with
                Kind = ItemKind.Message(phase, contents.Add(contentIndex, content))
            }
            state
    | _ -> state

/// A piece of a message's text or refusal; `final` is the whole text (`*.done`), sent only if none
/// streamed.
let private contentText
    (state: State)
    (index: int)
    (contentIndex: int)
    (kind: ContentKind)
    (piece: string)
    (final: bool)
    : Decoded<State> =
    match state.Items.TryFind index with
    | Some { Kind = ItemKind.Skipped } -> decoded state []
    | _ ->
        match content state index contentIndex kind with
        | Error e -> fail state e
        | Ok(state, content, events) ->
            if piece = "" || (final && content.Streamed) then
                decoded state events
            else
                decoded
                    (withContent state index contentIndex { content with Streamed = true })
                    (events @ [ ChatEvent.Delta(content.Part, piece) ])

let private contentKind (part: Json) : (ContentKind * string) option =
    match text "type" part with
    | Some "output_text" -> Some(ContentKind.Text, text "text" part |> Option.defaultValue "")
    | Some "refusal" -> Some(ContentKind.Refusal, text "refusal" part |> Option.defaultValue "")
    | _ -> None

let private contentAdded (state: State) (index: int) (contentIndex: int) (part: Json) : Decoded<State> =
    match state.Items.TryFind index, contentKind part with
    | Some { Kind = ItemKind.Skipped }, _
    | _, None -> decoded state []
    | _, Some(kind, initial) -> contentText state index contentIndex kind initial false

/// A piece of reasoning text, from summary part or reasoning content `section`: a blank line between
/// sections. Reasoning that was never added starts here.
let private reasoningText (state: State) (index: int) (section: string) (piece: string) : Decoded<State> =
    let found =
        match state.Items.TryFind index with
        | Some {
                   Kind = ItemKind.Reasoning(part, current, written)
               } -> Some(state, part, current, written, [])
        | Some _ -> None
        | None ->
            let state, part, started = start state PartStart.Reasoning
            Some(state, part, "", false, [ started ])

    match found with
    | None -> decoded state []
    | Some(state, part, current, written, events) ->
        let separator =
            if piece <> "" && written && current <> section then
                [ ChatEvent.Delta(part, "\n\n") ]
            else
                []

        let kind =
            if piece = "" then
                ItemKind.Reasoning(part, current, written)
            else
                ItemKind.Reasoning(part, section, true)

        decoded
            (setItem index { Kind = kind; Done = false } state)
            (events
             @ separator
             @ (if piece = "" then [] else [ ChatEvent.Delta(part, piece) ]))

let private arguments (state: State) (index: int) (piece: string) (final: bool) : Decoded<State> =
    match state.Items.TryFind index with
    | Some {
               Kind = ItemKind.FunctionCall(part, streamed)
               Done = false
           } ->
        if piece = "" || (final && streamed) then
            decoded state []
        else
            decoded
                (setItem
                    index
                    {
                        Kind = ItemKind.FunctionCall(part, true)
                        Done = false
                    }
                    state)
                [ ChatEvent.Delta(part, piece) ]
    | Some { Kind = ItemKind.Skipped } -> decoded state []
    | _ -> fail state $"arguments for output item {index}, which is not an open function call"

let private reasoningDone (state: State) (index: int) (item: Json) : Decoded<State> =
    let started = reasoningText state index "" ""

    match started.State.Items.TryFind index with
    | Some {
               Kind = ItemKind.Reasoning(part, _, written)
           } ->
        let state = started.State

        let summary =
            if written then
                ""
            else
                match Json.tryField "summary" item with
                | Some(Json.Array parts) ->
                    parts
                    |> List.choose (text "text")
                    |> List.filter (fun t -> t <> "")
                    |> String.concat "\n\n"
                | _ -> ""

        let encrypted =
            match Json.tryField "encrypted_content" item with
            | Some(Json.String s) when s <> "" -> true
            | _ -> false

        let state, end' = ended state part

        decoded
            (setItem
                index
                {
                    Kind = ItemKind.Reasoning(part, "", true)
                    Done = true
                }
                state)
            [
                yield! started.Events
                if summary <> "" then
                    ChatEvent.Delta(part, summary)
                // The item exactly as it came: replayed as it is in the next request.
                if encrypted then
                    ChatEvent.PartState(part, stamp state Request.ReasoningKind (Json.serialize item))
                end'
            ]
    | _ -> fail state $"output item {index} is not reasoning"

let private messageDone (state: State) (index: int) (item: Json) : Decoded<State> =
    let parts =
        match Json.tryField "content" item with
        | Some(Json.Array parts) -> parts |> List.indexed
        | _ -> []

    // Content that never streamed comes whole with the item.
    let filled =
        parts
        |> List.fold
            (fun (acc: Decoded<State>) (contentIndex, part) ->
                match acc.Events |> List.tryLast, contentKind part with
                | Some(ChatEvent.Failed _), _ -> acc
                | _, None -> acc
                | _, Some(kind, whole) ->
                    let next = contentText acc.State index contentIndex kind whole true
                    decoded next.State (acc.Events @ next.Events))
            (decoded state [])

    match filled.Events |> List.tryLast with
    | Some(ChatEvent.Failed _) -> filled
    | _ ->
        match filled.State.Items.TryFind index with
        | None ->
            decoded
                (setItem
                    index
                    {
                        Kind = ItemKind.Message(text "phase" item, Map.empty)
                        Done = true
                    }
                    filled.State)
                filled.Events
        | Some {
                   Kind = ItemKind.Message(added, contents)
               } ->
            let phase = text "phase" item |> Option.orElse added

            let state, endings =
                contents
                |> Map.toList
                |> List.fold
                    (fun (state, events) (_, content) ->
                        let state, end' = ended state content.Part

                        let phase =
                            match content.Kind, phase with
                            | ContentKind.Text, Some p when p <> "" ->
                                [ ChatEvent.PartState(content.Part, stamp state Request.PhaseKind p) ]
                            | _ -> []

                        state, events @ phase @ [ end' ])
                    (filled.State, [])

            decoded
                (setItem
                    index
                    {
                        Kind = ItemKind.Message(phase, contents)
                        Done = true
                    }
                    state)
                (filled.Events @ endings)
        | Some _ -> fail state $"output item {index} is not a message"

let private callDone (state: State) (index: int) (item: Json) : Decoded<State> =
    let started =
        if state.Items.ContainsKey index then
            decoded state []
        else
            callStart state index item

    match started.Events |> List.tryLast with
    | Some(ChatEvent.Failed _) -> started
    | _ ->
        let filled =
            arguments started.State index (text "arguments" item |> Option.defaultValue "") true

        match filled.Events |> List.tryLast, filled.State.Items.TryFind index with
        | Some(ChatEvent.Failed _), _ -> filled
        | _,
          Some {
                   Kind = ItemKind.FunctionCall(part, streamed)
               } ->
            let state, end' = ended filled.State part

            decoded
                (setItem
                    index
                    {
                        Kind = ItemKind.FunctionCall(part, streamed)
                        Done = true
                    }
                    state)
                (started.Events @ filled.Events @ [ end' ])
        | _ -> fail state $"output item {index} is not a function call"

let private itemDone (state: State) (index: int) (item: Json) : Decoded<State> =
    match state.Items.TryFind index with
    | Some { Done = true } -> fail state $"output item {index} was done twice"
    | Some { Kind = ItemKind.Skipped } -> decoded (setItem index { Kind = ItemKind.Skipped; Done = true } state) []
    | _ ->
        match text "type" item with
        | Some "reasoning" -> reasoningDone state index item
        | Some "message" -> messageDone state index item
        | Some "function_call" -> callDone state index item
        | _ -> decoded (setItem index { Kind = ItemKind.Skipped; Done = true } state) []

/// Ends the parts still open, in order (a response that stopped cut its items off).
let private closeAll (state: State) : State * ChatEvent list =
    state.Open
    |> Set.toList
    |> List.fold
        (fun (state, events) part ->
            let state, end' = ended state part
            state, events @ [ end' ])
        (state, [])

let private usageOf (response: Json) : ChatEvent list =
    match Json.tryField "usage" response |> Option.bind usage with
    | Some u -> [ ChatEvent.UsageUpdated u ]
    | None -> []

let private finished (state: State) (response: Json) (stop: StopReason) : Decoded<State> =
    let state, endings = closeAll state
    decoded state (endings @ usageOf response @ [ ChatEvent.Finished stop ])

let private response (data: Json) : Json =
    Json.tryField "response" data |> Option.defaultValue (Json.Object [])

/// One server-sent event: the new state and the canonical events it completes.
let decode (state: State, event: SseEvent) : Decoded<State> =
    match Json.parse event.Data with
    | Error e -> fail state $"an event is not JSON: {e}"
    | Ok data ->
        let kind = text "type" data |> Option.defaultValue event.Event
        let index = count "output_index" data

        let withIndex (f: int -> Decoded<State>) =
            match index with
            | Some i -> f i
            | None -> fail state $"{kind} without an output_index"

        let contentIndex = count "content_index" data |> Option.defaultValue 0

        let piece (name: string) =
            text name data |> Option.defaultValue ""

        match kind with
        | "response.created" -> created state data
        | "response.output_item.added" ->
            match index, Json.tryField "item" data with
            | Some i, Some(Json.Object _ as item) -> itemAdded state i item
            | _ -> fail state "response.output_item.added without an output_index or an item"
        | "response.output_item.done" ->
            match index, Json.tryField "item" data with
            | Some i, Some(Json.Object _ as item) -> itemDone state i item
            | _ -> fail state "response.output_item.done without an output_index or an item"
        | "response.content_part.added" ->
            match index, Json.tryField "part" data with
            | Some i, Some(Json.Object _ as part) -> contentAdded state i contentIndex part
            | _ -> fail state "response.content_part.added without an output_index or a part"
        | "response.output_text.delta" ->
            withIndex (fun i -> contentText state i contentIndex ContentKind.Text (piece "delta") false)
        | "response.output_text.done" ->
            withIndex (fun i -> contentText state i contentIndex ContentKind.Text (piece "text") true)
        | "response.refusal.delta" ->
            withIndex (fun i -> contentText state i contentIndex ContentKind.Refusal (piece "delta") false)
        | "response.refusal.done" ->
            withIndex (fun i -> contentText state i contentIndex ContentKind.Refusal (piece "refusal") true)
        | "response.reasoning_summary_text.delta" ->
            let section =
                "summary " + string (count "summary_index" data |> Option.defaultValue 0)

            withIndex (fun i -> reasoningText state i section (piece "delta"))
        | "response.reasoning_text.delta" ->
            withIndex (fun i -> reasoningText state i ("content " + string contentIndex) (piece "delta"))
        | "response.function_call_arguments.delta" -> withIndex (fun i -> arguments state i (piece "delta") false)
        | "response.function_call_arguments.done" -> withIndex (fun i -> arguments state i (piece "arguments") true)
        | "response.completed" ->
            let stop =
                if state.Calls then StopReason.ToolUse
                elif state.Refusals then StopReason.Refusal
                else StopReason.EndTurn

            finished state (response data) stop
        | "response.incomplete" ->
            let reason =
                Json.tryField "incomplete_details" (response data)
                |> Option.bind (text "reason")
                |> Option.defaultValue "unknown"

            finished state (response data) (incompleteReason reason)
        | "response.failed" ->
            decoded state (usageOf (response data) @ [ ChatEvent.Failed(Errors.ofFailed (response data)) ])
        | "error" -> decoded state [ ChatEvent.Failed(Errors.ofEvent data) ]
        // `response.in_progress`, `response.queued`, annotations, hosted tools, and events this
        // version does not know.
        | _ -> decoded state []
