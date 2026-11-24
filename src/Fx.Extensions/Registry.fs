namespace Fx.Extensions

open System
open Fx.Machines
open Fx.Sdk

/// Where an extension came from, which decides how far it is trusted (spec §16.5, §18).
[<RequireQualifiedAccess>]
type Source =
    /// Bundled in the binary.
    | Builtin
    /// From policy: it can be neither disabled nor replaced.
    | Policy
    /// From the user's host configuration.
    | HostConfig
    /// From the project (`.fx/`): only once the project is trusted, and never privileged.
    | Project

    override this.ToString() =
        match this with
        | Builtin -> "builtin"
        | Policy -> "policy"
        | HostConfig -> "host-config"
        | Project -> "project"

module Source =
    /// How far a source is trusted: an extension may replace only extensions of a source trusted no
    /// more than its own, and never a policy extension.
    let rank (source: Source) : int =
        match source with
        | Source.Project -> 0
        | Source.Builtin -> 1
        | Source.HostConfig -> 2
        | Source.Policy -> 3

    /// Whether extensions from this source may contribute privileged types (spec §16.5: only from
    /// host config or policy; built-ins come with the binary).
    let mayContributePrivileged (source: Source) : bool = source <> Source.Project

/// A contribution's key: its type and id. Unique among the extensions admitted at a time.
type ContributionKey =
    {
        Type: string
        Id: string
    }

    override this.ToString() = this.Type + "/" + this.Id

/// A contribution as admission sees it: its key, and what the host's catalog says about its type
/// (never what the manifest says).
type DeclaredContribution =
    {
        Key: ContributionKey
        /// Whether the host knows the type.
        Known: bool
        Privileged: bool
    }

    override this.ToString() = string this.Key

/// The host's side of admission: facts from the world that `Handle` decides with.
type TrustContext =
    {
        /// Whether the user trusts the project. Until then, project extensions are refused.
        ProjectTrusted: bool
        /// Whether extensions from outside the binary must be pinned by hash in host config.
        RequirePins: bool
        /// Where this registry runs (`host` or `environment`); extensions placed elsewhere are refused.
        Placement: string
        /// The SDK version the host implements.
        Sdk: string
        /// The extension protocol versions the host speaks.
        Protocols: string list
    }

    override this.ToString() =
        $"trusted project: {this.ProjectTrusted}, pins required: {this.RequirePins}, placement: {this.Placement}"

/// What admitting an extension needs: its manifest's facts, its hash, its source and pin, and the
/// host's trust context. The host builds it before running any of the extension's code.
type AdmitRequest =
    {
        Id: string
        Version: string
        Hash: string
        Runtime: string
        Placement: string
        Sdk: string
        Protocol: string
        Source: Source
        Contributions: DeclaredContribution list
        Methods: string list
        Replaces: string list
        /// The hash host config pins this extension to, if any.
        Pin: string option
        Trust: TrustContext
    }

    override this.ToString() =
        $"admit {this.Id} {this.Version} ({this.Runtime}, {this.Source})"

/// An admitted extension, as recorded: what the registry needs to replay.
type Admission =
    {
        Id: string
        Version: string
        Hash: string
        Runtime: string
        Placement: string
        Source: Source
        Contributions: ContributionKey list
        Methods: string list
        Replaces: string list
    }

    override this.ToString() =
        $"{this.Id} {this.Version} ({this.Runtime}, {this.Source})"

/// Why an extension was disabled.
[<RequireQualifiedAccess>]
type DisableReason =
    | Requested of reason: string
    | ReplacedBy of id: string

    override this.ToString() =
        match this with
        | Requested reason -> "requested: " + reason
        | ReplacedBy id -> "replaced by " + id

/// What happened to the registry: facts, kept forever.
[<RequireQualifiedAccess>]
type RegistryEvent =
    /// Admitted: trusted, pinned and without conflicts. Its contributions are reserved, but not in
    /// effect until it is activated.
    | Admitted of Admission
    /// Its runtime activated it and it provides exactly what it declared: its contributions are in effect.
    | Activated of id: string
    | ActivationFailed of id: string * reason: string
    | Disabled of id: string * reason: DisableReason

    override this.ToString() =
        match this with
        | Admitted admission -> "admitted " + string admission
        | Activated id -> "activated " + id
        | ActivationFailed(id, reason) -> $"activation of {id} failed: {reason}"
        | Disabled(id, reason) -> $"disabled {id} ({reason})"

[<RequireQualifiedAccess>]
type RegistryCommand =
    | Admit of AdmitRequest
    /// The runtime activated the extension; what it provides.
    | Activate of id: string * provided: ContributionKey list * methods: string list
    | Fail of id: string * reason: string
    | Disable of id: string * reason: string

    override this.ToString() =
        match this with
        | Admit request -> string request
        | Activate(id, provided, methods) ->
            $"activate {id} ({provided.Length} contributions, {methods.Length} methods)"
        | Fail(id, reason) -> $"fail {id}: {reason}"
        | Disable(id, reason) -> $"disable {id}: {reason}"

