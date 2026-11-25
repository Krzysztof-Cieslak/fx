/// The OpenAI provider as data (spec §10.2 `ProviderDef`): the OpenAI API's endpoint, its auth
/// methods, its quirks, and a small bundled catalog of current OpenAI models (live discovery is
/// T208). The quirks are what the `openai.responses` wire reads from any provider of its family, so a
/// gateway or another server that speaks the Responses API is a provider definition, not code.
module Fx.Ext.ModelOpenAI.Provider

open Fx.Contracts
open Fx.Models

/// The wire family: the Responses API (`POST /v1/responses`).
[<Literal>]
let Family = "openai.responses"

/// The provider id of the OpenAI API: models are `openai/<model id>`.
[<Literal>]
let Id = "openai"

/// The OpenAI API's base URL for the family.
[<Literal>]
let Endpoint = "https://api.openai.com/v1"

/// The quirks the wire reads (`ProviderDef.Quirks`), by name.
[<RequireQualifiedAccess>]
module Quirk =
    /// A string: the organization to bill, sent as `OpenAI-Organization` (default none).
    [<Literal>]
    let Organization = "organization"

    /// A string: the project to bill, sent as `OpenAI-Project` (default none).
    [<Literal>]
    let Project = "project"

    /// A string: where the system prompt goes: `developer` (default), a developer message at the
    /// start of `input`, or `instructions`, the top-level field.
    [<Literal>]
    let System = "system"

    /// A boolean: whether cache breakpoints become `prompt_cache_breakpoint`s for every model (true)
    /// or for none (false). Without it, for the models whose catalog entry says they take them.
    [<Literal>]
    let CacheBreakpoints = "cacheBreakpoints"

/// Where the system prompt goes.
[<RequireQualifiedAccess>]
type SystemPlacement =
    /// A developer message at the start of `input`: part of the cacheable prefix, and it can carry
    /// cache breakpoints.
    | Developer
    /// The top-level `instructions`: models from GPT-6.2 put it after `input` and never cache it.
    | Instructions

    override this.ToString() =
        match this with
        | Developer -> "developer"
        | Instructions -> "instructions"

/// What the wire takes from a provider's quirks.
type Settings =
    {
        Organization: string option
        Project: string option
        System: SystemPlacement
        /// `None`: as the catalog says.
        CacheBreakpoints: bool option
    }

    override this.ToString() =
        let breakpoints =
            this.CacheBreakpoints |> Option.map string |> Option.defaultValue "catalog"

        $"openai settings (system {this.System}, cache breakpoints {breakpoints})"

module Settings =
    let defaults: Settings =
        {
            Organization = None
            Project = None
            System = SystemPlacement.Developer
            CacheBreakpoints = None
        }

    let private text (json: Json) =
        match json with
        | Json.String s when s <> "" -> Some s
        | _ -> None

    let private flag (json: Json) =
        match json with
        | Json.Bool b -> Some b
        | _ -> None

    let private placement (json: Json) =
        match json with
        | Json.String "developer" -> Some SystemPlacement.Developer
        | Json.String "instructions" -> Some SystemPlacement.Instructions
        | _ -> None

    /// The settings of a provider: its quirks over the defaults (a quirk of the wrong type or value
    /// is ignored).
    let ofProvider (provider: ProviderDef) : Settings =
        let quirk name read =
            ProviderDef.quirk name provider |> Option.bind read

        {
            Organization = quirk Quirk.Organization text
            Project = quirk Quirk.Project text
            System = quirk Quirk.System placement |> Option.defaultValue defaults.System
            CacheBreakpoints = quirk Quirk.CacheBreakpoints flag
        }

