namespace Fx.Variants

open System
open System.Collections.Generic
open Fx.Contracts

/// The value of one slot and where it came from: what resolution decides for each slot, and what a
/// read of a slot returns and records.
type Resolved =
    {
        Slot: SlotId
        /// Canonical JSON (`Json.canonicalize`).
        Value: Json
        Source: Source
    }

    override this.ToString() =
        $"{this.Slot} = {Json.serialize this.Value} ({this.Source})"

module Resolved =
    /// `{"slot": "edit.guard", "value": "warn", "source": {"layer": "default", "origin": ""}}`.
    let codec: Codec<Resolved> =
        Codec.record {
            let! slot = Codec.field "slot" (fun (r: Resolved) -> r.Slot) SlotId.codec
            and! value = Codec.field "value" (fun r -> r.Value) Codec.json
            and! source = Codec.field "source" (fun r -> r.Source) Source.codec

            return
                {
                    Slot = slot
                    Value = Json.canonicalize value
                    Source = source
                }
        }
        |> Codec.describe "The value of a variant slot and where it came from."
        |> Codec.named "ResolvedVariant"

/// Why selections or policies cannot be resolved. Messages name the source, so a person can find the
/// line to fix; `VariantIssue.toIssues` gives the same as model-readable `Issue`s.
type VariantIssue =
    /// No registered slot has this id. `suggestions` are registered ids close to it.
    | UnknownSlot of name: string * source: Source * suggestions: SlotId list
    /// The value does not pass the slot's check (its codec, refinements and choices).
    | InvalidValue of slot: SlotId * source: Source * issues: Issue list
    /// One layer selects the slot twice.
    | DuplicateSelection of slot: SlotId * source: Source
    /// A project config or a session override selects a privileged slot.
    | NotSelectable of slot: SlotId * source: Source
    /// The value in effect is forbidden by a policy (`source` says where the value came from).
    | Forbidden of slot: SlotId * value: Json * source: Source * policy: string
    /// A selection differs from the value a policy pins.
    | Pinned of slot: SlotId * value: Json * source: Source * pinned: Json * policy: string
    /// Two policies pin one slot to different values.
    | ConflictingPins of slot: SlotId * first: Json * firstPolicy: string * second: Json * secondPolicy: string

    /// Where the issue is: the slot id or the unknown name.
    member this.Name =
        match this with
        | UnknownSlot(name, _, _) -> name
        | InvalidValue(slot, _, _)
        | DuplicateSelection(slot, _)
        | NotSelectable(slot, _)
        | Forbidden(slot, _, _, _)
        | Pinned(slot, _, _, _, _)
        | ConflictingPins(slot, _, _, _, _) -> slot.Value

    /// What is wrong, without the source; one line per problem for invalid values.
    member this.Problems: (PathSegment list * string) list =
        let show (json: Json) = Json.serialize json

        match this with
        | UnknownSlot(_, _, suggestions) ->
            let hint =
                match suggestions with
                | [] -> ""
                | ids ->
                    "; did you mean "
                    + String.Join(" or ", ids |> List.map (fun id -> Json.quote id.Value))
                    + "?"

            [ [], "unknown variant slot" + hint ]
        | InvalidValue(_, _, issues) -> issues |> List.map (fun issue -> issue.Path, issue.Message)
        | DuplicateSelection _ -> [ [], "selected more than once" ]
        | NotSelectable _ ->
            [
                [], "privileged: only host config, explicit selections and policy may select it"
            ]
        | Forbidden(_, value, source, policy) when source.Layer = Layer.Default ->
            [
                [], $"the default {show value} is forbidden by policy {policy}; select another value"
            ]
        | Forbidden(_, value, _, policy) -> [ [], $"{show value} is forbidden by policy {policy}" ]
        | Pinned(_, value, _, pinned, policy) ->
            [ [], $"{show value} conflicts with policy {policy}, which pins {show pinned}" ]
        | ConflictingPins(_, first, firstPolicy, second, secondPolicy) ->
            [
                [], $"pinned to {show first} by policy {firstPolicy} and to {show second} by policy {secondPolicy}"
            ]

    /// Where the offending value came from, if a layer is to blame (not for a default).
    member this.Source: Source option =
        match this with
        | UnknownSlot(_, source, _)
        | InvalidValue(_, source, _)
        | DuplicateSelection(_, source)
        | NotSelectable(_, source)
        | Forbidden(_, _, source, _)
        | Pinned(_, _, source, _, _) when source.Layer <> Layer.Default -> Some source
        | UnknownSlot _
        | InvalidValue _
        | DuplicateSelection _
        | NotSelectable _
        | Forbidden _
        | Pinned _
        | ConflictingPins _ -> None

    /// `host (~/.fx/config.json): edit.guard: expected one of "off", "warn", "require", got string "strict"`.
    override this.ToString() =
        let prefix =
            match this.Source with
            | Some source -> $"{source}: "
            | None -> ""

        let lines =
            this.Problems
            |> List.map (fun (path, message) ->
                let at = (JsonPath.render path).Substring 1
                $"{prefix}{this.Name}{at}: {message}")

        String.Join("\n", lines)

