namespace Fx.Sdk

open Fx.Contracts
open Fx.Variants

/// Variant slots that extensions define (spec §15, docs/modules/variants.md): a native extension
/// contributes `Slot.info slot` and keeps the typed `Slot<'T>` to read it with; a declarative
/// manifest or an extension process declares an enumerated slot as JSON (`SlotDecl`). The host builds
/// its slot registry from `VariantSlots.slots`, which makes the contributing extension the owner, so
/// an extension cannot claim a core slot or another extension's.
[<RequireQualifiedAccess>]
module VariantSlots =
    /// The owner of a slot that is not in effect yet: `slots` replaces it.
    let private unowned = SlotOwner.Extension ""

    /// Variant slots, as data: their JSON form is the slot's declaration (`SlotInfo.declCodec`).
    let contributionType: ContributionType<SlotInfo> =
        let name = "variant-slot"

        {
            Name = name
            Description =
                "A variant slot: a configurable behaviour, selected through IVariants and recorded in profiles."
            Kind = ContributionKind.Data
            Privileged = false
            Schema = Some SlotInfo.declCodec.Schema
            ToJson = fun info -> Some(SlotInfo.declCodec.Encode(SlotInfo.decl info))
            OfJson =
                fun json _ ->
                    match json with
                    | None -> Error [ Issue.create "missing value" ]
                    | Some json -> SlotInfo.declCodec.Decode json |> Result.bind (SlotInfo.ofDecl unowned)
            Serve = None
        }

    /// The slots in effect, each owned by the extension that contributed it, in registration order.
    let slots (contributed: Contributed<SlotInfo> list) : SlotInfo list =
        contributed
        |> List.map (fun c -> SlotInfo.withOwner (SlotOwner.Extension c.Extension) c.Value)
