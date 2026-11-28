namespace Fx.Sdk

open System
open System.Text.RegularExpressions
open Fx.Contracts
open Fx.Effects

/// How the host reaches envd in an environment (spec §9.2 `TransportKind`).
[<RequireQualifiedAccess>]
type TransportKind =
    /// Embedded mode (spec §3): envd runs in the host's own process and speaks over the in-process
    /// transport. Only for environments on the host's machine (`EnvMachine.SameHost`).
    | InProcess
    /// envd's standard input and output, through a process the provider starts (`Exec`).
    | Piped
    /// The host connects to an endpoint the provider gives (`Connect`).
    | Direct
    /// envd dials out to a relay that the host reaches too (T504).
    | Dialback

    override this.ToString() =
        match this with
        | InProcess -> "in-process"
        | Piped -> "piped"
        | Direct -> "direct"
        | Dialback -> "dialback"

/// What a provider can snapshot of a whole machine (spec §9.2 `SnapshotKind`). Workspace snapshots
/// are envd's (shadow git), whatever the provider.
[<RequireQualifiedAccess>]
type SnapshotKind =
    /// The machine's disk: an image or a volume.
    | Disk
    /// The running machine, processes included.
    | Memory

    override this.ToString() =
        match this with
        | Disk -> "disk"
        | Memory -> "memory"

/// Where an environment's workspace comes from (spec §9.3).
[<RequireQualifiedAccess>]
type MaterializeKind =
    /// A directory that exists in the environment, used in place.
    | Existing
    /// A host directory mounted into the environment.
    | Mount
    /// A clone of a repository at a ref.
    | Git
    /// A copy of a host directory, uploaded.
    | Upload
    /// A provider snapshot.
    | Snapshot

    override this.ToString() =
        match this with
        | Existing -> "existing"
        | Mount -> "mount"
        | Git -> "git"
        | Upload -> "upload"
        | Snapshot -> "snapshot"

/// How work leaves an environment after a diff review (spec §9.3).
[<RequireQualifiedAccess>]
type ExportKind =
    | Patch
    | Branch
    | Pr
    | Download

    override this.ToString() =
        match this with
        | Patch -> "patch"
        | Branch -> "branch"
        | Pr -> "pr"
        | Download -> "download"

/// Where an environment's components come from (spec §6.1 placement).
[<RequireQualifiedAccess>]
type ComponentPlacement =
    /// The environment's installer downloads them from the lockfile.
    | EnvFetch
    /// The host downloads them and pushes them into the environment.
    | HostPush
    /// They are already in the environment's image.
    | Prebaked

    override this.ToString() =
        match this with
        | EnvFetch -> "env-fetch"
        | HostPush -> "host-push"
        | Prebaked -> "prebaked"

/// What a provider can do (spec §9.2 `ProviderCaps`). The host checks environment specs against
/// it before anything is created.
type ProviderCaps =
    {
        /// The transports it offers, the default first.
        Transports: TransportKind list
        Suspend: bool
        Fork: bool
        Snapshots: SnapshotKind list
        Materialize: MaterializeKind list
        /// Its images can carry components (`ComponentPlacement.Prebaked`).
        Prebaked: bool
        Gpu: bool
    }

    override this.ToString() =
        let transports = this.Transports |> List.map string |> String.concat ", "
        let materialize = this.Materialize |> List.map string |> String.concat ", "
        $"caps (transports {transports}; materialize {materialize}; suspend {this.Suspend}; fork {this.Fork})"

/// Where an environment's workspace comes from and how work leaves it (spec §9.3, §9.4 `workspace`).
type WorkspaceSpec =
    {
        Source: MaterializeKind
        /// The directory: absolute or under `~` in the environment (`existing`), on the host (`mount`,
        /// `upload`). `None`: the host's project directory, which the host fills in.
        Path: string option
        /// The ref to check out (`git`).
        Ref: string option
        Export: ExportKind option
    }

    override this.ToString() =
        let path = this.Path |> Option.defaultValue "<project>"
        $"workspace {this.Source} {path}"

