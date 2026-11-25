namespace Fx.Components

open System
open System.Security.Cryptography
open System.Text
open Fx
open Fx.Contracts
open Fx.Sdk

/// Names one install of a component: its id, its version, and a hash of how it is installed (the
/// target, the method with its digest, and the layout). Two installs with one key have the same
/// files, so the key names the install's directory: `<id>/<version>-<hash>`.
type InstallKey =
    {
        Id: string
        Version: string
        /// The first 12 hex digits of the SHA-256 of the install's canonical JSON.
        Hash: string
    }

    /// The install's directory name under its component's directory.
    member this.Dir = this.Version + "-" + this.Hash

    /// `<id>@<version>-<hash>`: unique in an install root.
    member this.Name = this.Id + "@" + this.Dir

    override this.ToString() = this.Name

/// Everything the installer needs to install one component on one target: a lockfile entry with
/// the method chosen for the target.
type InstallSpec =
    {
        Key: InstallKey
        Id: string
        Version: SemVer
        Target: TargetTriple
        Method: InstallMethod
        Layout: InstallLayout
        Exports: Exports
        Probe: Probe option
        Size: SizeHint
        /// The ids of the components it depends on.
        DependsOn: string list
    }

    override this.ToString() = $"{this.Key} for {this.Target}"

module InstallSpec =
    let private keyJson
        (id: string)
        (version: SemVer)
        (target: TargetTriple)
        (method: InstallMethod)
        (layout: InstallLayout)
        =
        Json.Object
            [
                "id", Json.String id
                "version", Json.String(string version)
                "target", Json.String(string target)
                "method", Component.installMethod.Encode method
                "layout", Component.installLayout.Encode layout
            ]

    /// A spec, with its key computed from what determines the installed files.
    let create
        (id: string)
        (version: SemVer)
        (target: TargetTriple)
        (method: InstallMethod)
        (layout: InstallLayout)
        : InstallSpec =
        let canonical = Json.serializeCanonical (keyJson id version target method layout)
        let digest = SHA256.HashData(Encoding.UTF8.GetBytes canonical)

        {
            Key =
                {
                    Id = id
                    Version = string version
                    Hash = (Convert.ToHexStringLower digest).Substring(0, 12)
                }
            Id = id
            Version = version
            Target = target
            Method = method
            Layout = layout
            Exports = { Bin = []; Env = [] }
            Probe = None
            Size = { Download = None; Installed = None }
            DependsOn = []
        }

/// One component in the lockfile: the version the planner chose, the method for each target (with
/// URL templates expanded), and what the installer and its users need. Self-contained: installing
/// from a lockfile needs no component definitions.
type LockedComponent =
    {
        Id: string
        Version: SemVer
        /// Ids of the components it depends on, sorted.
        DependsOn: string list
        /// The method for each target, sorted by target.
        Install: (TargetTriple * InstallMethod) list
        Layout: InstallLayout
        Exports: Exports
        Probe: Probe option
        Size: SizeHint
    }

    override this.ToString() = $"{this.Id} {this.Version}"

/// The component lockfile, `fx.components.lock` (spec §6.1, principle 12): what the planner
/// resolved for a set of requirements and targets. Its hash is part of the profile (spec §15).
type Lockfile =
    {
        /// The file format, `Lockfile.FormatVersion`.
        Format: int
        /// The requirements it was planned for, sorted and without duplicates.
        Requires: ComponentRef list
        /// The targets it covers, sorted.
        Targets: TargetTriple list
        /// Sorted by id.
        Components: LockedComponent list
    }

    override this.ToString() =
        $"lockfile ({this.Components.Length} components, {this.Targets.Length} targets)"

