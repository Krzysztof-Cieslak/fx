namespace Fx.Sdk

open System
open Fx.Contracts
open Fx.Effects

/// What kind of value a contribution type holds (spec §16.2–16.3): data, a hook of one of the five
/// kinds, or other code.
[<RequireQualifiedAccess>]
type ContributionKind =
    /// A value that is data, given in a manifest or sent by an extension process. Data types are the
    /// `Contribute` hooks: their values are concatenated.
    | Data
    | Gate
    | Transform
    | Observe
    | Interpret
    /// Code of another shape (an extension runtime, a contribution type, …).
    | Code

    override this.ToString() =
        match this with
        | Data -> "data"
        | Gate -> "gate"
        | Transform -> "transform"
        | Observe -> "observe"
        | Interpret -> "interpret"
        | Code -> "code"

/// How an extension process runs a contribution's code for the host: JSON in, JSON out, in the
/// extension's environment. Input that does not decode is an issue list.
type Serve = Json -> Eff<IExtensionEnv, Validation<Json, Issue>>

/// A contribution type: a typed key under which extensions contribute values of type `'T` (spec
/// §16.2), and everything the host needs to move such values across runtimes. Anyone can define
/// one (the core, an extension, a test) without changing the core: the registry is keyed by these.
///
/// - `ToJson`: the JSON form of a value: a data value, or the settings of code (a transform's
///   priority). It goes in manifests and over the extension protocol.
/// - `OfJson`: builds a value from its JSON form, and, for code, from a `Remote` that reaches the code
///   in an extension process. Without a remote (a declarative manifest) code cannot be built.
/// - `Serve`: how an extension process serves a value's code; `None` for data and for code that
///   cannot leave its process (interpreter middleware).
[<NoEquality; NoComparison>]
type ContributionType<'T> =
    {
        /// Unique among all contribution types (`Manifest.Patterns.ContributionType`).
        Name: string
        Description: string
        Kind: ContributionKind
        /// Only extensions from host config, policy or the binary may contribute it (spec §16.5).
        Privileged: bool
        /// The schema of the JSON form, if it has one.
        Schema: Schema option
        ToJson: 'T -> Json option
        OfJson: Json option -> Remote option -> Validation<'T, Issue>
        Serve: ('T -> Serve) option
    }

    override this.ToString() = $"{this.Name} ({this.Kind})"

/// A value contributed under a type, with its type erased so that values of every type fit in one
/// list. `Contribution.tryValue` gets it back with the same `ContributionType`.
[<Sealed>]
type Contribution
    internal
    (
        typeName: string,
        id: string,
        kind: ContributionKind,
        privileged: bool,
        json: Json option,
        serve: Serve option,
        value: obj
    ) =
    member _.Type = typeName
    member _.Id = id
    member _.Kind = kind
    member _.Privileged = privileged
    /// The value's JSON form (`ContributionType.ToJson`).
    member _.Json = json
    /// How an extension process serves its code.
    member _.Serve = serve
    member internal _.Value = value

    /// Its declaration in a manifest or in the extension protocol (with its JSON form).
    member _.Decl: ContributionDecl =
        {
            Type = typeName
            Id = id
            Value = json
        }

    override _.ToString() = typeName + "/" + id

[<Sealed>]
type internal Boxed<'T>(value: 'T) =
    member _.Value = value

/// A contribution in effect: whose it is, its id, its value, and the environment of the extension that
/// contributed it, in which its code runs (with the caller's scope).
[<NoEquality; NoComparison>]
type Contributed<'T> =
    {
        Extension: string
        /// The contribution type's name.
        Type: string
        Id: string
        Value: 'T
        Env: IExtensionEnv
    }

    /// `<extension>/<id>`, how verdict reasons and failures name it.
    member this.By = this.Extension + "/" + this.Id

    override this.ToString() = this.By

