namespace Fx.Host

open System
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Variants
open Fx.Sdk
open Fx.Sdk.Process
open Fx.Extensions
open Fx.Environments
open Fx.Identity
open Fx.Rpc
open Fx.Auth

/// What the host starts with: the world as the entry point read it once (the process's variables, the
/// current directory, who runs it) and the interpreters of its capabilities. `HostOptions.live`
/// (Live.fs) has the real ones; tests replace what they script.
[<NoEquality; NoComparison>]
type HostOptions =
    {
        Paths: HostPaths
        /// The project directory: the workspace sessions work in, and where project config is read
        /// from once it is trusted.
        Workspace: string
        /// The process's environment variables.
        Variables: (string * string) list
        /// Selections from the command line (`--variant`, `--model`, `--surface`): the explicit layer.
        Explicit: (string * Json) list
        /// What the binary is built for.
        Target: TargetTriple
        /// The user's home directory.
        Home: string
        /// The OS account the host runs as (`PeerCredentials.currentAccount`).
        Account: OsAccount
        /// The running binary and the arguments before `envd` (the assembly on the JIT), for envd
        /// started as a process.
        Binary: string
        BinaryArgs: string list
        Clock: Clock
        Ids: Ids
        Trace: Trace
        FsRead: FsRead
        Fs: Fs
        Shell: Shell
        /// The host's HTTP client: model wires go through it (confined to their providers).
        Http: Http
        /// Downloads for the component installer.
        Fetch: Fx.Components.Fetch
        InstallFiles: Fx.Components.InstallFiles
        /// The OS keyring (`LocalKeyring.create`), private files in the data directory
        /// (`LocalPrivateFiles.create`) and the variables credentials are read from
        /// (`SystemVariables.variables`): what service credentials are kept in and come from.
        Keyring: Keyring
        PrivateFiles: PrivateFiles
        HostVariables: HostVariables
        /// Whether the keyring may ask the person (`Headless.prompts`).
        Prompts: bool
        /// envd in the host's process (embedded mode, `EnvdMain.embed`); `None`: piped only.
        Embed: (EmbeddedLaunch -> Task<Result<Embedded, string>>) option
        /// Opens the event and blob stores at the paths (`HostStores.live`: SQLite and files).
        OpenStores: HostPaths -> StoreDurability -> Result<HostStores, string>
        /// Extensions to load after the built-ins (tests; host-config extensions come later).
        Extensions: Candidate list
        /// How long an envd started as a process gets to exit once the host closed its input.
        StopTimeout: TimeSpan
    }

    override this.ToString() =
        $"HostOptions({this.Paths.Data}, workspace {this.Workspace})"

/// The configuration a host runs under: trust, config files, policy and the command line.
type HostConfig =
    {
        ProjectTrusted: bool
        Host: ConfigFile
        /// The project's config file, only once the project is trusted (spec §18).
        Project: ConfigFile option
        Policies: Policy list
        Explicit: Selections
    }

    override this.ToString() =
        $"HostConfig(trusted: {this.ProjectTrusted}, {this.Policies.Length} policies)"

    /// The layers under the policies, lowest first (variants.md).
    member this.Layers: Selections list =
        [
            this.Host.Variants
            match this.Project with
            | Some project -> project.Variants
            | None -> ()
            this.Explicit
        ]

