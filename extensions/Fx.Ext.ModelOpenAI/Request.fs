/// Canonical requests as Responses API requests (`POST <endpoint>/responses`, streamed, stateless).
/// Pure: the tests check the JSON per option, and the conformance cassettes check it whole.
///
/// - The system prompt is a developer message at the start of `input` (or `instructions`, a quirk);
///   user messages are `input_text`, `input_image` and `input_file` blocks; tool results are
///   `function_call_output` items, before the user's own content; assistant messages are their items
///   in order: reasoning items rebuilt from their provider state (byte for byte), text as assistant
///   messages with their `phase`, tool calls as `function_call` items.
/// - Cache breakpoints become `prompt_cache_breakpoint`s on the block before them (see `Input`).
/// - `store: false`: OpenAI keeps no conversation state; every request carries the whole history,
///   and reasoning travels as encrypted items (`include: ["reasoning.encrypted_content"]`).
module Fx.Ext.ModelOpenAI.Request

open System
open System.Text
open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Sdk

/// The path of the Responses endpoint below the family's endpoint.
[<Literal>]
let ResponsesPath = "/responses"

/// The provider-state kind of a reasoning item: its data is the item's JSON as the API sent it
/// (with its id, summary and `encrypted_content`).
[<Literal>]
let ReasoningKind = "reasoning"

/// The provider-state kind of an assistant message's `phase` (`commentary`, `final_answer`): its
/// data is the phase.
[<Literal>]
let PhaseKind = "phase"

/// What `include` asks for: reasoning items with their encrypted content, so they can be replayed
/// without server-side state.
[<Literal>]
let EncryptedReasoning = "reasoning.encrypted_content"

let private utf8 = UTF8Encoding(false)

let private str (text: string) = Json.String text

/// A content block (`input_text`, `input_image`, `input_file`): where a cache breakpoint can go.
[<NoEquality; NoComparison>]
type private Block =
    {
        Members: (string * Json) list
        mutable Mark: CacheTtl option
    }

    override this.ToString() =
        $"block ({this.Members.Length} members)"

/// One item of `input` before it is rendered.
[<RequireQualifiedAccess; NoEquality; NoComparison>]
type private Item =
    /// A user or developer message: its role and blocks.
    | Message of role: string * blocks: Block list
    /// An assistant's text, and its phase.
    | Assistant of text: string * phase: string option
    /// A tool's output: the call it answers and its blocks.
    | Output of callId: string * blocks: Block list
    /// An item as it is (reasoning, function calls).
    | Raw of Json

    override this.ToString() =
        match this with
        | Message(role, blocks) -> $"{role} message ({blocks.Length} blocks)"
        | Assistant(text, _) -> $"assistant text ({text.Length} chars)"
        | Output(callId, _) -> "output of " + callId
        | Raw _ -> "item"

/// Builds `input` in prompt order. A cache breakpoint marks the last block made before it (in the
/// canonical order of parts: tool results of a user message move before its content, their marks
/// with them); with none before it, or when breakpoints are off, it is dropped. Two on one block are
/// one, with the longer lifetime.
type private Input(breakpoints: bool) =
    let items = ResizeArray<Item>()
    let mutable last: Block option = None

    member _.Block(members: (string * Json) list) : Block =
        let block = { Members = members; Mark = None }
        last <- Some block
        block

    member _.Add(item: Item) = items.Add item

    member _.Break(ttl: CacheTtl) =
        match last with
        | Some block when breakpoints ->
            block.Mark <-
                match block.Mark with
                | Some CacheTtl.Long -> Some CacheTtl.Long
                | _ -> Some ttl
        | _ -> ()

    member _.Items: Item list = List.ofSeq items

    override _.ToString() = $"input ({items.Count} items)"

let private textBlock (text: string) =
    [ "type", str "input_text"; "text", str text ]

/// A media part as a block: images as `input_image` (data URLs for base64), PDFs as `input_file`.
let private media (part: MediaPart) : Result<(string * Json) list, ChatError> =
    match part.Kind, part.Source with
    | _, MediaSource.Blob digest ->
        Error(Unavailable $"{digest} is in the blob store: resolve media before the request leaves")
    | MediaKind.Image, MediaSource.Base64 data ->
        Ok
            [
                "type", str "input_image"
                "image_url", str $"data:{part.MediaType};base64,{data}"
                "detail", str "auto"
            ]
    | MediaKind.Image, MediaSource.Url url ->
        Ok [ "type", str "input_image"; "image_url", str url; "detail", str "auto" ]
    | MediaKind.Pdf, MediaSource.Base64 data ->
        Ok
            [
                "type", str "input_file"
                "filename", str (part.Name |> Option.defaultValue "document.pdf")
                "file_data", str $"data:{part.MediaType};base64,{data}"
            ]
    | MediaKind.Pdf, MediaSource.Url url ->
        Ok
            [
                "type", str "input_file"
                match part.Name with
                | Some name -> "filename", str name
                | None -> ()
                "file_url", str url
            ]
    | kind, _ -> Error(Unavailable $"{Provider.Family} takes no {kind} input")