/// Capabilities of a current reasoning model with these efforts. `explicit`: it takes explicit cache
/// breakpoints (GPT-5.6 and later); earlier models cache only at breakpoints of their own.
let private reasoning (efforts: Effort list) (explicit: bool) : ModelCaps =
    {
        Reasoning =
            Some
                {
                    Efforts = efforts
                    Budget = None
                    // Summaries (`reasoning.summary`).
                    Visible = true
                    // Encrypted reasoning items, replayed statelessly.
                    Replay = true
                }
        Tools =
            {
                Calls = true
                Parallel = true
                Forced = true
                Strict = true
            }
        StructuredOutput = true
        Logprobs = false
        Caching =
            if explicit then
                {
                    Mode = CacheMode.Breakpoints
                    // The API looks for matches at up to the latest 80 breakpoints of a conversation
                    // and writes the latest few; it refuses none.
                    MaxBreakpoints = 80
                    Key = true
                    // `ttl: "12h"`.
                    LongTtl = true
                }
            else
                {
                    Mode = CacheMode.Automatic
                    MaxBreakpoints = 0
                    Key = true
                    LongTtl = false
                }
        Media = [ MediaKind.Image; MediaKind.Pdf ]
        // Reasoning models take no sampling parameters, and the Responses API has no stop sequences
        // or seeds.
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

let private model
    (id: string)
    (name: string)
    (caps: ModelCaps)
    (input: decimal, output: decimal, cacheRead: decimal, cacheWrite: decimal option)
    : ModelInfo =
    {
        Ref = ModelRef.create Id id
        Kind = ModelKind.Chat
        Family = Some Family
        Name = Some name
        Limits =
            {
                Context = 1_050_000
                Output = 128_000
            }
        Caps = caps
        Cost =
            Some
                {
                    Input = input
                    Output = output
                    CacheRead = Some cacheRead
                    CacheWrite = cacheWrite
                }
        PlanBacked = false
    }

let private withNone =
    [ Effort.Off; Effort.Low; Effort.Medium; Effort.High; Effort.Max ]

let private withoutNone = [ Effort.Low; Effort.Medium; Effort.High; Effort.Max ]

/// The bundled catalog: current OpenAI models, with limits, capabilities and prices (dollars per
/// million tokens, for prompts of up to 272,000 input tokens; longer ones cost twice as much for
/// input and 1.5 times for output; cache writes at the 30-minute rate) as OpenAI documents them in
/// November 2026. `xhigh` effort is not reachable from the canonical efforts. Discovery
/// (`/v1/models`) and probes are T208's.
let models: ModelInfo list =
    [
        model "gpt-6.2-astra" "GPT-6.2 Astra" (reasoning withoutNone true) (8m, 40m, 0.40m, Some 10m)
        model "gpt-6.2-sol" "GPT-6.2 Sol" (reasoning withoutNone true) (2m, 10m, 0.10m, Some 2.50m)
        model "gpt-6.1-terra" "GPT-6.1 Terra" (reasoning withNone true) (1m, 5m, 0.05m, Some 1.25m)
        model "gpt-6-luna" "GPT-6 Luna" (reasoning withNone true) (0.10m, 0.50m, 0.01m, Some 0.125m)
        // Before GPT-5.6: implicit caching only, no cache writes billed.
        model
            "gpt-5.5"
            "GPT-5.5"
            (reasoning [ Effort.Off; Effort.Low; Effort.Medium; Effort.High ] false)
            (5m, 30m, 0.50m, None)
    ]

/// The OpenAI API as a provider: its endpoint, the catalog, and API keys (`Authorization: Bearer`,
/// typed in or from `OPENAI_API_KEY`; the methods are T121's). The organization and project headers
/// are quirks a host's configuration adds.
let def: ProviderDef =
    {
        Id = Id
        Name = "OpenAI"
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
                    Settings = Json.Object [ "header", Json.String "authorization"; "scheme", Json.String "Bearer" ]
                }
                {
                    Kind = "env"
                    Settings =
                        Json.Object
                            [
                                "variable", Json.String "OPENAI_API_KEY"
                                "header", Json.String "authorization"
                                "scheme", Json.String "Bearer"
                            ]
                }
            ]
        Quirks = []
        Transform = None
    }
