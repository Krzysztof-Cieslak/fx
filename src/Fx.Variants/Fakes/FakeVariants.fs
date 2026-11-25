namespace Fx.Variants.Fakes

open System
open Fx.Contracts
open Fx.Variants

/// Variants for tests and `fx test` environments: slots at their defaults, with selections on top.
module FakeVariants =
    /// The resolution of these slots with these selections as an explicit layer (`Selections.select`
    /// makes typed ones). Throws if the slots or the selections are invalid (a defect in the setup).
    let resolution (slots: SlotInfo list) (selections: (string * Json) list) : Resolution =
        let registry = SlotRegistry.ofSlots slots

        match Resolution.resolve registry [] [ Selections.create (Source.create Layer.Explicit "test") selections ] with
        | Ok resolution -> resolution
        | Error issues -> invalidArg (nameof selections) (String.Join("\n", issues |> List.map string))

    /// Variants of these slots with these selections.
    let create (slots: SlotInfo list) (selections: (string * Json) list) : Variants =
        Variants.ofResolution (resolution slots selections)

    /// The core slots (`Slots.all`) with these selections.
    let core (selections: (string * Json) list) : Variants = create Slots.all selections
