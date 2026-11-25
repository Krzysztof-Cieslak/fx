namespace Fx.Models

open System
open Fx.Contracts

/// Names a model: the provider that serves it and the provider's own id for it, written
/// `provider/model` (the model id may contain `/`).
type ModelRef =
    {
        /// A provider id: lowercase letters, digits, `.` and `-`.
        Provider: string
        Model: string
    }

    override this.ToString() = this.Provider + "/" + this.Model

/// What a model does (spec §10.2).
[<RequireQualifiedAccess>]
type ModelKind =
    | Chat
    | Decision
    | Embedding

    override this.ToString() =
        match this with
        | Chat -> "chat"
        | Decision -> "decision"
        | Embedding -> "embedding"

/// How hard a model should reason. Providers name fewer levels; adapters map to the nearest.
[<RequireQualifiedAccess>]
type Effort =
    /// No reasoning, where the model allows turning it off.
    | Off
    | Minimal
    | Low
    | Medium
    | High
    | Max

    override this.ToString() =
        match this with
        | Off -> "off"
        | Minimal -> "minimal"
        | Low -> "low"
        | Medium -> "medium"
        | High -> "high"
        | Max -> "max"

/// A range of token counts, both ends included.
type TokenRange =
    {
        Min: int
        Max: int
    }

    override this.ToString() = $"{this.Min}..{this.Max}"

/// What a model can do when it reasons.
type ReasoningCaps =
    {
        /// The efforts it takes (empty: none, e.g. only a budget).
        Efforts: Effort list
        /// The thinking budget it takes, if it takes one.
        Budget: TokenRange option
        /// Whether it can return its reasoning (summaries or thinking text).
        Visible: bool
        /// Whether its reasoning state (`ProviderState`) must be replayed in later turns.
        Replay: bool
    }

    override this.ToString() =
        let efforts = this.Efforts |> List.map string |> String.concat "|"
        let budget = this.Budget |> Option.map string |> Option.defaultValue "none"
        $"reasoning(efforts {efforts}, budget {budget}, visible {this.Visible}, replay {this.Replay})"

type ToolCaps =
    {
        /// Whether it calls tools at all.
        Calls: bool
        /// Whether it may call several tools in one response.
        Parallel: bool
        /// Whether a tool call can be required or forced (`ToolChoice.Required`, `ToolChoice.Tool`).
        Forced: bool
        /// Whether tool inputs can be constrained to their schemas exactly (strict mode).
        Strict: bool
    }

    override this.ToString() =
        $"tools(calls {this.Calls}, parallel {this.Parallel}, forced {this.Forced}, strict {this.Strict})"

/// How a provider caches prompts.
[<RequireQualifiedAccess>]
type CacheMode =
    /// It does not.
    | None
    /// It caches prefixes by itself; cache breakpoints are hints at most.
    | Automatic
    /// It caches up to explicit breakpoints (`Part.CacheBreakpoint`).
    | Breakpoints

    override this.ToString() =
        match this with
        | None -> "none"
        | Automatic -> "automatic"
        | Breakpoints -> "breakpoints"

type CachingCaps =
    {
        Mode: CacheMode
        /// How many breakpoints a request may have (for `Breakpoints`).
        MaxBreakpoints: int
        /// Whether requests can name their cache (`CacheHints.Key`).
        Key: bool
        /// Whether a long time-to-live can be asked for (`CacheTtl.Long`).
        LongTtl: bool
    }

    override this.ToString() =
        $"caching({this.Mode}, {this.MaxBreakpoints} breakpoints, key {this.Key}, long {this.LongTtl})"

/// Media a model takes as input.
[<RequireQualifiedAccess>]
type MediaKind =
    | Image
    | Pdf
    | Audio
    | Video

    override this.ToString() =
        match this with
        | Image -> "image"
        | Pdf -> "pdf"
        | Audio -> "audio"
        | Video -> "video"

/// The sampling parameters a model takes.
type SamplingCaps =
    {
        Temperature: bool
        TopP: bool
        TopK: bool
        Seed: bool
        Stop: bool
    }

    override this.ToString() =
        $"sampling(temperature {this.Temperature}, topP {this.TopP}, topK {this.TopK}, seed {this.Seed}, stop {this.Stop})"

/// How responses can stream from a model's endpoint.
[<RequireQualifiedAccess>]
type ModelTransport =
    /// Server-sent events over HTTP.
    | Sse
    | WebSocket
    /// One response, not streamed.
    | Http

    override this.ToString() =
        match this with
        | Sse -> "sse"
        | WebSocket -> "websocket"
        | Http -> "http"

