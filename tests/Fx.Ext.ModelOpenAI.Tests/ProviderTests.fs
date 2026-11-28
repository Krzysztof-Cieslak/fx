/// The provider's data (catalog, quirks, auth), the extension's contributions, the replay scope, and
/// the wire behind `ModelAccess` with the catalog (fitting before the wire).
module Fx.Ext.ModelOpenAI.Tests.ProviderTests

open System.Threading
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Ext.ModelOpenAI
open Fx.Ext.ModelOpenAI.Tests.Support
open Fx.Models
open Fx.Sdk

type private ExtEnv =
    {
        Clock: Clock
        Scope: Scope
    }

    interface IExtensionEnv with
        member _.Extension = OpenAI.id
        member _.Capabilities = ExtensionCapabilities.none OpenAI.id

    interface Fx.Variants.IVariants with
        member _.Variants = (ExtensionCapabilities.none OpenAI.id).Variants

    interface IPackages with
        member _.Packages = (ExtensionCapabilities.none OpenAI.id).Packages

    interface IGrants with
        member _.Grants = (ExtensionCapabilities.none OpenAI.id).Grants

    interface IClock with
        member env.Clock = env.Clock

    interface IScopeWith<IExtensionEnv> with
        member env.Scope = env.Scope

        member env.WithScope scope =
            { env with Scope = scope } :> IExtensionEnv

/// Runs a request through `ModelAccess` with the provider's catalog, answering with a short stream;
/// returns the body the wire sent.
let private sent (chat: ChatRequest) : Json =
    let clock = (FakeClock.Create()).Clock

    let reply =
        HttpReply.text 200 (source "text.sse")
        |> HttpReply.withHeader "content-type" "text/event-stream"

    let http = InMemoryHttp(clock, (fun _ -> Some(Ok reply)))
    let access = ModelAccess.create [ OpenAI.wire ] [ Provider.def ] http.Http clock

    let result =
        let stream = wait ((ModelAccess.llm access).Stream chat CancellationToken.None)
        let collected = wait (ChatStream.collect stream CancellationToken.None)
        ChatAggregate.result collected

    Expect.isOk result "answered"

    match http.Requests with
    | [ request ] -> json (HttpRequest.bodyText request)
    | other -> failtest $"{other.Length} requests"

