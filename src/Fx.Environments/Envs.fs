namespace Fx.Environments

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Components
open Fx.Contracts
open Fx.Effects
open Fx.Envd
open Fx.Machines
open Fx.Machines.Host
open Fx.Rpc
open Fx.Sdk
open Fx.Variants

/// What the host asks of an embedded envd (embedded mode, spec §3): the composition root builds it
/// in the host's process (`EnvdMain.embed`).
type EmbeddedLaunch =
    {
        Workspace: string
        /// envd's state directory (`EnvLayout.EnvdState`).
        State: string
        /// `pwsh`, `powershell` or `sh`; `None`: envd's default (the `shell.windows` variant on
        /// Windows is passed here).
        Shell: string option
        /// The variables of the processes envd starts: the host's, scrubbed (`HostEnvironment`).
        Environment: (string * string) list
    }

    override this.ToString() = $"embedded envd for {this.Workspace}"

/// An embedded envd: the host's end of its in-process connection, and what stops it.
type Embedded =
    {
        Transport: Transport
        /// Ends the connection, kills envd's processes and closes its stores.
        Stop: unit -> Task<unit>
    }

    override _.ToString() = "embedded envd"

/// How environments get the components of the lockfile (spec §6.1; the last step of the bootstrap,
/// spec §9.2).
type EnvComponents =
    {
        /// What every environment gets: the lockfile's requirements. Empty: nothing to install.
        Requires: ComponentRef list
        /// The installer that serves a machine (`ComponentInstaller.components` over its install root
        /// and target); `None` when the host cannot reach one there yet (remote installs over the
        /// environment protocol come with T501).
        For: EnvMachine -> Components option
    }

    override this.ToString() =
        $"components ({this.Requires.Length} requirements)"

module EnvComponents =
    /// No components.
    let none: EnvComponents = { Requires = []; For = fun _ -> None }

/// What the environments host needs from its composition root.
[<NoEquality; NoComparison>]
type EnvsSettings =
    {
        /// The stream of the host's environments (`Lifecycle.StreamKind`).
        Stream: StreamId
        /// The providers in effect (`ExtensionHost.contributions EnvProviders.contributionType`).
        Providers: Contributed<EnvProvider> list
        /// The sandbox drivers in effect (`ExtensionHost.contributions SandboxDrivers.contributionType`):
        /// `auto` picks the first that confines processes on an environment's OS, so list the
        /// built-ins first. Without `none`, an environment that no driver can confine fails.
        Sandboxes: Contributed<SandboxDriver> list
        /// The host's own files and processes, handed to providers (`IProviderEnv`).
        FsRead: FsRead
        Fs: Fs
        Shell: Shell
        HostInfo: HostInfo
        /// The running binary: envd for environments on the host's machine (`<binary> envd --stdio`).
        Binary: string
        /// Arguments before `envd`: the assembly, when the binary is `dotnet` (the JIT).
        BinaryArgs: string list
        /// The host's variables, read once; environments on the host's machine get them scrubbed.
        HostEnvironment: (string * string) list
        /// Starts envd in the host's process; `None`: the in-process transport is not available.
        Embed: (EmbeddedLaunch -> Task<Result<Embedded, string>>) option
        Components: EnvComponents
        /// How long a piped envd gets to exit once the host closed its input; then it is killed.
        StopTimeout: TimeSpan
        Limits: ConnectionLimits
    }

    override this.ToString() = $"EnvsSettings({this.Stream})"

/// An environment the host is connected to: its capability records (`EnvClient`: files, processes,
/// terminals, blobs, snapshots) and where things live in it.
[<NoEquality; NoComparison>]
type EnvHandle =
    {
        Id: string
        Spec: EnvSpec
        /// envd's capability records, with every process and terminal started in the sandbox under
        /// `Sandbox.Policy` (spec §9.1: what an agent runs there is confined).
        Client: EnvClient
        Machine: EnvMachine
        Layout: EnvLayout
        /// The components installed for it, dependencies first.
        Installed: InstalledComponent list
        /// What confines its processes, and the policy `Client` applies.
        Sandbox: EnvSandbox
    }

    override this.ToString() =
        $"environment {this.Id} ({this.Spec.Name})"

[<RequireQualifiedAccess>]
module EnvHandle =
    /// The same environment with an agent's sandbox overrides (spec §7.1, e.g. a read-only workspace
    /// for a reviewer): its processes and terminals start under the tighter policy. Overrides only
    /// take access away.
    let attenuate (overrides: SandboxOverrides) (handle: EnvHandle) : EnvHandle =
        let policy = SandboxPolicy.tighten overrides handle.Sandbox.Policy
        let sandbox = { handle.Sandbox with Policy = policy }

        { handle with
            Client = EnvSandbox.client sandbox policy
            Sandbox = sandbox
        }

/// Why an environment operation failed.
[<RequireQualifiedAccess>]
type EnvError =
    /// No provider of this kind is in effect.
    | UnknownProvider of kind: string
    /// The spec is not one the provider can create (capability negotiation); paths into the spec.
    | Invalid of Issue list
    | UnknownEnvironment of id: string
    /// The lifecycle refused the command (e.g. resuming an environment that is not suspended).
    | Rejected of Rejection
    /// The environment did not become ready; its record says how far it got and why.
    | NotReady of EnvRecord
    /// The host stopped.
    | Stopped

    override this.ToString() =
        match this with
        | UnknownProvider kind -> $"no environment provider {Json.quote kind}"
        | Invalid issues -> "invalid environment spec:\n" + Issue.renderAtMost 10 issues
        | UnknownEnvironment id -> $"no environment {id}"
        | Rejected rejection -> string rejection
        | NotReady record ->
            let reason =
                match record.Reason with
                | Some reason -> ": " + reason
                | None -> ""

            $"{record.Id} is {record.Phase}{reason}"
        | Stopped -> "the environments host stopped"