/// How an environment gets its components (spec §9.4 `components`).
type ComponentsSpec =
    {
        Placement: ComponentPlacement
        /// Where they come from when `Placement` cannot provide them.
        Fallback: ComponentPlacement option
    }

    override this.ToString() =
        match this.Fallback with
        | Some fallback -> $"components {this.Placement} (else {fallback})"
        | None -> $"components {this.Placement}"

/// Commands run in a new environment once its components are installed (spec §9.4 `setup`).
type SetupSpec =
    {
        /// Command lines for the environment's shell, run in the workspace in order; each must exit 0.
        Run: string list
        /// The network policy while they run.
        Network: string option
        /// Secrets they may use, by name (`secret:<name>` in the host's stores).
        Secrets: string list
    }

    override this.ToString() = $"setup ({this.Run.Length} commands)"

/// Limits the host applies to an environment (spec §9.4 `policy`); recorded, enforced by later tasks.
type EnvPolicy =
    {
        Network: string option
        /// e.g. `suspend:15m`.
        Idle: string option
        /// e.g. `8h`.
        Ttl: string option
        /// e.g. `$5/day`.
        Budget: string option
    }

    override this.ToString() = "policy"

/// An environment spec (spec §9.4): data, read from config (`environments.<name>`).
type EnvSpec =
    {
        /// The spec's name, its key in config: lowercase letters, digits, `-` and `_`.
        Name: string
        /// The provider's kind, e.g. `local`.
        Provider: string
        /// The provider's own settings, checked by its `Config` codec; `{}` when none.
        Config: Json
        /// The transport; `None`: the provider's default (the first of its `Transports`).
        Transport: TransportKind option
        /// The sandbox driver, e.g. `none`, `bubblewrap`; `None`: the default (T113).
        Sandbox: string option
        /// The network policy, e.g. `open`, `allowlist:dev`; `None`: unrestricted.
        Network: string option
        Workspace: WorkspaceSpec
        Components: ComponentsSpec
        Setup: SetupSpec
        Policy: EnvPolicy
        /// The variables the environment's commands get (spec §9.4 `variables`), by name: `host:<NAME>`
        /// (the host's own variable), `secret:<name>` (an fx secret) or a literal value (only for names
        /// that do not look like credentials). The host decides what applies: a project's credentials
        /// need a person's consent (docs/modules/environments.md, "Variables").
        Variables: Map<string, string>
    }

    override this.ToString() =
        $"environment {this.Name} ({this.Provider}, {this.Workspace})"

/// What a provider holds for one environment (spec §9.2 `Lease`). The host persists it and gives it
/// back to every call about that environment, also after a restart.
type Lease =
    {
        /// The environment's id, as the host passed it to `Create`: providers name what they create
        /// after it, so creating again is idempotent and the host finds its leases in `List`.
        Id: string
        /// The provider's kind.
        Provider: string
        /// The workspace: an absolute path in the environment.
        Workspace: string
        /// A directory in the environment that belongs to this environment alone, outside the
        /// workspace: envd's state, assets and owned directories live under it.
        Home: string
        /// Provider-specific facts (a container id, a host name, …).
        Data: Map<string, string>
    }

    override this.ToString() = $"lease {this.Id} ({this.Provider})"

/// The machine an environment runs on, as its provider reports it before envd runs (spec §9.2
/// `Platform`); envd reports the rest when the host connects.
type EnvMachine =
    {
        /// What binaries must be built for: picks envd and the components.
        Target: TargetTriple
        /// The product's per-user data directory on that machine (`~/.<product>`,
        /// `%LOCALAPPDATA%\<product>`): where components and envd binaries go.
        DataDir: string
        /// The environment is the host's own machine and filesystem: the host's binary runs there as
        /// it is, and the host's installs are visible there.
        SameHost: bool
    }

    override this.ToString() =
        let where =
            if this.SameHost then
                "the host's machine"
            else
                "another machine"

        $"{this.Target} ({where})"

