namespace Fx.Variants

open System
open Fx.Contracts

/// The id of a variant slot: two or more dotted segments, each a lowercase letter followed by
/// letters, digits, `_` or `-`, e.g. `agent.actionSurface` or `model.role.main`. At most 128
/// characters. Ids name slots in config files, on the command line, in profiles and in eval matrices,
/// so they never change once released: rename a slot by adding a new one.
[<Struct>]
type SlotId =
    private
    | SlotId of string

    member this.Value =
        let (SlotId id) = this
        id

    override this.ToString() = this.Value

module SlotId =
    [<Literal>]
    let MaxLength = 128

    /// The ECMAScript pattern of a slot id (anchored), for schemas.
    [<Literal>]
    let Pattern = "^[a-z][A-Za-z0-9_-]*(\\.[a-z][A-Za-z0-9_-]*)+$"

    let private isSegment (segment: string) =
        segment.Length > 0
        && segment[0] >= 'a'
        && segment[0] <= 'z'
        && segment
           |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_' || c = '-')

    /// Checks the syntax of an id.
    let tryCreate (id: string) : Result<SlotId, string> =
        if String.IsNullOrEmpty id || id.Length > MaxLength then
            Error $"a slot id has 1 to {MaxLength} characters"
        else
            let segments = id.Split '.'

            if segments.Length < 2 then
                Error "a slot id has at least two dotted segments, e.g. \"agent.actionSurface\""
            elif not (Array.forall isSegment segments) then
                Error "each segment of a slot id starts with a lowercase letter and has only letters, digits, _ and -"
            else
                Ok(SlotId id)

    /// An id from code. Throws on invalid syntax (a defect).
    let create (id: string) : SlotId =
        match tryCreate id with
        | Ok slot -> slot
        | Error reason -> invalidArg (nameof id) $"Invalid slot id \"{id}\": {reason}."

    /// `"agent.actionSurface"`.
    let codec: Codec<SlotId> =
        Codec.string
        |> Codec.tryMap tryCreate (fun (id: SlotId) -> id.Value)
        |> Codec.describe "A variant slot id: dotted segments, e.g. \"agent.actionSurface\"."

/// Who defines a slot. The host assigns the owner of a contributed slot (the extension that
/// contributed it), so an extension cannot claim a core slot.
[<RequireQualifiedAccess>]
type SlotOwner =
    | Core
    | Extension of id: string

    override this.ToString() =
        match this with
        | Core -> "core"
        | Extension id -> id

/// A typed variant slot (spec §15): one configurable behaviour, as a value. The code that behaves
/// differently keeps the slot and reads it through `IVariants` (`Variants.get`), which returns a
/// `'T` and records the read. Its id, codec, default and choices give the schema and the docs of the
/// config files, and the axes of eval matrices.
///
/// Slots are values: define one with `Slot.create` or `Slot.enum` next to the code that reads it, and
/// register it (core slots in `Slots.all`, an extension's as contributions; see
/// docs/modules/variants.md).
[<NoEquality; NoComparison>]
type Slot<'T> =
    {
        Id: SlotId
        /// What the slot changes, for docs, config schemas and `fx` help. One or two sentences.
        Description: string
        Owner: SlotOwner
        /// Reads, writes and validates values. Refinements (`Codec.between`, …) restrict them.
        Codec: Codec<'T>
        /// The value when no layer selects one. Provisional until evals decide (spec §1, principle 7).
        Default: 'T
        /// The values an enumerated slot may take, in display order (eval matrices use them as
        /// an axis); `None` for an open slot that takes any value its codec accepts.
        Choices: 'T list option
        /// Only host config, explicit selections (command line, eval profiles) and policy may select
        /// it, never project config or session overrides: slots that weaken isolation or protection.
        Privileged: bool
    }

    override this.ToString() = $"Slot({this.Id})"

/// A slot with its value type erased: what the registry keeps and resolution works on. Values are
/// canonical JSON (`Json.canonicalize`), so equal values compare equal.
///
/// `Slot.info` makes one from a typed slot; `SlotInfo.ofDecl` from a declaration in JSON (a
/// declarative extension, or an extension process), whose values are limited to its choices.
[<NoEquality; NoComparison>]
type SlotInfo =
    {
        Id: SlotId
        Description: string
        Owner: SlotOwner
        Schema: Schema
        /// The default, canonical.
        Default: Json
        /// The allowed values, canonical, in display order.
        Choices: Json list option
        Privileged: bool
        /// Validates a value and returns its canonical form, or issues worded for a model.
        Check: Json -> Validation<Json, Issue>
    }

    override this.ToString() =
        $"SlotInfo({this.Id}, owner {this.Owner})"

/// The JSON form of a slot: what `fx` lists, and how a declarative extension or an extension
/// process declares one (`SlotInfo.ofDecl`).
type SlotDecl =
    {
        Id: string
        Description: string
        Default: Json
        Choices: Json list option
        Privileged: bool
    }

    override this.ToString() = $"SlotDecl({this.Id})"