[<RequireQualifiedAccess>]
type Status =
    | Admitted
    | Active
    | Failed of reason: string
    | Disabled of DisableReason

    override this.ToString() =
        match this with
        | Admitted -> "admitted"
        | Active -> "active"
        | Failed reason -> "failed: " + reason
        | Disabled reason -> "disabled: " + string reason

type Entry =
    {
        Admission: Admission
        Status: Status
    }

    override this.ToString() = $"{this.Admission} {this.Status}"

/// The registry: a fold of its events (spec §16 "replayed transforms"). Replaying the same events
/// gives the same registry; which contributions are in effect, and in what order, follows from it.
type RegistryState =
    {
        Entries: Map<string, Entry>
        /// Extension ids in admission order: the order of their contributions.
        Order: string list
        /// Which extension holds each contribution key (admitted or active).
        Owners: Map<ContributionKey, string>
    }

    override this.ToString() =
        $"Registry({this.Entries.Count} extensions, {this.Owners.Count} contributions)"

[<RequireQualifiedAccess>]
module Registry =

    let empty: RegistryState =
        {
            Entries = Map.empty
            Order = []
            Owners = Map.empty
        }

    let private reject (code: string) (message: string) : Result<RegistryEvent list, Rejection> =
        Error(Rejection.create code message)

    let private listed (items: string seq) = String.Join(", ", items)

    let private keys (admission: Admission) = set admission.Contributions

    /// The extensions whose contributions are in effect, in admission order.
    let active (state: RegistryState) : string list =
        state.Order
        |> List.filter (fun id ->
            match state.Entries.TryFind id with
            | Some entry -> entry.Status = Status.Active
            | None -> false)

    let private admit (state: RegistryState) (request: AdmitRequest) : Result<RegistryEvent list, Rejection> =
        let existing = state.Entries.TryFind request.Id
        let trust = request.Trust

        let replaced =
            request.Replaces
            |> List.choose (fun id -> state.Entries.TryFind id |> Option.map (fun entry -> id, entry))

        let unknownTypes =
            request.Contributions
            |> List.filter (fun c -> not c.Known)
            |> List.map (fun c -> c.Key.Type)
            |> List.distinct

        let privileged =
            request.Contributions |> List.filter (fun c -> c.Privileged) |> List.map string

        let releasing =
            replaced
            |> List.filter (fun (_, entry) ->
                match entry.Status with
                | Status.Admitted
                | Status.Active -> true
                | _ -> false)
            |> List.map fst
            |> set

        let conflicts =
            request.Contributions
            |> List.choose (fun c ->
                match state.Owners.TryFind c.Key with
                | Some owner when not (releasing.Contains owner) -> Some $"{c.Key} (held by {owner})"
                | _ -> None)

        match existing with
        | Some {
                   Status = (Status.Admitted | Status.Active)
               } -> reject "extension.duplicate" $"{request.Id} is already admitted"
        | Some {
                   Status = Status.Disabled(DisableReason.ReplacedBy by)
               } -> reject "extension.replaced" $"{request.Id} is replaced by {by}"
        | _ when request.Placement <> trust.Placement ->
            reject
                "extension.placement"
                $"{request.Id} runs in the {request.Placement}, and this registry is the {trust.Placement}'s"
        | _ when not (Versions.sdkCompatible trust.Sdk request.Sdk) ->
            reject "extension.sdk" $"{request.Id} needs SDK {request.Sdk}; this host implements SDK {trust.Sdk}"
        | _ when not (List.contains request.Protocol trust.Protocols) ->
            reject
                "extension.protocol"
                $"{request.Id} speaks extension protocol {request.Protocol}; this host speaks {listed trust.Protocols}"
        | _ when request.Source = Source.Project && not trust.ProjectTrusted ->
            reject
                "trust.untrusted-project"
                $"{request.Id} comes from the project, which is not trusted: nothing project-local runs before trust"
        | _ when request.Pin.IsSome && request.Pin <> Some request.Hash ->
            reject
                "trust.hash-mismatch"
                $"{request.Id} is pinned to {request.Pin.Value}, but its manifest's hash is {request.Hash}"
        | _ when request.Pin.IsNone && trust.RequirePins && request.Source <> Source.Builtin ->
            reject "trust.unpinned" $"{request.Id} is not pinned, and policy requires pinned extensions"
        | _ when not unknownTypes.IsEmpty ->
            reject
                "contribution.unknown-type"
                $"{request.Id} contributes types this host does not know: {listed unknownTypes}"
        | _ when not privileged.IsEmpty && not (Source.mayContributePrivileged request.Source) ->
            reject
                "trust.privileged"
                $"{request.Id} comes from the {request.Source} and may not contribute privileged types: {listed privileged}"
        | _ ->
            let forbidden =
                replaced
                |> List.filter (fun (_, entry) ->
                    entry.Admission.Source = Source.Policy
                    || Source.rank entry.Admission.Source > Source.rank request.Source)
                |> List.map fst

            if not forbidden.IsEmpty then
                reject
                    "trust.replace"
                    $"{request.Id} ({request.Source}) may not replace {listed forbidden}: policy extensions cannot be replaced, and others only by a source trusted as much"
            elif not conflicts.IsEmpty then
                reject "contribution.conflict" $"{request.Id} contributes what others hold: {listed conflicts}"
            else
                let admission: Admission =
                    {
                        Id = request.Id
                        Version = request.Version
                        Hash = request.Hash
                        Runtime = request.Runtime
                        Placement = request.Placement
                        Source = request.Source
                        Contributions = request.Contributions |> List.map (fun c -> c.Key)
                        Methods = request.Methods
                        Replaces = request.Replaces
                    }

                Ok(
                    (request.Replaces
                     |> List.filter releasing.Contains
                     |> List.map (fun id -> RegistryEvent.Disabled(id, DisableReason.ReplacedBy request.Id)))
                    @ [ RegistryEvent.Admitted admission ]
                )

    let private activate (state: RegistryState) id (provided: ContributionKey list) (methods: string list) =
        match state.Entries.TryFind id with
        | Some({ Status = Status.Admitted } as entry) ->
            let declared = keys entry.Admission
            let providedSet = set provided
            let missing = Set.difference declared providedSet |> Seq.map string |> List.ofSeq
            let extra = Set.difference providedSet declared |> Seq.map string |> List.ofSeq
            let declaredMethods = set entry.Admission.Methods
            let methodSet = set methods

            let missingMethods = Set.difference declaredMethods methodSet |> List.ofSeq

            let extraMethods = Set.difference methodSet declaredMethods |> List.ofSeq

            let problems =
                [
                    if not missing.IsEmpty then
                        "declared but not provided: " + listed missing
                    if not extra.IsEmpty then
                        "provided but not declared: " + listed extra
                    if not missingMethods.IsEmpty then
                        "methods declared but not provided: " + listed missingMethods
                    if not extraMethods.IsEmpty then
                        "methods provided but not declared: " + listed extraMethods
                    if provided.Length <> providedSet.Count then
                        "a contribution is provided twice"
                ]

            if problems.IsEmpty then
                Ok [ RegistryEvent.Activated id ]
            else
                let text = String.Join("; ", problems)
                reject "extension.mismatch" $"{id} does not provide what its manifest declares: {text}"
        | _ -> reject "extension.not-admitted" $"{id} is not waiting for activation"

    let private handle (state: RegistryState) (command: RegistryCommand) : Result<RegistryEvent list, Rejection> =
        match command with
        | RegistryCommand.Admit request -> admit state request
        | RegistryCommand.Activate(id, provided, methods) -> activate state id provided methods
        | RegistryCommand.Fail(id, reason) ->
            match state.Entries.TryFind id with
            | Some { Status = Status.Admitted } -> Ok [ RegistryEvent.ActivationFailed(id, reason) ]
            | _ -> reject "extension.not-admitted" $"{id} is not waiting for activation"
        | RegistryCommand.Disable(id, reason) ->
            match state.Entries.TryFind id with
            | Some { Admission = admission } when admission.Source = Source.Policy ->
                reject "trust.policy" $"{id} comes from policy and cannot be disabled"
            | Some {
                       Status = (Status.Admitted | Status.Active)
                   } -> Ok [ RegistryEvent.Disabled(id, DisableReason.Requested reason) ]
            | _ -> reject "extension.not-enabled" $"{id} is not admitted or active"

    let private release (id: string) (owners: Map<ContributionKey, string>) =
        owners |> Map.filter (fun _ owner -> owner <> id)

    let private setStatus (id: string) (status: Status) (state: RegistryState) =
        match state.Entries.TryFind id with
        | Some entry ->
            { state with
                Entries = state.Entries.Add(id, { entry with Status = status })
            }
        | None -> state

    let private evolve (state: RegistryState) (event: RegistryEvent) : RegistryState =
        match event with
        | RegistryEvent.Admitted admission ->
            {
                Entries =
                    state.Entries.Add(
                        admission.Id,
                        {
                            Admission = admission
                            Status = Status.Admitted
                        }
                    )
                Order = (state.Order |> List.filter (fun id -> id <> admission.Id)) @ [ admission.Id ]
                Owners =
                    admission.Contributions
                    |> List.fold (fun owners key -> Map.add key admission.Id owners) (release admission.Id state.Owners)
            }
        | RegistryEvent.Activated id -> setStatus id Status.Active state
        | RegistryEvent.ActivationFailed(id, reason) ->
            { setStatus id (Status.Failed reason) state with
                Owners = release id state.Owners
            }
        | RegistryEvent.Disabled(id, reason) ->
            { setStatus id (Status.Disabled reason) state with
                Owners = release id state.Owners
            }

    /// The registry as a machine (spec §4.4): `Handle` makes every trust decision from the command
    /// alone, `Evolve` folds the events. It has no intents: the host runs the extensions.
    let machine: Machine<RegistryCommand, RegistryEvent, RegistryState, unit> =
        {
            Initial = empty
            Evolve = evolve
            Handle = handle
            React = fun _ _ -> []
        }

    /// The registry after these events.
    let replay (events: RegistryEvent list) : RegistryState = Machine.replay machine events