/// A file the host copies into an environment (spec §9.2 `Upload`), e.g. envd for its target.
type Upload =
    {
        /// The file on the host (absolute).
        Source: string
        /// Where it goes in the environment (absolute).
        Destination: string
        /// Whether to make it executable.
        Executable: bool
    }

    override this.ToString() = $"{this.Source} -> {this.Destination}"

/// Where envd listens, for the `Direct` transport (spec §9.2 `Endpoint`).
[<RequireQualifiedAccess>]
type Endpoint =
    /// A WebSocket URL (T501, T502).
    | WebSocket of url: string

    override this.ToString() =
        match this with
        | WebSocket url -> url

/// A snapshot a provider took of a whole machine (spec §9.2 `ProviderSnapshot`).
type ProviderSnapshot =
    {
        Id: string
        Kind: SnapshotKind
        Data: Map<string, string>
    }

    override this.ToString() = $"snapshot {this.Id} ({this.Kind})"

/// A provider's expected failures. Defects are exceptions.
[<RequireQualifiedAccess>]
type ProviderError =
    /// The spec, its `config` or the request is not one the provider accepts; the issues say why.
    | Invalid of Issue list
    /// The provider does not do this (yet), e.g. a materialize kind; the text names it.
    | Unsupported of what: string
    /// The provider has no such lease.
    | NotFound of lease: string
    /// The provider tried and failed (an I/O error, a remote API's answer).
    | Failed of reason: string

    override this.ToString() =
        match this with
        | Invalid issues -> "invalid:\n" + Issue.renderAtMost 10 issues
        | Unsupported what -> $"not supported: {what}"
        | NotFound lease -> $"no lease {lease}"
        | Failed reason -> reason

/// What the host asks a provider to create.
type EnvRequest =
    {
        /// The environment's id: unique, chosen by the host. The lease's `Id`.
        Id: string
        Spec: EnvSpec
    }

    override this.ToString() = $"create {this.Id} from {this.Spec}"

/// Facts about the host's machine that providers need, read once by the composition root.
type HostInfo =
    {
        /// What the host's binary is built for.
        Target: TargetTriple
        /// The product's per-user data directory (`~/.<product>`, `%LOCALAPPDATA%\<product>`).
        DataDir: string
        /// The user's home directory, for `~` in paths.
        Home: string
    }

    override this.ToString() =
        $"host {this.Target} (data in {this.DataDir})"

/// An environment that knows the host's machine.
type IHostInfo =
    abstract HostInfo: HostInfo

/// The environment an environment provider's code runs in (spec §9.2): its extension's environment,
/// the host machine's files and processes (through which providers reach their environments: a
/// local directory, `ssh`, `docker`, …) and facts about the host. Providers are privileged
/// contributions (spec §16.5), so only extensions from the binary, host config or policy get it.
type IProviderEnv =
    inherit IExtensionEnv
    inherit IFsRead
    inherit IFs
    inherit IShell
    inherit IHostInfo

/// An environment provider (spec §9.2): creates environments and starts processes in them; the host
/// bootstraps envd with it (`Platform`, `Upload`, `Exec`) and keeps its leases. Contributed by
/// native extensions under `EnvProviders.contributionType` (privileged), with the kind as the
/// contribution id. Every operation runs in the provider's `IProviderEnv` with the caller's scope.
/// Module spec: docs/modules/environments.md.
[<NoEquality; NoComparison>]
type EnvProvider =
    {
        /// What specs name in `provider`, e.g. `local`.
        Kind: string
        Description: string
        Caps: ProviderCaps
        /// Checks the spec's `config` member (spec §9.2 `Config`): the provider's settings.
        Config: Codec<Json>
        /// Creates the environment, or finds the one an earlier call created for the same id.
        Create: EnvRequest -> Eff<IProviderEnv, Result<Lease, ProviderError>>
        Platform: Lease -> Eff<IProviderEnv, Result<EnvMachine, ProviderError>>
        /// Starts a process in the environment with pipes; the host starts envd with it (`Piped`).
        Exec: Lease * ProcSpec -> Eff<IProviderEnv, Result<Proc, ProviderError>>
        /// Copies a host file into the environment.
        Upload: Lease * Upload -> Eff<IProviderEnv, Result<unit, ProviderError>>
        /// Where envd listens (`Direct`).
        Connect: (Lease -> Eff<IProviderEnv, Result<Endpoint, ProviderError>>) option
        Suspend: (Lease -> Eff<IProviderEnv, Result<unit, ProviderError>>) option
        Resume: (Lease -> Eff<IProviderEnv, Result<unit, ProviderError>>) option
        Snapshot: (Lease -> Eff<IProviderEnv, Result<ProviderSnapshot, ProviderError>>) option
        /// Copies of the environment, `n` of them.
        Fork: (Lease * int -> Eff<IProviderEnv, Result<Lease list, ProviderError>>) option
        /// Releases everything the lease holds (never what it did not create, such as an existing
        /// workspace). Destroying what is gone succeeds.
        Destroy: Lease -> Eff<IProviderEnv, Result<unit, ProviderError>>
        /// The leases the provider still holds: what the host reconciles its records with.
        List: Eff<IProviderEnv, Result<Lease list, ProviderError>>
    }

    override this.ToString() =
        $"environment provider {this.Kind} ({this.Caps})"