/// The reasoning item a state replays: the item exactly as the API sent it. State of another family
/// or kind, or that is not a reasoning item, replays nothing.
let private reasoningItem (state: ProviderState option) : Json option =
    match state with
    | Some s when s.Family = Provider.Family && s.Kind = ReasoningKind ->
        match Json.parse s.Data with
        | Ok(Json.Object members as item) when members |> List.contains ("type", str "reasoning") -> Some item
        | _ -> None
    | _ -> None

/// The phase of an assistant's text.
let private phase (state: ProviderState option) : string option =
    match state with
    | Some s when s.Family = Provider.Family && s.Kind = PhaseKind && s.Data <> "" -> Some s.Data
    | _ -> None

let private functionCall (call: ToolCall) : Json =
    Json.Object
        [
            "type", str "function_call"
            "call_id", str call.Id
            "name", str call.Name
            "arguments", str call.Arguments
        ]

/// A tool result as a `function_call_output` item.
let private output (input: Input) (result: ToolResult) : Result<Item, ChatError> =
    let blocks = ResizeArray<Block>()
    let mutable error = None

    for content in result.Content do
        if error.IsNone then
            match content with
            | ToolContent.Text "" -> ()
            | ToolContent.Text text -> blocks.Add(input.Block(textBlock text))
            | ToolContent.Media m ->
                match media m with
                | Ok members -> blocks.Add(input.Block members)
                | Error e -> error <- Some e

    match error with
    | Some e -> Error e
    | None -> Ok(Item.Output(result.CallId, List.ofSeq blocks))

/// A user message: its tool results first (as outputs), then its content as one message.
let private user (input: Input) (parts: Part list) : Result<unit, ChatError> =
    let outputs = ResizeArray<Item>()
    let content = ResizeArray<Block>()
    let mutable error = None

    for part in parts do
        if error.IsNone then
            match part with
            | Part.Text t when t.Text <> "" -> content.Add(input.Block(textBlock t.Text))
            | Part.Refusal text when text <> "" -> content.Add(input.Block(textBlock text))
            | Part.Media m ->
                match media m with
                | Ok members -> content.Add(input.Block members)
                | Error e -> error <- Some e
            | Part.ToolResult r ->
                match output input r with
                | Ok item -> outputs.Add item
                | Error e -> error <- Some e
            | Part.ToolCall c -> outputs.Add(Item.Raw(functionCall c))
            | Part.CacheBreakpoint ttl -> input.Break ttl
            // Empty text, and reasoning (only assistants reason).
            | _ -> ()

    match error with
    | Some e -> Error e
    | None ->
        for item in outputs do
            input.Add item

        if content.Count > 0 then
            input.Add(Item.Message("user", List.ofSeq content))

        Ok()

/// An assistant message: its items in order.
let private assistant (input: Input) (parts: Part list) : Result<unit, ChatError> =
    let mutable error = None

    for part in parts do
        if error.IsNone then
            match part with
            | Part.Reasoning r ->
                // Reasoning without this family's state cannot be sent back (stateless requests
                // replay only encrypted items): it is left out.
                match reasoningItem r.State with
                | Some item -> input.Add(Item.Raw item)
                | None -> ()
            | Part.Text t when t.Text <> "" -> input.Add(Item.Assistant(t.Text, phase t.State))
            // Another provider's refusal: what the assistant said.
            | Part.Refusal text when text <> "" -> input.Add(Item.Assistant(text, None))
            | Part.ToolCall c -> input.Add(Item.Raw(functionCall c))
            | Part.ToolResult r ->
                match output input r with
                | Ok item -> input.Add item
                | Error e -> error <- Some e
            | Part.Media m ->
                error <-
                    Some(Unavailable $"{Provider.Family} takes {m.Kind} input only from the user and in tool results")
            | Part.CacheBreakpoint ttl -> input.Break ttl
            | _ -> ()

    match error with
    | Some e -> Error e
    | None -> Ok()

let private breakpoint (ttl: CacheTtl) : Json =
    match ttl with
    | CacheTtl.Short -> Json.Object [ "mode", str "explicit" ]
    | CacheTtl.Long -> Json.Object [ "mode", str "explicit"; "ttl", str "12h" ]

let private renderBlock (block: Block) : Json =
    match block.Mark with
    | Some ttl -> Json.Object(block.Members @ [ "prompt_cache_breakpoint", breakpoint ttl ])
    | None -> Json.Object block.Members

