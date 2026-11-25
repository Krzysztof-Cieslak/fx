namespace Fx.Models

open System
open Fx.Contracts

/// Opaque state a provider attached to a part of a response (spec §10.3): a thinking signature, an
/// encrypted reasoning item, a thought signature. It is replayed to the provider byte for byte in
/// later turns, and only where the replay rules allow (`Replay`).
type ProviderState =
    {
        /// The wire family that produced it (`ChatWire.Family`), e.g. `anthropic.messages`.
        Family: string
        /// The model that produced it.
        Model: ModelRef
        /// What it is, in the family's terms, e.g. `signature`, `redacted`, `encrypted`.
        Kind: string
        /// The state itself, exactly as the provider sent it (or as the adapter packed it).
        Data: string
    }

    override this.ToString() =
        $"state({this.Family} {this.Kind} from {this.Model}, {this.Data.Length} chars)"

/// How long a cached prefix should live.
[<RequireQualifiedAccess>]
type CacheTtl =
    /// The provider's default (minutes).
    | Short
    /// Longer, where the provider offers it (about an hour).
    | Long

    override this.ToString() =
        match this with
        | Short -> "short"
        | Long -> "long"

/// Where the bytes of a media part are.
[<RequireQualifiedAccess>]
type MediaSource =
    /// In the request, base64-encoded.
    | Base64 of data: string
    /// At a URL the provider fetches.
    | Url of url: string
    /// In the host's blob store (`sha256:…`): resolved before the request leaves.
    | Blob of digest: string

    override this.ToString() =
        match this with
        | Base64 data -> $"base64({data.Length} chars)"
        | Url url -> url
        | Blob digest -> digest

/// Lines of a file, 1-based and inclusive.
type LineRange =
    {
        First: int
        Last: int
    }

    override this.ToString() = $"{this.First}-{this.Last}"

/// Where content in a part came from (spec §18 content exclusions, §5 `SeenRanges`): a file, as the
/// code that put its content in the part saw it. The model-egress hooks match these against the
/// content exclusions; they never leave for a model (`ModelAccess` drops them after the hooks).
type ContentSource =
    {
        /// The file: relative to the workspace, with `/`, when inside it; otherwise absolute.
        Path: string
        /// The lines quoted, if not the whole file.
        Lines: LineRange option
    }

    override this.ToString() =
        match this.Lines with
        | Some lines -> $"{this.Path}:{lines}"
        | None -> this.Path

/// An image, document or other media input.
type MediaPart =
    {
        Kind: MediaKind
        /// The MIME type, e.g. `image/png`.
        MediaType: string
        Source: MediaSource
        /// A file name, for documents.
        Name: string option
        /// The files its bytes came from (provenance; never sent).
        Sources: ContentSource list
    }

    override this.ToString() =
        $"{this.Kind} {this.MediaType} {this.Source}"

type TextPart =
    {
        Text: string
        State: ProviderState option
        /// The files its text quotes (provenance; never sent).
        Sources: ContentSource list
    }

    override this.ToString() = $"text({this.Text.Length} chars)"

/// A model's reasoning: its visible text (thinking or a summary; may be empty) and the state that
/// replays it.
type ReasoningPart =
    {
        Text: string
        State: ProviderState option
    }

    override this.ToString() = $"reasoning({this.Text.Length} chars)"

/// A tool call the model made.
type ToolCall =
    {
        /// The provider's id for the call; tool results refer to it.
        Id: string
        Name: string
        /// The input as the model wrote it: JSON text, which may be invalid (e.g. cut off).
        Arguments: string
        State: ProviderState option
    }

    override this.ToString() = $"call {this.Id} {this.Name}"

/// Content of a tool result.
[<RequireQualifiedAccess>]
type ToolContent =
    | Text of text: string
    | Media of MediaPart

    override this.ToString() =
        match this with
        | Text text -> $"text({text.Length} chars)"
        | Media media -> string media

/// The result of a tool call, sent back to the model.
type ToolResult =
    {
        CallId: string
        /// The tool's name (some providers want it with the result).
        Name: string
        Content: ToolContent list
        /// The tool failed; the content says why.
        IsError: bool
        /// The files its content quotes (provenance; never sent).
        Sources: ContentSource list
    }

    override this.ToString() =
        let error = if this.IsError then " (error)" else ""
        $"result {this.CallId} {this.Name}{error}"

