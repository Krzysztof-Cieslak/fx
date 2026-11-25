namespace Fx.Variants

open System
open Fx.Contracts

/// One axis of an eval matrix: a slot and the values to try, in order.
type Axis =
    {
        Slot: string
        Values: Json list
    }

    override this.ToString() =
        let values = String.Join(", ", this.Values |> List.map Json.serialize)
        $"{this.Slot} in [{values}]"

/// Eval matrices (spec §15): the combinations of slot values an eval suite runs. Each combination is
/// an explicit layer; resolving it with the base layers and policies gives one profile per
/// combination, and its issues if a combination is not allowed.
module Matrix =
    /// An axis over chosen values of a slot.
    let axis (slot: Slot<'T>) (values: 'T list) : Axis =
        {
            Slot = slot.Id.Value
            Values = values |> List.map (Slot.encode slot)
        }

    /// An axis over every choice of an enumerated slot; `None` for an open slot.
    let choices (slot: SlotInfo) : Axis option =
        slot.Choices
        |> Option.map (fun values ->
            {
                Slot = slot.Id.Value
                Values = values
            })

    /// Every combination of the axes, the first axis varying slowest, as explicit layers. The origin of
    /// each names the matrix and the combination: `smoke: agent.actionSurface="hybrid", edit.guard="warn"`.
    /// No axes give one empty combination; an axis without values gives none.
    let expand (name: string) (axes: Axis list) : Selections list =
        let combinations =
            List.foldBack
                (fun (axis: Axis) (rest: (string * Json) list list) ->
                    [
                        for value in axis.Values do
                            for tail in rest -> (axis.Slot, value) :: tail
                    ])
                axes
                [ [] ]

        combinations
        |> List.map (fun values ->
            let label =
                String.Join(", ", values |> List.map (fun (slot, value) -> slot + "=" + Json.serialize value))

            Selections.create (Source.create Layer.Explicit $"{name}: {label}") values)