[<RequireQualifiedAccess>]
module Lockfile =

    /// The lockfile's file name (`fx.components.lock`, from the product name).
    let fileName = Product.componentsLockFileName

    [<Literal>]
    let FormatVersion = 1

    let private installCodec: Codec<(TargetTriple * InstallMethod) list> =
        Codec.keyValues Component.installMethod
        |> Codec.tryMap
            (fun pairs ->
                let parsed =
                    pairs
                    |> List.map (fun (target, method) -> TargetTriple.parse target |> Result.map (fun t -> t, method))

                match
                    parsed
                    |> List.tryPick (function
                        | Error e -> Some e
                        | Ok _ -> None)
                with
                | Some error -> Error error
                | None ->
                    Ok(
                        parsed
                        |> List.choose (function
                            | Ok pair -> Some pair
                            | Error _ -> None)
                    ))
            (List.map (fun (target, method) -> string target, method))
        |> Codec.describe "The install method for each target."

    let private componentId = Codec.string |> Codec.pattern Component.IdPattern

    let lockedComponent: Codec<LockedComponent> =
        Codec.record {
            let! id = Codec.field "id" (fun (c: LockedComponent) -> c.Id) componentId
            and! version = Codec.field "version" (fun c -> c.Version) SemVer.codec

            and! dependsOn =
                Codec.defaultField "dependsOn" (fun c -> c.DependsOn) [] (Codec.list componentId)

            and! install = Codec.field "install" (fun c -> c.Install) installCodec
            and! layout = Codec.field "layout" (fun c -> c.Layout) Component.installLayout
            and! exports = Codec.field "exports" (fun c -> c.Exports) Component.exports
            and! probe = Codec.optionalField "probe" (fun c -> c.Probe) Component.probe
            and! size = Codec.field "size" (fun c -> c.Size) Component.sizeHint

            return
                {
                    Id = id
                    Version = version
                    DependsOn = dependsOn
                    Install = install
                    Layout = layout
                    Exports = exports
                    Probe = probe
                    Size = size
                }
        }
        |> Codec.strict
        |> Codec.named "LockedComponent"

    let codec: Codec<Lockfile> =
        Codec.record {
            let! format =
                Codec.field
                    "lockfileVersion"
                    (fun (l: Lockfile) -> l.Format)
                    (Codec.int
                     |> Codec.refine (fun v ->
                         if v = FormatVersion then
                             Ok()
                         else
                             Error $"this version reads lockfile version {FormatVersion}, got {v}"))

            and! requires =
                Codec.field "requires" (fun l -> l.Requires) (Codec.list Manifest.componentRef)

            and! targets =
                Codec.field "targets" (fun l -> l.Targets) (Codec.list TargetTriple.codec)

            and! components =
                Codec.field "components" (fun l -> l.Components) (Codec.list lockedComponent)

            return
                {
                    Format = format
                    Requires = requires
                    Targets = targets
                    Components = components
                }
        }
        |> Codec.strict
        |> Codec.describe
            "The component lockfile: the components resolved for the requirements, with a method for each target."
        |> Codec.named "Lockfile"

    /// The file's text: canonical JSON (RFC 8785 key order) indented for review, with a final
    /// newline. The same lockfile always renders to the same bytes.
    let render (lockfile: Lockfile) : string =
        Json.serializeIndented (Json.canonicalize (codec.Encode lockfile)) + "\n"

    let parse (text: string) : Result<Lockfile, string> =
        Codec.decodeText codec text |> Result.mapError (Issue.renderAtMost 10)

    /// `sha256:<hex>` of the canonical JSON: what profiles record (spec §15). Independent of the
    /// file's formatting.
    let hash (lockfile: Lockfile) : string =
        let canonical = Json.serializeCanonical (codec.Encode lockfile)

        "sha256:"
        + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes canonical))

    /// Requirements in the lockfile's order: sorted by id, then range text, without duplicates.
    let normalizeRequires (requires: ComponentRef list) : ComponentRef list =
        requires
        |> List.distinct
        |> List.sortWith (fun a b ->
            match String.CompareOrdinal(a.Id, b.Id) with
            | 0 -> String.CompareOrdinal(a.Version, b.Version)
            | c -> c)

    /// Whether the lockfile was planned for exactly these requirements and covers these targets:
    /// then it is used as it is (a frozen lockfile), and nothing is planned again.
    let isCurrent (requires: ComponentRef list) (targets: TargetTriple list) (lockfile: Lockfile) : bool =
        normalizeRequires requires = lockfile.Requires
        && targets |> List.forall (fun t -> List.contains t lockfile.Targets)

    let tryFind (id: string) (lockfile: Lockfile) : LockedComponent option =
        lockfile.Components |> List.tryFind (fun c -> c.Id = id)

    /// The install spec of a locked component for a target.
    let spec (target: TargetTriple) (locked: LockedComponent) : Result<InstallSpec, string> =
        match locked.Install |> List.tryFind (fun (t, _) -> t = target) with
        | None -> Error $"the lockfile has no install of {locked.Id} {locked.Version} for {target}"
        | Some(_, method) ->
            Ok
                { InstallSpec.create locked.Id locked.Version target method locked.Layout with
                    Exports = locked.Exports
                    Probe = locked.Probe
                    Size = locked.Size
                    DependsOn = locked.DependsOn
                }

    /// The locked components that requirements name, each checked against its range.
    let resolve (requires: ComponentRef list) (lockfile: Lockfile) : Result<string list, string> =
        let resolved =
            requires
            |> List.map (fun r ->
                match tryFind r.Id lockfile, VersionRange.parse r.Version with
                | _, Error e -> Error e
                | None, _ -> Error $"the lockfile has no component {r.Id}; plan it again"
                | Some locked, Ok range when not (VersionRange.satisfies range locked.Version) ->
                    Error $"the lockfile has {r.Id} {locked.Version}, which is not in {r.Version}; plan it again"
                | Some locked, Ok _ -> Ok locked.Id)

        match
            resolved
            |> List.tryPick (function
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some error -> Error error
        | None ->
            Ok(
                resolved
                |> List.choose (function
                    | Ok id -> Some id
                    | Error _ -> None)
            )

    /// What to install on a target for these components: them and their dependencies, each after
    /// the components it depends on (ties by id).
    let installs (target: TargetTriple) (ids: string list) (lockfile: Lockfile) : Result<InstallSpec list, string> =
        let ordered = ResizeArray<InstallSpec>()
        let mutable visiting = Set.empty
        let mutable visited = Set.empty
        let mutable error = None
        // Iterative depth-first search: a stack of (id, dependencies left to visit).
        let stack = Collections.Generic.Stack<string * string list>()

        let enter (id: string) =
            if error.IsNone && not (visited.Contains id) then
                if visiting.Contains id then
                    error <- Some $"the lockfile's dependencies form a cycle through {id}"
                else
                    match tryFind id lockfile with
                    | None -> error <- Some $"the lockfile has no component {id}"
                    | Some locked ->
                        visiting <- visiting.Add id
                        stack.Push((id, List.sort locked.DependsOn))

        for root in List.sort (List.distinct ids) do
            enter root

            while error.IsNone && stack.Count > 0 do
                match stack.Pop() with
                | id, next :: rest ->
                    stack.Push((id, rest))
                    enter next
                | id, [] ->
                    visiting <- visiting.Remove id
                    visited <- visited.Add id

                    match tryFind id lockfile with
                    | Some locked ->
                        match spec target locked with
                        | Ok spec -> ordered.Add spec
                        | Error e -> error <- Some e
                    | None -> ()

        match error with
        | Some error -> Error error
        | None -> Ok(List.ofSeq ordered)
