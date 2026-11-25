namespace Fx.Variants

open System
open Fx.Contracts

/// Where a selection comes from, in increasing precedence: a later layer overrides an earlier one.
/// `Policy` is not a layer that selects: it pins and forbids values on top of all of them (policy only
/// tightens).
[<RequireQualifiedAccess>]
type Layer =
    /// The slot's default.
    | Default
    /// The host's config (the user's, on this machine).
    | Host
    /// The project's config. Read only once the project is trusted (spec §18).
    | Project
    /// Overrides made in a session, e.g. by a TUI command.
    | Session
    /// Explicit selections for one invocation: an eval profile, then command-line flags.
    | Explicit
    /// Pinned by a policy.
    | Policy

    override this.ToString() =
        match this with
        | Default -> "default"
        | Host -> "host"
        | Project -> "project"
        | Session -> "session"
        | Explicit -> "explicit"
        | Policy -> "policy"

module Layer =
    /// The precedence of a layer: higher wins.
    let rank (layer: Layer) : int =
        match layer with
        | Layer.Default -> 0
        | Layer.Host -> 1
        | Layer.Project -> 2
        | Layer.Session -> 3
        | Layer.Explicit -> 4
        | Layer.Policy -> 5

    /// Whether selections from this layer may set a privileged slot.
    let maySelectPrivileged (layer: Layer) : bool =
        match layer with
        | Layer.Project
        | Layer.Session -> false
        | Layer.Default
        | Layer.Host
        | Layer.Explicit
        | Layer.Policy -> true

    let codec: Codec<Layer> =
        Codec.enum
            [
                Layer.Default, "default"
                Layer.Host, "host"
                Layer.Project, "project"
                Layer.Session, "session"
                Layer.Explicit, "explicit"
                Layer.Policy, "policy"
            ]
        |> Codec.describe "Where a value comes from: default, host, project, session, explicit or policy."

/// Where a value came from: the layer, and a human-readable origin within it (a config file's path,
/// `--variant`, an eval profile's name, a policy's name); empty when there is nothing to add.
type Source =
    {
        Layer: Layer
        Origin: string
    }

    override this.ToString() =
        if String.IsNullOrEmpty this.Origin then
            string this.Layer
        else
            $"{this.Layer} ({this.Origin})"

module Source =
    let create (layer: Layer) (origin: string) : Source = { Layer = layer; Origin = origin }

    /// The slot's default.
    let defaultValue: Source = create Layer.Default ""

    /// `{"layer": "host", "origin": "~/.fx/config.json"}`.
    let codec: Codec<Source> =
        Codec.record {
            let! layer = Codec.field "layer" (fun (s: Source) -> s.Layer) Layer.codec
            and! origin = Codec.defaultField "origin" (fun s -> s.Origin) "" Codec.string
            return create layer origin
        }
        |> Codec.describe "Where a variant value comes from."
        |> Codec.named "VariantSource"

/// The selections of one layer, as read: slot ids (unchecked text) and values (unchecked JSON), in
/// the order given. Resolution checks them against the registry.
type Selections =
    {
        Source: Source
        Values: (string * Json) list
    }

    override this.ToString() =
        $"Selections({this.Source}, {this.Values.Length} values)"

module Selections =
    let create (source: Source) (values: (string * Json) list) : Selections = { Source = source; Values = values }

    /// A typed selection, e.g. `Selections.select Slots.editGuard EditGuard.Require`.
    let select (slot: Slot<'T>) (value: 'T) : string * Json = slot.Id.Value, Slot.encode slot value

    /// The `variants` member of a config file: an object from slot ids to values. Absent or `null`
    /// means no selections. Other members of the file belong to other readers and are ignored.
    let configCodec: Codec<(string * Json) list> =
        Codec.record {
            let! values = Codec.defaultField "variants" id [] (Codec.keyValues Codec.json)
            return values
        }

    /// The selections of a config file (already parsed): its `variants` member.
    let ofConfig (source: Source) (config: Json) : Validation<Selections, Issue> =
        Codec.decode configCodec config |> Result.map (create source)

    /// Reads `<slot>=<value>` (a command-line flag such as `--variant edit.guard=require`). The value
    /// is JSON if it parses as JSON, and a string otherwise: `edit.guard=warn` selects `"warn"`, and
    /// `values.inlineMaxBytes=4096` selects `4096`; quote a string that looks like JSON
    /// (`x.y="true"`).
    let parseArgument (text: string) : Result<string * Json, string> =
        let index = text.IndexOf '='

        if index <= 0 then
            Error $"expected <slot>=<value>, e.g. agent.actionSurface=hybrid, got \"{text}\""
        else
            let name = text.Substring(0, index)
            let raw = text.Substring(index + 1)

            let value =
                match Json.parse raw with
                | Ok json -> json
                | Error _ -> Json.String raw

            Ok(name, value)

/// A policy (spec §11.5, §13): pins and forbids values. Policies only tighten: they never select a
/// value a slot does not allow, and resolution reports every selection that conflicts with them
/// instead of quietly overriding it. Slot ids and values are unchecked until resolution.
type Policy =
    {
        /// Names the policy in issues, e.g. its file.
        Origin: string
        /// Slots fixed to one value.
        Pins: (string * Json) list
        /// Values slots may not take.
        Forbids: (string * Json list) list
    }

    override this.ToString() =
        $"Policy({this.Origin}, {this.Pins.Length} pins, {this.Forbids.Length} forbids)"

module Policy =
    /// A policy that pins and forbids nothing.
    let empty (origin: string) : Policy =
        {
            Origin = origin
            Pins = []
            Forbids = []
        }

    /// Pins a slot to a value.
    let pin (slot: Slot<'T>) (value: 'T) (policy: Policy) : Policy =
        { policy with
            Pins = policy.Pins @ [ Selections.select slot value ]
        }

    /// Forbids values of a slot.
    let forbid (slot: Slot<'T>) (values: 'T list) (policy: Policy) : Policy =
        { policy with
            Forbids = policy.Forbids @ [ slot.Id.Value, values |> List.map (Slot.encode slot) ]
        }

    let private body: Codec<(string * Json) list * (string * Json list) list> =
        Codec.record {
            let! pins = Codec.defaultField "pin" fst [] (Codec.keyValues Codec.json)

            and! forbids =
                Codec.defaultField "forbid" snd [] (Codec.keyValues (Codec.list Codec.json))

            return pins, forbids
        }

    /// `{"pin": {"<slot>": value}, "forbid": {"<slot>": [values]}}`.
    let codec (origin: string) : Codec<Policy> =
        body
        |> Codec.map
            (fun (pins, forbids) ->
                {
                    Origin = origin
                    Pins = pins
                    Forbids = forbids
                })
            (fun policy -> policy.Pins, policy.Forbids)
        |> Codec.describe "A variants policy: slots pinned to one value, and values slots may not take."

    /// A policy from its JSON form.
    let ofJson (origin: string) (json: Json) : Validation<Policy, Issue> = Codec.decode (codec origin) json