/// What a model can do (spec §10.2). Requests are fitted to it (`Fit.request`).
type ModelCaps =
    {
        Reasoning: ReasoningCaps option
        Tools: ToolCaps
        StructuredOutput: bool
        Logprobs: bool
        Caching: CachingCaps
        Media: MediaKind list
        Sampling: SamplingCaps
        Transports: ModelTransport list
    }

    override this.ToString() =
        let reasoning =
            this.Reasoning |> Option.map string |> Option.defaultValue "no reasoning"

        let media = this.Media |> List.map string |> String.concat "|"

        $"caps({reasoning}, {this.Tools}, structured {this.StructuredOutput}, {this.Caching}, media {media}, {this.Sampling})"

/// Token limits of a model (spec §10.2 `Limits`).
type ModelLimits =
    {
        /// The context window: input and output together.
        Context: int
        /// The most output tokens one response may have.
        Output: int
    }

    override this.ToString() =
        $"context {this.Context}, output {this.Output}"

/// Prices in US dollars per million tokens.
type Cost =
    {
        Input: decimal
        Output: decimal
        /// Input read from the cache; `Input` if absent.
        CacheRead: decimal option
        /// Input written to the cache; `Input` if absent.
        CacheWrite: decimal option
    }

    override this.ToString() =
        let show =
            function
            | Some(d: decimal) -> string d
            | None -> "-"

        $"${this.Input} in, ${this.Output} out, ${show this.CacheRead} cache read, ${show this.CacheWrite} cache write"

/// A model in a catalog (spec §10.2).
type ModelInfo =
    {
        Ref: ModelRef
        Kind: ModelKind
        /// The wire family that serves it (`ChatWire.Family`); `None` for the provider's first endpoint.
        Family: string option
        /// A name for people.
        Name: string option
        Limits: ModelLimits
        Caps: ModelCaps
        Cost: Cost option
        /// Served through a subscription plan rather than paid per token (not for evals, spec §10.4).
        PlanBacked: bool
    }

    override this.ToString() =
        $"{this.Ref} ({this.Kind}, {this.Limits}, {this.Caps})"

/// A role a model plays (spec §10.4): which model plays it is a variant (T208).
type ModelRole =
    | ModelRole of string

    override this.ToString() =
        let (ModelRole name) = this
        name

module ModelRef =
    /// The pattern of provider ids.
    [<Literal>]
    let ProviderPattern = "^[a-z0-9][a-z0-9.-]*$"

    let private providerRegex =
        Text.RegularExpressions.Regex(ProviderPattern, Text.RegularExpressions.RegexOptions.ECMAScript)

    /// Whether a provider id is well formed.
    let isProviderId (id: string) : bool = providerRegex.IsMatch id

    let create (provider: string) (model: string) : ModelRef = { Provider = provider; Model = model }

    /// Parses `provider/model`.
    let tryParse (text: string) : Result<ModelRef, string> =
        let slash = text.IndexOf '/'

        if slash <= 0 || slash = text.Length - 1 then
            Error "expected provider/model"
        else
            let provider = text.Substring(0, slash)

            if not (isProviderId provider) then
                Error "a provider id has lowercase letters, digits, '.' and '-'"
            else
                Ok(create provider (text.Substring(slash + 1)))

    let codec: Codec<ModelRef> =
        Codec.string
        |> Codec.tryMap tryParse string
        |> Codec.describe "A model: `provider/model`."
        |> Codec.named "ModelRef"

module ModelKind =
    let codec: Codec<ModelKind> =
        Codec.enum
            [
                ModelKind.Chat, "chat"
                ModelKind.Decision, "decision"
                ModelKind.Embedding, "embedding"
            ]
        |> Codec.named "ModelKind"

module Effort =
    let all =
        [
            Effort.Off
            Effort.Minimal
            Effort.Low
            Effort.Medium
            Effort.High
            Effort.Max
        ]

    let codec: Codec<Effort> =
        Codec.enum (all |> List.map (fun e -> e, string e))
        |> Codec.describe "How hard a model reasons."
        |> Codec.named "Effort"

    /// Its position from `Off` (0) to `Max` (5).
    let rank (effort: Effort) : int = List.findIndex ((=) effort) all

module TokenRange =
    let codec: Codec<TokenRange> =
        Codec.record {
            let! min = Codec.field "min" (fun (r: TokenRange) -> r.Min) Codec.int
            and! max = Codec.field "max" (fun r -> r.Max) Codec.int
            return { Min = min; Max = max }
        }
        |> Codec.named "TokenRange"

    let clamp (range: TokenRange) (value: int) : int = max range.Min (min range.Max value)