/// A contribution type with its value type erased: what the host's catalog keeps, to load values of
/// types it learns about at run time (from a manifest, or from an extension process).
[<NoEquality; NoComparison>]
type ContributionTypeInfo =
    {
        Name: string
        Description: string
        Kind: ContributionKind
        Privileged: bool
        Schema: Schema option
        /// Loads a contribution with this id from its JSON form and, for code, a remote.
        Load: string -> Json option -> Remote option -> Validation<Contribution, Issue>
    }

    override this.ToString() = $"{this.Name} ({this.Kind})"

module Contribution =
    /// A contribution of a value under a type, with an id unique for the type.
    let create (contributionType: ContributionType<'T>) (id: string) (value: 'T) : Contribution =
        Contribution(
            contributionType.Name,
            id,
            contributionType.Kind,
            contributionType.Privileged,
            contributionType.ToJson value,
            contributionType.Serve |> Option.map (fun serve -> serve value),
            Boxed<'T>(value)
        )

    /// The value, if the contribution is of this type.
    let tryValue (contributionType: ContributionType<'T>) (contribution: Contribution) : 'T option =
        if contribution.Type <> contributionType.Name then
            None
        else
            match contribution.Value with
            | :? Boxed<'T> as boxed -> Some boxed.Value
            | _ ->
                invalidOp
                    $"Two contribution types are named {Json.quote contributionType.Name}, with different value types."

module ContributionType =
    let private noRemote (name: string) : Validation<'T, Issue> =
        Error
            [
                Issue.create $"{name} is code: a native or process extension must provide it"
            ]

    /// A data contribution type (a `Contribute` hook): values are data, given in a declarative
    /// manifest or sent by an extension process, and concatenated across extensions.
    let data (name: string) (description: string) (codec: Codec<'T>) : ContributionType<'T> =
        {
            Name = name
            Description = description
            Kind = ContributionKind.Data
            Privileged = false
            Schema = Some codec.Schema
            ToJson = fun value -> Some(codec.Encode value)
            OfJson =
                fun json _ ->
                    match json with
                    | Some json -> codec.Decode json
                    | None -> Error [ Issue.create "missing value" ]
            Serve = None
        }

    /// A code contribution type that only runs in process (native extensions): it has no JSON form
    /// and cannot be served by or proxied to an extension process.
    let inProcess (name: string) (description: string) (kind: ContributionKind) : ContributionType<'T> =
        {
            Name = name
            Description = description
            Kind = kind
            Privileged = false
            Schema = None
            ToJson = fun _ -> None
            OfJson =
                fun _ remote ->
                    match remote with
                    | None -> noRemote name
                    | Some _ ->
                        Error
                            [
                                Issue.create $"{name} runs in the host's process only: use a native extension"
                            ]
            Serve = None
        }

    /// Only extensions from host config, policy or the binary may contribute values of this type
    /// (spec §16.5): runtimes, environments, models, credentials, egress hooks, stores, …
    let privileged (contributionType: ContributionType<'T>) : ContributionType<'T> =
        { contributionType with
            Privileged = true
        }

    /// The type with its value type erased, for the host's catalog.
    let info (contributionType: ContributionType<'T>) : ContributionTypeInfo =
        {
            Name = contributionType.Name
            Description = contributionType.Description
            Kind = contributionType.Kind
            Privileged = contributionType.Privileged
            Schema = contributionType.Schema
            Load =
                fun id json remote ->
                    contributionType.OfJson json remote
                    |> Result.map (Contribution.create contributionType id)
        }

    /// The issue for code given without a way to run it (in a declarative manifest).
    let internal needsCode (name: string) : Validation<'T, Issue> = noRemote name

/// Contribution types of the extension system itself.
[<RequireQualifiedAccess>]
module ContributionTypes =
    /// Defines a new contribution type (its value is the erased type), so that later extensions,
    /// declarative manifests and extension processes can contribute to it. Only native extensions can
    /// define types: a type is code (its codecs). The core's own types need no definition.
    let contributionType: ContributionType<ContributionTypeInfo> =
        ContributionType.inProcess
            "contribution-type"
            "Defines a contribution type that later extensions can contribute to."
            ContributionKind.Code