/// A typed part of a message (spec §10.3).
[<RequireQualifiedAccess>]
type Part =
    | Text of TextPart
    | Media of MediaPart
    | Reasoning of ReasoningPart
    | ToolCall of ToolCall
    | ToolResult of ToolResult
    /// The model declined to answer, with its explanation.
    | Refusal of text: string
    /// Everything before this point is a cacheable prefix (spec §5: stable receipts become provider
    /// cache breakpoints).
    | CacheBreakpoint of CacheTtl

    override this.ToString() =
        match this with
        | Text t -> string t
        | Media m -> string m
        | Reasoning r -> string r
        | ToolCall c -> string c
        | ToolResult r -> string r
        | Refusal text -> $"refusal({text.Length} chars)"
        | CacheBreakpoint ttl -> $"cache breakpoint ({ttl})"

/// Who wrote a message. Instructions are the request's `System`; tool results are parts of user
/// messages.
[<RequireQualifiedAccess>]
type Role =
    | User
    | Assistant

    override this.ToString() =
        match this with
        | User -> "user"
        | Assistant -> "assistant"

type Message =
    {
        Role: Role
        Parts: Part list
    }

    override this.ToString() =
        $"{this.Role}: {this.Parts.Length} parts"

/// A tool the model may call, with its input schema (JSON Schema, from a codec: `ToolSpec.ofCodec`).
type ToolSpec =
    {
        Name: string
        Description: string
        /// JSON Schema (draft 2020-12) of the input object.
        Parameters: Json
        /// Constrain inputs to the schema exactly, where the model can.
        Strict: bool
    }

    override this.ToString() = $"tool {this.Name}"

[<RequireQualifiedAccess>]
type ToolChoice =
    /// The model decides.
    | Auto
    /// No tool calls.
    | None
    /// At least one tool call.
    | Required
    /// A call of this tool.
    | Tool of name: string

    override this.ToString() =
        match this with
        | Auto -> "auto"
        | None -> "none"
        | Required -> "required"
        | Tool name -> "tool " + name