module ModelRole =
    let main = ModelRole "main"
    let fast = ModelRole "fast"
    let summarizer = ModelRole "summarizer"
    let review = ModelRole "review"
    let judge = ModelRole "judge"
    let embed = ModelRole "embed"

    /// The built-in roles; agents and extensions add their own.
    let builtIn = [ main; fast; summarizer; review; judge; embed ]

    let codec: Codec<ModelRole> =
        Codec.string
        |> Codec.pattern "^[a-z][a-z0-9-]*(\\.[a-z0-9][a-z0-9-]*)*$"
        |> Codec.map ModelRole string
        |> Codec.describe "A role a model plays, e.g. main, fast, summarizer."
        |> Codec.named "ModelRole"

module ModelCaps =
    let private reasoning: Codec<ReasoningCaps> =
        Codec.record {
            let! efforts =
                Codec.defaultField "efforts" (fun (r: ReasoningCaps) -> r.Efforts) [] (Codec.list Effort.codec)

            and! budget = Codec.optionalField "budget" (fun r -> r.Budget) TokenRange.codec
            and! visible = Codec.defaultField "visible" (fun r -> r.Visible) false Codec.bool
            and! replay = Codec.defaultField "replay" (fun r -> r.Replay) false Codec.bool

            return
                {
                    Efforts = efforts
                    Budget = budget
                    Visible = visible
                    Replay = replay
                }
        }
        |> Codec.named "ReasoningCaps"

    let private tools: Codec<ToolCaps> =
        Codec.record {
            let! calls =
                Codec.defaultField "calls" (fun (t: ToolCaps) -> t.Calls) false Codec.bool

            and! parallelCalls =
                Codec.defaultField "parallel" (fun t -> t.Parallel) false Codec.bool

            and! forced = Codec.defaultField "forced" (fun t -> t.Forced) false Codec.bool
            and! strict = Codec.defaultField "strict" (fun t -> t.Strict) false Codec.bool

            return
                {
                    Calls = calls
                    Parallel = parallelCalls
                    Forced = forced
                    Strict = strict
                }
        }
        |> Codec.named "ToolCaps"

    let private cacheMode: Codec<CacheMode> =
        Codec.enum
            [
                CacheMode.None, "none"
                CacheMode.Automatic, "automatic"
                CacheMode.Breakpoints, "breakpoints"
            ]

    let private caching: Codec<CachingCaps> =
        Codec.record {
            let! mode =
                Codec.defaultField "mode" (fun (c: CachingCaps) -> c.Mode) CacheMode.None cacheMode

            and! maxBreakpoints =
                Codec.defaultField "maxBreakpoints" (fun c -> c.MaxBreakpoints) 0 Codec.int

            and! key = Codec.defaultField "key" (fun c -> c.Key) false Codec.bool
            and! longTtl = Codec.defaultField "longTtl" (fun c -> c.LongTtl) false Codec.bool

            return
                {
                    Mode = mode
                    MaxBreakpoints = maxBreakpoints
                    Key = key
                    LongTtl = longTtl
                }
        }
        |> Codec.named "CachingCaps"

    let mediaKind: Codec<MediaKind> =
        Codec.enum
            [
                MediaKind.Image, "image"
                MediaKind.Pdf, "pdf"
                MediaKind.Audio, "audio"
                MediaKind.Video, "video"
            ]
        |> Codec.named "MediaKind"

    let private sampling: Codec<SamplingCaps> =
        Codec.record {
            let! temperature =
                Codec.defaultField "temperature" (fun (s: SamplingCaps) -> s.Temperature) false Codec.bool

            and! topP = Codec.defaultField "topP" (fun s -> s.TopP) false Codec.bool
            and! topK = Codec.defaultField "topK" (fun s -> s.TopK) false Codec.bool
            and! seed = Codec.defaultField "seed" (fun s -> s.Seed) false Codec.bool
            and! stop = Codec.defaultField "stop" (fun s -> s.Stop) false Codec.bool

            return
                {
                    Temperature = temperature
                    TopP = topP
                    TopK = topK
                    Seed = seed
                    Stop = stop
                }
        }
        |> Codec.named "SamplingCaps"

    let private transport: Codec<ModelTransport> =
        Codec.enum
            [
                ModelTransport.Sse, "sse"
                ModelTransport.WebSocket, "websocket"
                ModelTransport.Http, "http"
            ]

    /// A model that takes text and nothing else: no reasoning, tools, caching or sampling parameters.
    let minimal: ModelCaps =
        {
            Reasoning = None
            Tools =
                {
                    Calls = false
                    Parallel = false
                    Forced = false
                    Strict = false
                }
            StructuredOutput = false
            Logprobs = false
            Caching =
                {
                    Mode = CacheMode.None
                    MaxBreakpoints = 0
                    Key = false
                    LongTtl = false
                }
            Media = []
            Sampling =
                {
                    Temperature = false
                    TopP = false
                    TopK = false
                    Seed = false
                    Stop = false
                }
            Transports = [ ModelTransport.Sse ]
        }

    let codec: Codec<ModelCaps> =
        Codec.record {
            let! reasoning =
                Codec.optionalField "reasoning" (fun (c: ModelCaps) -> c.Reasoning) reasoning

            and! tools = Codec.defaultField "tools" (fun c -> c.Tools) minimal.Tools tools

            and! structured =
                Codec.defaultField "structuredOutput" (fun c -> c.StructuredOutput) false Codec.bool

            and! logprobs = Codec.defaultField "logprobs" (fun c -> c.Logprobs) false Codec.bool

            and! caching =
                Codec.defaultField "caching" (fun c -> c.Caching) minimal.Caching caching

            and! media = Codec.defaultField "media" (fun c -> c.Media) [] (Codec.list mediaKind)

            and! sampling =
                Codec.defaultField "sampling" (fun c -> c.Sampling) minimal.Sampling sampling

            and! transports =
                Codec.defaultField "transports" (fun c -> c.Transports) minimal.Transports (Codec.list transport)

            return
                {
                    Reasoning = reasoning
                    Tools = tools
                    StructuredOutput = structured
                    Logprobs = logprobs
                    Caching = caching
                    Media = media
                    Sampling = sampling
                    Transports = transports
                }
        }
        |> Codec.describe "What a model can do."
        |> Codec.named "ModelCaps"