module Slot =
    let private canonical (codec: Codec<'T>) (value: 'T) : Json = Json.canonicalize (codec.Encode value)

    let private showChoices (choices: Json list) =
        String.Join(", ", choices |> List.map Json.serialize)

    /// An open core slot. Throws if the id is invalid (a defect).
    let create (id: string) (description: string) (codec: Codec<'T>) (defaultValue: 'T) : Slot<'T> =
        {
            Id = SlotId.create id
            Description = description
            Owner = SlotOwner.Core
            Codec = codec
            Default = defaultValue
            Choices = None
            Privileged = false
        }

    /// An enumerated core slot whose values are written as strings, e.g.
    /// `Slot.enum "edit.guard" "…" [ Off, "off"; Warn, "warn"; Require, "require" ] Warn`.
    let enum (id: string) (description: string) (cases: ('T * string) list) (defaultValue: 'T) : Slot<'T> =
        { create id description (Codec.enum cases) defaultValue with
            Choices = Some(List.map fst cases)
        }

    /// Restricts a slot to these values.
    let withChoices (choices: 'T list) (slot: Slot<'T>) : Slot<'T> = { slot with Choices = Some choices }

    /// Marks a slot that only host config, explicit selections and policy may select.
    let privileged (slot: Slot<'T>) : Slot<'T> = { slot with Privileged = true }

    /// A slot defined by an extension. The host sets the owner of contributed slots itself.
    let ownedBy (extensionId: string) (slot: Slot<'T>) : Slot<'T> =
        { slot with
            Owner = SlotOwner.Extension extensionId
        }

    /// A value of the slot as canonical JSON.
    let encode (slot: Slot<'T>) (value: 'T) : Json = canonical slot.Codec value

    /// The slot with its type erased. Its check decodes with the codec (so refinements apply),
    /// requires one of the choices if there are any, and returns the canonical re-encoding.
    let info (slot: Slot<'T>) : SlotInfo =
        let choices = slot.Choices |> Option.map (List.map (canonical slot.Codec))

        let check (json: Json) : Validation<Json, Issue> =
            match slot.Codec.Decode json with
            | Error issues -> Error issues
            | Ok value ->
                let normal = canonical slot.Codec value

                match choices with
                | Some allowed when not (List.contains normal allowed) ->
                    Error [ Issue.expected $"one of {showChoices allowed}" json ]
                | _ -> Ok normal

        {
            Id = slot.Id
            Description = slot.Description
            Owner = slot.Owner
            Schema = slot.Codec.Schema
            Default = canonical slot.Codec slot.Default
            Choices = choices
            Privileged = slot.Privileged
            Check = check
        }

module SlotInfo =
    let private showChoices (choices: Json list) =
        String.Join(", ", choices |> List.map Json.serialize)

    /// The JSON form of a slot.
    let decl (info: SlotInfo) : SlotDecl =
        {
            Id = info.Id.Value
            Description = info.Description
            Default = info.Default
            Choices = info.Choices
            Privileged = info.Privileged
        }

    /// A slot from its JSON form, owned by `owner`. Declared slots are enumerated: values are limited
    /// to the choices (compared in canonical form), which must be distinct and include the default.
    let ofDecl (owner: SlotOwner) (decl: SlotDecl) : Validation<SlotInfo, Issue> =
        let at name message =
            Issue.at [ PathSegment.Key name ] message

        let id =
            SlotId.tryCreate decl.Id |> Result.mapError (fun reason -> [ at "id" reason ])

        let description =
            if String.IsNullOrWhiteSpace decl.Description then
                Error [ at "description" "expected a non-empty description" ]
            else
                Ok decl.Description

        let choices =
            match decl.Choices with
            | None
            | Some [] -> Error [ at "choices" "a declared slot lists the values it may take" ]
            | Some choices ->
                let canonical = List.map Json.canonicalize choices

                if List.distinct canonical <> canonical then
                    Error [ at "choices" "expected distinct values" ]
                elif not (List.contains (Json.canonicalize decl.Default) canonical) then
                    Error [ at "default" $"expected one of the choices {showChoices canonical}" ]
                else
                    Ok canonical

        let build (id: SlotId) (description: string) (choices: Json list) : SlotInfo =
            let allStrings =
                choices
                |> List.forall (function
                    | Json.String _ -> true
                    | _ -> false)

            let kind =
                if allStrings then
                    SchemaKind.Enum(
                        choices
                        |> List.choose (function
                            | Json.String s -> Some s
                            | _ -> None)
                    )
                else
                    SchemaKind.AnyOf(choices |> List.map (fun c -> Schema.ofKind (SchemaKind.Const c)))

            let check (json: Json) : Validation<Json, Issue> =
                let normal = Json.canonicalize json

                if List.contains normal choices then
                    Ok normal
                else
                    Error [ Issue.expected $"one of {showChoices choices}" json ]

            {
                Id = id
                Description = description
                Owner = owner
                Schema = Schema.ofKind kind
                Default = Json.canonicalize decl.Default
                Choices = Some choices
                Privileged = decl.Privileged
                Check = check
            }

        Validation.map3 build id description choices

    /// The slot as declared by an extension: the host sets the owner.
    let withOwner (owner: SlotOwner) (info: SlotInfo) : SlotInfo = { info with Owner = owner }

    /// `{"id", "description", "default", "choices"?, "privileged"}`.
    let declCodec: Codec<SlotDecl> =
        Codec.record {
            let! id =
                Codec.field "id" (fun (d: SlotDecl) -> d.Id) (Codec.string |> Codec.pattern SlotId.Pattern)

            and! description = Codec.field "description" (fun d -> d.Description) Codec.string
            and! defaultValue = Codec.field "default" (fun d -> d.Default) Codec.json

            and! choices =
                Codec.optionalField "choices" (fun d -> d.Choices) (Codec.list Codec.json)

            and! privileged =
                Codec.defaultField "privileged" (fun d -> d.Privileged) false Codec.bool

            return
                {
                    Id = id
                    Description = description
                    Default = defaultValue
                    Choices = choices
                    Privileged = privileged
                }
        }
        |> Codec.describe "A variant slot: its id, what it changes, its default and the values it may take."
        |> Codec.named "SlotDecl"
