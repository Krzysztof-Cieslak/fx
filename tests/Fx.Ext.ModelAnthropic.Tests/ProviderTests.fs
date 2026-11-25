/// The provider's data (catalog, quirks, auth), the extension's contributions, the replay scope, and
/// the wire behind `ModelAccess` with the catalog (fitting before the wire).
module Fx.Ext.ModelAnthropic.Tests.ProviderTests

open System.Threading
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Ext.ModelAnthropic
open Fx.Ext.ModelAnthropic.Tests.Support
open Fx.Models
open Fx.Sdk

type private ExtEnv =
    {
        Clock: Clock
        Scope: Scope
    }

    interface IExtensionEnv with
        member _.Extension = Anthropic.id

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
    let access = ModelAccess.create [ Anthropic.wire ] [ Provider.def ] http.Http clock

    let events =
        let stream = wait ((ModelAccess.llm access).Stream chat CancellationToken.None)
        let collected = wait (ChatStream.collect stream CancellationToken.None)
        ChatAggregate.result collected

    Expect.isOk events "answered"

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

                Expect.equal (ProviderDef.endpoint Provider.Family def) (Some "https://api.anthropic.com/v1") "endpoint"
                Expect.isTrue (ProviderDef.allows "https://api.anthropic.com/v1/messages" def) "messages"
                Expect.isFalse (ProviderDef.allows "https://api.anthropic.com/v2/messages" def) "elsewhere"
                Expect.equal (def.Auth |> List.map (fun a -> a.Kind)) [ "api_key"; "env" ] "auth"
                Expect.isTrue (ModelRef.isProviderId def.Id) "id"
            }

            test "every catalog model is a chat model of the family with prices and the documented limits" {
                Expect.isNonEmpty Provider.models "models"

                for model in Provider.models do
                    Expect.equal model.Ref.Provider Provider.Id $"{model.Ref}"
                    Expect.equal model.Family (Some Provider.Family) $"{model.Ref}"
                    Expect.isSome model.Cost $"{model.Ref}"
                    Expect.isTrue (model.Limits.Output < model.Limits.Context) $"{model.Ref}"
                    Expect.equal model.Caps.Caching.MaxBreakpoints 4 $"{model.Ref}"
                    Expect.isFalse model.Caps.Sampling.Temperature $"{model.Ref}: 2026-10-21 has no temperature"

                    match model.Caps.Reasoning with
                    | Some reasoning ->
                        Expect.isTrue reasoning.Replay $"{model.Ref}: signatures are replayed"

                        // A budget-only model; the others refuse budget_tokens.
                        match reasoning.Budget with
                        | Some budget -> Expect.isLessThan budget.Max model.Limits.Output $"{model.Ref}"
                        | None -> ()
                    | None -> failtest $"{model.Ref} reasons"

                let ids = Provider.models |> List.map (fun m -> m.Ref.Model)
                Expect.equal ids (List.distinct ids) "unique"

                let disables (id: string) =
                    (info (ModelRef.create Provider.Id id)).Caps.Reasoning.Value.Efforts
                    |> List.contains Effort.Off

                Expect.isFalse (disables "claude-opus-5-5") "Opus 5.5 refuses disabled thinking"
                Expect.isTrue (disables "claude-haiku-5-5") "Haiku 5.5 takes it"
                Expect.isFalse (info opus).Caps.Tools.Forced "Opus 5.5 refuses forced tool use"
            }

            test "settings: the quirks over the defaults; quirks of the wrong type are ignored" {
                Expect.equal (Provider.Settings.ofProvider Provider.def) Provider.Settings.defaults "the Claude API"

                let odd =
                    { Provider.def with
                        Quirks =
                            [
                                Provider.Quirk.Version, Json.int 3
                                Provider.Quirk.Betas, json """["x", 1, "y"]"""
                                Provider.Quirk.MaxCacheBreakpoints, Json.String "four"
                                Provider.Quirk.DefaultMaxTokens, Json.int 1000
                            ]
                    }

                Expect.equal
                    (Provider.Settings.ofProvider odd)
                    { Provider.Settings.defaults with
                        Betas = [ "x"; "y" ]
                        DefaultMaxTokens = 1000
                    }
                    "odd"

                Expect.isFalse (Provider.Settings.takesSampling Provider.Settings.defaults) "2026-10-21"

                Expect.isTrue
                    (Provider.Settings.takesSampling
                        { Provider.Settings.defaults with
                            Version = "2023-06-01"
                        })
                    "2023-06-01"
            }

            test "the extension contributes the wire and the provider, as its manifest declares" {
                Expect.isEmpty (Manifest.check Anthropic.manifest) "manifest"
                Expect.equal (string Anthropic.id) (Fx.Product.extensionId "model.anthropic") "id"

                let activation =
                    wait (
                        Eff.run
                            ({
                                Clock = (FakeClock.Create()).Clock
                                Scope = Scope.root
                            }
                            : ExtEnv)
                            (Eff.local (fun env -> env :> IExtensionEnv) Anthropic.extension.Activate)
                    )

                Expect.equal
                    (activation.Contributions |> List.map (fun c -> c.Type, c.Id))
                    (Anthropic.manifest.Contributions |> List.map (fun c -> c.Type, c.Id))
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

            test "the replay scope: thinking travels between the provider's models, not to other providers" {
                let policy = Anthropic.withReplay Replay.defaults
                Expect.equal (Anthropic.withReplay policy) policy "idempotent"

                let thought = state opus Request.SignatureKind "sig"

                Expect.isTrue (Replay.allows policy sonnet Provider.Family thought) "another Claude model"

                Expect.isFalse
                    (Replay.allows policy (ModelRef.create "gateway" "claude-opus-5-5") Provider.Family thought)
                    "another provider of the family"

                Expect.isFalse
                    (Replay.allows policy (ModelRef.create "openai" "gpt-6") "openai.responses" thought)
                    "another family"

                Expect.isFalse (Replay.allows Replay.defaults sonnet Provider.Family thought) "the default: same model"
            }

            test "through ModelAccess, requests are fitted to the catalog before the wire builds them" {
                let body =
                    sent
                        { ChatRequest.create opus [ Message.userText "Hi" ] with
                            Sampling =
                                { Sampling.none with
                                    Temperature = Some 0.3
                                }
                            Reasoning =
                                Some
                                    {
                                        Effort = Some Effort.Off
                                        BudgetTokens = Some 4000
                                        Visible = true
                                    }
                            Tools =
                                [
                                    {
                                        Name = "a"
                                        Description = ""
                                        Parameters = json """{"type": "object"}"""
                                        Strict = true
                                    }
                                ]
                            ToolChoice = ToolChoice.Required
                            MaxOutputTokens = Some 1_000_000
                        }

                // Opus 5.5: no temperature, thinking cannot be turned off (Off becomes low effort),
                // no budgets, no forced tool use, at most 128000 output tokens.
                Expect.equal (Json.tryField "max_tokens" body) (Some(Json.int 128_000)) "clamped"
                Expect.isNone (Json.tryField "temperature" body) "temperature"
                Expect.isNone (Json.tryField "tool_choice" body) "forced tool use relaxed"

                Expect.equal
                    (Json.tryField "thinking" body |> Option.map Json.serializeCanonical)
                    (Some """{"display":"summarized","type":"adaptive"}""")
                    "thinking"

                Expect.equal
                    (Json.tryField "output_config" body |> Option.map Json.serializeCanonical)
                    (Some """{"effort":"low"}""")
                    "effort"

                let haikuBody =
                    sent
                        { ChatRequest.create haiku45 [ Message.userText "Hi" ] with
                            Reasoning =
                                Some
                                    {
                                        Effort = Some Effort.High
                                        BudgetTokens = None
                                        Visible = false
                                    }
                        }

                Expect.equal
                    (Json.tryField "thinking" haikuBody |> Option.map Json.serializeCanonical)
                    (Some """{"budget_tokens":16384,"type":"enabled"}""")
                    "Haiku 4.5: a budget"

                Expect.equal (Json.tryField "max_tokens" haikuBody) (Some(Json.int 64_000)) "its output limit"
            }
        ]
