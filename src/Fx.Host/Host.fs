namespace Fx.Host

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Variants
open Fx.Models
open Fx.Sdk
open Fx.Extensions
open Fx.Components
open Fx.Environments
open Fx.Grants
open Fx.Grants.Host
open Fx.Identity
open Fx.Identity.Host
open Fx.Kernel
open Fx.Sessions
open Fx.Sessions.Host
open Fx.Prompt
open Fx.CodeAct
open Fx.Replay
open Fx.Auth

/// Yolo approvals approve first installs of components too (docs/modules/codeact.md, "Approval
/// modes"): the host's `codeact.grants.mode = yolo`, a host grant of `approvals.yolo`, or one of the
/// sessions in the environment whose `Ensure` asks (a person's `/yolo`). Checked on `none`: the
/// installer is not confined. Otherwise the request goes to a person (`Approvals.viaGrants`).
[<RequireQualifiedAccess>]
module YoloInstalls =
    [<Literal>]
    let private EnvironmentHolder = "environment:"

    /// Why yolo approvals approve an install that `holder` asked for (`environment:<id>`: the sessions
    /// `sessionsOf` gives for the environment count too), if they do.
    let approval<'R when 'R :> IScope>
        (grants: Grants)
        (mode: GrantMode)
        (sessionsOf: string -> string list)
        (holder: string)
        : Eff<'R, string option> =
        eff {
            let sessions =
                if holder.StartsWith(EnvironmentHolder, StringComparison.Ordinal) then
                    sessionsOf (holder.Substring EnvironmentHolder.Length)
                else
                    []

            let contexts: GrantContext list =
                (None :: (sessions |> List.map Some))
                |> List.map (fun session ->
                    {
                        Session = session
                        Run = None
                        Repo = None
                    })

            let mutable approved = None

            for context in contexts do
                if approved.IsNone then
                    let! check =
                        fun (env: 'R) ->
                            grants.Check (YoloApprovals.query AutoApprovals.Unsandboxed context) env.Scope.Cancellation

                    match YoloApprovals.decide mode AutoApprovals.Unsandboxed check with
                    | AutoApprovals.On because -> approved <- Some because
                    | AutoApprovals.Off _ -> ()

            return approved
        }

    /// Approves an install on yolo approvals, or leaves it to `otherwise`.
    let approve<'R when 'R :> IScope>
        (grants: Grants)
        (mode: GrantMode)
        (sessionsOf: string -> string list)
        (otherwise: ApprovalRequest -> Eff<'R, ApprovalAnswer>)
        : ApprovalRequest -> Eff<'R, ApprovalAnswer> =
        fun request ->
            eff {
                match! approval grants mode sessionsOf request.Holder with
                | Some because -> return Approved("yolo approvals: " + because)
                | None -> return! otherwise request
            }

/// A running host (spec §3, the "brain"): its stores, the extensions in effect, the variant registry
/// and its resolution, the grants log, service credentials, identity, environments, model access and
/// the session host with the CodeAct strategy. Built by `Host.start`, released by `Host.stop`. One per data directory: the
/// host holds its lock while it runs.
[<Sealed>]
type Host
    internal
    (
        options: HostOptions,
        config: HostConfig,
        env: HostEnv,
        extensions: Fx.Extensions.Host,
        registry: SlotRegistry,
        resolution: Fx.Variants.Resolution,
        stores: HostStores,
        grants: GrantsService<HostEnv>,
        auth: AuthService,
        authenticator: Authenticator,
        environments: EnvsHost<HostEnv>,
        sessions: SessionHost<HostEnv>,
        profiles: ProfileBinder<HostEnv>,
        index: EventLog<IndexEvent>,
        entries: ConcurrentDictionary<string, SessionEntry>,
        ensureEnvironment: Eff<HostEnv, Result<string, string>>,
        lockfile: Lockfile option,
        warnings: string list,
        streams: Streams,
        profileOf: string -> Eff<HostEnv, Variants>,
        stop: unit -> Task
    ) =
    member _.Options = options
    member _.Config = config
    /// The host's environment, with the lifetime scope of the host.
    member _.Env = env
    member _.Extensions = extensions
    member _.Registry = registry
    /// The host's resolution: what new sessions' profiles start from.
    member _.Resolution = resolution
    member _.Stores = stores
    member _.Grants = grants
    /// Service credentials: what model requests authenticate with (`fx auth`, auth.md).
    member _.Auth = auth
    member _.Authenticator = authenticator
    member _.Environments = environments
    member _.Sessions = sessions
    member _.Profiles = profiles
    member _.Index = index
    member internal _.Entries = entries
    member internal _.EnsureEnvironment = ensureEnvironment
    /// The components planned for environments (`None` when planning failed: see `Warnings`).
    member _.Lockfile = lockfile
    /// What a person should know about this host's setup (an extension that failed to load, …).
    member _.Warnings = warnings
    /// What the model writes in running turns, as it writes it (for clients that follow a session).
    member _.Streams = streams
    /// The variants of a session's profile (by its id), as its turns read them.
    member internal _.ProfileOf(profile: string) : Eff<HostEnv, Variants> = profileOf profile
    member internal _.StopAll() = stop ()

    override _.ToString() = $"Host({options.Paths.Data})"

/// A host's start, and what sessions need from it.
[<RequireQualifiedAccess>]
module Host =
    /// The id of the host's environments stream and installer.
    [<Literal>]
    let Name = "host"

    /// What the host's own inputs are attributed to.
    let system = Attribution.system "host"

    /// How long a model request may wait for its response to start, and between pieces of it.
    let modelTimeouts: HttpTimeouts =
        {
            Response = Some(TimeSpan.FromMinutes 10.0)
            Idle = Some(TimeSpan.FromMinutes 5.0)
        }

    let rec private redactJson (redaction: Redaction) (json: Json) : Json =
        match json with
        | Json.String text -> Json.String(Redaction.text redaction text)
        | Json.Array items -> Json.Array(items |> List.map (redactJson redaction))
        | Json.Object members ->
            Json.Object(members |> List.map (fun (name, value) -> name, redactJson redaction value))
        | other -> other

    /// A model whose requests are cleaned of the credentials environments' commands got (T138: below
    /// the hands' redaction, a second line), before the egress hooks and any wire see them. Exact
    /// matches only: encoded copies are not found (T601).
    let redactingLlm (redaction: unit -> Redaction) (inner: Llm) : Llm =
        {
            Stream =
                fun request cancellation ->
                    let current = redaction ()

                    if current.IsEmpty then
                        inner.Stream request cancellation
                    else
                        let cleaned =
                            match
                                Codec.decode ChatRequest.codec (redactJson current (ChatRequest.codec.Encode request))
                            with
                            | Ok cleaned -> cleaned
                            | Error issues ->
                                invalidOp ("a redacted model request does not read: " + Issue.renderAtMost 3 issues)

                        inner.Stream cleaned cancellation
        }

    /// The environment a host creates for its project when config names none: `fx.env.local` on the
    /// workspace, envd in the host's process (embedded mode) when it can run there.
    let defaultSpec (embedded: bool) (workspace: string) : EnvSpec =
        { (EnvSpec.create "default" Fx.Ext.EnvLocal.EnvLocal.Kind
           |> EnvSpec.withWorkspace workspace) with
            Transport =
                Some(
                    if embedded then
                        TransportKind.InProcess
                    else
                        TransportKind.Piped
                )
        }

    /// The project's environment: the spec named `default` in host config (or, once trusted, project
    /// config), on the project's directory unless it names another; else `defaultSpec`.
    let workspaceSpec (options: HostOptions) (config: HostConfig) : EnvSpec =
        let configured =
            EnvSpecs.merge
                [
                    config.Host.Environments
                    match config.Project with
                    | Some project -> project.Environments
                    | None -> []
                ]
            |> List.tryFind (fun spec -> spec.Name = "default")

        match configured with
        | Some spec when spec.Workspace.Path.IsNone ->
            { spec with
                Workspace =
                    { spec.Workspace with
                        Path = Some options.Workspace
                    }
            }
        | Some spec -> spec
        | None -> defaultSpec options.Embed.IsSome options.Workspace

    /// A small, fixed environment for the installer's probes (runtimes.md): the variables a program
    /// needs to start, never the host's credentials or injection vectors.
    let probeEnvironment (variables: (string * string) list) : EnvVars =
        let keep =
            [
                "PATH"
                "HOME"
                "USERPROFILE"
                "SYSTEMROOT"
                "WINDIR"
                "TEMP"
                "TMP"
                "TMPDIR"
                "LANG"
            ]

        variables
        |> List.filter (fun (name, _) ->
            keep
            |> List.exists (fun k -> String.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
        |> HostEnvironment.scrub
        |> Exactly

    /// The components environments get: planned for this machine from the definitions in effect
    /// (components are privileged: only the binary, host config and policy define them) and the
    /// requirements of every interpreter in effect.
    let plan (target: TargetTriple) (extensions: Fx.Extensions.Host) : Result<Lockfile, string> =
        let catalog =
            ExtensionHost.contributions ComponentDefinitions.contributionType extensions
            |> List.map _.Value

        let requires =
            ExtensionHost.contributions InterpreterProviders.contributionType extensions
            |> List.collect (fun c ->
                c.Value.Requires
                |> List.map (fun r ->
                    {
                        Ref = r
                        By = "interpreter " + c.Value.Id
                    }))

        Planner.plan PlanSettings.defaults catalog requires [ target ]
        |> Result.mapError (fun errors -> errors |> List.map string |> String.concat "; ")

    /// The profile of a resolution: its values, the extensions in effect and the lockfile.
    let describe
        (extensions: Fx.Extensions.Host)
        (lockfile: Lockfile option)
        (resolution: Fx.Variants.Resolution)
        : Profile =
        let profile =
            extensions.Loaded
            |> Map.fold
                (fun profile id (loaded: Loaded) ->
                    Profile.withExtension
                        id
                        {
                            Version = loaded.Manifest.Version
                            Hash = Some(Manifest.hash loaded.Manifest)
                        }
                        profile)
                (Profile.ofResolution Product.Version resolution)

        match lockfile with
        | Some lockfile -> Profile.withLockfile (Lockfile.hash lockfile) profile
        | None -> profile

    /// A profile's selections as variants with today's registry: slots it does not know get their
    /// defaults, slots that no longer exist are left out.
    let variantsOfProfile (registry: SlotRegistry) (profile: Profile) : Result<Variants, string list> =
        let known =
            profile.Selections
            |> Map.toList
            |> List.filter (fun (slot, _) -> registry.Contains slot)
            |> List.map (fun (slot, value) -> slot.Value, value)

        let layer =
            Selections.create (Source.create Layer.Explicit $"profile {Profile.id profile}") known

        Resolution.resolve registry [] [ layer ]
        |> Result.map Variants.ofResolution
        |> Result.mapError (List.map string)

    let private machineIdLength = 16

    /// The host's stable machine id (identity.md): created once, random, in the data directory.
    let machineId (path: string) : Eff<HostEnv, Result<string, string>> =
        eff {
            let valid (text: string) =
                text.Length > 0
                && text.Length <= 64
                && text
                   |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '.' || c = '_' || c = '-')

            match! Fs.readText path with
            | Ok text when valid (text.Trim()) -> return Ok(text.Trim())
            | Ok _ -> return Error $"{path} does not hold a machine id; delete it to make a new one"
            | Error(FsError.NotFound _) ->
                let! bytes = Ids.randomBytes (machineIdLength / 2)
                let id = Convert.ToHexStringLower bytes

                match! InstallFiles.replaceFile path (Text.Encoding.ASCII.GetBytes(id + "\n")) with
                | Ok() -> return Ok id
                | Error e -> return Error $"the machine id could not be written: {e}"
            | Error e -> return Error $"the machine id could not be read: {e}"
        }

    /// The host's environment before its stores, model access and environments are up: what reading
    /// config and loading extensions need (`fx variants` runs on it alone).
    let environment (options: HostOptions) (scope: Scope) : HostEnv =
        let unavailable (name: string) : 'T =
            invalidOp $"{name} is not available before the host has started it"

        {
            Clock = options.Clock
            Ids = options.Ids
            Trace = options.Trace
            Variants = { Read = fun _ -> None }
            FsRead = options.FsRead
            Fs = options.Fs
            Shell = options.Shell
            Http = options.Http
            Fetch = options.Fetch
            InstallFiles = options.InstallFiles
            Blobs =
                {
                    Put = fun _ _ -> unavailable "the blob store"
                    Get = fun _ _ -> unavailable "the blob store"
                    Read = fun _ _ _ _ -> unavailable "the blob store"
                    Stat = fun _ _ -> unavailable "the blob store"
                    Create = fun _ -> unavailable "the blob store"
                }
            Llm =
                {
                    Stream = fun _ _ -> unavailable "model access"
                }
            Envs =
                {
                    Create = fun _ _ -> unavailable "environments"
                    Get = fun _ _ -> unavailable "environments"
                    List = fun _ -> unavailable "environments"
                    Open = fun _ _ -> unavailable "environments"
                    Suspend = fun _ _ -> unavailable "environments"
                    Resume = fun _ _ -> unavailable "environments"
                    Destroy = fun _ _ -> unavailable "environments"
                }
            Scope = scope
        }

    /// An expected failure of a start step, as the lines `start` reports.
    let private failing (what: string) (effect: Eff<HostEnv, Result<'T, 'E>>) : Eff<HostEnv, Result<'T, string list>> =
        effect |> EffResult.mapError (fun e -> [ $"{what}: {e}" ])

    /// Runs the starting steps; `cleanup` collects what to undo, last first.
    let private starting
        (options: HostOptions)
        (lifetime: CancellationToken)
        (cleanup: Collections.Generic.Stack<unit -> Task>)
        (release: unit -> Task)
        : Eff<HostEnv, Result<Host, string list>> =
        effResult {
            let paths = options.Paths
            let! env0 = Eff.ask<HostEnv>
            let hostScope = Scope.ofCancellation lifetime

            // The data directory and its lock: one host per data directory.
            do! failing $"the data directory {paths.Data} could not be created" (Fs.createDirectory paths.Data)

            let! held =
                failing $"the host's lock {paths.Lock} could not be taken" (InstallFiles.tryLock paths.Lock)

            let! held =
                match held with
                | Some held -> Ok held
                | None ->
                    Error
                        [
                            $"another host is running on {paths.Data}: stop it, or set {HostPaths.homeVariable} to another directory"
                        ]

            cleanup.Push(fun () -> held.Release() :> Task)

            let! config = Setup.readConfig options

            // Extensions. Content exclusions read the host's resolution, made below.
            let exclusionsFrom =
                ref (Variants.ofResolution (Resolution.defaults SlotRegistry.empty))

            let exclusions: Eff<IExtensionEnv, ExclusionSettings> =
                fun e -> ContentExclusions.ofVariants exclusionsFrom.Value e

            let! extensions = Setup.bootstrap options config exclusions
            cleanup.Push(fun () -> Eff.run env0 (ExtensionHost.stop extensions) :> Task)
            let! registry = Setup.registry extensions
            let! resolution = Setup.resolve registry config
            let variants = Variants.ofResolution resolution
            exclusionsFrom.Value <- variants
            let env1 = { env0 with Variants = variants }

            // Stores.
            do!
                failing
                    "the event store's directory could not be created"
                    (Fs.createDirectory (IO.Path.GetDirectoryName paths.Events |> nonNull))

            do! failing "the blob store's directory could not be created" (Fs.createDirectory paths.Blobs)

            let! stores =
                options.OpenStores paths (Setup.value HostSlots.durability variants)
                |> Result.mapError List.singleton

            cleanup.Push(fun () -> Task.Run stores.Close)
            let env2 = { env1 with Blobs = stores.Blobs }
            let warnings = ResizeArray<string>(Setup.problems extensions)

            // The grants log, with the vocabulary and policy of the extensions in effect.
            let! actions =
                Actions.withContributed (
                    ExtensionHost.contributions GrantActions.contributionType extensions
                    |> List.map _.Value
                )

            let! grants =
                Eff.provide
                    env2
                    (GrantsService.start
                        { GrantsSettings.create (GrantStoreLog.hostLocal stores.Log) with
                            Actions = actions
                            // A policy file that forbids auto approvals as the host's mode forbids
                            // them as a person's grant too.
                            Policy =
                                (ExtensionHost.contributions GrantPolicy.rules extensions |> List.map _.Value)
                                @ AutoApprovals.policyRules config.Policies
                                @ YoloApprovals.policyRules config.Policies
                            Gates = ExtensionHost.contributions GrantPolicy.gates extensions
                        })

            cleanup.Push(fun () -> Eff.run env2 (GrantsService.stop grants) :> Task)

            // Sessions (read before components: yolo approvals of a session approve its environment's
            // first installs).
            let index = SessionIndex.typed stores.Log
            let! indexed = SessionIndex.entries index

            let entries =
                ConcurrentDictionary<string, SessionEntry>(
                    indexed |> List.map (fun e -> Collections.Generic.KeyValuePair(e.Session, e)),
                    StringComparer.Ordinal
                )

            // Components: planned for this machine, installed into the host's install root when an
            // environment on it needs them; the first install of each is a grant (spec §6.1, §13), which
            // yolo approvals give.
            let lockfile =
                match plan options.Target extensions with
                | Ok lockfile -> Some lockfile
                | Error reason ->
                    warnings.Add $"components could not be planned ({reason}): kernels will not start"
                    None

            let! components =
                match lockfile with
                | None -> Eff.ret EnvComponents.none
                | Some lockfile ->
                    eff {
                        let! hostGrants = GrantSettings.ofVariants variants

                        let sessionsOf (environment: string) =
                            entries.Values
                            |> Seq.filter (fun entry -> entry.Environment = environment)
                            |> Seq.map _.Session
                            |> List.ofSeq

                        let approve =
                            Approvals.viaGrants
                                (GrantsService.grants grants)
                                {
                                    Principal = "system"
                                    Agent = None
                                    Extension = None
                                }
                                {
                                    Session = None
                                    Run = None
                                    Repo = None
                                }
                            |> YoloInstalls.approve (GrantsService.grants grants) hostGrants.Mode sessionsOf

                        let settings =
                            { InstallerSettings.create paths.Runtimes Name approve with
                                ProbeEnv = probeEnvironment options.Variables
                            }

                        let! installer =
                            Eff.provide
                                env2
                                (ComponentInstaller.start settings (InMemoryEventLog<InstallerEvent>()).Log)

                        cleanup.Push(fun () -> Eff.run env2 (Actor.stop installer) :> Task)

                        let installs =
                            ComponentInstaller.components installer lockfile options.Target (fun () ->
                                string (options.Ids.NewId()))

                        return
                            {
                                Requires = lockfile.Requires
                                For = fun machine -> if machine.SameHost then Some installs else None
                            }
                    }

            // Identity.
            let! machine = machineId paths.MachineId |> EffResult.mapError List.singleton

            let authenticator =
                Authenticator.create
                    (ExtensionHost.contributions IdentityProviders.contributionType extensions)
                    {
                        Machine = machine
                        Account = options.Account
                    }
                    (SessionTokens())

            // Service credentials: one store, chosen once (an error stops the host), and the methods
            // and sources of the extensions in effect (spec §11.3–11.5). Before environments: their
            // variables may name secrets (T138).
            let! auth =
                Eff.provide env2 (Setup.auth options extensions)
                |> EffResult.mapError (fun e -> [ $"service credentials: {e}" ])

            // The variables environments' commands get: the person's, the specs', consent and secrets.
            let variablesFor =
                EnvVariableConsent.resolver
                    {
                        Person = Setup.value VariableSlots.variables variants
                        Deny = Setup.value VariableSlots.deny variants
                        HostVariables = options.Variables
                        Secrets = auth.Secrets
                        Grants = GrantsService.grants grants
                        Repo = HostRepo.id options.Workspace
                        Config = config
                    }

            // Environments.
            let! environments =
                Eff.provide
                    env2
                    (EnvsHost.start
                        {
                            Stream = EnvsHost.stream Name
                            Providers = ExtensionHost.contributions EnvProviders.contributionType extensions
                            Sandboxes = ExtensionHost.contributions SandboxDrivers.contributionType extensions
                            FsRead = options.FsRead
                            Fs = options.Fs
                            Shell = options.Shell
                            HostInfo =
                                {
                                    Target = options.Target
                                    DataDir = paths.Data
                                    Home = options.Home
                                }
                            Binary = options.Binary
                            BinaryArgs = options.BinaryArgs
                            HostEnvironment = options.Variables
                            Embed = options.Embed
                            Components = components
                            StopTimeout = options.StopTimeout
                            Limits = Fx.Rpc.ConnectionLimits.defaults
                            Variables = variablesFor
                        }
                        (EnvLog.typed stores.Log))

            cleanup.Push(fun () -> EnvsHost.stop environments :> Task)


            // Models: every request through the egress hooks; credentials only here.
            let providers = Setup.providers extensions
            warnings.AddRange(AuthService.check auth providers)

            let access =
                { ModelAccess.create
                      (ExtensionHost.contributions ChatWires.contributionType extensions
                       |> List.map _.Value)
                      providers
                      options.Http
                      options.Clock with
                    Egress =
                        {
                            Transforms = ExtensionHost.contributions ModelEgress.transforms extensions
                            Gates = ExtensionHost.contributions ModelEgress.gates extensions
                        }
                    Auth = AuthService.modelAuth auth
                    Timeouts = modelTimeouts
                    Replay =
                        Replay.defaults
                        |> Fx.Ext.ModelAnthropic.Anthropic.withReplay
                        |> Fx.Ext.ModelOpenAI.OpenAI.withReplay
                    Trace = options.Trace
                }

            let env =
                { env2 with
                    Envs = EnvsHost.envs environments
                    Llm = redactingLlm (fun () -> EnvsHost.redaction environments) (ModelAccess.llm access)
                }

            // Sessions.
            let spec = workspaceSpec options config
            let environmentGate = new SemaphoreSlim(1, 1)

            let ensureEnvironment: Eff<HostEnv, Result<string, string>> =
                eff {
                    let! token = Eff.cancellation
                    do! Eff.ofTask (fun () -> task { do! environmentGate.WaitAsync token })

                    try
                        let! records = Envs.list

                        let same (record: EnvRecord) =
                            record.Spec.Name = spec.Name
                            && record.Spec.Provider = spec.Provider
                            && record.Spec.Workspace.Path = spec.Workspace.Path

                        match records |> List.tryFindBack (fun r -> same r && not r.Terminal) with
                        | Some record -> return Ok record.Id
                        | None ->
                            match! Envs.create spec with
                            | Ok record -> return Ok record.Id
                            | Error(EnvError.NotReady record) ->
                                // Nothing more happens to it: the next session tries afresh.
                                let! _ = Envs.destroy record.Id
                                return Error(string (EnvError.NotReady record))
                            | Error e -> return Error(string e)
                    finally
                        environmentGate.Release() |> ignore
                }

            let catalog (model: ModelRef) =
                providers
                |> List.tryPick (fun p -> p.Catalog.Models |> List.tryFind (fun m -> m.Ref = model))

            let profileVariants = ConcurrentDictionary<string, Variants>(StringComparer.Ordinal)

            let profileOf (id: string) : Eff<HostEnv, Variants> =
                eff {
                    match profileVariants.TryGetValue id with
                    | true, variants -> return variants
                    | _ ->
                        match ProfileId.tryParse id, BlobHash.tryParse id with
                        | Some profileId, Some hash ->
                            match! Blobs.get hash with
                            | Error e -> return invalidOp $"the profile {id} is not in the blob store: {e}"
                            | Ok bytes ->
                                match Profile.ofBytes profileId bytes with
                                | Error e -> return invalidOp $"the profile {id} does not read: {e}"
                                | Ok profile ->
                                    match variantsOfProfile registry profile with
                                    | Error issues ->
                                        return
                                            invalidOp (
                                                $"the profile {id} does not resolve: " + String.concat "; " issues
                                            )
                                    | Ok variants ->
                                        profileVariants[id] <- variants
                                        return variants
                        | _ -> return invalidOp $"not a profile id: {id}"
                }

            let launcher = HostEnvironment.scrub options.Variables

            let kernelPlan (variants: Variants) : Result<KernelPlan, string> =
                Setup.interpreter extensions (Setup.value HostSlots.interpreter variants)
                |> Result.map (fun (provider, runtime) ->
                    {
                        Provider = provider
                        Runtime = runtime
                        Options = Setup.kernelOptions
                        ProjectTrusted = config.ProjectTrusted
                        Launcher = launcher
                        // The grants log's checks come per turn (GrantGuard.attach, below).
                        Guard = fun _ -> EffResult.ok []
                        // The session's agent's overrides come with agents (T213).
                        Sandbox = SandboxOverrides.none
                    })

            let sessionLog = SessionLog.typed stores.Log
            let grantsCapability = GrantsService.grants grants

            // Each turn's hands ask the grants log before sensitive host calls (T127b), with the
            // settings of the turn's profile: kernels outlive turns, and use the running turn's guard.
            let hands (turn: TurnStart) : Eff<HostEnv, Result<Hands<EnvHands>, string>> =
                effResult {
                    let! variants = profileOf turn.Profile

                    let! environment =
                        match entries.TryGetValue turn.Session with
                        | true, entry -> EffResult.ok entry.Environment
                        | _ -> ensureEnvironment

                    let! plan = kernelPlan variants
                    let! handle = Envs.openEnv environment |> EffResult.mapError string
                    let! grantSettings = GrantSettings.ofVariants variants

                    // Auto approvals are checked on the driver that confines the hands (`none` when
                    // nothing does), so policy can forbid them unsandboxed only.
                    let guarded =
                        { GrantSetup.create grantsCapability sessionLog turn grantSettings with
                            Sandbox =
                                if handle.Sandbox.Info.Confines then
                                    handle.Sandbox.Info.Driver
                                else
                                    AutoApprovals.Unsandboxed
                        }

                    return
                        GrantGuard.attach guarded (EnvHands.ofHandle options.Clock hostScope plan turn.Session handle)
                }

            // Sessions are recorded (session.record) for exact replay (`fx replay`, spec §15): every
            // capability a turn reaches is journaled. A turn reaches the clock and the model of the host's
            // environment (above ModelAccess: requests and responses, never credentials), and its hands'
            // clock, files and shell (the sandboxed one); terminals are refused in recorded hands. The
            // guard (grants included), snapshots and kernel launches are journaled by the strategy.
            let wraps: Wraps<HostEnv, EnvHands> =
                {
                    Host =
                        fun taps env ->
                            { env with
                                Clock = taps.Clock env.Clock
                                Llm = taps.Llm env.Llm
                            }
                    Hands =
                        fun taps env ->
                            { env with
                                Clock = taps.Clock env.Clock
                                Client =
                                    { env.Client with
                                        FsRead = taps.FsRead env.Client.FsRead
                                        Fs = taps.Fs env.Client.Fs
                                        FsOps = taps.FsOps env.Client.FsOps
                                        Shell = taps.Shell env.Client.Shell
                                        Terminal = taps.Terminal env.Client.Terminal
                                    }
                            }
                }

            let codeact =
                Recording.codeact
                    (Recording.recorder stores.Log stores.Blobs options.Clock)
                    wraps
                    {
                        Sessions = sessionLog
                        Contexts = ContextLog.typed stores.Log
                        Steps = StepJournal.typed stores.Log
                        Blobs = Some stores.Blobs
                        Profile = profileOf
                        Hands = hands
                        Catalog = catalog
                    }

            // Kernels close before environments stop.
            cleanup.Push(fun () -> codeact.Close())

            let profiles =
                ProfileBinder.ofVariants registry config.Policies config.Layers (describe extensions lockfile)

            let settings =
                { SessionSettings.defaults with
                    Kinds = Questions.kinds
                    SnapshotEvery = int64 (Setup.value HostSlots.snapshotEvery variants)
                    MaxGap = int64 (Setup.value HostSlots.subscribeMaxGap variants)
                    ProfileAttempts = Setup.value HostSlots.profileAttempts variants
                }

            // Clients see replies as they are written; the log records them when complete.
            let streams = Streams()

            let! sessions =
                Eff.provide
                    env
                    (SessionHost.create
                        (SessionLog.typed stores.Log)
                        stores.Snapshots
                        (Streams.runner streams codeact.Runner)
                        settings)

            cleanup.Push(fun () -> Eff.run env (SessionHost.stop sessions) :> Task)

            // Sessions with work in flight: starting their actors recovers them (spec §5).
            for entry in indexed do
                let! _ = Eff.provide env (SessionHost.state sessions entry.Session)
                ()

            return
                Host(
                    options,
                    config,
                    env,
                    extensions,
                    registry,
                    resolution,
                    stores,
                    grants,
                    auth,
                    authenticator,
                    environments,
                    sessions,
                    profiles,
                    index,
                    entries,
                    ensureEnvironment,
                    lockfile,
                    List.ofSeq warnings,
                    streams,
                    profileOf,
                    release
                )
        }

    /// Starts a host: takes the data directory's lock, reads trust, config and policy, loads the
    /// built-ins, resolves the variants, opens the stores, then starts the grants log, the component
    /// installer, service credentials, identity, environments, model access and the session host, and
    /// starts the sessions the index knows (so turns that were running are recovered through the
    /// step journal and timers armed again: spec §5). An `Error` lists what is wrong; nothing is left
    /// running.
    let start (options: HostOptions) : Task<Result<Host, string list>> =
        task {
            let lifetime = new CancellationTokenSource()
            let cleanup = Collections.Generic.Stack<unit -> Task>()

            let released = ref 0

            // Undoes what started, last first; once.
            let release () : Task =
                task {
                    if Interlocked.Exchange(&released.contents, 1) = 0 then
                        while cleanup.Count > 0 do
                            let step = cleanup.Pop()

                            try
                                do! (step ()).WaitAsync(TimeSpan.FromSeconds 60.0)
                            with _ ->
                                ()

                        lifetime.Cancel()
                        lifetime.Dispose()
                }

            let env0 = environment options (Scope.ofCancellation lifetime.Token)

            let! started = Eff.catch (starting options lifetime.Token cleanup release) env0

            match started with
            | Ok(Ok host) -> return Ok host
            | Ok(Error errors) ->
                do! release ()
                return Error errors
            | Error e ->
                do! release ()
                return raise (Exception("the host failed to start", e))
        }

    /// The host's environment with a caller's cancellation.
    let within (host: Host) (cancellation: CancellationToken) : HostEnv =
        { host.Env with
            Scope =
                { host.Env.Scope with
                    Cancellation = cancellation
                }
        }

    /// Stops a host: sessions (running turns are cancelled), kernels, environments (envds stop, leases
    /// stay), the installer, the grants log, extensions, then closes the stores and releases the lock.
    let stop (host: Host) : Task = host.StopAll()
