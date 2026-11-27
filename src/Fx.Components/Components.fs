namespace Fx.Components

open System
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Sdk

/// Why components could not be made available.
type ComponentError =
    /// The lockfile does not have what is required (plan again).
    | NotLocked of reason: string
    /// The installer refused the request.
    | Refused of reason: string
    | InstallFailed of install: string * failure: InstallFailure
    /// The installer stopped before it answered.
    | InstallerStopped

    override this.ToString() =
        match this with
        | NotLocked reason -> reason
        | Refused reason -> $"refused: {reason}"
        | InstallFailed(install, failure) -> $"{install}: {failure}"
        | InstallerStopped -> "the installer stopped"

/// Components that a holder needs.
type EnsureRequest =
    {
        /// Who uses them, e.g. `extension:<id>`: what the holder used before and no longer
        /// requires can be pruned.
        Holder: string
        Requires: ComponentRef list
    }

    override this.ToString() =
        let requires = this.Requires |> List.map string |> String.concat ", "
        $"{this.Holder}: {requires}"

/// The components capability (spec §4.2 `IComponents`): makes components available and says where
/// they are. Data in, data out, so it can be served to an environment over JSON-RPC (T111).
///
/// Interpreter: `ComponentInstaller.components`, over an installer actor and a lockfile.
type Components =
    {
        /// Installs the components (and their dependencies) from the lockfile if needed, records
        /// that the holder uses exactly these, and returns them, dependencies first.
        Ensure: EnsureRequest -> CancellationToken -> Task<Result<InstalledComponent list, ComponentError>>
    }

    override _.ToString() = "Components"

type IComponents =
    abstract Components: Components

module Components =
    let ensure<'R when 'R :> IComponents and 'R :> IScope>
        (request: EnsureRequest)
        : Eff<'R, Result<InstalledComponent list, ComponentError>> =
        fun env -> env.Components.Ensure request env.Scope.Cancellation

    /// The holder name of an extension.
    let extensionHolder (id: string) = "extension:" + id

    /// The components phase of the extension bootstrap (spec §16.1) with these components: installs
    /// what an admitted extension's manifest requires, held by `extension:<id>`, and says where they are.
    let installWith<'R when 'R :> IScope>
        (components: Components)
        (manifest: Manifest)
        : Eff<'R, Result<InstalledComponent list, string>> =
        fun env ->
            task {
                match!
                    components.Ensure
                        {
                            Holder = extensionHolder manifest.Id
                            Requires = manifest.Requires
                        }
                        env.Scope.Cancellation
                with
                | Ok installed -> return Ok installed
                | Error e -> return Error $"components: {e}"
            }

    /// `installWith` the environment's components. Fits `HostSettings.InstallComponents`.
    let installFor<'R when 'R :> IComponents and 'R :> IScope>
        (manifest: Manifest)
        : Eff<'R, Result<InstalledComponent list, string>> =
        fun env -> installWith env.Components manifest env

