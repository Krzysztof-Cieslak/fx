/// `fx.model.openai` (spec §10.1, §16.4): the `openai.responses` chat wire (the Responses API,
/// streamed and stateless) and the OpenAI API as a provider. A native extension built only on the
/// public SDK: the wire is `SseWire.create` over the pure parts in `Request`, `Decoder` and `Errors`,
/// and the provider is data (`Provider.def`). See README.md and docs/modules/models.md.
module Fx.Ext.ModelOpenAI.OpenAI

open Fx
open Fx.Effects
open Fx.Models
open Fx.Sdk

/// The wire family.
[<Literal>]
let Family = Provider.Family

/// The adapter, as pure functions.
let adapter: SseAdapter<Decoder.State> =
    {
        Family = Family
        Request = Request.http
        Error = Errors.ofResponse
        Initial = Decoder.initial
        Decode = Decoder.decode
        End = fun _ -> []
    }

/// The wire.
let wire: ChatWire = SseWire.create adapter

/// How far the family's provider state may travel when a conversation moves to another model
/// (spec §10.3). The OpenAI API itself leaves out encrypted reasoning a model cannot use (another
/// model family, a less capable model, another organization or end user, an edited prefix) and says
/// so in the response's `input_transformations`, without an error; so within one provider reasoning
/// items go to every model, and the API decides. Not across providers: another provider of the family
/// (a gateway, a compatible server, another cloud) may refuse OpenAI's encrypted items or another's.
/// `phase` state is harmless anywhere in the family, but travels with the same rule.
let replayScope: ReplayScope = ReplayScope.SameProvider

/// The replay policy with this family's scope (what the host's `ModelAccess` uses, T129).
let withReplay (policy: ReplayPolicy) : ReplayPolicy =
    { policy with
        Families =
            (Family, replayScope)
            :: (policy.Families |> List.filter (fun (family, _) -> family <> Family))
    }

/// The extension's id: `fx.model.openai`.
let id = Product.extensionId "model.openai"

let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description = Some "OpenAI's Responses API (openai.responses) and the OpenAI API as a model provider."
        Contributions =
            [
                Declare.contribution ChatWires.contributionType Family
                Declare.contribution ProviderDefs.contributionType Provider.Id
            ]
    }

let extension: Extension =
    {
        Manifest = manifest
        Activate =
            Eff.ret (
                Activation.create
                    [
                        Contribution.create ChatWires.contributionType Family wire
                        Contribution.create ProviderDefs.contributionType Provider.Id Provider.def
                    ]
                    []
            )
    }