/// The environments capability (spec §4.2 `IEnvs`, §9): create environments from specs, find them,
/// obtain their capability records, and destroy them. The host server (T129) and agents use it.
///
/// Interpreter: `EnvsHost.envs`.
[<NoEquality; NoComparison>]
type Envs =
    {
        /// Creates an environment and returns its record once it is ready (or why it is not).
        Create: EnvSpec -> CancellationToken -> Task<Result<EnvRecord, EnvError>>
        Get: string -> CancellationToken -> Task<EnvRecord option>
        /// Every environment the host has a record of, in the order they were requested.
        List: CancellationToken -> Task<EnvRecord list>
        /// The capability records of a ready environment; starts envd again if the host has no
        /// connection to it (after a restart or a lost connection).
        Open: string -> CancellationToken -> Task<Result<EnvHandle, EnvError>>
        Suspend: string -> CancellationToken -> Task<Result<EnvRecord, EnvError>>
        Resume: string -> CancellationToken -> Task<Result<EnvRecord, EnvError>>
        /// Destroys an environment: stops envd and releases what the provider holds. Destroying
        /// a destroyed environment succeeds.
        Destroy: string -> CancellationToken -> Task<Result<EnvRecord, EnvError>>
    }

    override _.ToString() = "Envs"

/// An environment that can create and use environments.
type IEnvs =
    abstract Envs: Envs

[<RequireQualifiedAccess>]
module Envs =
    let create<'R when 'R :> IEnvs and 'R :> IScope> (spec: EnvSpec) : Eff<'R, Result<EnvRecord, EnvError>> =
        fun env -> env.Envs.Create spec env.Scope.Cancellation

    let get<'R when 'R :> IEnvs and 'R :> IScope> (id: string) : Eff<'R, EnvRecord option> =
        fun env -> env.Envs.Get id env.Scope.Cancellation

    let list<'R when 'R :> IEnvs and 'R :> IScope> : Eff<'R, EnvRecord list> =
        fun env -> env.Envs.List env.Scope.Cancellation

    let openEnv<'R when 'R :> IEnvs and 'R :> IScope> (id: string) : Eff<'R, Result<EnvHandle, EnvError>> =
        fun env -> env.Envs.Open id env.Scope.Cancellation

    let suspend<'R when 'R :> IEnvs and 'R :> IScope> (id: string) : Eff<'R, Result<EnvRecord, EnvError>> =
        fun env -> env.Envs.Suspend id env.Scope.Cancellation

    let resume<'R when 'R :> IEnvs and 'R :> IScope> (id: string) : Eff<'R, Result<EnvRecord, EnvError>> =
        fun env -> env.Envs.Resume id env.Scope.Cancellation

    let destroy<'R when 'R :> IEnvs and 'R :> IScope> (id: string) : Eff<'R, Result<EnvRecord, EnvError>> =
        fun env -> env.Envs.Destroy id env.Scope.Cancellation

    /// What a person must be told about an environment while it exists, e.g. that it runs without a
    /// sandbox (spec §9.1, §18: a persistent warning, the TUI's "unsandboxed" banner).
    let warnings (record: EnvRecord) : string list =
        [
            match record.Sandbox with
            | Some sandbox when sandbox.Confines -> ()
            | Some sandbox ->
                let why =
                    match sandbox.Notes with
                    | [] -> ""
                    | notes -> " (" + String.Join("; ", notes) + ")"

                $"runs unsandboxed: commands can read and change everything the user can{why}"
            | None -> "runs unsandboxed: commands can read and change everything the user can"
        ]

/// A connection the host holds to an environment's envd, for one attempt.
[<NoEquality; NoComparison>]
type internal Live =
    {
        Attempt: int
        Machine: EnvMachine
        Layout: EnvLayout
        Transport: Transport
        /// Stops envd (after the client closed).
        Stop: unit -> Task<unit>
        Client: EnvClient option
        Installed: InstalledComponent list
        Sandbox: EnvSandbox option
    }

    override this.ToString() = $"Live(attempt {this.Attempt})"

/// The host's live connections, by environment. Releasing an environment up to an attempt also
/// refuses connections of those attempts that register later (an intent that finishes after the
/// environment moved on).
[<Sealed>]
type internal LiveTable() =
    let gate = obj ()
    let entries = Dictionary<string, Live>(StringComparer.Ordinal)
    let released = Dictionary<string, int>(StringComparer.Ordinal)

    let watermark (id: string) =
        match released.TryGetValue id with
        | true, upTo -> upTo
        | false, _ -> 0

    /// Adds the connection of an attempt; returns what it replaces (to stop), or `Error` with the
    /// connection itself if its attempt was released.
    member _.Register(id: string, live: Live) : Result<Live option, Live> =
        lock gate (fun () ->
            if live.Attempt <= watermark id then
                Error live
            else
                let previous =
                    match entries.TryGetValue id with
                    | true, previous -> Some previous
                    | false, _ -> None

                entries[id] <- live
                Ok previous)

    member _.Get(id: string) : Live option =
        lock gate (fun () ->
            match entries.TryGetValue id with
            | true, live -> Some live
            | false, _ -> None)

    /// Changes the connection of an attempt, if it is the current one.
    member _.Update(id: string, attempt: int, change: Live -> Live) : Live option =
        lock gate (fun () ->
            match entries.TryGetValue id with
            | true, live when live.Attempt = attempt ->
                let changed = change live
                entries[id] <- changed
                Some changed
            | _ -> None)

    /// Whether this attempt's connection is the current one.
    member _.IsCurrent(id: string, attempt: int) : bool =
        lock gate (fun () ->
            match entries.TryGetValue id with
            | true, live -> live.Attempt = attempt
            | false, _ -> false)

    /// Removes the connection if its attempt is at most `upTo`, and refuses those attempts from now on.
    member _.Release(id: string, upTo: int) : Live option =
        lock gate (fun () ->
            released[id] <- max upTo (watermark id)

            match entries.TryGetValue id with
            | true, live when live.Attempt <= upTo ->
                entries.Remove id |> ignore
                Some live
            | _ -> None)

    /// Removes every connection.
    member _.Clear() : Live list =
        lock gate (fun () ->
            let all = List.ofSeq entries.Values
            entries.Clear()
            all)

    override _.ToString() = "LiveTable"