/// The environment a provider's code runs in, built by the host for one provider.
type ProviderEnv =
    {
        /// The provider's extension's environment (its id, clock and scope).
        Extension: IExtensionEnv
        FsRead: FsRead
        Fs: Fs
        Shell: Shell
        HostInfo: HostInfo
    }

    interface IProviderEnv

    interface IExtensionEnv with
        member env.Extension = env.Extension.Extension
        member env.Capabilities = env.Extension.Capabilities

    interface IClock with
        member env.Clock = env.Extension.Clock

    interface Fx.Variants.IVariants with
        member env.Variants = env.Extension.Variants

    interface IPackages with
        member env.Packages = env.Extension.Packages

    interface IGrants with
        member env.Grants = env.Extension.Grants

    interface IScopeWith<IExtensionEnv> with
        member env.Scope = env.Extension.Scope

        member env.WithScope scope =
            { env with
                Extension = env.Extension.WithScope scope
            }
            :> IExtensionEnv

    interface IFsRead with
        member env.FsRead = env.FsRead

    interface IFs with
        member env.Fs = env.Fs

    interface IShell with
        member env.Shell = env.Shell

    interface IHostInfo with
        member env.HostInfo = env.HostInfo

    override env.ToString() =
        $"ProviderEnv({env.Extension.Extension})"

