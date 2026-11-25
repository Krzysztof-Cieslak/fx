/// The Anthropic provider as data (spec §10.2 `ProviderDef`): the Claude API's endpoint, its auth
/// methods, its quirks, and a small bundled catalog of current Claude models (live discovery is
/// T208). The quirks are what the `anthropic.messages` wire reads from any provider of its family, so
/// a gateway or another platform that speaks the Messages API is a provider definition, not code.
module Fx.Ext.ModelAnthropic.Provider

open Fx.Contracts
open Fx.Models

/// The wire family: the Messages API (`POST /v1/messages`).
[<Literal>]
let Family = "anthropic.messages"

/// The provider id of the Claude API: models are `anthropic/<model id>`.
[<Literal>]
let Id = "anthropic"

/// The Claude API's base URL for the family.
[<Literal>]
let Endpoint = "https://api.anthropic.com/v1"

/// The API version the wire speaks (`anthropic-version`), unless a provider's quirk names another.
/// `2026-10-21` removed `temperature`, `top_p` and `top_k`.
[<Literal>]
let ApiVersion = "2026-10-21"

/// The first API version without the sampling parameters.
[<Literal>]
let SamplingRemovedIn = "2026-10-21"

/// The quirks the wire reads (`ProviderDef.Quirks`), by name.
[<RequireQualifiedAccess>]
module Quirk =
    /// A string: the `anthropic-version` header (default `ApiVersion`).
    [<Literal>]
    let Version = "anthropicVersion"

    /// An array of strings: beta features, sent as `anthropic-beta` (default none).
    [<Literal>]
    let Betas = "betas"

    /// A number: the most `cache_control` breakpoints a request may have (default 4).
    [<Literal>]
    let MaxCacheBreakpoints = "maxCacheBreakpoints"

    /// A number: `max_tokens` when neither the request nor the catalog sets an output limit
    /// (default 16384).
    [<Literal>]
    let DefaultMaxTokens = "defaultMaxTokens"

/// What the wire takes from a provider's quirks.
type Settings =
    {
        Version: string
        Betas: string list
        MaxCacheBreakpoints: int
        DefaultMaxTokens: int
    }

    override this.ToString() =
        $"anthropic settings (version {this.Version}, {this.Betas.Length} betas)"

module Settings =
    let defaults: Settings =
        {
            Version = ApiVersion
            Betas = []
            MaxCacheBreakpoints = 4
            DefaultMaxTokens = 16384
        }

    let private text (json: Json) =
        match json with
        | Json.String s when s <> "" -> Some s
        | _ -> None

    let private count (json: Json) =
        match json with
        | Json.Number n ->
            match JsonNumber.tryToDecimal n with
            | Some d when d >= 0m && d <= 1_000_000_000m && d = System.Math.Floor d -> Some(int d)
            | _ -> None
        | _ -> None

    let private strings (json: Json) =
        match json with
        | Json.Array items -> Some(items |> List.choose text)
        | _ -> None

    /// The settings of a provider: its quirks over the defaults (a quirk of the wrong type is
    /// ignored).
    let ofProvider (provider: ProviderDef) : Settings =
        let quirk name read =
            ProviderDef.quirk name provider |> Option.bind read

        {
            Version = quirk Quirk.Version text |> Option.defaultValue defaults.Version
            Betas = quirk Quirk.Betas strings |> Option.defaultValue defaults.Betas
            MaxCacheBreakpoints =
                quirk Quirk.MaxCacheBreakpoints count
                |> Option.defaultValue defaults.MaxCacheBreakpoints
            DefaultMaxTokens =
                quirk Quirk.DefaultMaxTokens count
                |> Option.filter (fun n -> n > 0)
                |> Option.defaultValue defaults.DefaultMaxTokens
        }

    /// Whether the version still takes `temperature`, `top_p` and `top_k` (versions are dates).
    let takesSampling (settings: Settings) : bool =
        System.String.CompareOrdinal(settings.Version, SamplingRemovedIn) < 0

let private efforts = [ Effort.Low; Effort.Medium; Effort.High; Effort.Max ]

