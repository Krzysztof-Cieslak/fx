namespace Fx.Variants

open System
open Fx.Contracts

/// Why a set of slots cannot form a registry. Each is a defect in the code or the extension that
/// defined the slot; the host reports it with the slot's owner.
type RegistryIssue =
    /// Two slots with one id.
    | DuplicateSlot of slot: SlotId * first: SlotOwner * second: SlotOwner
    /// The default does not pass the slot's own check.
    | InvalidDefault of slot: SlotId * owner: SlotOwner * issues: Issue list
    /// A choice does not pass the slot's own check.
    | InvalidChoice of slot: SlotId * owner: SlotOwner * choice: Json * issues: Issue list
    /// An enumerated slot without choices, or with the same choice twice.
    | InvalidChoices of slot: SlotId * owner: SlotOwner * reason: string

    override this.ToString() =
        let render (issues: Issue list) = String.Join("; ", issues)

        match this with
        | DuplicateSlot(slot, first, second) -> $"slot {slot} is defined twice, by {first} and by {second}"
        | InvalidDefault(slot, owner, issues) -> $"slot {slot} ({owner}): its default is invalid: {render issues}"
        | InvalidChoice(slot, owner, choice, issues) ->
            $"slot {slot} ({owner}): its choice {choice} is invalid: {render issues}"
        | InvalidChoices(slot, owner, reason) -> $"slot {slot} ({owner}): {reason}"

/// The variant registry (spec §3): every slot this host knows, in registration order. The core
/// registers its own slots and those that extensions contribute; resolution and profiles cover
/// exactly these. Immutable: build a new one when extensions change.
[<Sealed>]
type SlotRegistry private (slots: SlotInfo list, byId: Map<SlotId, SlotInfo>) =
    /// The slots in registration order.
    member _.Slots = slots

    member _.TryFind(id: SlotId) : SlotInfo option = byId.TryFind id

    member _.Contains(id: SlotId) = byId.ContainsKey id

    member _.Count = slots.Length

    override _.ToString() = $"SlotRegistry({slots.Length} slots)"

    static member internal Create(slots: SlotInfo list) =
        SlotRegistry(slots, slots |> List.map (fun s -> s.Id, s) |> Map.ofList)

module SlotRegistry =
    /// A registry without slots.
    let empty: SlotRegistry = SlotRegistry.Create []

    let private checkSlot (slot: SlotInfo) : RegistryIssue list =
        let defaultIssues =
            match slot.Check slot.Default with
            | Ok _ -> []
            | Error issues -> [ InvalidDefault(slot.Id, slot.Owner, issues) ]

        let choiceIssues =
            match slot.Choices with
            | None -> []
            | Some [] ->
                [
                    InvalidChoices(slot.Id, slot.Owner, "an enumerated slot needs at least one choice")
                ]
            | Some choices ->
                let invalid =
                    choices
                    |> List.choose (fun choice ->
                        match slot.Check choice with
                        | Ok _ -> None
                        | Error issues -> Some(InvalidChoice(slot.Id, slot.Owner, choice, issues)))

                let duplicates =
                    if List.distinct choices <> choices then
                        [ InvalidChoices(slot.Id, slot.Owner, "its choices are not distinct") ]
                    else
                        []

                invalid @ duplicates

        defaultIssues @ choiceIssues

    /// A registry of these slots, in this order. Ids must be unique, and every default and choice
    /// must pass its slot's check.
    let create (slots: SlotInfo list) : Validation<SlotRegistry, RegistryIssue> =
        let duplicates =
            slots
            |> List.fold
                (fun (seen: Map<SlotId, SlotOwner>, issues) slot ->
                    match seen.TryFind slot.Id with
                    | Some first -> seen, DuplicateSlot(slot.Id, first, slot.Owner) :: issues
                    | None -> seen.Add(slot.Id, slot.Owner), issues)
                (Map.empty, [])
            |> snd
            |> List.rev

        match duplicates @ List.collect checkSlot slots with
        | [] -> Ok(SlotRegistry.Create slots)
        | issues -> Error issues

    /// A registry of these slots. Throws if they do not form one (a defect), e.g. in tests.
    let ofSlots (slots: SlotInfo list) : SlotRegistry =
        match create slots with
        | Ok registry -> registry
        | Error issues -> invalidArg (nameof slots) (String.Join("\n", issues |> List.map string))

    let tryFind (id: SlotId) (registry: SlotRegistry) : SlotInfo option = registry.TryFind id

    let slots (registry: SlotRegistry) : SlotInfo list = registry.Slots

    /// The slots as JSON, in registration order, for `fx` to list and for docs: each slot's id,
    /// owner, description, default, choices, whether it is privileged, and the JSON Schema of its
    /// values.
    let describe (registry: SlotRegistry) : Json =
        Json.Array
            [
                for slot in registry.Slots ->
                    Json.Object
                        [
                            yield "id", Json.String slot.Id.Value
                            yield "owner", Json.String(string slot.Owner)
                            yield "description", Json.String slot.Description
                            yield "default", slot.Default
                            match slot.Choices with
                            | Some choices -> yield "choices", Json.Array choices
                            | None -> ()
                            yield "privileged", Json.Bool slot.Privileged
                            yield "schema", Schema.toJsonSchema slot.Schema
                        ]
            ]

    /// The schema of a config file's `variants` object: one optional member per slot, with its
    /// description and default; unknown members are rejected. `Schema.toJsonSchema` turns it into
    /// a JSON Schema for editors.
    let configSchema (registry: SlotRegistry) : Schema =
        let field (slot: SlotInfo) : FieldSchema =
            {
                Name = slot.Id.Value
                Schema =
                    { slot.Schema with
                        Description = Some slot.Description
                    }
                Presence = Presence.Optional
            }

        Schema.ofKind (
            SchemaKind.Object
                {
                    Fields = registry.Slots |> List.map field
                    UnknownFields = UnknownFields.Reject
                }
        )
        |> Schema.describe "Variant selections: slot ids and their values."