let private render (item: Item) : Json =
    match item with
    | Item.Message(role, blocks) ->
        Json.Object [ "role", str role; "content", Json.Array(blocks |> List.map renderBlock) ]
    | Item.Assistant(text, phase) ->
        Json.Object
            [
                "role", str "assistant"
                "content", str text
                match phase with
                | Some p -> "phase", str p
                | None -> ()
            ]
    | Item.Output(callId, blocks) ->
        let output =
            match blocks with
            | [] -> str ""
            // One text without a breakpoint: the plain string, as OpenAI's examples send it.
            | [ {
                    Members = [ "type", Json.String "input_text"; "text", Json.String text ]
                    Mark = None
                } ] -> str text
            | blocks -> Json.Array(blocks |> List.map renderBlock)

        Json.Object [ "type", str "function_call_output"; "call_id", str callId; "output", output ]
    | Item.Raw json -> json

/// The effort's name in the API (`Off` is `none`; `xhigh` is not reachable).
let effortName (effort: Effort) : string =
    match effort with
    | Effort.Off -> "none"
    | Effort.Minimal -> "minimal"
    | Effort.Low -> "low"
    | Effort.Medium -> "medium"
    | Effort.High -> "high"
    | Effort.Max -> "max"

/// `reasoning`: the effort, and `summary: "auto"` for visible reasoning; nothing for the model's
/// defaults. Budgets have no place in the API (`Fit` drops them for catalog models).
let reasoning (request: ReasoningRequest option) : Json option =
    match request with
    | None -> None
    | Some r ->
        let members =
            [
                match r.Effort with
                | Some effort -> "effort", str (effortName effort)
                | None -> ()
                if r.Visible then
                    "summary", str "auto"
            ]

        if members.IsEmpty then None else Some(Json.Object members)

/// A tool as a function tool. Strict tools are rewritten into strict mode's dialect
/// (`Strict.tryRewrite`); one that cannot be is sent as given with `strict: false`. `strict` is
/// always sent: the Responses API makes tools strict when it is left out.
let tool (spec: ToolSpec) : Json =
    let parameters, strict =
        if spec.Strict then
            match Strict.schema spec.Parameters with
            | Some rewritten -> rewritten, true
            | None -> spec.Parameters, false
        else
            spec.Parameters, false

    Json.Object
        [
            "type", str "function"
            "name", str spec.Name
            if spec.Description <> "" then
                "description", str spec.Description
            "parameters", parameters
            "strict", Json.Bool strict
        ]

/// A name the API takes for an output format (`^[a-zA-Z0-9_-]{1,64}$`).
let formatName (name: string) : string =
    let safe =
        String(
            name.ToCharArray()
            |> Array.map (fun c ->
                if Char.IsAsciiLetterOrDigit c || c = '_' || c = '-' then
                    c
                else
                    '_')
        )

    if safe = "" then "output"
    elif safe.Length > 64 then safe.Substring(0, 64)
    else safe

let private format (output: OutputFormat) : Json option =
    match output with
    | OutputFormat.Text -> None
    | OutputFormat.Json(name, schema, strict) ->
        let schema, strict =
            if strict then
                match Strict.schema schema with
                | Some rewritten -> rewritten, true
                | None -> schema, false
            else
                schema, false

        Some(
            Json.Object
                [
                    "format",
                    Json.Object
                        [
                            "type", str "json_schema"
                            "name", str (formatName name)
                            "schema", schema
                            "strict", Json.Bool strict
                        ]
                ]
        )

/// The keys of the request's metadata that name its user: `safety_identifier`.
let private userKeys = [ "user_id"; "userId" ]

/// The other metadata as `metadata`: what the API takes (16 pairs, keys of at most 64 characters,
/// values of at most 512); the rest is left out.
let private metadata (pairs: (string * string) list) : (string * Json) list =
    pairs
    |> List.filter (fun (key, value) ->
        not (List.contains key userKeys)
        && key <> ""
        && key.Length <= 64
        && value.Length <= 512)
    |> List.truncate 16
    |> List.map (fun (key, value) -> key, str value)

/// Whether cache breakpoints are sent: as the provider's `cacheBreakpoints` quirk says, else for the
/// models whose catalog entry takes them (GPT-5.6 and later). A model the catalog does not know gets
/// none: earlier models do not take them, and implicit caching works without them.
let takesBreakpoints (settings: Provider.Settings) (info: ModelInfo option) : bool =
    match settings.CacheBreakpoints, info with
    | Some value, _ -> value
    | None, Some info -> info.Caps.Caching.Mode = CacheMode.Breakpoints
    | None, None -> false