module VariantIssue =
    /// The issue as model-readable `Issue`s, with paths into the selections object
    /// (`$["edit.guard"]`) and the source at the end of each message.
    let toIssues (issue: VariantIssue) : Issue list =
        let suffix =
            match issue.Source with
            | Some source -> $" (from {source})"
            | None -> ""

        issue.Problems
        |> List.map (fun (path, message) -> Issue.at (PathSegment.Key issue.Name :: path) (message + suffix))

    /// One issue per line.
    let render (issues: VariantIssue list) : string =
        String.Join("\n", issues |> List.map string)

/// What resolution decided: one `Resolved` per registered slot. Made only by `Resolution.resolve`,
/// so every value passed its slot's check and every policy.
[<Sealed>]
type Resolution internal (registry: SlotRegistry, values: Resolved list) =
    let byId = values |> List.map (fun r -> r.Slot, r) |> Map.ofList

    /// The registry the values were resolved against.
    member _.Registry = registry

    /// One value per registered slot, in registration order.
    member _.Values: Resolved list = values

    member _.TryFind(slot: SlotId) : Resolved option = byId.TryFind slot

    /// The value of every slot, by id: what a profile records.
    member _.Selections: Map<SlotId, Json> = byId |> Map.map (fun _ r -> r.Value)

    override _.ToString() = $"Resolution({values.Length} slots)"

/// Thrown when code reads a slot that the registry does not have. Every slot an effect reads must be
/// registered, or its value would not be in the profile (a defect).
type UnregisteredSlotException(slot: SlotId) =
    inherit Exception($"The variant slot {slot} is not registered: register it so that profiles cover it.")

    member _.Slot = slot

/// Thrown when a registered value does not decode with the codec of the slot that reads it: two
/// slots with one id and different types (a defect).
type SlotTypeMismatchException(slot: SlotId, issues: Issue list) =
    inherit
        Exception(
            $"The value of the variant slot {slot} does not decode with the reader's codec: {Issue.render issues}"
        )

    member _.Slot = slot
    member _.Issues = issues