[<RequireQualifiedAccess>]
module ProviderEnv =
    /// Runs provider code in the provider's environment with the caller's scope.
    let within (providerEnv: ProviderEnv) (effect: Eff<IProviderEnv, 'T>) : Eff<#IScope, 'T> =
        fun caller ->
            effect
                { providerEnv with
                    Extension = providerEnv.Extension.WithScope caller.Scope
                }

/// Codecs and checks of environment specs and what providers return.
[<RequireQualifiedAccess>]
module EnvSpec =
    /// Spec names: lowercase letters, digits, `-` and `_`, starting with a letter.
    [<Literal>]
    let NamePattern = "^[a-z][a-z0-9_-]*$"

    /// Provider kinds: lowercase letters, digits and `-`, starting with a letter.
    [<Literal>]
    let KindPattern = "^[a-z][a-z0-9-]*$"

    let transportKind: Codec<TransportKind> =
        Codec.enum
            [
                TransportKind.InProcess, "in-process"
                TransportKind.Piped, "piped"
                TransportKind.Direct, "direct"
                TransportKind.Dialback, "dialback"
            ]
        |> Codec.describe "How the host reaches envd: in-process (embedded), piped, direct or dialback."
        |> Codec.named "TransportKind"

    let snapshotKind: Codec<SnapshotKind> =
        Codec.enum [ SnapshotKind.Disk, "disk"; SnapshotKind.Memory, "memory" ]
        |> Codec.named "SnapshotKind"

    let materializeKind: Codec<MaterializeKind> =
        Codec.enum
            [
                MaterializeKind.Existing, "existing"
                MaterializeKind.Mount, "mount"
                MaterializeKind.Git, "git"
                MaterializeKind.Upload, "upload"
                MaterializeKind.Snapshot, "snapshot"
            ]
        |> Codec.describe "Where the workspace comes from."
        |> Codec.named "MaterializeKind"

    let exportKind: Codec<ExportKind> =
        Codec.enum
            [
                ExportKind.Patch, "patch"
                ExportKind.Branch, "branch"
                ExportKind.Pr, "pr"
                ExportKind.Download, "download"
            ]
        |> Codec.named "ExportKind"

    let placement: Codec<ComponentPlacement> =
        Codec.enum
            [
                ComponentPlacement.EnvFetch, "env-fetch"
                ComponentPlacement.HostPush, "host-push"
                ComponentPlacement.Prebaked, "prebaked"
            ]
        |> Codec.named "ComponentPlacement"

    let caps: Codec<ProviderCaps> =
        Codec.record {
            let! transports =
                Codec.field "transports" (fun (c: ProviderCaps) -> c.Transports) (Codec.list transportKind)

            and! suspend = Codec.field "suspend" (fun c -> c.Suspend) Codec.bool
            and! fork = Codec.field "fork" (fun c -> c.Fork) Codec.bool

            and! snapshots =
                Codec.field "snapshots" (fun c -> c.Snapshots) (Codec.list snapshotKind)

            and! materialize =
                Codec.field "materialize" (fun c -> c.Materialize) (Codec.list materializeKind)

            and! prebaked = Codec.field "prebaked" (fun c -> c.Prebaked) Codec.bool
            and! gpu = Codec.field "gpu" (fun c -> c.Gpu) Codec.bool

            return
                {
                    Transports = transports
                    Suspend = suspend
                    Fork = fork
                    Snapshots = snapshots
                    Materialize = materialize
                    Prebaked = prebaked
                    Gpu = gpu
                }
        }
        |> Codec.describe "What an environment provider can do."
        |> Codec.named "ProviderCaps"

    /// A workspace that exists in the environment, at the project directory.
    let defaultWorkspace: WorkspaceSpec =
        {
            Source = MaterializeKind.Existing
            Path = None
            Ref = None
            Export = None
        }

    let defaultComponents: ComponentsSpec =
        {
            Placement = ComponentPlacement.EnvFetch
            Fallback = None
        }

    let noSetup: SetupSpec =
        {
            Run = []
            Network = None
            Secrets = []
        }

    let noPolicy: EnvPolicy =
        {
            Network = None
            Idle = None
            Ttl = None
            Budget = None
        }

    let workspace: Codec<WorkspaceSpec> =
        Codec.record {
            let! source =
                Codec.defaultField
                    "source"
                    (fun (w: WorkspaceSpec) -> w.Source)
                    MaterializeKind.Existing
                    materializeKind

            and! path =
                Codec.optionalField "path" (fun w -> w.Path) (Codec.string |> Codec.nonEmpty)

            and! reference =
                Codec.optionalField "ref" (fun w -> w.Ref) (Codec.string |> Codec.nonEmpty)

            and! export = Codec.optionalField "export" (fun w -> w.Export) exportKind

            return
                {
                    Source = source
                    Path = path
                    Ref = reference
                    Export = export
                }
        }
        |> Codec.strict
        |> Codec.describe "Where the workspace comes from and how work leaves it."
        |> Codec.named "WorkspaceSpec"

    let components: Codec<ComponentsSpec> =
        Codec.record {
            let! placement' =
                Codec.defaultField
                    "placement"
                    (fun (c: ComponentsSpec) -> c.Placement)
                    ComponentPlacement.EnvFetch
                    placement

            and! fallback = Codec.optionalField "fallback" (fun c -> c.Fallback) placement

            return
                {
                    Placement = placement'
                    Fallback = fallback
                }
        }
        |> Codec.strict
        |> Codec.named "ComponentsSpec"

    let setup: Codec<SetupSpec> =
        Codec.record {
            let! run =
                Codec.defaultField "run" (fun (s: SetupSpec) -> s.Run) [] (Codec.list (Codec.string |> Codec.nonEmpty))

            and! network =
                Codec.optionalField "network" (fun s -> s.Network) (Codec.string |> Codec.nonEmpty)

            and! secrets =
                Codec.defaultField "secrets" (fun s -> s.Secrets) [] (Codec.list (Codec.string |> Codec.nonEmpty))

            return
                {
                    Run = run
                    Network = network
                    Secrets = secrets
                }
        }
        |> Codec.strict
        |> Codec.named "SetupSpec"

    let policy: Codec<EnvPolicy> =
        let text = Codec.string |> Codec.nonEmpty

        Codec.record {
            let! network = Codec.optionalField "network" (fun (p: EnvPolicy) -> p.Network) text
            and! idle = Codec.optionalField "idle" (fun p -> p.Idle) text
            and! ttl = Codec.optionalField "ttl" (fun p -> p.Ttl) text
            and! budget = Codec.optionalField "budget" (fun p -> p.Budget) text

            return
                {
                    Network = network
                    Idle = idle
                    Ttl = ttl
                    Budget = budget
                }
        }
        |> Codec.strict
        |> Codec.named "EnvPolicy"

    /// The `variables` member: names to `host:<NAME>`, `secret:<name>` or a literal value. Absent
    /// reads as none, and none is not written, so specs recorded before it read and write as they did.
    let variablesField: FieldSpec<Map<string, string>> =
        let values =
            Codec.stringMap Codec.string
            |> Codec.describe
                "Variables the environment's commands get, by name: \"host:<NAME>\" (the host's variable), \"secret:<name>\" (an fx secret) or a literal value (not for names that look like credentials)."

        { Field.withDefault "variables" Map.empty values with
            Encode =
                fun value ->
                    if Map.isEmpty value then
                        None
                    else
                        Some(values.Encode value)
        }

    /// An environment spec with its name; config files give the name as the member's key
    /// (`EnvSpecs.ofConfig` in the core).
    let codec: Codec<EnvSpec> =
        Codec.record {
            let! name =
                Codec.field
                    "name"
                    (fun (s: EnvSpec) -> s.Name)
                    (Codec.string
                     |> Codec.pattern NamePattern
                     |> Codec.describe "The environment's name.")

            and! provider =
                Codec.field
                    "provider"
                    (fun s -> s.Provider)
                    (Codec.string
                     |> Codec.pattern KindPattern
                     |> Codec.describe "The provider's kind, e.g. \"local\".")

            and! config =
                Codec.defaultField "config" (fun s -> s.Config) (Json.Object []) Codec.json

            and! transport = Codec.optionalField "transport" (fun s -> s.Transport) transportKind

            and! sandbox =
                Codec.optionalField "sandbox" (fun s -> s.Sandbox) (Codec.string |> Codec.nonEmpty)

            and! network =
                Codec.optionalField "network" (fun s -> s.Network) (Codec.string |> Codec.nonEmpty)

            and! workspace' =
                Codec.defaultField "workspace" (fun s -> s.Workspace) defaultWorkspace workspace

            and! components' =
                Codec.defaultField "components" (fun s -> s.Components) defaultComponents components

            and! setup' = Codec.defaultField "setup" (fun s -> s.Setup) noSetup setup
            and! policy' = Codec.defaultField "policy" (fun s -> s.Policy) noPolicy policy
            and! variables' = Codec.fieldOf variablesField (fun s -> s.Variables)


            return
                {
                    Name = name
                    Provider = provider
                    Config = config
                    Transport = transport
                    Sandbox = sandbox
                    Network = network
                    Workspace = workspace'
                    Components = components'
                    Setup = setup'
                    Policy = policy'
                    Variables = variables'
                }
        }
        |> Codec.strict
        |> Codec.describe "An environment spec: which provider creates it, and how."
        |> Codec.named "EnvSpec"

    /// A spec with only a name and a provider; everything else at its default.
    let create (name: string) (provider: string) : EnvSpec =
        {
            Name = name
            Provider = provider
            Config = Json.Object []
            Transport = None
            Sandbox = None
            Network = None
            Workspace = defaultWorkspace
            Components = defaultComponents
            Setup = noSetup
            Policy = noPolicy
            Variables = Map.empty
        }

    /// The same spec with an existing workspace at a path.
    let withWorkspace (path: string) (spec: EnvSpec) : EnvSpec =
        { spec with
            Workspace =
                { spec.Workspace with
                    Source = MaterializeKind.Existing
                    Path = Some path
                }
        }

    let lease: Codec<Lease> =
        Codec.record {
            let! id = Codec.field "id" (fun (l: Lease) -> l.Id) (Codec.string |> Codec.nonEmpty)

            and! provider =
                Codec.field "provider" (fun l -> l.Provider) (Codec.string |> Codec.pattern KindPattern)

            and! workspace' =
                Codec.field "workspace" (fun l -> l.Workspace) (Codec.string |> Codec.nonEmpty)

            and! home = Codec.field "home" (fun l -> l.Home) (Codec.string |> Codec.nonEmpty)

            and! data =
                Codec.defaultField "data" (fun l -> l.Data) Map.empty (Codec.stringMap Codec.string)

            return
                {
                    Id = id
                    Provider = provider
                    Workspace = workspace'
                    Home = home
                    Data = data
                }
        }
        |> Codec.describe "What a provider holds for one environment."
        |> Codec.named "Lease"

    let machine: Codec<EnvMachine> =
        Codec.record {
            let! target =
                Codec.field "target" (fun (m: EnvMachine) -> m.Target) TargetTriple.codec

            and! dataDir =
                Codec.field "dataDir" (fun m -> m.DataDir) (Codec.string |> Codec.nonEmpty)

            and! sameHost = Codec.field "sameHost" (fun m -> m.SameHost) Codec.bool

            return
                {
                    Target = target
                    DataDir = dataDir
                    SameHost = sameHost
                }
        }
        |> Codec.describe "The machine an environment runs on, as its provider reports it."
        |> Codec.named "EnvMachine"

/// The `environment-provider` contribution type and checks of providers.
[<RequireQualifiedAccess>]
module EnvProviders =
    /// Environment providers (spec §9.1), by kind (the contribution id). Code that runs in the host's
    /// process: native extensions contribute them. Privileged (spec §16.5).
    let contributionType: ContributionType<EnvProvider> =
        ContributionType.inProcess
            "environment-provider"
            "An environment provider: creates environments and starts envd in them."
            ContributionKind.Code
        |> ContributionType.privileged

    /// Every issue with a provider's data: its kind, and whether its caps match the operations it
    /// provides. Empty for a valid provider.
    let validate (provider: EnvProvider) : Issue list =
        let at (field: string) (message: string) =
            Issue.at [ PathSegment.Key field ] message

        let caps = provider.Caps
        let has (transport: TransportKind) = List.contains transport caps.Transports

        [
            if not (Regex.IsMatch(provider.Kind, EnvSpec.KindPattern)) then
                at "kind" $"expected lowercase letters, digits and \"-\", got {Json.quote provider.Kind}"
            if caps.Transports.IsEmpty then
                at "caps" "a provider offers at least one transport"
            if List.distinct caps.Transports <> caps.Transports then
                at "caps" "transports are listed once each"
            if caps.Materialize.IsEmpty then
                at "caps" "a provider materializes workspaces in at least one way"
            if has TransportKind.Direct <> provider.Connect.IsSome then
                at "connect" "Connect is provided exactly when the Direct transport is offered"
            if caps.Suspend <> (provider.Suspend.IsSome && provider.Resume.IsSome) then
                at "suspend" "Suspend and Resume are provided exactly when caps.suspend is true"
            if provider.Suspend.IsSome <> provider.Resume.IsSome then
                at "resume" "Suspend and Resume come together"
            if caps.Fork <> provider.Fork.IsSome then
                at "fork" "Fork is provided exactly when caps.fork is true"
            if caps.Snapshots.IsEmpty = provider.Snapshot.IsSome then
                at "snapshot" "Snapshot is provided exactly when caps.snapshots is not empty"
        ]
