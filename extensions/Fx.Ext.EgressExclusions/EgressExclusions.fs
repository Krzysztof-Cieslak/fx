/// `fx.egress.exclusions` (spec §16.4, §18): content exclusions on every model request. A native
/// extension built only on the public SDK: it contributes the exclusions' variant slots and a
/// model-egress transform and gate (`ContentExclusions.hooks`). Content is matched by the
/// provenance of request parts (`ContentSource`); see docs/modules/egress.md.
module Fx.Ext.EgressExclusions.EgressExclusions

open Fx
open Fx.Effects
open Fx.Sdk

/// The extension's id: `fx.egress.exclusions`.
let id = Product.extensionId "egress.exclusions"

/// The id of its transform and its gate.
[<Literal>]
let HookId = "exclusions"

let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description = Some "Content exclusions: keeps the content of excluded files out of every model request."
        Capabilities = [ Capability.Variants ]
        Contributions =
            (ContentExclusions.slots
             |> List.map (fun slot -> Declare.contribution VariantSlots.contributionType (string slot.Id)))
            @ [
                Declare.contribution ModelEgress.transforms HookId
                Declare.contribution ModelEgress.gates HookId
            ]
    }

/// The extension with the settings it reads for each request, in its environment (tests pass fixed
/// settings).
let create (settings: Eff<IExtensionEnv, ExclusionSettings>) : Extension =
    {
        Manifest = manifest
        Activate =
            eff {
                let transform, gate = ContentExclusions.hooks settings

                return
                    Activation.create
                        ((ContentExclusions.slots
                          |> List.map (fun slot ->
                              Contribution.create VariantSlots.contributionType (string slot.Id) slot))
                         @ [
                             Contribution.create ModelEgress.transforms HookId transform
                             Contribution.create ModelEgress.gates HookId gate
                         ])
                        []
            }
    }

/// The built-in: it reads its own slots for each request (`ContentExclusions.read`, through the
/// `variants` capability), as the host resolved them.
let extension: Extension = create ContentExclusions.read