/// Runs an installer as an actor and serves `Components` from it.
module ComponentInstaller =

    type Installer = Actor<InstallerCommand, InstallerEvent, InstallerState>

    /// The installer's stream for an install root, named after the installer.
    let stream (name: string) : StreamId = StreamId.create "components" name

    /// The actor spec: the installer machine, its pipeline, recovery of work in flight, and intents
    /// one at a time (so roots are published before the installs that rely on them).
    let spec<'R
        when 'R :> IInstallFiles
        and 'R :> IFs
        and 'R :> IFetch
        and 'R :> IShell
        and 'R :> IClock
        and 'R :> ITrace
        and 'R :> IScopeWith<'R>>
        (settings: InstallerSettings<'R>)
        (log: EventLog<InstallerEvent>)
        : ActorSpec<'R, InstallerCommand, InstallerEvent, InstallerState, InstallerIntent> =
        { ActorSpec.create (stream settings.Name) Installer.machine log (Pipeline.interpret settings) with
            Recover = Installer.recover
            Intents = IntentOrder.Sequential
        }

    let start<'R
        when 'R :> IInstallFiles
        and 'R :> IFs
        and 'R :> IFetch
        and 'R :> IShell
        and 'R :> IClock
        and 'R :> ITrace
        and 'R :> IScopeWith<'R>>
        (settings: InstallerSettings<'R>)
        (log: EventLog<InstallerEvent>)
        : Eff<'R, Installer> =
        Actor.start (spec settings log)

    let private installed (spec: InstallSpec) (info: InstalledInfo) : InstalledComponent =
        let exeSuffix = TargetTriple.exeSuffix spec.Target

        let path (relative: string) =
            match Component.expand [ "exeSuffix", exeSuffix ] relative with
            | Ok relative -> InstallRoot.file info.Dir relative
            | Error _ -> InstallRoot.file info.Dir relative

        {
            Id = spec.Id
            Version = spec.Version
            Dir = info.Dir
            Bin = spec.Exports.Bin |> List.map (fun (name, relative) -> name, path relative)
            Env =
                spec.Exports.Env
                |> List.map (fun (name, value) ->
                    name,
                    (match Component.expand [ "dir", info.Dir ] value with
                     | Ok value -> value
                     | Error _ -> value))
        }

    /// The outcome of an install, if it has one: installed, or failed.
    let private settled (state: InstallerState) (key: InstallKey) : Result<InstalledInfo, InstallFailure> option =
        match state.Installs.TryFind key with
        | Some(Installed(_, info)) -> Some(Ok info)
        | Some(Failed(_, _, _, failure)) -> Some(Error failure)
        | _ -> None

    /// Sends `Ensure` and waits until the install it started or joined settles: installed (or
    /// found), or failed. `key` is the command's idempotency key; make it unique.
    let ensureTask
        (installer: Installer)
        (key: string)
        (holder: string)
        (spec: InstallSpec)
        (cancellation: CancellationToken)
        : Task<Result<InstalledInfo, ComponentError>> =
        task {
            let envelope =
                Envelope.create
                    (IdempotencyKey key)
                    (Attribution.system "components")
                    (InstallerCommand.Ensure(holder, spec))

            match! installer.Send envelope cancellation with
            | Outcome.Stopped -> return Error InstallerStopped
            | Outcome.Rejected rejection -> return Error(Refused rejection.Message)
            | Outcome.Accepted _ ->
                // The actor publishes its state before it replies: the install is now settled or in
                // progress. Follow the log until it settles, or until the installer stops.
                let snapshot = installer.Snapshot()
                let mutable seen = snapshot.Seq
                let mutable outcome = settled snapshot.State spec.Key
                let mutable stopped = false

                while outcome.IsNone && not stopped do
                    use waiting = CancellationTokenSource.CreateLinkedTokenSource cancellation
                    let past = installer.Log.WaitPast installer.Stream seen waiting.Token
                    let! first = Task.WhenAny(past :> Task, installer.Completion :> Task)

                    if Object.ReferenceEquals(first, installer.Completion) && not past.IsCompleted then
                        waiting.Cancel()
                        stopped <- true
                    else
                        let! events = installer.Log.Read installer.Stream seen 1000 cancellation

                        for recorded in events do
                            seen <- recorded.Seq

                            if outcome.IsNone then
                                match recorded.Event with
                                | InstallerEvent.Found(key, info)
                                | InstallerEvent.Installed(key, info) when key = spec.Key -> outcome <- Some(Ok info)
                                | InstallerEvent.Failed(key, _, failure) when key = spec.Key ->
                                    outcome <- Some(Error failure)
                                | _ -> ()

                match outcome with
                | Some(Ok info) -> return Ok info
                | Some(Error failure) -> return Error(InstallFailed(spec.Key.Name, failure))
                | None -> return Error InstallerStopped
        }

    /// `ensureTask` as an effect.
    let ensure<'R when 'R :> IScope>
        (installer: Installer)
        (key: string)
        (holder: string)
        (spec: InstallSpec)
        : Eff<'R, Result<InstalledInfo, ComponentError>> =
        fun env -> ensureTask installer key holder spec env.Scope.Cancellation

    /// `Components` from an installer and a lockfile, for one target. `newKey` gives unique
    /// idempotency keys (e.g. from `Ids`).
    let components
        (installer: Installer)
        (lockfile: Lockfile)
        (target: TargetTriple)
        (newKey: unit -> string)
        : Components =
        let ensure (request: EnsureRequest) (cancellation: CancellationToken) =
            task {
                match
                    Lockfile.resolve request.Requires lockfile
                    |> Result.bind (fun ids -> Lockfile.installs target ids lockfile)
                with
                | Error reason -> return Error(NotLocked reason)
                | Ok specs ->
                    let hold =
                        Envelope.create
                            (IdempotencyKey(newKey ()))
                            (Attribution.system "components")
                            (InstallerCommand.Hold(request.Holder, specs |> List.map (fun s -> s.Key)))

                    match! installer.Send hold cancellation with
                    | Outcome.Stopped -> return Error InstallerStopped
                    | Outcome.Rejected rejection -> return Error(Refused rejection.Message)
                    | Outcome.Accepted _ ->
                        let results = ResizeArray<InstalledComponent>()
                        let mutable failure = None

                        for spec in specs do
                            if failure.IsNone then
                                match! ensureTask installer (newKey ()) request.Holder spec cancellation with
                                | Ok info -> results.Add(installed spec info)
                                | Error e -> failure <- Some e

                        match failure with
                        | Some e -> return Error e
                        | None -> return Ok(List.ofSeq results)
            }

        { Ensure = ensure }

    /// Sends a command to the installer.
    let send (installer: Installer) (key: string) (command: InstallerCommand) : Eff<#IScope, Outcome> =
        Actor.send installer (Envelope.create (IdempotencyKey key) (Attribution.system "components") command)
