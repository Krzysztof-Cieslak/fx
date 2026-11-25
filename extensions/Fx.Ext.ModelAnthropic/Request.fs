/// Canonical requests as Messages API requests (`POST <endpoint>/messages`, streamed). Pure: the
/// tests check the JSON per option, and the conformance cassettes check it whole.
///
/// - The system prompt is text blocks; messages are content blocks: `text`, `image`, `document`
///   (PDF), `thinking` and `redacted_thinking` (rebuilt from their provider state, byte for byte),
///   `tool_use` (the arguments parsed back into the `input` object), `tool_result` (`is_error`).
/// - Cache breakpoints become `cache_control` on the block before them (see `marks`).
/// - Reasoning becomes `thinking` and `output_config.effort` (see `thinking`).
module Fx.Ext.ModelAnthropic.Request

open System
open System.Text
open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Sdk

/// The path of the Messages endpoint below the family's endpoint.
[<Literal>]
let MessagesPath = "/messages"

/// The provider-state kind of a thinking block's signature.
[<Literal>]
let SignatureKind = "signature"

/// The provider-state kind of a redacted thinking block's data.
[<Literal>]
let RedactedKind = "redacted"

let private utf8 = UTF8Encoding(false)

/// A tool definition, system block or content block as JSON members, and whether `cache_control` may
/// go on it (not on thinking blocks).
type private Block =
    {
        Members: (string * Json) list
        Cacheable: bool
    }

    override this.ToString() =
        $"block ({this.Members.Length} members)"

/// Where a block is in prompt order: the tool definitions, the system prompt, or a message.
[<RequireQualifiedAccess>]
type private Section =
    | Tools
    | System
    | Message of index: int

    override this.ToString() =
        match this with
        | Tools -> "tools"
        | System -> "system"
        | Message index -> $"message {index}"

/// One item in prompt order: a block, or a breakpoint after the blocks before it.
[<RequireQualifiedAccess>]
type private Item =
    | Block of Block
    | Breakpoint of CacheTtl

    override this.ToString() =
        match this with
        | Block block -> string block
        | Breakpoint ttl -> $"breakpoint ({ttl})"

let private str (text: string) = Json.String text

let private block (members: (string * Json) list) : Block = { Members = members; Cacheable = true }