type Sampling =
    {
        Temperature: float option
        TopP: float option
        TopK: int option
        Seed: int64 option
        /// Stop sequences.
        Stop: string list
    }

    override this.ToString() =
        let show (value: 'T option) =
            value |> Option.map string |> Option.defaultValue "-"

        $"sampling(temperature {show this.Temperature}, topP {show this.TopP}, topK {show this.TopK}, seed {show this.Seed}, {this.Stop.Length} stops)"

type ReasoningRequest =
    {
        /// `None`: the model's default.
        Effort: Effort option
        /// A thinking budget in tokens, for models that take one.
        BudgetTokens: int option
        /// Ask for visible reasoning (thinking text or summaries).
        Visible: bool
    }

    override this.ToString() =
        let effort = this.Effort |> Option.map string |> Option.defaultValue "default"
        let budget = this.BudgetTokens |> Option.map string |> Option.defaultValue "-"
        $"reasoning(effort {effort}, budget {budget}, visible {this.Visible})"

/// What the response's text must be.
[<RequireQualifiedAccess>]
type OutputFormat =
    | Text
    /// JSON matching the schema (structured output).
    | Json of name: string * schema: Json * strict: bool

    override this.ToString() =
        match this with
        | Text -> "text"
        | Json(name, _, strict) -> $"json {name} (strict {strict})"

/// Hints for prompt caching beyond the breakpoints in the content.
type CacheHints =
    {
        /// Names the cache this request should share (OpenAI `prompt_cache_key`).
        Key: string option
        /// A breakpoint after the tool definitions.
        Tools: CacheTtl option
    }

    override this.ToString() =
        let key = this.Key |> Option.defaultValue "-"
        let tools = this.Tools |> Option.map string |> Option.defaultValue "-"
        $"cache(key {key}, tools {tools})"

/// A request to a chat model (spec §10.3): everything an adapter needs, in no provider's terms.
type ChatRequest =
    {
        Model: ModelRef
        /// Instructions: the stable prefix (text parts and cache breakpoints).
        System: Part list
        Messages: Message list
        Tools: ToolSpec list
        ToolChoice: ToolChoice
        /// Whether several tool calls may come in one response; `None` for the default.
        ParallelToolCalls: bool option
        Sampling: Sampling
        /// `None`: the model's default.
        Reasoning: ReasoningRequest option
        MaxOutputTokens: int option
        Output: OutputFormat
        Cache: CacheHints
        /// Tags for the provider (e.g. a user id), never secrets.
        Metadata: (string * string) list
    }

    override this.ToString() =
        $"chat {this.Model} ({this.Messages.Length} messages, {this.Tools.Length} tools)"

module ProviderState =
    let codec: Codec<ProviderState> =
        Codec.record {
            let! family = Codec.field "family" (fun (s: ProviderState) -> s.Family) Codec.string
            and! model = Codec.field "model" (fun s -> s.Model) ModelRef.codec
            and! kind = Codec.field "kind" (fun s -> s.Kind) Codec.string
            and! data = Codec.field "data" (fun s -> s.Data) Codec.string

            return
                {
                    Family = family
                    Model = model
                    Kind = kind
                    Data = data
                }
        }
        |> Codec.describe "Opaque provider state, replayed byte for byte."
        |> Codec.named "ProviderState"

module CacheTtl =
    let codec: Codec<CacheTtl> =
        Codec.enum [ CacheTtl.Short, "short"; CacheTtl.Long, "long" ]
        |> Codec.named "CacheTtl"

module LineRange =
    let codec: Codec<LineRange> =
        Codec.record {
            let! first =
                Codec.field "first" (fun (r: LineRange) -> r.First) (Codec.int |> Codec.atLeast 1)

            and! last = Codec.field "last" (fun r -> r.Last) (Codec.int |> Codec.atLeast 1)
            return { First = first; Last = last }
        }
        |> Codec.refine (fun r ->
            if r.Last >= r.First then
                Ok()
            else
                Error $"expected last ({r.Last}) at least first ({r.First})")
        |> Codec.describe "Lines of a file, 1-based and inclusive."
        |> Codec.named "LineRange"

module ContentSource =
    let codec: Codec<ContentSource> =
        Codec.record {
            let! path =
                Codec.field "path" (fun (s: ContentSource) -> s.Path) (Codec.string |> Codec.nonEmpty)

            and! lines = Codec.optionalField "lines" (fun s -> s.Lines) LineRange.codec
            return { Path = path; Lines = lines }
        }
        |> Codec.describe "A file whose content a part quotes, relative to the workspace (with /) when inside it."
        |> Codec.named "ContentSource"

    /// A whole file.
    let file (path: string) : ContentSource = { Path = path; Lines = None }

    /// The `sources` field of parts: left out when empty, so parts without provenance keep their JSON.
    let field (get: 'T -> ContentSource list) : RecordFields<'T, ContentSource list option> =
        Codec.optionalField
            "sources"
            (fun value ->
                match get value with
                | [] -> None
                | sources -> Some sources)
            (Codec.list codec
             |> Codec.describe "The files its content quotes (provenance for content exclusions; never sent).")

module MediaPart =
    let source: Codec<MediaSource> =
        Codec.union "kind" {
            let! base64 =
                Codec.case1 "base64" MediaSource.Base64 (Field.required "data" Codec.string)

            and! url = Codec.case1 "url" MediaSource.Url (Field.required "url" Codec.string)

            and! blob =
                Codec.case1 "blob" MediaSource.Blob (Field.required "digest" Codec.string)

            return
                fun value ->
                    match value with
                    | MediaSource.Base64 data -> base64 data
                    | MediaSource.Url u -> url u
                    | MediaSource.Blob digest -> blob digest
        }
        |> Codec.named "MediaSource"

    let codec: Codec<MediaPart> =
        Codec.record {
            let! kind = Codec.field "media" (fun (m: MediaPart) -> m.Kind) ModelCaps.mediaKind
            and! mediaType = Codec.field "mediaType" (fun m -> m.MediaType) Codec.string
            and! source = Codec.field "source" (fun m -> m.Source) source
            and! name = Codec.optionalField "name" (fun m -> m.Name) Codec.string
            and! sources = ContentSource.field (fun m -> m.Sources)

            return
                {
                    Kind = kind
                    MediaType = mediaType
                    Source = source
                    Name = name
                    Sources = defaultArg sources []
                }
        }
        |> Codec.named "MediaPart"

module ToolContent =
    let codec: Codec<ToolContent> =
        Codec.union "kind" {
            let! text = Codec.case1 "text" ToolContent.Text (Field.required "text" Codec.string)
            and! media = Codec.caseOf "media" ToolContent.Media MediaPart.codec

            return
                fun value ->
                    match value with
                    | ToolContent.Text t -> text t
                    | ToolContent.Media m -> media m
        }
        |> Codec.named "ToolContent"

module Part =
    let private textPart: Codec<TextPart> =
        Codec.record {
            let! text = Codec.field "text" (fun (t: TextPart) -> t.Text) Codec.string
            and! state = Codec.optionalField "state" (fun t -> t.State) ProviderState.codec
            and! sources = ContentSource.field (fun t -> t.Sources)

            return
                {
                    Text = text
                    State = state
                    Sources = defaultArg sources []
                }
        }

    let private reasoningPart: Codec<ReasoningPart> =
        Codec.record {
            let! text = Codec.field "text" (fun (r: ReasoningPart) -> r.Text) Codec.string
            and! state = Codec.optionalField "state" (fun r -> r.State) ProviderState.codec
            return { Text = text; State = state }
        }

    let toolCall: Codec<ToolCall> =
        Codec.record {
            let! id = Codec.field "id" (fun (c: ToolCall) -> c.Id) Codec.string
            and! name = Codec.field "name" (fun c -> c.Name) Codec.string
            and! arguments = Codec.field "arguments" (fun c -> c.Arguments) Codec.string
            and! state = Codec.optionalField "state" (fun c -> c.State) ProviderState.codec

            return
                {
                    Id = id
                    Name = name
                    Arguments = arguments
                    State = state
                }
        }
        |> Codec.named "ToolCall"

    let toolResult: Codec<ToolResult> =
        Codec.record {
            let! callId = Codec.field "callId" (fun (r: ToolResult) -> r.CallId) Codec.string
            and! name = Codec.field "name" (fun r -> r.Name) Codec.string

            and! content =
                Codec.field "content" (fun r -> r.Content) (Codec.list ToolContent.codec)

            and! isError = Codec.defaultField "isError" (fun r -> r.IsError) false Codec.bool
            and! sources = ContentSource.field (fun r -> r.Sources)

            return
                {
                    CallId = callId
                    Name = name
                    Content = content
                    IsError = isError
                    Sources = defaultArg sources []
                }
        }
        |> Codec.named "ToolResult"

    let codec: Codec<Part> =
        Codec.union "kind" {
            let! text = Codec.caseOf "text" Part.Text textPart
            and! media = Codec.caseOf "media" Part.Media MediaPart.codec
            and! reasoning = Codec.caseOf "reasoning" Part.Reasoning reasoningPart
            and! call = Codec.caseOf "toolCall" Part.ToolCall toolCall
            and! result = Codec.caseOf "toolResult" Part.ToolResult toolResult

            and! refusal =
                Codec.case1 "refusal" Part.Refusal (Field.required "text" Codec.string)

            and! breakpoint =
                Codec.case1
                    "cacheBreakpoint"
                    Part.CacheBreakpoint
                    (Field.withDefault "ttl" CacheTtl.Short CacheTtl.codec)

            return
                fun value ->
                    match value with
                    | Part.Text t -> text t
                    | Part.Media m -> media m
                    | Part.Reasoning r -> reasoning r
                    | Part.ToolCall c -> call c
                    | Part.ToolResult r -> result r
                    | Part.Refusal t -> refusal t
                    | Part.CacheBreakpoint ttl -> breakpoint ttl
        }
        |> Codec.describe "A typed part of a message."
        |> Codec.named "Part"

    let text (text: string) : Part =
        Part.Text
            {
                Text = text
                State = None
                Sources = []
            }

    /// Text that quotes files: content exclusions match the part by its sources.
    let quoting (sources: ContentSource list) (text: string) : Part =
        Part.Text
            {
                Text = text
                State = None
                Sources = sources
            }

    /// Every source of a part, its tool result's media included.
    let sources (part: Part) : ContentSource list =
        match part with
        | Part.Text t -> t.Sources
        | Part.Media m -> m.Sources
        | Part.ToolResult r ->
            r.Sources
            @ (r.Content
               |> List.collect (fun c ->
                   match c with
                   | ToolContent.Media m -> m.Sources
                   | ToolContent.Text _ -> []))
        | Part.Reasoning _
        | Part.ToolCall _
        | Part.Refusal _
        | Part.CacheBreakpoint _ -> []

    /// The part without provenance: what leaves for a model.
    let withoutSources (part: Part) : Part =
        match part with
        | Part.Text t when not t.Sources.IsEmpty -> Part.Text { t with Sources = [] }
        | Part.Media m when not m.Sources.IsEmpty -> Part.Media { m with Sources = [] }
        | Part.ToolResult r when not (sources part).IsEmpty ->
            Part.ToolResult
                { r with
                    Sources = []
                    Content =
                        r.Content
                        |> List.map (fun c ->
                            match c with
                            | ToolContent.Media m -> ToolContent.Media { m with Sources = [] }
                            | ToolContent.Text _ -> c)
                }
        | _ -> part

    /// The state a part carries, if any.
    let state (part: Part) : ProviderState option =
        match part with
        | Part.Text t -> t.State
        | Part.Reasoning r -> r.State
        | Part.ToolCall c -> c.State
        | Part.Media _
        | Part.ToolResult _
        | Part.Refusal _
        | Part.CacheBreakpoint _ -> None

module Role =
    let codec: Codec<Role> =
        Codec.enum [ Role.User, "user"; Role.Assistant, "assistant" ]
        |> Codec.named "Role"

module Message =
    let codec: Codec<Message> =
        Codec.record {
            let! role = Codec.field "role" (fun (m: Message) -> m.Role) Role.codec
            and! parts = Codec.field "parts" (fun m -> m.Parts) (Codec.list Part.codec)
            return { Role = role; Parts = parts }
        }
        |> Codec.named "Message"

    let user (parts: Part list) : Message = { Role = Role.User; Parts = parts }

    let assistant (parts: Part list) : Message =
        { Role = Role.Assistant; Parts = parts }

    /// A user message of one text part.
    let userText (text: string) : Message = user [ Part.text text ]

module ToolSpec =
    let codec: Codec<ToolSpec> =
        Codec.record {
            let! name =
                Codec.field "name" (fun (t: ToolSpec) -> t.Name) (Codec.string |> Codec.pattern "^[A-Za-z0-9_-]{1,64}$")

            and! description =
                Codec.defaultField "description" (fun t -> t.Description) "" Codec.string

            and! parameters = Codec.field "parameters" (fun t -> t.Parameters) Codec.json
            and! strict = Codec.defaultField "strict" (fun t -> t.Strict) false Codec.bool

            return
                {
                    Name = name
                    Description = description
                    Parameters = parameters
                    Strict = strict
                }
        }
        |> Codec.describe "A tool the model may call, with the JSON Schema of its input."
        |> Codec.named "ToolSpec"

    /// A tool whose input is described by a codec (spec §4.1: tools are defined from contracts).
    let ofCodec (name: string) (description: string) (input: Codec<'T>) : ToolSpec =
        {
            Name = name
            Description = description
            Parameters = Schema.toJsonSchema input.Schema
            Strict = false
        }

module ToolChoice =
    let codec: Codec<ToolChoice> =
        Codec.union "kind" {
            let! auto = Codec.case0 "auto" ToolChoice.Auto
            and! none = Codec.case0 "none" ToolChoice.None
            and! required = Codec.case0 "required" ToolChoice.Required
            and! tool = Codec.case1 "tool" ToolChoice.Tool (Field.required "name" Codec.string)

            return
                fun value ->
                    match value with
                    | ToolChoice.Auto -> auto
                    | ToolChoice.None -> none
                    | ToolChoice.Required -> required
                    | ToolChoice.Tool name -> tool name
        }
        |> Codec.named "ToolChoice"

module Sampling =
    let none: Sampling =
        {
            Temperature = None
            TopP = None
            TopK = None
            Seed = None
            Stop = []
        }

    let codec: Codec<Sampling> =
        Codec.record {
            let! temperature =
                Codec.optionalField "temperature" (fun (s: Sampling) -> s.Temperature) Codec.float

            and! topP = Codec.optionalField "topP" (fun s -> s.TopP) Codec.float
            and! topK = Codec.optionalField "topK" (fun s -> s.TopK) Codec.int
            and! seed = Codec.optionalField "seed" (fun s -> s.Seed) Codec.int64
            and! stop = Codec.defaultField "stop" (fun s -> s.Stop) [] (Codec.list Codec.string)

            return
                {
                    Temperature = temperature
                    TopP = topP
                    TopK = topK
                    Seed = seed
                    Stop = stop
                }
        }
        |> Codec.named "Sampling"

module ReasoningRequest =
    let codec: Codec<ReasoningRequest> =
        Codec.record {
            let! effort =
                Codec.optionalField "effort" (fun (r: ReasoningRequest) -> r.Effort) Effort.codec

            and! budget = Codec.optionalField "budgetTokens" (fun r -> r.BudgetTokens) Codec.int
            and! visible = Codec.defaultField "visible" (fun r -> r.Visible) false Codec.bool

            return
                {
                    Effort = effort
                    BudgetTokens = budget
                    Visible = visible
                }
        }
        |> Codec.named "ReasoningRequest"

module OutputFormat =
    let codec: Codec<OutputFormat> =
        Codec.union "kind" {
            let! text = Codec.case0 "text" OutputFormat.Text

            and! json =
                Codec.case3
                    "json"
                    (fun name schema strict -> OutputFormat.Json(name, schema, strict))
                    (Field.required "name" Codec.string)
                    (Field.required "schema" Codec.json)
                    (Field.withDefault "strict" false Codec.bool)

            return
                fun value ->
                    match value with
                    | OutputFormat.Text -> text
                    | OutputFormat.Json(name, schema, strict) -> json name schema strict
        }
        |> Codec.named "OutputFormat"

module CacheHints =
    let none: CacheHints = { Key = None; Tools = None }

    let codec: Codec<CacheHints> =
        Codec.record {
            let! key = Codec.optionalField "key" (fun (c: CacheHints) -> c.Key) Codec.string
            and! tools = Codec.optionalField "tools" (fun c -> c.Tools) CacheTtl.codec
            return { Key = key; Tools = tools }
        }
        |> Codec.named "CacheHints"

module ChatRequest =
    /// A request with these messages and every option at its default.
    let create (model: ModelRef) (messages: Message list) : ChatRequest =
        {
            Model = model
            System = []
            Messages = messages
            Tools = []
            ToolChoice = ToolChoice.Auto
            ParallelToolCalls = None
            Sampling = Sampling.none
            Reasoning = None
            MaxOutputTokens = None
            Output = OutputFormat.Text
            Cache = CacheHints.none
            Metadata = []
        }

    let codec: Codec<ChatRequest> =
        Codec.record {
            let! model = Codec.field "model" (fun (r: ChatRequest) -> r.Model) ModelRef.codec

            and! system =
                Codec.defaultField "system" (fun r -> r.System) [] (Codec.list Part.codec)

            and! messages =
                Codec.field "messages" (fun r -> r.Messages) (Codec.list Message.codec)

            and! tools =
                Codec.defaultField "tools" (fun r -> r.Tools) [] (Codec.list ToolSpec.codec)

            and! toolChoice =
                Codec.defaultField "toolChoice" (fun r -> r.ToolChoice) ToolChoice.Auto ToolChoice.codec

            and! parallelCalls =
                Codec.optionalField "parallelToolCalls" (fun r -> r.ParallelToolCalls) Codec.bool

            and! sampling =
                Codec.defaultField "sampling" (fun r -> r.Sampling) Sampling.none Sampling.codec

            and! reasoning =
                Codec.optionalField "reasoning" (fun r -> r.Reasoning) ReasoningRequest.codec

            and! maxOutput =
                Codec.optionalField "maxOutputTokens" (fun r -> r.MaxOutputTokens) Codec.int

            and! output =
                Codec.defaultField "output" (fun r -> r.Output) OutputFormat.Text OutputFormat.codec

            and! cache =
                Codec.defaultField "cache" (fun r -> r.Cache) CacheHints.none CacheHints.codec

            and! metadata =
                Codec.defaultField "metadata" (fun r -> r.Metadata) [] (Codec.keyValues Codec.string)

            return
                {
                    Model = model
                    System = system
                    Messages = messages
                    Tools = tools
                    ToolChoice = toolChoice
                    ParallelToolCalls = parallelCalls
                    Sampling = sampling
                    Reasoning = reasoning
                    MaxOutputTokens = maxOutput
                    Output = output
                    Cache = cache
                    Metadata = metadata
                }
        }
        |> Codec.describe "A request to a chat model, in no provider's terms."
        |> Codec.named "ChatRequest"

    /// Every provider state in the request's messages.
    let states (request: ChatRequest) : ProviderState list =
        request.Messages |> List.collect (fun m -> m.Parts |> List.choose Part.state)

    /// Every source of the request's parts (system and messages), in order, with duplicates.
    let sources (request: ChatRequest) : ContentSource list =
        (request.System |> List.collect Part.sources)
        @ (request.Messages |> List.collect (fun m -> m.Parts |> List.collect Part.sources))

    /// The request without provenance: what a wire gets (`ModelAccess` drops it after the hooks).
    let withoutSources (request: ChatRequest) : ChatRequest =
        if (sources request).IsEmpty then
            request
        else
            { request with
                System = request.System |> List.map Part.withoutSources
                Messages =
                    request.Messages
                    |> List.map (fun m ->
                        { m with
                            Parts = m.Parts |> List.map Part.withoutSources
                        })
            }