module Resolution =
    let private lower (s: string) = s.ToLowerInvariant()

    let private lastSegment (s: string) =
        match s.LastIndexOf '.' with
        | -1 -> s
        | i -> s.Substring(i + 1)

    /// Levenshtein distance.
    let private distance (a: string) (b: string) : int =
        let mutable previous = Array.init (b.Length + 1) id
        let mutable current = Array.zeroCreate (b.Length + 1)

        for i in 1 .. a.Length do
            current[0] <- i

            for j in 1 .. b.Length do
                let cost = if a[i - 1] = b[j - 1] then 0 else 1
                current[j] <- min (min (previous[j] + 1) (current[j - 1] + 1)) (previous[j - 1] + cost)

            let swap = previous
            previous <- current
            current <- swap

        previous[b.Length]

    /// Registered ids close to an unknown name: within a small edit distance, or with the same last
    /// segment (`actionSurface` suggests `agent.actionSurface`). At most three, closest first.
    let suggest (registry: SlotRegistry) (name: string) : SlotId list =
        let name = lower name
        let limit = max 2 (name.Length / 4)

        registry.Slots
        |> List.choose (fun slot ->
            let id = lower slot.Id.Value
            let d = distance name id

            if d <= limit || lastSegment id = lastSegment name then
                Some(d, slot.Id)
            else
                None)
        |> List.sortBy (fun (d, id) -> d, id.Value)
        |> List.truncate 3
        |> List.map snd

    /// Resolves every registered slot (spec §15): pure and deterministic.
    ///
    /// - **Layers**: the selections of the highest layer win (`Layer.rank`); within one layer, later
    ///   selections win (an eval profile, then command-line flags). Selections come only from the host,
    ///   project, session and explicit layers.
    /// - **Checks**: every selection is checked, also those a higher layer overrides: unknown slots,
    ///   invalid values, duplicates within a layer, privileged slots selected by a project or a session.
    /// - **Policies** only tighten. A pinned slot takes the pinned value; a selection that differs is an
    ///   issue. A value in effect that a policy forbids is an issue, including a default.
    ///
    /// Any issue fails resolution, and every issue is reported, in a fixed order.
    let resolve
        (registry: SlotRegistry)
        (policies: Policy list)
        (layers: Selections list)
        : Validation<Resolution, VariantIssue> =
        for layer in layers do
            match layer.Source.Layer with
            | Layer.Default
            | Layer.Policy ->
                invalidArg
                    (nameof layers)
                    $"Selections come from the host, project, session or explicit layer, not {layer.Source.Layer}."
            | Layer.Host
            | Layer.Project
            | Layer.Session
            | Layer.Explicit -> ()

        let issues = ResizeArray<VariantIssue>()

        let lookup (source: Source) (name: string) : SlotInfo option =
            let found =
                match SlotId.tryCreate name with
                | Ok id -> registry.TryFind id
                | Error _ -> None

            if found.IsNone then
                issues.Add(UnknownSlot(name, source, suggest registry name))

            found

        let check (source: Source) (slot: SlotInfo) (json: Json) : Json option =
            match slot.Check json with
            | Ok value -> Some value
            | Error errors ->
                issues.Add(InvalidValue(slot.Id, source, errors))
                None

        let selected = Dictionary<SlotId, Json * Source>()

        for layer in layers |> List.sortBy (fun l -> Layer.rank l.Source.Layer) do
            let source = layer.Source
            let seen = HashSet<SlotId>()

            for name, json in layer.Values do
                match lookup source name with
                | None -> ()
                | Some slot when not (seen.Add slot.Id) -> issues.Add(DuplicateSelection(slot.Id, source))
                | Some slot when slot.Privileged && not (Layer.maySelectPrivileged source.Layer) ->
                    issues.Add(NotSelectable(slot.Id, source))
                | Some slot ->
                    match check source slot json with
                    | Some value -> selected[slot.Id] <- (value, source)
                    | None -> ()

        let pins = Dictionary<SlotId, Json * string>()
        let forbidden = Dictionary<SlotId, ResizeArray<Json * string>>()

        for policy in policies do
            let source = Source.create Layer.Policy policy.Origin

            for name, json in policy.Pins do
                match lookup source name with
                | None -> ()
                | Some slot ->
                    match check source slot json with
                    | None -> ()
                    | Some value ->
                        match pins.TryGetValue slot.Id with
                        | true, (other, otherPolicy) ->
                            if other <> value then
                                issues.Add(ConflictingPins(slot.Id, other, otherPolicy, value, policy.Origin))
                        | false, _ -> pins[slot.Id] <- (value, policy.Origin)

            for name, values in policy.Forbids do
                match lookup source name with
                | None -> ()
                | Some slot ->
                    for json in values do
                        match check source slot json with
                        | None -> ()
                        | Some value ->
                            match forbidden.TryGetValue slot.Id with
                            | true, list -> list.Add(value, policy.Origin)
                            | false, _ -> forbidden[slot.Id] <- ResizeArray [ value, policy.Origin ]

        let forbiddenBy (slot: SlotId) (value: Json) : string option =
            match forbidden.TryGetValue slot with
            | true, list -> list |> Seq.tryFind (fun (v, _) -> v = value) |> Option.map snd
            | false, _ -> None

        let values =
            registry.Slots
            |> List.map (fun slot ->
                let value, source =
                    match selected.TryGetValue slot.Id with
                    | true, choice -> choice
                    | false, _ -> slot.Default, Source.defaultValue

                match pins.TryGetValue slot.Id with
                | true, (pinned, policy) ->
                    let pinSource = Source.create Layer.Policy policy

                    if source.Layer <> Layer.Default && value <> pinned then
                        issues.Add(Pinned(slot.Id, value, source, pinned, policy))

                    match forbiddenBy slot.Id pinned with
                    | Some by -> issues.Add(Forbidden(slot.Id, pinned, pinSource, by))
                    | None -> ()

                    {
                        Slot = slot.Id
                        Value = pinned
                        Source = pinSource
                    }
                | false, _ ->
                    match forbiddenBy slot.Id value with
                    | Some by -> issues.Add(Forbidden(slot.Id, value, source, by))
                    | None -> ()

                    {
                        Slot = slot.Id
                        Value = value
                        Source = source
                    })

        if issues.Count = 0 then
            Ok(Resolution(registry, values))
        else
            Error(List.ofSeq issues)

    /// Every slot at its default, without policies.
    let defaults (registry: SlotRegistry) : Resolution =
        Resolution(
            registry,
            registry.Slots
            |> List.map (fun slot ->
                {
                    Slot = slot.Id
                    Value = slot.Default
                    Source = Source.defaultValue
                })
        )

    let tryFind (slot: SlotId) (resolution: Resolution) : Resolved option = resolution.TryFind slot

    /// The typed value of a slot. Throws `UnregisteredSlotException` or `SlotTypeMismatchException`
    /// (defects).
    let value (slot: Slot<'T>) (resolution: Resolution) : 'T =
        match resolution.TryFind slot.Id with
        | None -> raise (UnregisteredSlotException slot.Id)
        | Some resolved ->
            match slot.Codec.Decode resolved.Value with
            | Ok value -> value
            | Error issues -> raise (SlotTypeMismatchException(slot.Id, issues))

    /// One line per slot, in registration order: `edit.guard = "warn" (default)`.
    let explain (resolution: Resolution) : string list = resolution.Values |> List.map string