/// Each item's values, in order; the first error stops it.
let private collect (f: 'T -> Result<'U list, ChatError>) (items: 'T list) : Result<'U list, ChatError> =
    let values = ResizeArray<'U>()
    let mutable error = None

    for item in items do
        if error.IsNone then
            match f item with
            | Ok more -> values.AddRange more
            | Error e -> error <- Some e

    match error with
    | Some e -> Error e
    | None -> Ok(List.ofSeq values)

let private textBlock (text: string) : Block =
    block [ "type", str "text"; "text", str text ]

let private source (media: MediaPart) : Result<Json, ChatError> =
    match media.Source with
    | MediaSource.Base64 data ->
        Ok(Json.Object [ "type", str "base64"; "media_type", str media.MediaType; "data", str data ])
    | MediaSource.Url url -> Ok(Json.Object [ "type", str "url"; "url", str url ])
    | MediaSource.Blob digest ->
        Error(Unavailable $"{digest} is in the blob store: resolve media before the request leaves")

let private media (part: MediaPart) : Result<Block, ChatError> =
    match part.Kind with
    | MediaKind.Image -> source part |> Result.map (fun s -> block [ "type", str "image"; "source", s ])
    | MediaKind.Pdf ->
        source part
        |> Result.map (fun s ->
            block
                [
                    "type", str "document"
                    "source", s
                    match part.Name with
                    | Some name -> "title", str name
                    | None -> ()
                ])
    | kind -> Error(Unavailable $"{Provider.Family} takes no {kind} input")

/// A reasoning part as the block that produced it: `thinking` with its signature, or
/// `redacted_thinking` with its data, both exactly as they arrived. Reasoning without state of this
/// family cannot be sent back as thinking and is left out.
let private thinkingBlock (part: ReasoningPart) : Block option =
    match part.State with
    | Some state when state.Family = Provider.Family && state.Kind = SignatureKind ->
        Some
            {
                Members =
                    [
                        "type", str "thinking"
                        "thinking", str part.Text
                        "signature", str state.Data
                    ]
                Cacheable = false
            }
    | Some state when state.Family = Provider.Family && state.Kind = RedactedKind ->
        Some
            {
                Members = [ "type", str "redacted_thinking"; "data", str state.Data ]
                Cacheable = false
            }
    | _ -> None

/// A tool call's input: its arguments parsed back into an object (the API wants an object; invalid
/// or cut-off arguments become `{}`).
let toolInput (arguments: string) : Json =
    match Json.parse arguments with
    | Ok(Json.Object _ as input) -> input
    | _ -> Json.Object []

let private toolResult (result: ToolResult) : Result<Block, ChatError> =
    result.Content
    |> collect (fun content ->
        match content with
        | ToolContent.Text "" -> Ok []
        | ToolContent.Text text -> Ok [ Json.Object (textBlock text).Members ]
        | ToolContent.Media m -> media m |> Result.map (fun b -> [ Json.Object b.Members ]))
    |> Result.map (fun content ->
        block
            [
                "type", str "tool_result"
                "tool_use_id", str result.CallId
                if not content.IsEmpty then
                    "content", Json.Array content
                if result.IsError then
                    "is_error", Json.Bool true
            ])

let private item (part: Part) : Result<Item list, ChatError> =
    match part with
    | Part.Text t when t.Text = "" -> Ok []
    | Part.Text t -> Ok [ Item.Block(textBlock t.Text) ]
    | Part.Media m -> media m |> Result.map (fun b -> [ Item.Block b ])
    | Part.Reasoning r -> Ok(thinkingBlock r |> Option.map Item.Block |> Option.toList)
    | Part.ToolCall c ->
        Ok
            [
                Item.Block(
                    block
                        [
                            "type", str "tool_use"
                            "id", str c.Id
                            "name", str c.Name
                            "input", toolInput c.Arguments
                        ]
                )
            ]
    | Part.ToolResult r -> toolResult r |> Result.map (fun b -> [ Item.Block b ])
    // Another provider's refusal: what the assistant said.
    | Part.Refusal "" -> Ok []
    | Part.Refusal text -> Ok [ Item.Block(textBlock text) ]
    | Part.CacheBreakpoint ttl -> Ok [ Item.Breakpoint ttl ]

let private systemItem (part: Part) : Result<Item list, ChatError> =
    match part with
    | Part.Text _
    | Part.CacheBreakpoint _ -> item part
    | other -> Error(Unavailable $"{Provider.Family} takes only text in the system prompt, not {other}")

let private tool (spec: ToolSpec) : Block =
    block
        [
            "name", str spec.Name
            if spec.Description <> "" then
                "description", str spec.Description
            "input_schema", spec.Parameters
            if spec.Strict then
                "strict", Json.Bool true
        ]

/// Where the cache breakpoints go (spec §5: stable receipts become provider breakpoints), as the
/// index of the block that gets `cache_control` and its time-to-live. The policy:
///
/// 1. A breakpoint marks the last block before it that can carry `cache_control` (in prompt order:
///    tools, system, messages; thinking blocks and empty text cannot). With none before it, it is
///    dropped. `CacheHints.Tools` is a breakpoint after the tool definitions.
/// 2. Breakpoints on one block are one (with the longer time-to-live).
/// 3. At most `maxBreakpoints` (4 on the Claude API): the earliest beyond it are dropped, as
///    `Fit.request` does for models in the catalog.
/// 4. A longer time-to-live may not follow a shorter one (the API refuses it): after the first
///    5-minute breakpoint, later 1-hour ones become 5-minute ones.
let private marks (maxBreakpoints: int) (items: Item list) : Map<int, CacheTtl> =
    let cacheable = ResizeArray<bool>()
    let found = ResizeArray<int * CacheTtl>()

    for item in items do
        match item with
        | Item.Block b -> cacheable.Add b.Cacheable
        | Item.Breakpoint ttl ->
            match Seq.tryFindIndexBack id cacheable with
            | Some index -> found.Add(index, ttl)
            | None -> ()

    let merged =
        found
        |> List.ofSeq
        |> List.groupBy fst
        |> List.map (fun (index, ttls) ->
            index,
            (if ttls |> List.exists (fun (_, t) -> t = CacheTtl.Long) then
                 CacheTtl.Long
             else
                 CacheTtl.Short))
        |> List.sortBy fst

    let kept = merged |> List.skip (max 0 (merged.Length - max 0 maxBreakpoints))

    let mutable short = false

    kept
    |> List.map (fun (index, ttl) ->
        let ttl = if short then CacheTtl.Short else ttl

        if ttl = CacheTtl.Short then
            short <- true

        index, ttl)
    |> Map.ofList

let private cacheControl (ttl: CacheTtl) : Json =
    match ttl with
    | CacheTtl.Short -> Json.Object [ "type", str "ephemeral" ]
    | CacheTtl.Long -> Json.Object [ "type", str "ephemeral"; "ttl", str "1h" ]

let private isToolResult (block: Json) =
    match Json.tryField "type" block with
    | Some(Json.String "tool_result") -> true
    | _ -> false

/// The effort's name in the API (`Minimal` is the lowest the API has: `low`).
let effortName (effort: Effort) : string =
    match effort with
    | Effort.Off
    | Effort.Minimal
    | Effort.Low -> "low"
    | Effort.Medium -> "medium"
    | Effort.High -> "high"
    | Effort.Max -> "max"

/// The thinking budget an effort stands for on a model that takes only budgets.
let budgetFor (effort: Effort) : int =
    match effort with
    | Effort.Off -> 0
    | Effort.Minimal -> 1024
    | Effort.Low -> 4096
    | Effort.Medium -> 8192
    | Effort.High -> 16384
    | Effort.Max -> 32768

/// The smallest thinking budget the API takes.
[<Literal>]
let MinBudget = 1024

/// The `thinking` object and the `output_config.effort` of a reasoning request:
///
/// - none: neither (the model's default);
/// - `Off`: `{type: "disabled"}`;
/// - a model whose catalog entry takes a budget (extended thinking only): `{type: "enabled",
///   budget_tokens}`, the budget asked for or the effort's (`budgetFor`), within the model's range
///   and below `max_tokens` (thinking is left off if that leaves less than the minimum);
/// - another model, with a budget (a model the catalog does not know): `enabled` as above;
/// - otherwise adaptive thinking, `{type: "adaptive"}`, and the effort.
///
/// `display: "summarized"` asks for visible thinking (models default to `omitted` from Claude 5).
let thinking
    (info: ModelInfo option)
    (maxTokens: int)
    (request: ReasoningRequest option)
    : Json option * string option =
    match request with
    | None -> None, None
    | Some r ->
        let display = if r.Visible then [ "display", str "summarized" ] else []

        let enabled (budget: int) =
            let budget = min budget (maxTokens - 1)

            if budget < MinBudget then
                None, None
            else
                Some(Json.Object([ "type", str "enabled"; "budget_tokens", Json.int budget ] @ display)), None

        let range =
            info
            |> Option.bind (fun i -> i.Caps.Reasoning)
            |> Option.bind (fun caps -> caps.Budget)

        match r.Effort, range, r.BudgetTokens with
        | Some Effort.Off, _, _ -> Some(Json.Object [ "type", str "disabled" ]), None
        | effort, Some range, budget ->
            let wanted =
                budget
                |> Option.orElse (effort |> Option.map budgetFor)
                |> Option.defaultValue (budgetFor Effort.Medium)

            enabled (TokenRange.clamp range wanted)
        | _, None, Some budget -> enabled budget
        | effort, None, None -> Some(Json.Object([ "type", str "adaptive" ] @ display)), effort |> Option.map effortName

/// The user id among the request's metadata (`user_id` or `userId`): the only metadata the API takes.
let private userId (metadata: (string * string) list) : string option =
    metadata
    |> List.tryFind (fun (key, _) -> key = "user_id" || key = "userId")
    |> Option.map snd

/// The JSON body of a request.
let body (settings: Provider.Settings) (info: ModelInfo option) (chat: ChatRequest) : Result<Json, ChatError> =
    let sections =
        [
            Section.Tools, Ok(chat.Tools |> List.map (tool >> Item.Block))
            Section.Tools, Ok(chat.Cache.Tools |> Option.map Item.Breakpoint |> Option.toList)
            Section.System, collect systemItem chat.System
            for index, message in List.indexed chat.Messages do
                Section.Message index, collect item message.Parts
        ]

    match
        sections
        |> List.tryPick (fun (_, items) ->
            match items with
            | Error e -> Some e
            | Ok _ -> None)
    with
    | Some error -> Error error
    | None ->
        let items =
            sections
            |> List.collect (fun (section, items) ->
                match items with
                | Ok items -> items |> List.map (fun item -> section, item)
                | Error _ -> [])

        let marks = marks settings.MaxCacheBreakpoints (items |> List.map snd)

        let blocks =
            items
            |> List.choose (fun (section, item) ->
                match item with
                | Item.Block b -> Some(section, b)
                | Item.Breakpoint _ -> None)
            |> List.mapi (fun index (section, b) ->
                let members =
                    match marks.TryFind index with
                    | Some ttl -> b.Members @ [ "cache_control", cacheControl ttl ]
                    | None -> b.Members

                section, Json.Object members)

        let inSection (section: Section) =
            blocks |> List.filter (fun (s, _) -> s = section) |> List.map snd

        let messages =
            chat.Messages
            |> List.indexed
            |> List.choose (fun (index, message) ->
                let content = inSection (Section.Message index)

                // Tool results come first in a user message (the API refuses them after text).
                let content =
                    if message.Role = Role.User then
                        List.filter isToolResult content @ List.filter (isToolResult >> not) content
                    else
                        content

                // A message with nothing to send (e.g. only reasoning that cannot travel) is left out.
                if content.IsEmpty then
                    None
                else
                    Some(Json.Object [ "role", str (string message.Role); "content", Json.Array content ]))

        let maxTokens =
            chat.MaxOutputTokens
            |> Option.orElse (info |> Option.map (fun i -> i.Limits.Output))
            |> Option.defaultValue settings.DefaultMaxTokens

        let thinking, effort = thinking info maxTokens chat.Reasoning
        let tools = inSection Section.Tools
        let system = inSection Section.System

        let disableParallel =
            match chat.ParallelToolCalls with
            | Some false -> [ "disable_parallel_tool_use", Json.Bool true ]
            | _ -> []

        let toolChoice =
            if tools.IsEmpty then
                None
            else
                match chat.ToolChoice with
                | ToolChoice.Auto when disableParallel.IsEmpty -> None
                | ToolChoice.Auto -> Some(Json.Object([ "type", str "auto" ] @ disableParallel))
                | ToolChoice.None -> Some(Json.Object [ "type", str "none" ])
                | ToolChoice.Required -> Some(Json.Object([ "type", str "any" ] @ disableParallel))
                | ToolChoice.Tool name -> Some(Json.Object([ "type", str "tool"; "name", str name ] @ disableParallel))

        let outputConfig =
            [
                match effort with
                | Some effort -> "effort", str effort
                | None -> ()

                match chat.Output with
                | OutputFormat.Json(_, schema, _) ->
                    "format", Json.Object [ "type", str "json_schema"; "schema", schema ]
                | OutputFormat.Text -> ()
            ]

        let sampling = chat.Sampling

        Ok(
            Json.Object
                [
                    "model", str chat.Model.Model
                    "max_tokens", Json.int maxTokens
                    if not system.IsEmpty then
                        "system", Json.Array system
                    "messages", Json.Array messages
                    if not tools.IsEmpty then
                        "tools", Json.Array tools
                    match toolChoice with
                    | Some choice -> "tool_choice", choice
                    | None -> ()
                    match thinking with
                    | Some thinking -> "thinking", thinking
                    | None -> ()
                    if not outputConfig.IsEmpty then
                        "output_config", Json.Object outputConfig
                    if not sampling.Stop.IsEmpty then
                        "stop_sequences", Json.Array(sampling.Stop |> List.map str)
                    // Only API versions before 2026-10-21 take these; seeds none.
                    if Provider.Settings.takesSampling settings then
                        match sampling.Temperature with
                        | Some t -> "temperature", Json.float t
                        | None -> ()

                        match sampling.TopP with
                        | Some p -> "top_p", Json.float p
                        | None -> ()

                        match sampling.TopK with
                        | Some k -> "top_k", Json.int k
                        | None -> ()
                    match userId chat.Metadata with
                    | Some user -> "metadata", Json.Object [ "user_id", str user ]
                    | None -> ()
                    "stream", Json.Bool true
                ]
        )

/// The HTTP request for a chat request: the body, the API version and beta headers (from the
/// provider's quirks). Credentials are not the adapter's: the connection adds them.
let http (ctx: WireCtx, chat: ChatRequest) : Result<HttpRequest, ChatError> =
    let settings = Provider.Settings.ofProvider ctx.Provider

    body settings ctx.Info chat
    |> Result.map (fun json ->
        let request =
            HttpRequest.post (WireCtx.url ctx MessagesPath) (utf8.GetBytes(Json.serialize json))
            |> HttpRequest.withHeader "content-type" "application/json"
            |> HttpRequest.withHeader "accept" "text/event-stream"
            |> HttpRequest.withHeader "anthropic-version" settings.Version

        match settings.Betas with
        | [] -> request
        | betas -> request |> HttpRequest.withHeader "anthropic-beta" (String.concat "," betas))