/// Capabilities of a model with adaptive thinking (effort-driven, `budget_tokens` refused).
/// `canDisable`: it takes `thinking: {type: "disabled"}` (the `Off` effort). `forced`: it takes
/// forced tool use (`tool_choice` `any` or `tool`).
let private adaptive (canDisable: bool) (forced: bool) : ModelCaps =
    {
        Reasoning =
            Some
                {
                    Efforts = (if canDisable then Effort.Off :: efforts else efforts)
                    Budget = None
                    Visible = true
                    Replay = true
                }
        Tools =
            {
                Calls = true
                Parallel = true
                Forced = forced
                Strict = true
            }
        StructuredOutput = true
        Logprobs = false
        Caching =
            {
                Mode = CacheMode.Breakpoints
                MaxBreakpoints = 4
                Key = false
                LongTtl = true
            }
        Media = [ MediaKind.Image; MediaKind.Pdf ]
        // API version 2026-10-21 refuses temperature, top_p and top_k on every model.
        Sampling =
            {
                Temperature = false
                TopP = false
                TopK = false
                Seed = false
                Stop = true
            }
        Transports = [ ModelTransport.Sse ]
    }

/// Capabilities of a model with extended thinking only (`budget_tokens`, no `adaptive`): the wire
/// turns an effort into a budget.
let private budgeted (budget: TokenRange) : ModelCaps =
    { adaptive true true with
        Reasoning =
            Some
                {
                    Efforts = Effort.Off :: efforts
                    Budget = Some budget
                    Visible = true
                    Replay = true
                }
    }

let private model
    (id: string)
    (name: string)
    (context: int)
    (output: int)
    (caps: ModelCaps)
    (input: decimal, outputPrice: decimal, cacheRead: decimal, cacheWrite: decimal)
    : ModelInfo =
    {
        Ref = ModelRef.create Id id
        Kind = ModelKind.Chat
        Family = Some Family
        Name = Some name
        Limits = { Context = context; Output = output }
        Caps = caps
        Cost =
            Some
                {
                    Input = input
                    Output = outputPrice
                    CacheRead = Some cacheRead
                    CacheWrite = Some cacheWrite
                }
        PlanBacked = false
    }

/// The bundled catalog: current Claude models, with limits, capabilities and prices (dollars per
/// million tokens; cache writes at the 5-minute rate) as Anthropic documents them in November 2026.
/// Discovery (`/v1/models`) and probes are T208's.
let models: ModelInfo list =
    [
        model "claude-fable-5-2" "Claude Fable 5.2" 1_000_000 128_000 (adaptive false false) (8m, 40m, 0.20m, 10m)
        model "claude-opus-5-5" "Claude Opus 5.5" 1_000_000 128_000 (adaptive false false) (4m, 20m, 0.20m, 5m)
        model "claude-sonnet-5-5" "Claude Sonnet 5.5" 1_000_000 128_000 (adaptive false false) (2m, 10m, 0.10m, 2.50m)
        // Prices for prompts up to 100,000 tokens (longer ones cost five times as much).
        model "claude-haiku-5-5" "Claude Haiku 5.5" 1_000_000 128_000 (adaptive true true) (0.10m, 0.50m, 0.01m, 0.125m)
        model "claude-opus-5" "Claude Opus 5" 1_000_000 128_000 (adaptive true true) (5m, 25m, 0.50m, 6.25m)
        model "claude-sonnet-5" "Claude Sonnet 5" 1_000_000 128_000 (adaptive true true) (2m, 10m, 0.20m, 2.50m)
        model "claude-sonnet-4-6" "Claude Sonnet 4.6" 1_000_000 128_000 (adaptive true true) (3m, 15m, 0.30m, 3.75m)
        model
            "claude-haiku-4-5"
            "Claude Haiku 4.5"
            200_000
            64_000
            (budgeted { Min = 1024; Max = 63_999 })
            (1m, 5m, 0.10m, 1.25m)
    ]

/// The Claude API as a provider: its endpoint, the catalog, API keys (`x-api-key`, typed in or from
/// `ANTHROPIC_API_KEY`; the methods are T121's) and the API version.
let def: ProviderDef =
    {
        Id = Id
        Name = "Anthropic"
        Endpoints = [ Family, Endpoint ]
        Catalog =
            {
                Models = models
                Discover = Some "/models"
            }
        Auth =
            [
                {
                    Kind = "api_key"
                    Settings = Json.Object [ "header", Json.String "x-api-key" ]
                }
                {
                    Kind = "env"
                    Settings =
                        Json.Object
                            [
                                "variable", Json.String "ANTHROPIC_API_KEY"
                                "header", Json.String "x-api-key"
                            ]
                }
            ]
        Quirks =
            [
                Quirk.Version, Json.String ApiVersion
                Quirk.MaxCacheBreakpoints, Json.int Settings.defaults.MaxCacheBreakpoints
            ]
        Transform = None
    }