module ModelLimits =
    let codec: Codec<ModelLimits> =
        Codec.record {
            let! context =
                Codec.field "context" (fun (l: ModelLimits) -> l.Context) (Codec.int |> Codec.atLeast 1)

            and! output = Codec.field "output" (fun l -> l.Output) (Codec.int |> Codec.atLeast 1)
            return { Context = context; Output = output }
        }
        |> Codec.describe "Token limits: the context window and the most output tokens per response."
        |> Codec.named "ModelLimits"

module Cost =
    let codec: Codec<Cost> =
        Codec.record {
            let! input = Codec.field "input" (fun (c: Cost) -> c.Input) Codec.decimal
            and! output = Codec.field "output" (fun c -> c.Output) Codec.decimal
            and! cacheRead = Codec.optionalField "cacheRead" (fun c -> c.CacheRead) Codec.decimal

            and! cacheWrite =
                Codec.optionalField "cacheWrite" (fun c -> c.CacheWrite) Codec.decimal

            return
                {
                    Input = input
                    Output = output
                    CacheRead = cacheRead
                    CacheWrite = cacheWrite
                }
        }
        |> Codec.describe "Prices in US dollars per million tokens."
        |> Codec.named "Cost"

module ModelInfo =
    let codec: Codec<ModelInfo> =
        Codec.record {
            let! reference = Codec.field "ref" (fun (m: ModelInfo) -> m.Ref) ModelRef.codec

            and! kind =
                Codec.defaultField "kind" (fun m -> m.Kind) ModelKind.Chat ModelKind.codec

            and! family = Codec.optionalField "family" (fun m -> m.Family) Codec.string
            and! name = Codec.optionalField "name" (fun m -> m.Name) Codec.string
            and! limits = Codec.field "limits" (fun m -> m.Limits) ModelLimits.codec

            and! caps =
                Codec.defaultField "caps" (fun m -> m.Caps) ModelCaps.minimal ModelCaps.codec

            and! cost = Codec.optionalField "cost" (fun m -> m.Cost) Cost.codec

            and! planBacked =
                Codec.defaultField "planBacked" (fun m -> m.PlanBacked) false Codec.bool

            return
                {
                    Ref = reference
                    Kind = kind
                    Family = family
                    Name = name
                    Limits = limits
                    Caps = caps
                    Cost = cost
                    PlanBacked = planBacked
                }
        }
        |> Codec.describe "A model in a catalog: its limits, capabilities and prices."
        |> Codec.named "ModelInfo"

    /// A chat model with minimal capabilities and the given limits.
    let create (reference: ModelRef) (limits: ModelLimits) : ModelInfo =
        {
            Ref = reference
            Kind = ModelKind.Chat
            Family = None
            Name = None
            Limits = limits
            Caps = ModelCaps.minimal
            Cost = None
            PlanBacked = false
        }