[<Tests>]
let tests =
    testList
        "provider"
        [
            test "the provider round-trips through its codec and has its endpoint and auth methods" {
                let def = Provider.def

                Expect.equal
                    (Codec.decodeText ProviderDef.codec (Codec.encodeText ProviderDef.codec def))
                    (Ok def)
                    "round trip"

                Expect.equal (ProviderDef.endpoint Provider.Family def) (Some "https://api.openai.com/v1") "endpoint"
                Expect.isTrue (ProviderDef.allows "https://api.openai.com/v1/responses" def) "responses"
                Expect.isFalse (ProviderDef.allows "https://api.openai.com/v2/responses" def) "elsewhere"
                Expect.equal (def.Auth |> List.map (fun a -> a.Kind)) [ "api_key"; "env" ] "auth"

                Expect.equal
                    (def.Auth[1].Settings |> Json.tryField "variable")
                    (Some(Json.String "OPENAI_API_KEY"))
                    "the environment variable"

                Expect.isTrue (ModelRef.isProviderId def.Id) "id"
                Expect.equal def.Catalog.Discover (Some "/models") "discovery"
            }

            test "every catalog model is a reasoning chat model of the family with prices and the documented limits" {
                Expect.equal
                    (Provider.models |> List.map (fun m -> m.Ref.Model))
                    [ "gpt-6.2-astra"; "gpt-6.2-sol"; "gpt-6.1-terra"; "gpt-6-luna"; "gpt-5.5" ]
                    "the models"

                for model in Provider.models do
                    Expect.equal model.Ref.Provider Provider.Id $"{model.Ref}"
                    Expect.equal model.Family (Some Provider.Family) $"{model.Ref} family"
                    Expect.equal model.Kind ModelKind.Chat $"{model.Ref} kind"

                    Expect.equal
                        model.Limits
                        {
                            Context = 1_050_000
                            Output = 128_000
                        }
                        $"{model.Ref} limits"

                    Expect.isSome model.Cost $"{model.Ref} prices"
                    Expect.isTrue model.Caps.Tools.Strict $"{model.Ref} strict tools"
                    Expect.isTrue model.Caps.Caching.Key $"{model.Ref} cache keys"

                    let reasoning = model.Caps.Reasoning.Value
                    Expect.isTrue (reasoning.Replay && reasoning.Visible) $"{model.Ref} replays and summarizes"
                    Expect.isNone reasoning.Budget $"{model.Ref} no budgets"

                let caching (model: ModelRef) = (info model).Caps.Caching.Mode
                Expect.equal (caching sol) CacheMode.Breakpoints "GPT-6.2 Sol takes breakpoints"
                Expect.equal (caching gpt55) CacheMode.Automatic "GPT-5.5 does not"

                Expect.isFalse
                    ((info astra).Caps.Reasoning.Value.Efforts |> List.contains Effort.Off)
                    "GPT-6.2 Astra rejects none"

                Expect.isTrue
                    ((info luna).Caps.Reasoning.Value.Efforts |> List.contains Effort.Off)
                    "GPT-6 Luna takes none"

                let used =
                    {
                        Input = 2_000_000
                        Output = 1_000_000
                        CacheRead = 1_000_000
                        CacheWrite = 500_000
                        Reasoning = 0
                    }

                // GPT-6.2 Sol: 500,000 uncached at $2, 1M cached at $0.10, 500,000 written at $2.50,
                // 1M out at $10.
                Expect.equal (Usage.cost (info sol).Cost.Value used) 12.35m "a priced usage"
            }

            test "settings: the quirks over the defaults; quirks of the wrong type are ignored" {
                Expect.equal (Provider.Settings.ofProvider Provider.def) Provider.Settings.defaults "the defaults"

                let settings =
                    Provider.Settings.ofProvider
                        { Provider.def with
                            Quirks =
                                [
                                    Provider.Quirk.Organization, Json.String "org-1"
                                    Provider.Quirk.Project, Json.String "proj_1"
                                    Provider.Quirk.System, Json.String "instructions"
                                    Provider.Quirk.CacheBreakpoints, Json.Bool true
                                ]
                        }

                Expect.equal
                    settings
                    {
                        Organization = Some "org-1"
                        Project = Some "proj_1"
                        System = Provider.SystemPlacement.Instructions
                        CacheBreakpoints = Some true
                    }
                    "all set"

                let wrong =
                    Provider.Settings.ofProvider
                        { Provider.def with
                            Quirks =
                                [
                                    Provider.Quirk.Organization, Json.int 1
                                    Provider.Quirk.Project, Json.String ""
                                    Provider.Quirk.System, Json.String "user"
                                    Provider.Quirk.CacheBreakpoints, Json.String "yes"
                                ]
                        }

                Expect.equal wrong Provider.Settings.defaults "ignored"
            }

            test "the extension contributes the wire and the provider, as its manifest declares" {
                Expect.isEmpty (Manifest.check OpenAI.manifest) "manifest"
                Expect.equal (string OpenAI.id) (Fx.Product.extensionId "model.openai") "id"

                let activation =
                    wait (
                        Eff.run
                            ({
                                Clock = (FakeClock.Create()).Clock
                                Scope = Scope.root
                            }
                            : ExtEnv)
                            (Eff.local (fun env -> env :> IExtensionEnv) OpenAI.extension.Activate)
                    )

                Expect.equal
                    (activation.Contributions |> List.map (fun c -> c.Type, c.Id))
                    (OpenAI.manifest.Contributions |> List.map (fun c -> c.Type, c.Id))
                    "as declared"

                Expect.equal
                    (activation.Contributions
                     |> List.choose (Contribution.tryValue ChatWires.contributionType)
                     |> List.map (fun w -> w.Family))
                    [ Provider.Family ]
                    "the wire"

                Expect.equal
                    (activation.Contributions
                     |> List.choose (Contribution.tryValue ProviderDefs.contributionType))
                    [ Provider.def ]
                    "the provider"
            }

            test "the replay scope: reasoning travels between the provider's models, not to other providers" {
                let policy = OpenAI.withReplay Replay.defaults
                Expect.equal (OpenAI.withReplay policy) policy "idempotent"

                let reasoning = state astra Request.ReasoningKind "{}"

                Expect.isTrue (Replay.allows policy sol Provider.Family reasoning) "another OpenAI model"

                Expect.isFalse
                    (Replay.allows policy (ModelRef.create "azure" "gpt-6.2-astra") Provider.Family reasoning)
                    "another provider of the family"

                Expect.isFalse
                    (Replay.allows policy (ModelRef.create "anthropic" "claude-opus-5-5") "anthropic.messages" reasoning)
                    "another family"

                Expect.isFalse (Replay.allows Replay.defaults sol Provider.Family reasoning) "the default: same model"

                // A switch to another family drops the reasoning and the phase, and keeps the text.
                let messages, changes =
                    Replay.prepare
                        policy
                        (ModelRef.create "anthropic" "claude-opus-5-5")
                        "anthropic.messages"
                        [
                            Message.assistant
                                [
                                    Part.Reasoning
                                        {
                                            Text = "summary"
                                            State = Some reasoning
                                        }
                                    Part.Text
                                        {
                                            Text = "Done."
                                            State = Some(state astra Request.PhaseKind "final_answer")
                                            Sources = []
                                        }
                                ]
                        ]

                Expect.equal messages [ Message.assistant [ Part.text "Done." ] ] "the text"
                Expect.equal changes.Length 2 "two changes"
            }

            test "through ModelAccess, requests are fitted to the catalog before the wire builds them" {
                let body =
                    sent
                        { ChatRequest.create astra [ Message.userText "Hi" ] with
                            Sampling =
                                { Sampling.none with
                                    Temperature = Some 0.3
                                    Stop = [ "###" ]
                                }
                            Reasoning =
                                Some
                                    {
                                        Effort = Some Effort.Off
                                        BudgetTokens = Some 4000
                                        Visible = true
                                    }
                            MaxOutputTokens = Some 1_000_000
                            Cache = { Key = Some "k"; Tools = None }
                        }

                // GPT-6.2 Astra: no temperature, no `none` effort (Off becomes low), no budgets, at
                // most 128000 output tokens; the cache key goes.
                Expect.equal (Json.tryField "max_output_tokens" body) (Some(Json.int 128_000)) "clamped"
                Expect.isNone (Json.tryField "temperature" body) "temperature"
                Expect.equal (Json.tryField "prompt_cache_key" body) (Some(Json.String "k")) "key"

                Expect.equal
                    (Json.tryField "reasoning" body |> Option.map Json.serializeCanonical)
                    (Some """{"effort":"low","summary":"auto"}""")
                    "reasoning"

                let luna =
                    sent
                        { ChatRequest.create luna [ Message.userText "Hi" ] with
                            Reasoning =
                                Some
                                    {
                                        Effort = Some Effort.Minimal
                                        BudgetTokens = None
                                        Visible = false
                                    }
                        }

                Expect.equal
                    (Json.tryField "reasoning" luna |> Option.map Json.serializeCanonical)
                    (Some """{"effort":"low"}""")
                    "GPT-6 Luna: minimal becomes low"
            }
        ]