/// The steps of a host's start that commands such as `fx variants` share: reading config, loading the
/// built-ins, building the variant registry and resolving it. No store is opened.
[<RequireQualifiedAccess>]
module Setup =
    /// The variables as the host reads them (case-insensitive names on Windows).
    let variable (options: HostOptions) (name: string) : string option =
        options.Variables
        |> List.tryFind (fun (n, _) ->
            if OperatingSystem.IsWindows() then
                String.Equals(n, name, StringComparison.OrdinalIgnoreCase)
            else
                n = name)
        |> Option.map snd

    /// The trust store, the host's config and policy, and the project's config once it is trusted.
    let readConfig (options: HostOptions) : Eff<HostEnv, Result<HostConfig, string list>> =
        eff {
            let! trust = TrustStore.read options.Paths.Trust

            match trust with
            | Error e -> return Error [ e ]
            | Ok trust ->
                let trusted = TrustStore.isTrusted TrustStore.ignoreCase options.Workspace trust
                let! host = ConfigFile.read Layer.Host options.Paths.Config
                let! policies = PolicyFile.read options.Paths.Policy

                let! project =
                    if trusted then
                        ConfigFile.read Layer.Project (HostPaths.projectConfig options.Workspace)
                        |> Eff.map (Result.map Some)
                    else
                        Eff.ret (Ok None)

                let errors =
                    [
                        match host with
                        | Error e -> yield! e
                        | Ok _ -> ()
                        match policies with
                        | Error e -> yield! e
                        | Ok _ -> ()
                        match project with
                        | Error e -> yield! e
                        | Ok _ -> ()
                    ]

                match host, policies, project with
                | Ok host, Ok policies, Ok project ->
                    return
                        Ok
                            {
                                ProjectTrusted = trusted
                                Host = host
                                Project = project
                                Policies = policies
                                Explicit =
                                    Selections.create (Source.create Layer.Explicit "command line") options.Explicit
                            }
                | _ -> return Error errors
        }

    /// The extension system's trust context for this host.
    let trust (config: HostConfig) : TrustContext =
        {
            ProjectTrusted = config.ProjectTrusted
            RequirePins = false
            Placement = "host"
            Sdk = Versions.Sdk
            Protocols = ExtensionProtocol.versions
        }

    let private processSettings (options: HostOptions) : ProcessRuntime.ProcessSettings =
        {
            Limits = ConnectionLimits.defaults
            Info =
                {
                    Name = Product.binaryName
                    Version = Product.Version
                }
            HostEnvironment = options.Variables
            Workspace = Some options.Workspace
            IsWindows = OperatingSystem.IsWindows()
            StopTimeout = options.StopTimeout
            StderrBytes = 4096
        }

    let private settings (options: HostOptions) (config: HostConfig) : HostSettings<HostEnv> =
        { ExtensionHost.settings (trust config) (BasicExtensionEnv.forManifest options.Clock) (processSettings options) with
            Catalog = Builtins.catalog
            Disabled = config.Host.Disabled
        }

    /// The built-ins (their bundled packages under the data directory's `extensions`) and the
    /// options' extensions.
    let candidates (options: HostOptions) (exclusions: Eff<IExtensionEnv, ExclusionSettings>) : Candidate list =
        Builtins.candidates options.Paths.Extensions exclusions @ options.Extensions

    /// The bootstrap's first phase (spec §16.1): the built-ins and the options' extensions that are in
    /// the binary (native, then declarative). Commands such as `fx variants` stop here; the variant
    /// registry, grants and the component plan are made from what it loaded. Nothing is written and
    /// no process starts.
    let bootstrap
        (options: HostOptions)
        (config: HostConfig)
        (exclusions: Eff<IExtensionEnv, ExclusionSettings>)
        : Eff<HostEnv, Fx.Extensions.Host> =
        ExtensionHost.bootstrapInBinary (settings options config) (candidates options exclusions)

    /// The bootstrap's later phases on what `bootstrap` loaded: the components phase (`install`: the
    /// host's installer), then extension processes (`fx.exthost.bun` among them), then extensions on
    /// contributed runtimes (`bun`).
    let bootstrapRest
        (options: HostOptions)
        (config: HostConfig)
        (exclusions: Eff<IExtensionEnv, ExclusionSettings>)
        (install: Manifest -> Eff<HostEnv, Result<InstalledComponent list, string>>)
        (host: Fx.Extensions.Host)
        : Eff<HostEnv, Fx.Extensions.Host> =
        ExtensionHost.bootstrapRest
            { settings options config with
                InstallComponents = install
            }
            host
            (candidates options exclusions)

    /// What went wrong loading extensions, one line each (activated and skipped ones are fine).
    let problems (extensions: Fx.Extensions.Host) : string list =
        extensions.Report
        |> List.choose (fun entry ->
            match entry.Outcome with
            | Outcome.Rejected _
            | Outcome.Failed _ -> Some(string entry)
            | Outcome.Activated
            | Outcome.Skipped _ -> None)

    /// The variant registry: the core's slots and those the extensions in effect contribute.
    let registry (extensions: Fx.Extensions.Host) : Result<SlotRegistry, string list> =
        let contributed =
            ExtensionHost.contributions VariantSlots.contributionType extensions
            |> VariantSlots.slots

        SlotRegistry.create (HostSlots.core @ contributed)
        |> Result.mapError (List.map string)

    /// The host's service credentials (auth.md): the credential stores, auth methods and secret
    /// sources the extensions in effect contribute, over the options' keyring, private files and
    /// variables. Runs under the host's resolution (`credentials.store`, `secrets.sources`); an
    /// `Error` (no store can be used as configured) stops the host.
    let auth (options: HostOptions) (extensions: Fx.Extensions.Host) : Eff<HostEnv, Result<AuthService, string>> =
        AuthHost.start
            {
                Keyring = options.Keyring
                PrivateFiles = options.PrivateFiles
                Variables = options.HostVariables
                Http = options.Http
                Prompts = options.Prompts
                Trace = options.Trace
            }
            (ExtensionHost.contributions CredentialStores.contributionType extensions)
            (ExtensionHost.contributions AuthMethods.contributionType extensions)
            (ExtensionHost.contributions SecretSources.contributionType extensions)

    /// The model providers the extensions in effect contribute.
    let providers (extensions: Fx.Extensions.Host) : Fx.Models.ProviderDef list =
        ExtensionHost.contributions ProviderDefs.contributionType extensions
        |> List.map _.Value

    /// The options the host's kernels start with.
    let kernelOptions: Fx.Kernel.KernelOptions =
        { Fx.Kernel.KernelOptions.defaults with
            Peer =
                {
                    Name = Product.binaryName
                    Version = Product.Version
                }
        }

    /// The interpreter provider of an id in effect, and the runtime it runs on.
    let interpreter (extensions: Fx.Extensions.Host) (id: string) : Result<InterpreterProvider * RuntimeSpec, string> =
        let interpreters =
            ExtensionHost.contributions InterpreterProviders.contributionType extensions

        let runtimes = ExtensionHost.contributions RuntimeSpecs.contributionType extensions

        match interpreters |> List.tryFind (fun c -> c.Value.Id = id) with
        | None -> Error $"no interpreter {Json.quote id} is in effect (agent.interpreter)"
        | Some interpreter ->
            let provider = interpreter.Value

            match runtimes |> List.tryFind (fun c -> c.Id = provider.Launch.Runtime) with
            | None -> Error $"the interpreter {provider.Id} runs on {provider.Launch.Runtime}, which is not in effect"
            | Some runtime -> Ok(provider, runtime.Value)

    /// The host's resolution: the layers under the policies. Every issue is reported.
    let resolve (registry: SlotRegistry) (config: HostConfig) : Result<Fx.Variants.Resolution, string list> =
        Resolution.resolve registry config.Policies config.Layers
        |> Result.mapError (List.map string)

    /// A slot's value in a variants interpreter: its default if the interpreter does not have it.
    let value (slot: Slot<'T>) (variants: Variants) : 'T =
        match variants.Read slot.Id with
        | Some resolved ->
            match slot.Codec.Decode resolved.Value with
            | Ok value -> value
            | Error _ -> slot.Default
        | None -> slot.Default
