/// `fx.model.anthropic` (spec §10.1, §16.4): the `anthropic.messages` chat wire (the Messages API,
/// streamed) and the Claude API as a provider. A native extension built only on the public SDK: the
/// wire is `SseWire.create` over the pure parts in `Request`, `Decoder` and `Errors`, and the provider
/// is data (`Provider.def`). See README.md and docs/modules/models.md.
module Fx.Ext.ModelAnthropic.Anthropic

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
/// (spec §10.3). The Claude API reads thinking blocks from the models that can read them and drops
/// the others without an error (Anthropic: "keep passing thinking blocks back unchanged when you
/// switch models"), so within one provider they go to every model. Not across providers: another
/// provider of the family may be a gateway or a third party's Messages-compatible API that does not
/// verify Claude signatures, and thinking from some models is bound to the account that produced it.
/// A host whose providers of the family are all Anthropic platforms may widen it to `sameFamily`.
let replayScope: ReplayScope = ReplayScope.SameProvider

/// The replay policy with this family's scope (what the host's `ModelAccess` uses, T129).
let withReplay (policy: ReplayPolicy) : ReplayPolicy =
    { policy with
        Families =
            (Family, replayScope)
            :: (policy.Families |> List.filter (fun (family, _) -> family <> Family))
    }

/// The extension's id: `fx.model.anthropic`.
let id = Product.extensionId "model.anthropic"

let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description = Some "Anthropic's Messages API (anthropic.messages) and the Claude API as a model provider."
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