/// Keeps the end of a child's standard error, for diagnostics.
[<Sealed>]
type internal ErrorTail(limit: int) =
    let gate = obj ()
    let buffer = List<byte>()

    member _.Append(bytes: byte array) =
        lock gate (fun () ->
            buffer.AddRange bytes

            if buffer.Count > limit then
                buffer.RemoveRange(0, buffer.Count - limit))

    member _.Text = lock gate (fun () -> Encoding.UTF8.GetString(buffer.ToArray()).Trim())

    override _.ToString() = "ErrorTail"

/// Runs the host's environments (spec §9): the lifecycle machine as one actor on the host's
/// environments stream, the bootstrap of envd through the providers (`Platform` → `Upload` →
/// `Exec`, or envd in this process) and the connections to it, the components of the lockfile,
/// setup, and reconciliation of leases at a restart. Serves `Envs`. Use the functions of
/// `EnvsHost`.
[<Sealed>]
type EnvsHost<'R when 'R :> IClock and 'R :> ITrace and 'R :> IIds and 'R :> IVariants and 'R :> IScopeWith<'R>>
    internal (env: 'R, settings: EnvsSettings, log: EventLog<EnvEvent>) =
    let live = LiveTable()
    // The last stop of each environment's envd, which a new attempt waits for.
    let stops = Dictionary<string, Task<unit>>(StringComparer.Ordinal)
    let lifetime = new CancellationTokenSource()
    let mutable actor: Actor<EnvCommand, EnvEvent, EnvsState> option = None
    let mutable stopping = false

    let system = Attribution.system "environments"

    let provider (kind: string) : (EnvProvider * ProviderEnv) option =
        settings.Providers
        |> List.tryFind (fun c -> c.Value.Kind = kind)
        |> Option.map (fun c ->
            c.Value,
            {
                Extension = c.Env
                FsRead = settings.FsRead
                Fs = settings.Fs
                Shell = settings.Shell
                HostInfo = settings.HostInfo
            })

    let noProvider (kind: string) =
        $"no environment provider {Json.quote kind} is in effect"

    /// Runs provider code; a provider that throws is a failure of the step, not of the host.
    let call
        (providerEnv: ProviderEnv)
        (effect: Eff<IProviderEnv, Result<'T, ProviderError>>)
        : Eff<'R, Result<'T, string>> =
        fun env ->
            task {
                try
                    let! result = ProviderEnv.within providerEnv effect env

                    return result |> Result.mapError string
                with e when not (e :? OperationCanceledException) ->
                    return Error $"the provider failed ({e.GetType().Name})"
            }

    let stopLive (entry: Live) : Task<unit> =
        task {
            match entry.Client with
            | Some client ->
                try
                    let! _ = (EnvClient.close client).WaitAsync settings.StopTimeout
                    ()
                with _ ->
                    ()
            | None -> ()

            try
                do! entry.Stop()
            with _ ->
                ()
        }

    /// The same result, but continuations never run inline where it completes. Actors reply, and the
    /// in-memory log wakes subscribers, on the thread that appends: code that goes on to read the log
    /// or send again must not run inside the actor's append.
    let later (work: Task<'T>) : Task<'T> =
        let result =
            TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)

        work.ContinueWith(fun (completed: Task<'T>) ->
            if completed.IsCanceled then
                result.TrySetCanceled() |> ignore
            elif completed.IsFaulted then
                match completed.Exception with
                | null -> result.TrySetException(InvalidOperationException "the task faulted") |> ignore
                | e -> result.TrySetException e.InnerExceptions |> ignore
            else
                result.TrySetResult completed.Result |> ignore)
        |> ignore

        result.Task

    let send (key: string) (by: Attribution) (command: EnvCommand) (cancellation: CancellationToken) : Task<Outcome> =
        match actor with
        | Some actor -> later (actor.Send (Envelope.create (IdempotencyKey key) by command) cancellation)
        | None -> Task.FromResult Outcome.Stopped

    /// The connection of an attempt ended: unless the host closed it, the environment detaches.
    let watch (id: string) (attempt: int) (client: EnvClient) =
        client.Peer.Completion.ContinueWith(fun (completed: Task<CloseReason>) ->
            if not stopping && live.IsCurrent(id, attempt) then
                let reason =
                    if completed.IsCompletedSuccessfully then
                        $"the connection to envd ended ({completed.Result})"
                    else
                        "the connection to envd failed"

                send
                    $"disconnected:{id}:{attempt}"
                    system
                    (EnvCommand.Disconnected(id, attempt, reason))
                    CancellationToken.None
                |> ignore)
        |> ignore

    let windowsShell (machine: EnvMachine) : Eff<'R, string option> =
        eff {
            if machine.Target.Os = Os.Windows then
                let! shell = Variants.get Slots.windowsShell
                return Some(string shell)
            else
                return None
        }

    /// envd in this process.
    let startEmbedded (launch: EmbeddedLaunch) : Eff<'R, Result<Transport * (unit -> Task<unit>), string>> =
        fun _ ->
            task {
                match settings.Embed with
                | None -> return Error "this host cannot run envd in its own process"
                | Some embed ->
                    match! embed launch with
                    | Error e -> return Error $"envd did not start: {e}"
                    | Ok embedded -> return Ok(embedded.Transport, embedded.Stop)
            }

    /// envd through the provider's `Exec`, speaking on its standard input and output.
    let startPiped
        (providerEnv: ProviderEnv)
        (provider: EnvProvider)
        (lease: Lease)
        (machine: EnvMachine)
        (args: string list)
        (environment: (string * string) list)
        : Eff<'R, Result<Transport * (unit -> Task<unit>) * ErrorTail, string>> =
        eff {
            if not machine.SameHost then
                // envd binaries for other targets are components (T501): until then only the host's own
                // binary runs, on the host's machine.
                return Error "starting envd on another machine needs an envd for its target (T501)"
            else
                let spec: ProcSpec =
                    {
                        Program = Exec(settings.Binary, settings.BinaryArgs @ args)
                        Cwd = None
                        Env = Exactly environment
                    }

                match! call providerEnv (provider.Exec(lease, spec)) with
                | Error e -> return Error $"envd did not start: {e}"
                | Ok proc ->
                    let errors = ErrorTail 4096

                    let transport =
                        ProcTransport.create (ConnectionLimits.frames settings.Limits) proc errors.Append

                    let stop () =
                        task {
                            try
                                do! proc.CloseStdin()
                            with _ ->
                                ()

                            try
                                let! _ = (proc.Wait CancellationToken.None).WaitAsync settings.StopTimeout
                                ()
                            with _ ->
                                do! proc.Kill()
                                let! _ = proc.Wait CancellationToken.None
                                ()

                            transport.Abort()
                        }

                    return Ok(transport, stop, errors)
        }

    /// Releases the connections of attempts up to `upTo` and waits until envd stopped, also when
    /// another release is stopping it: one envd per state directory at a time.
    let release (id: string) (upTo: int) : Eff<'R, unit> =
        fun _ ->
            let stopping =
                lock stops (fun () ->
                    match live.Release(id, upTo) with
                    | Some entry ->
                        let stopped = stopLive entry
                        stops[id] <- stopped
                        stopped
                    | None ->
                        match stops.TryGetValue id with
                        | true, stopped -> stopped
                        | false, _ -> Task.FromResult())

            stopping

    let provision (id: string) (attempt: int) (spec: EnvSpec) : Eff<'R, EnvCommand list> =
        eff {
            match provider spec.Provider with
            | None -> return [ EnvCommand.Provisioned(id, attempt, Error(noProvider spec.Provider)) ]
            | Some(provider, providerEnv) ->
                let! created = call providerEnv (provider.Create { Id = id; Spec = spec })

                let checkedLease =
                    created
                    |> Result.bind (fun lease ->
                        if lease.Id <> id then
                            Error $"the provider leased {lease.Id} for the environment {id}"
                        else
                            Ok lease)

                return [ EnvCommand.Provisioned(id, attempt, checkedLease) ]
        }

    let bootstrap (id: string) (attempt: int) (spec: EnvSpec) (lease: Lease) : Eff<'R, EnvCommand list> =
        eff {
            // The envd of an earlier attempt stops before this one starts on the same state.
            do! release id (attempt - 1)

            match provider spec.Provider with
            | None -> return [ EnvCommand.Bootstrapped(id, attempt, Error(noProvider spec.Provider)) ]
            | Some(provider, providerEnv) ->
                match! call providerEnv (provider.Platform lease) with
                | Error e -> return [ EnvCommand.Bootstrapped(id, attempt, Error $"platform: {e}") ]
                | Ok machine ->
                    let transport = EnvSpecs.transport provider spec
                    let layout = EnvLayout.create machine lease
                    let! shell = windowsShell machine

                    let environment =
                        if machine.SameHost then
                            HostEnvironment.scrub settings.HostEnvironment
                        else
                            []

                    let! started =
                        match transport with
                        | TransportKind.InProcess when machine.SameHost ->
                            startEmbedded
                                {
                                    Workspace = lease.Workspace
                                    State = layout.EnvdState
                                    Shell = shell
                                    Environment = environment
                                }
                        | TransportKind.InProcess ->
                            Eff.ret (
                                Error "envd runs in the host's process only for environments on the host's machine"
                            )
                        | TransportKind.Piped ->
                            let args =
                                [
                                    "envd"
                                    "--stdio"
                                    "--workspace"
                                    lease.Workspace
                                    "--state"
                                    layout.EnvdState
                                    match shell with
                                    | Some shell ->
                                        "--shell"
                                        shell
                                    | None -> ()
                                ]

                            startPiped providerEnv provider lease machine args environment
                            |> Eff.map (Result.map (fun (transport, stop, _) -> transport, stop))
                        | TransportKind.Direct
                        | TransportKind.Dialback ->
                            Eff.ret (Error $"the {transport} transport is not supported yet (T501, T504)")

                    match started with
                    | Error e -> return [ EnvCommand.Bootstrapped(id, attempt, Error e) ]
                    | Ok(connection, stop) ->
                        let entry =
                            {
                                Attempt = attempt
                                Machine = machine
                                Layout = layout
                                Transport = connection
                                Stop = stop
                                Client = None
                                Installed = []
                                Sandbox = None
                            }

                        match live.Register(id, entry) with
                        | Error refused ->
                            do! Eff.ofTask (fun () -> stopLive refused)
                            return []
                        | Ok previous ->
                            match previous with
                            | Some previous -> do! Eff.ofTask (fun () -> stopLive previous)
                            | None -> ()

                            return [ EnvCommand.Bootstrapped(id, attempt, Ok(transport, machine)) ]
        }

    let connect (id: string) (attempt: int) : Eff<'R, EnvCommand list> =
        eff {
            match live.Get id with
            | Some entry when entry.Attempt = attempt ->
                let connectionEnv: ConnectionEnv =
                    {
                        Clock = env.Clock
                        Scope = Scope.ofCancellation lifetime.Token
                    }

                let! connected =
                    Eff.ofTask (fun () -> Eff.run connectionEnv (EnvClient.connect entry.Transport))

                match connected with
                | Error e -> return [ EnvCommand.Connected(id, attempt, Error $"connecting to envd: {e}") ]
                | Ok client ->
                    match live.Update(id, attempt, fun entry -> { entry with Client = Some client }) with
                    | None ->
                        let! _ = Eff.ofTask (fun () -> EnvClient.close client)
                        return []
                    | Some _ ->
                        watch id attempt client
                        return [ EnvCommand.Connected(id, attempt, Ok client.Platform) ]
            | _ -> return [ EnvCommand.Connected(id, attempt, Error "envd is not running") ]
        }

    let runSetup (shell: Shell) (workspace: string) (commands: string list) : Eff<'R, Result<unit, string>> =
        fun env ->
            task {
                let mutable failure = None

                for line in commands do
                    if failure.IsNone then
                        let command = Command.script line |> Command.inDir workspace

                        match! shell.Run command env.Scope.Cancellation with
                        | Error e -> failure <- Some $"setup: {e}"
                        | Ok output when output.ExitCode <> 0 ->
                            let errors = (Output.stderrText output).Trim()

                            let tail =
                                if errors.Length > 500 then
                                    "…" + errors.Substring(errors.Length - 500)
                                else
                                    errors

                            failure <- Some $"setup: {Json.quote line} exited with {output.ExitCode}: {tail}"
                        | Ok _ -> ()

                return
                    match failure with
                    | Some reason -> Error reason
                    | None -> Ok()
            }

    /// Runs a driver's probe in its extension's environment over envd's own (unsandboxed)
    /// capabilities; a driver that throws fails the probe, not the host.
    let probe
        (contributed: Contributed<SandboxDriver>)
        (client: EnvClient)
        (site: SandboxSite)
        : Eff<'R, Result<SandboxReady, SandboxUnavailable>> =
        fun env ->
            task {
                let sandboxEnv: SandboxEnv =
                    {
                        Extension = contributed.Env
                        FsRead = client.FsRead
                        Fs = client.Fs
                        Shell = client.Shell
                    }

                try
                    return! SandboxEnv.within sandboxEnv (contributed.Value.Probe site) env
                with e when not (e :? OperationCanceledException) ->
                    return
                        Error
                            {
                                Reason = $"the sandbox driver {contributed.Value.Id} failed ({e.GetType().Name})"
                                Remedy = None
                            }
            }

    /// The sandbox of a connected environment (spec §9.1): the driver the host's `sandbox.driver`
    /// and the spec choose for its OS, probed in it; `auto` falls back to `none` (with notes that
    /// say why) where no driver can confine processes, a required driver never does.
    let sandboxFor (entry: Live) (client: EnvClient) (spec: EnvSpec) : Eff<'R, Result<EnvSandbox, string>> =
        eff {
            let! hostChoice = Variants.get SandboxSlots.driver
            let! writable = Variants.get SandboxSlots.writable
            let! denied = Variants.get SandboxSlots.denied
            let machine = entry.Machine
            let layout = entry.Layout

            // The user's home on the host's machine; other machines' come with T501.
            let home =
                if machine.SameHost then
                    Some settings.HostInfo.Home
                else
                    None

            let network = EnvSpecs.networkAccess spec.Network
            let policy = EnvSandbox.policy layout machine home writable denied network
            let site = EnvSandbox.site layout machine client.Platform.Shell home policy

            // NuGet's fallback folder must exist: NuGet fails on one that does not.
            let hostVariables = if machine.SameHost then settings.HostEnvironment else []

            let! userPackages =
                match EnvSandbox.nugetPackages home hostVariables with
                | None -> Eff.ret None
                | Some folder ->
                    fun (env: 'R) ->
                        task {
                            let! entries = client.FsOps.StatMany [ folder ] env.Scope.Cancellation

                            return
                                match entries with
                                | [ Ok entry ] when entry.Kind = EntryKind.Directory -> Some folder
                                | _ -> None
                        }

            let contributedAs (driver: SandboxDriver) =
                settings.Sandboxes |> List.find (fun c -> c.Value.Id = driver.Id)

            let sandbox (contributed: Contributed<SandboxDriver>) (ready: SandboxReady) (notes: string list) =
                {
                    Driver = contributed.Value
                    Ready = ready
                    Policy = policy
                    Info =
                        {
                            Driver = contributed.Value.Id
                            Confines = contributed.Value.Confines
                            Notes = notes @ ready.Notes
                        }
                    Defaults =
                        if contributed.Value.Confines then
                            EnvSandbox.defaults layout policy userPackages
                        else
                            []
                    Unsandboxed = client
                }

            let unconfined (notes: string list) : Eff<'R, Result<EnvSandbox, string>> =
                eff {
                    match
                        settings.Sandboxes
                        |> List.tryFind (fun c -> c.Value.Id = SandboxDrivers.NoSandbox)
                    with
                    | None ->
                        let why = String.Join("; ", notes)

                        return
                            Error
                                $"sandbox: no driver confines processes here ({why}), and none (fx.sandbox.none) is not in effect"
                    | Some none ->
                        match! probe none client site with
                        | Ok ready -> return Ok(sandbox none ready notes)
                        | Error e -> return Error $"sandbox: {e}"
                }

            let! made =
                fun (env: 'R) ->
                    task {
                        let! temp = client.Fs.CreateDirectory layout.Temp env.Scope.Cancellation
                        let! cache = client.Fs.CreateDirectory layout.Cache env.Scope.Cancellation
                        let! own = client.Fs.CreateDirectory layout.Sandbox env.Scope.Cancellation
                        return temp |> Result.bind (fun () -> cache) |> Result.bind (fun () -> own)
                    }

            match made with
            | Error e -> return Error $"sandbox: cannot create its directories: {e}"
            | Ok() ->
                let drivers = settings.Sandboxes |> List.map _.Value

                let! chosen =
                    match EnvSandbox.choose hostChoice spec machine.Target.Os drivers with
                    | Error e -> Eff.ret (Error $"sandbox: {e}")
                    | Ok(SandboxChoice.Required driver) ->
                        eff {
                            let contributed = contributedAs driver

                            match! probe contributed client site with
                            | Ok ready -> return Ok(sandbox contributed ready [])
                            | Error unavailable -> return Error $"sandbox {driver.Id}: {unavailable}"
                        }
                    | Ok(SandboxChoice.Preferred driver) ->
                        eff {
                            let contributed = contributedAs driver

                            match! probe contributed client site with
                            | Ok ready -> return Ok(sandbox contributed ready [])
                            | Error unavailable ->
                                return! unconfined [ $"{driver.Id} cannot sandbox here: {unavailable}" ]
                        }
                    | Ok(SandboxChoice.Unconfined reason) -> unconfined [ reason ]

                match chosen with
                | Ok sandbox when network = NetworkAccess.Denied && not sandbox.Driver.Confines ->
                    let why = String.Join("; ", sandbox.Info.Notes)

                    return
                        Error
                            $"network \"none\" needs a sandbox that confines processes, and this environment has none ({why})"
                | chosen -> return chosen
        }

    let install (id: string) (attempt: int) (spec: EnvSpec) : Eff<'R, EnvCommand list> =
        eff {
            match live.Get id with
            | Some({ Client = Some client } as entry) when entry.Attempt = attempt ->
                let! installed =
                    if settings.Components.Requires.IsEmpty then
                        Eff.ret (Ok [])
                    else
                        match settings.Components.For entry.Machine with
                        | None ->
                            Eff.ret (
                                Error
                                    $"no component installer serves {entry.Machine} yet: components there come with T501"
                            )
                        | Some components ->
                            fun (env: 'R) ->
                                task {
                                    let! result =
                                        components.Ensure
                                            {
                                                Holder = "environment:" + id
                                                Requires = settings.Components.Requires
                                            }
                                            env.Scope.Cancellation

                                    return result |> Result.mapError (fun e -> $"components: {e}")
                                }

                match installed with
                | Error e -> return [ EnvCommand.Installed(id, attempt, Error e) ]
                | Ok installed ->
                    match! sandboxFor entry client spec with
                    | Error e -> return [ EnvCommand.Installed(id, attempt, Error e) ]
                    | Ok sandbox ->
                        // Setup is the project's code: it runs sandboxed, with its own network policy.
                        let setupPolicy =
                            { sandbox.Policy with
                                Network = EnvSpecs.networkAccess (spec.Setup.Network |> Option.orElse spec.Network)
                            }

                        match!
                            runSetup (EnvSandbox.shell sandbox setupPolicy) entry.Layout.Workspace spec.Setup.Run
                        with
                        | Error e -> return [ EnvCommand.Installed(id, attempt, Error e) ]
                        | Ok() ->
                            live.Update(
                                id,
                                attempt,
                                fun entry ->
                                    { entry with
                                        Installed = installed
                                        Sandbox = Some sandbox
                                    }
                            )
                            |> ignore

                            let names = installed |> List.map (fun c -> $"{c.Id}@{c.Version}")

                            return
                                [
                                    EnvCommand.Installed(
                                        id,
                                        attempt,
                                        Ok
                                            {
                                                Components = names
                                                Sandbox = Some sandbox.Info
                                            }
                                    )
                                ]
            | _ ->
                return
                    [
                        EnvCommand.Installed(id, attempt, Error "the host has no connection to envd")
                    ]
        }

    let leaseOf (provider: EnvProvider) (providerEnv: ProviderEnv) (id: string) (lease: Lease option) =
        eff {
            match lease with
            | Some lease -> return Ok(Some lease)
            | None ->
                // Destroyed while it was being created: the provider names what it creates after the id.
                let! leases = call providerEnv provider.List
                return leases |> Result.map (List.tryFind (fun l -> l.Id = id))
        }

    let destroy (id: string) (spec: EnvSpec) (lease: Lease option) : Eff<'R, EnvCommand list> =
        eff {
            do! release id Int32.MaxValue

            match provider spec.Provider with
            | None -> return [ EnvCommand.Destroyed(id, Error(noProvider spec.Provider)) ]
            | Some(provider, providerEnv) ->
                match! leaseOf provider providerEnv id lease with
                | Error e -> return [ EnvCommand.Destroyed(id, Error e) ]
                | Ok None -> return [ EnvCommand.Destroyed(id, Ok()) ]
                | Ok(Some lease) ->
                    let! destroyed = call providerEnv (provider.Destroy lease)
                    return [ EnvCommand.Destroyed(id, destroyed) ]
        }

    let suspendOrResume
        (id: string)
        (spec: EnvSpec)
        (lease: Lease)
        (pick: EnvProvider -> (Lease -> Eff<IProviderEnv, Result<unit, ProviderError>>) option)
        (finish: string * Result<unit, string> -> EnvCommand)
        : Eff<'R, EnvCommand list> =
        eff {
            match provider spec.Provider with
            | None -> return [ finish (id, Error(noProvider spec.Provider)) ]
            | Some(provider, providerEnv) ->
                match pick provider with
                | None -> return [ finish (id, Error $"the {provider.Kind} provider cannot do this") ]
                | Some operation ->
                    let! result = call providerEnv (operation lease)
                    return [ finish (id, result) ]
        }

    let reconcile (leases: (string * int * EnvSpec * Lease) list) : Eff<'R, EnvCommand list> =
        eff {
            let present = ResizeArray<string * int>()
            let missing = ResizeArray<string * string>()

            for kind in leases |> List.map (fun (_, _, spec, _) -> spec.Provider) |> List.distinct do
                let ofKind = leases |> List.filter (fun (_, _, spec, _) -> spec.Provider = kind)

                match provider kind with
                | None ->
                    // Its extension is not loaded: the environment may well exist; it stays detached.
                    for id, attempt, _, _ in ofKind do
                        present.Add(id, attempt)
                | Some(provider, providerEnv) ->
                    match! call providerEnv provider.List with
                    | Error _ ->
                        for id, attempt, _, _ in ofKind do
                            present.Add(id, attempt)
                    | Ok held ->
                        for id, attempt, _, _ in ofKind do
                            if held |> List.exists (fun l -> l.Id = id) then
                                present.Add(id, attempt)
                            else
                                missing.Add(id, $"the {kind} provider no longer has it")

            return [ EnvCommand.Reconciled(List.ofSeq present, List.ofSeq missing) ]
        }

    let interpret (intent: EnvIntent) : Eff<'R, EnvCommand list> =
        match intent with
        | EnvIntent.Provision(id, attempt, spec) -> provision id attempt spec
        | EnvIntent.Bootstrap(id, attempt, spec, lease) -> bootstrap id attempt spec lease
        | EnvIntent.Connect(id, attempt) -> connect id attempt
        | EnvIntent.Install(id, attempt, spec) -> install id attempt spec
        | EnvIntent.Suspend(id, spec, lease) -> suspendOrResume id spec lease (fun p -> p.Suspend) EnvCommand.Suspended
        | EnvIntent.Resume(id, spec, lease) -> suspendOrResume id spec lease (fun p -> p.Resume) EnvCommand.Resumed
        | EnvIntent.Destroy(id, spec, lease) -> destroy id spec lease
        | EnvIntent.Release(id, upTo) -> release id upTo |> Eff.map (fun () -> [])
        | EnvIntent.Reconcile leases -> reconcile leases

    let theActor () =
        match actor with
        | Some actor -> actor
        | None -> invalidOp "The environments host is not started."

    let followGate = obj ()

    /// The newest state `waitUntil` returned a record from, when the actor has not published it yet:
    /// the actor appends events, which wakes the log's followers, and only then publishes the state
    /// they make. Reads take the newer of the two, so they never show less than a result did (`Create`
    /// returned a `Ready` record, and `Get` must not say `Connected` after it).
    let mutable followed: Snapshot<EnvsState> option = None

    let snapshot () =
        let published = Actor.snapshot (theActor ())

        lock followGate (fun () ->
            match followed with
            | Some newer when newer.Seq > published.Seq -> newer
            | _ -> published)

    let saw (seen: Snapshot<EnvsState>) =
        lock followGate (fun () ->
            match followed with
            | Some newer when newer.Seq >= seen.Seq -> ()
            | _ -> followed <- Some seen)

    let tryRecord (id: string) : EnvRecord option =
        (snapshot ()).State.Environments.TryFind id

    /// Waits until the environment's record satisfies `settled`, following the log after a snapshot:
    /// the record, `None` if there is no such environment, or `Error` once the host stopped.
    let waitUntil
        (id: string)
        (settled: EnvRecord -> bool)
        (cancellation: CancellationToken)
        : Task<Result<EnvRecord option, EnvError>> =
        task {
            let current = snapshot ()

            match current.State.Environments.TryFind id with
            | Some record when settled record -> return Ok(Some record)
            | None -> return Ok None
            | Some _ ->
                let state = ref current.State
                let found = ref None

                use linked =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token)

                let subscriber = env.WithScope(Scope.ofCancellation linked.Token)

                try
                    let! _ =
                        EventLog.subscribe log settings.Stream current.Seq (fun recorded ->
                            eff {
                                state.Value <- Lifecycle.evolve state.Value recorded.Event

                                match state.Value.Environments.TryFind id with
                                | Some record when settled record ->
                                    found.Value <- Some record

                                    saw
                                        {
                                            Seq = recorded.Seq
                                            State = state.Value
                                        }

                                    return false
                                | _ -> return true
                            })
                        |> Eff.run subscriber
                        |> later

                    ()
                with :? OperationCanceledException when not cancellation.IsCancellationRequested ->
                    ()

                cancellation.ThrowIfCancellationRequested()

                match found.Value with
                | Some record -> return Ok(Some record)
                | None -> return Error EnvError.Stopped
        }

    let idle (record: EnvRecord) = record.Step.IsNone

    let newKey (prefix: string) : Task<string> =
        task {
            let! id = Ids.newId env
            return prefix + ":" + string id
        }

    /// Sends a command and waits until the environment has no step in flight: the record, if it is
    /// then in one of the phases the command is for.
    let sendAndSettle
        (prefix: string)
        (id: string)
        (command: EnvCommand)
        (expected: Phase list)
        (cancellation: CancellationToken)
        : Task<Result<EnvRecord, EnvError>> =
        task {
            let! key = newKey (prefix + ":" + id)

            match! send key system command cancellation with
            | Outcome.Stopped -> return Error EnvError.Stopped
            | Outcome.Rejected rejection when rejection.Code = "environment.unknown" ->
                return Error(EnvError.UnknownEnvironment id)
            | Outcome.Rejected rejection -> return Error(EnvError.Rejected rejection)
            | Outcome.Accepted _ ->
                match! waitUntil id idle cancellation with
                | Ok(Some record) when List.contains record.Phase expected -> return Ok record
                | Ok(Some record) -> return Error(EnvError.NotReady record)
                | Ok None -> return Error(EnvError.UnknownEnvironment id)
                | Error e -> return Error e
        }

    let createEnv (spec: EnvSpec) (cancellation: CancellationToken) : Task<Result<EnvRecord, EnvError>> =
        task {
            match provider spec.Provider with
            | None -> return Error(EnvError.UnknownProvider spec.Provider)
            | Some(provider, _) ->
                let drivers = settings.Sandboxes |> List.map _.Value.Id

                match EnvSpecs.check provider spec @ EnvSpecs.checkSandbox drivers spec with
                | _ :: _ as issues -> return Error(EnvError.Invalid issues)
                | [] ->
                    let! id = Ids.newId env
                    let! now = Clock.now env
                    let id = string id

                    let request =
                        EnvCommand.Request
                            {
                                Id = id
                                Spec = spec
                                At = now.UnixMicroseconds
                            }

                    match! send ("request:" + id) system request cancellation with
                    | Outcome.Stopped -> return Error EnvError.Stopped
                    | Outcome.Rejected rejection -> return Error(EnvError.Rejected rejection)
                    | Outcome.Accepted _ ->
                        match! waitUntil id idle cancellation with
                        | Ok(Some record) when record.Phase = Phase.Ready -> return Ok record
                        | Ok(Some record) -> return Error(EnvError.NotReady record)
                        | Ok None -> return Error(EnvError.UnknownEnvironment id)
                        | Error e -> return Error e
        }

    let handleOf (record: EnvRecord) : EnvHandle option =
        match live.Get record.Id with
        | Some({
                   Client = Some _
                   Sandbox = Some sandbox
               } as entry) when entry.Attempt = record.Attempt ->
            Some
                {
                    Id = record.Id
                    Spec = record.Spec
                    Client = EnvSandbox.client sandbox sandbox.Policy
                    Machine = entry.Machine
                    Layout = entry.Layout
                    Installed = entry.Installed
                    Sandbox = sandbox
                }
        | _ -> None

    let openEnv (id: string) (cancellation: CancellationToken) : Task<Result<EnvHandle, EnvError>> =
        task {
            let mutable result = None
            let mutable attached = false

            while result.IsNone do
                match tryRecord id with
                | None -> result <- Some(Error(EnvError.UnknownEnvironment id))
                | Some record ->
                    match record.Step, record.Phase, handleOf record with
                    | Some _, _, _ ->
                        match! waitUntil id idle cancellation with
                        | Error e -> result <- Some(Error e)
                        | Ok _ -> ()
                    | None, Phase.Ready, Some handle -> result <- Some(Ok handle)
                    | None, (Phase.Provisioning | Phase.Bootstrapped | Phase.Connected | Phase.Ready), _ when
                        not attached
                        ->
                        attached <- true

                        match! sendAndSettle "attach" id (EnvCommand.Attach id) [ Phase.Ready ] cancellation with
                        | Error e -> result <- Some(Error e)
                        | Ok _ -> ()
                    | _ -> result <- Some(Error(EnvError.NotReady record))

            return result.Value
        }

    let suspendOrResumeEnv (id: string) (resume: bool) (cancellation: CancellationToken) =
        task {
            match tryRecord id with
            | None -> return Error(EnvError.UnknownEnvironment id)
            | Some record ->
                match provider record.Spec.Provider with
                | None -> return Error(EnvError.UnknownProvider record.Spec.Provider)
                | Some(provider, _) when not provider.Caps.Suspend ->
                    return
                        Error(
                            EnvError.Rejected(
                                Rejection.create
                                    "environment.unsupported"
                                    $"the {provider.Kind} provider cannot suspend environments"
                            )
                        )
                | Some _ when resume ->
                    return! sendAndSettle "resume" id (EnvCommand.Resume id) [ Phase.Provisioning ] cancellation
                | Some _ -> return! sendAndSettle "suspend" id (EnvCommand.Suspend id) [ Phase.Suspended ] cancellation
        }

    member internal _.Interpret = interpret

    member internal _.Attach(started: Actor<EnvCommand, EnvEvent, EnvsState>) = actor <- Some started

    member _.Envs: Envs =
        {
            Create = createEnv
            Get = fun id _ -> Task.FromResult(tryRecord id)
            List = fun _ -> Task.FromResult(Lifecycle.records (snapshot ()).State)
            Open = openEnv
            Suspend = fun id cancellation -> suspendOrResumeEnv id false cancellation
            Resume = fun id cancellation -> suspendOrResumeEnv id true cancellation
            Destroy =
                fun id cancellation ->
                    sendAndSettle "destroy" id (EnvCommand.Destroy id) [ Phase.Destroyed ] cancellation
        }

    member _.Snapshot() : Snapshot<EnvsState> = snapshot ()

    /// Stops the actor, then closes every connection (envd stops; leases stay, for the next host).
    member _.StopAsync() : Task<unit> =
        task {
            stopping <- true

            match actor with
            | Some actor -> do! actor.Stop()
            | None -> ()

            for entry in live.Clear() do
                do! stopLive entry

            lifetime.Cancel()
        }

    override _.ToString() = $"EnvsHost({settings.Stream})"

[<RequireQualifiedAccess>]
module EnvsHost =
    /// The host's environments stream.
    let stream (host: string) : StreamId =
        StreamId.create Lifecycle.StreamKind host

    /// Starts the host on a log: rebuilds the environments from it, picks up the steps that were in
    /// flight, and reconciles the leases with their providers (spec §9.5).
    let start<'R when 'R :> IClock and 'R :> ITrace and 'R :> IIds and 'R :> IVariants and 'R :> IScopeWith<'R>>
        (settings: EnvsSettings)
        (log: EventLog<EnvEvent>)
        : Eff<'R, EnvsHost<'R>> =
        eff {
            let! env = Eff.ask
            let host = EnvsHost<'R>(env, settings, log)

            let spec =
                { ActorSpec.create settings.Stream Lifecycle.machine log host.Interpret with
                    Recover = Lifecycle.recover
                }

            let! actor = Actor.start spec
            host.Attach actor
            return host
        }

    /// The `Envs` capability it serves.
    let envs (host: EnvsHost<'R>) : Envs = host.Envs

    let stop (host: EnvsHost<'R>) : Task<unit> = host.StopAsync()