/// The JSON body of a request.
let body (settings: Provider.Settings) (info: ModelInfo option) (chat: ChatRequest) : Result<Json, ChatError> =
    let input = Input(takesBreakpoints settings info)

    let system =
        chat.System
        |> List.tryPick (fun part ->
            match part with
            | Part.Text _
            | Part.CacheBreakpoint _ -> None
            | other -> Some(Unavailable $"{Provider.Family} takes only text in the system prompt, not {other}"))

    match system with
    | Some error -> Error error
    | None ->
        let instructions =
            match settings.System with
            | Provider.SystemPlacement.Instructions ->
                chat.System
                |> List.choose (fun part ->
                    match part with
                    | Part.Text t when t.Text <> "" -> Some t.Text
                    | _ -> None)
                |> function
                    | [] -> None
                    | texts -> Some(String.concat "\n\n" texts)
            | Provider.SystemPlacement.Developer ->
                let blocks = ResizeArray<Block>()

                for part in chat.System do
                    match part with
                    | Part.Text t when t.Text <> "" -> blocks.Add(input.Block(textBlock t.Text))
                    | Part.CacheBreakpoint ttl -> input.Break ttl
                    | _ -> ()

                if blocks.Count > 0 then
                    input.Add(Item.Message("developer", List.ofSeq blocks))

                None

        let messages =
            chat.Messages
            |> List.fold
                (fun result message ->
                    result
                    |> Result.bind (fun () ->
                        match message.Role with
                        | Role.User -> user input message.Parts
                        | Role.Assistant -> assistant input message.Parts))
                (Ok())

        match messages with
        | Error e -> Error e
        | Ok() ->
            let tools = chat.Tools |> List.map tool

            let toolChoice =
                if tools.IsEmpty then
                    None
                else
                    match chat.ToolChoice with
                    | ToolChoice.Auto -> None
                    | ToolChoice.None -> Some(str "none")
                    | ToolChoice.Required -> Some(str "required")
                    | ToolChoice.Tool name -> Some(Json.Object [ "type", str "function"; "name", str name ])

            let reasons =
                match info with
                | Some info -> info.Caps.Reasoning.IsSome
                | None -> true

            let user =
                chat.Metadata
                |> List.tryFind (fun (key, _) -> List.contains key userKeys)
                |> Option.map snd

            let metadata = metadata chat.Metadata

            Ok(
                Json.Object
                    [
                        "model", str chat.Model.Model
                        match instructions with
                        | Some text -> "instructions", str text
                        | None -> ()
                        "input", Json.Array(input.Items |> List.map render)
                        if not tools.IsEmpty then
                            "tools", Json.Array tools
                        match toolChoice with
                        | Some choice -> "tool_choice", choice
                        | None -> ()
                        match chat.ParallelToolCalls with
                        | Some value when not tools.IsEmpty -> "parallel_tool_calls", Json.Bool value
                        | _ -> ()
                        match reasoning chat.Reasoning with
                        | Some r -> "reasoning", r
                        | None -> ()
                        if reasons then
                            "include", Json.Array [ str EncryptedReasoning ]
                        match format chat.Output with
                        | Some text -> "text", text
                        | None -> ()
                        match chat.MaxOutputTokens with
                        | Some n -> "max_output_tokens", Json.int n
                        | None -> ()
                        // Top-k, seeds and stop sequences have no place in the Responses API.
                        match chat.Sampling.Temperature with
                        | Some t -> "temperature", Json.float t
                        | None -> ()
                        match chat.Sampling.TopP with
                        | Some p -> "top_p", Json.float p
                        | None -> ()
                        match chat.Cache.Key with
                        | Some key -> "prompt_cache_key", str key
                        | None -> ()
                        match user with
                        | Some id -> "safety_identifier", str id
                        | None -> ()
                        if not metadata.IsEmpty then
                            "metadata", Json.Object metadata
                        "store", Json.Bool false
                        "stream", Json.Bool true
                    ]
            )

/// The HTTP request for a chat request: the body, and the organization and project headers (from
/// the provider's quirks). Credentials are not the adapter's: the connection adds them.
let http (ctx: WireCtx, chat: ChatRequest) : Result<HttpRequest, ChatError> =
    let settings = Provider.Settings.ofProvider ctx.Provider

    body settings ctx.Info chat
    |> Result.map (fun json ->
        let request =
            HttpRequest.post (WireCtx.url ctx ResponsesPath) (utf8.GetBytes(Json.serialize json))
            |> HttpRequest.withHeader "content-type" "application/json"
            |> HttpRequest.withHeader "accept" "text/event-stream"

        let request =
            match settings.Organization with
            | Some organization -> request |> HttpRequest.withHeader "openai-organization" organization
            | None -> request

        match settings.Project with
        | Some project -> request |> HttpRequest.withHeader "openai-project" project
        | None -> request)
