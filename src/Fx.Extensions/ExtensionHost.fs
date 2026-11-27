namespace Fx.Extensions

open System
open System.Collections.Generic
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Sdk

/// An extension package the host may load.
[<RequireQualifiedAccess; NoEquality; NoComparison>]
type Package =
    /// Compiled into the binary: a value from a static list (spec §16.4), never a loaded assembly.
    | Native of Extension
    /// A manifest and the package directory it came from: declarative, process, or a runtime an
    /// extension contributes.
    | Manifest of Manifest * root: string option

    override this.ToString() =
        match this with
        | Native extension -> "native " + string extension
        | Manifest(manifest, _) -> string manifest

module Package =
    let manifest (package: Package) : Manifest =
        match package with
        | Package.Native extension -> extension.Manifest
        | Package.Manifest(manifest, _) -> manifest

/// A package to load, where it came from, and the hash host config pins it to.
[<NoEquality; NoComparison>]
type Candidate =
    {
        Source: Source
        Package: Package
        Pin: string option
    }

    override this.ToString() = $"{this.Package} from {this.Source}"

module Candidate =
    let create (source: Source) (package: Package) : Candidate =
        {
            Source = source
            Package = package
            Pin = None
        }

/// The bootstrap's phases, in order (spec §16.1).
[<RequireQualifiedAccess>]
type Phase =
    /// Native extensions (in the binary), then declarative ones (data only).
    | InBinary
    /// Admits the extensions of the later phases and installs the components they require.
    | Components
    /// Extension processes.
    | Process
    /// Extensions whose runtime an extension contributes (e.g. `bun`, from a process extension).
    | Hosted

    override this.ToString() =
        match this with
        | InBinary -> "in-binary"
        | Components -> "components"
        | Process -> "process"
        | Hosted -> "hosted"

/// What became of a candidate.
[<RequireQualifiedAccess>]
type Outcome =
    | Activated
    /// Refused before any of its code ran: trust, pins, versions, conflicts, an invalid manifest.
    | Rejected of Rejection
    /// Admitted, but its components or its activation failed.
    | Failed of reason: string
    /// Not for this host (another placement), or disabled in host config.
    | Skipped of reason: string

    override this.ToString() =
        match this with
        | Activated -> "activated"
        | Rejected rejection -> "rejected: " + string rejection
        | Failed reason -> "failed: " + reason
        | Skipped reason -> "skipped: " + reason

type ReportEntry =
    {
        Id: string
        Phase: Phase
        Outcome: Outcome
    }

    override this.ToString() =
        $"{this.Id} [{this.Phase}] {this.Outcome}"

/// How the host loads extensions.
[<NoEquality; NoComparison>]
type HostSettings<'R> =
    {
        Trust: TrustContext
        /// The contribution types the host knows besides the core's (its own hook points).
        Catalog: Catalog
        /// The environment each extension's code runs in, attenuated to its manifest.
        EnvFor: Manifest -> IExtensionEnv
        /// Installs the components an admitted extension requires (spec §6.1) and says where they are:
        /// an extension process's launch names their commands (`${bin.<name>}`).
        InstallComponents: Manifest -> Eff<'R, Result<InstalledComponent list, string>>
        Process: ProcessRuntime.ProcessSettings
        /// Extensions disabled in host config. Policy extensions cannot be disabled.
        Disabled: string list
    }

    override this.ToString() = $"HostSettings({this.Trust})"

/// An activated extension.
[<NoEquality; NoComparison>]
type Loaded =
    {
        Manifest: Manifest
        Source: Source
        Env: IExtensionEnv
        Activation: Activation
        /// Deactivates it and releases its runtime (stops its process).
        Stop: Eff<IExtensionEnv, unit>
    }

    override this.ToString() = string this.Manifest

/// The extensions in effect: the registry's state (a fold of `Events`), the activated extensions, and
/// the catalog. Queries read it; changes return a new one.
[<NoEquality; NoComparison>]
type Host =
    {
        State: RegistryState
        /// Every registry event so far, in order: `Registry.replay Events = State`.
        Events: RegistryEvent list
        Loaded: Map<string, Loaded>
        Catalog: Catalog
        Report: ReportEntry list
    }

    override this.ToString() =
        $"Host({this.Loaded.Count} active extensions, {this.Events.Length} events)"

/// The work of one bootstrap or change: the registry's state, the events it appended, the running
/// extensions. Only `ExtensionHost` touches it, one step at a time.
type private Work(host: Host) =
    member val State = host.State with get, set
    member val Catalog = host.Catalog with get, set
    member val Events = ResizeArray<RegistryEvent>(host.Events)
    member val Loaded = Dictionary<string, Loaded>(host.Loaded)
    member val Report = ResizeArray<ReportEntry>(host.Report)

    member this.Send(command: RegistryCommand) : Result<RegistryEvent list, Rejection> =
        match Machine.step Registry.machine this.State command with
        | Ok step ->
            this.State <- step.State
            this.Events.AddRange step.Events
            Ok step.Events
        | Error rejection -> Error rejection

    member this.Snapshot() : Host =
        {
            State = this.State
            Events = List.ofSeq this.Events
            Loaded = this.Loaded |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq
            Catalog = this.Catalog
            Report = List.ofSeq this.Report
        }

    /// The contributions of a type in effect: active extensions in admission order, each in the
    /// order it provides them.
    member this.Contributions(contributionType: ContributionType<'T>) : Contributed<'T> list =
        [
            for id in Registry.active this.State do
                match this.Loaded.TryGetValue id with
                | true, loaded ->
                    for c in loaded.Activation.Contributions do
                        match Contribution.tryValue contributionType c with
                        | Some value ->
                            {
                                Extension = id
                                Type = contributionType.Name
                                Id = c.Id
                                Value = value
                                Env = loaded.Env
                            }
                        | None -> ()
                | _ -> ()
        ]

[<RequireQualifiedAccess>]
module ExtensionHost =

    /// Settings that fail closed: no component installer yet (T110), so extensions that require
    /// components fail.
    let settings
        (trust: TrustContext)
        (envFor: Manifest -> IExtensionEnv)
        (processes: ProcessRuntime.ProcessSettings)
        : HostSettings<'R> =
        {
            Trust = trust
            Catalog = Catalog.create []
            EnvFor = envFor
            InstallComponents =
                fun manifest ->
                    if manifest.Requires.IsEmpty then
                        EffResult.ok []
                    else
                        EffResult.error "this host has no component installer yet"
            Process = processes
            Disabled = []
        }

    /// A host with nothing loaded.
    let empty (catalog: Catalog) : Host =
        {
            State = Registry.empty
            Events = []
            Loaded = Map.empty
            Catalog = catalog
            Report = []
        }

    let private phaseOf (manifest: Manifest) : Phase =
        if manifest.Runtime = Runtimes.Native || manifest.Runtime = Runtimes.Declarative then
            Phase.InBinary
        elif manifest.Runtime = Runtimes.Process then
            Phase.Process
        else
            Phase.Hosted

    let private report (work: Work) (id: string) (phase: Phase) (outcome: Outcome) =
        work.Report.Add
            {
                Id = id
                Phase = phase
                Outcome = outcome
            }

    let private stopLoaded<'R when 'R :> IScope> (loaded: Loaded) : Eff<'R, unit> =
        fun env ->
            task {
                let! _ = Eff.run (loaded.Env.WithScope env.Scope) (Eff.catch loaded.Stop)
                return ()
            }

    /// Stops the extensions that these events disabled.
    let private stopDisabled<'R when 'R :> IScope> (work: Work) (events: RegistryEvent list) : Eff<'R, unit> =
        eff {
            for event in events do
                match event with
                | RegistryEvent.Disabled(id, _) ->
                    match work.Loaded.TryGetValue id with
                    | true, loaded ->
                        work.Loaded.Remove id |> ignore
                        do! stopLoaded loaded
                    | _ -> ()
                | _ -> ()
        }

    let private admitRequest (settings: HostSettings<'R>) (work: Work) (candidate: Candidate) (manifest: Manifest) =
        RegistryCommand.Admit
            {
                Id = manifest.Id
                Version = manifest.Version
                Hash = Manifest.hash manifest
                Runtime = manifest.Runtime
                Placement = string manifest.Placement
                Sdk = manifest.Sdk
                Protocol = manifest.Protocol
                Source = candidate.Source
                Contributions =
                    manifest.Contributions
                    |> List.map (fun decl ->
                        let info = Catalog.tryFind decl.Type work.Catalog

                        {
                            Key = ({ Type = decl.Type; Id = decl.Id }: ContributionKey)
                            Known = info.IsSome
                            Privileged = info |> Option.exists (fun i -> i.Privileged)
                        })
                Methods = manifest.Rpc.Methods |> List.map (fun m -> m.Name)
                Replaces = manifest.Replaces
                Pin = candidate.Pin
                Trust = settings.Trust
            }

    /// Checks the manifest and asks the registry to admit it. Nothing of the extension runs.
    let private admit<'R when 'R :> IScope>
        (settings: HostSettings<'R>)
        (work: Work)
        (phase: Phase)
        (candidate: Candidate)
        : Eff<'R, bool> =
        eff {
            let manifest = Package.manifest candidate.Package

            match Manifest.check manifest with
            | _ :: _ as issues ->
                report
                    work
                    manifest.Id
                    phase
                    (Outcome.Rejected(Rejection.create "manifest.invalid" (Issue.render issues)))

                return false
            | [] ->
                match work.Send(admitRequest settings work candidate manifest) with
                | Error rejection ->
                    report work manifest.Id phase (Outcome.Rejected rejection)
                    return false
                | Ok events ->
                    do! stopDisabled work events
                    return true
        }

    let private failed (work: Work) (id: string) (phase: Phase) (reason: string) =
        work.Send(RegistryCommand.Fail(id, reason)) |> ignore
        report work id phase (Outcome.Failed reason)

    let private catching (effect: Eff<IExtensionEnv, Result<'T, string>>) : Eff<IExtensionEnv, Result<'T, string>> =
        eff {
            match! Eff.catch effect with
            | Ok outcome -> return outcome
            | Error e -> return Error $"it failed with {e.GetType().Name}"
        }

    /// Runs the extension's runtime: what it provides, and how to stop it.
    let private start<'R when 'R :> IShell and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: HostSettings<'R>)
        (work: Work)
        (candidate: Candidate)
        (installed: InstalledComponent list)
        (env: IExtensionEnv)
        : Eff<'R, Result<Activation * Eff<IExtensionEnv, unit>, string>> =
        fun (hostEnv: 'R) ->
            let inExtension (effect: Eff<IExtensionEnv, 'T>) =
                Eff.run (env.WithScope hostEnv.Scope) effect

            task {
                match candidate.Package with
                | Package.Native extension ->
                    let! outcome = inExtension (catching (extension.Activate |> Eff.map Ok))
                    return outcome |> Result.map (fun activation -> activation, activation.Deactivate)
                | Package.Manifest(manifest, _) when manifest.Runtime = Runtimes.Declarative ->
                    let loaded =
                        manifest.Contributions
                        |> Validation.traverseIndexed (fun i decl ->
                            Catalog.load work.Catalog decl None
                            |> Result.mapError (
                                List.map (
                                    Issue.under (PathSegment.Index i)
                                    >> Issue.under (PathSegment.Key "contributions")
                                )
                            ))

                    match loaded with
                    | Error issues -> return Error("its contributions do not load:\n" + Issue.renderAtMost 10 issues)
                    | Ok contributions ->
                        let activation = Activation.create contributions []
                        return Ok(activation, activation.Deactivate)
                | Package.Manifest(manifest, root) when manifest.Runtime = Runtimes.Process ->
                    match root with
                    | None -> return Error "a process extension needs its package directory"
                    | Some root ->
                        match!
                            ProcessRuntime.start
                                settings.Process
                                settings.Trust.ProjectTrusted
                                manifest
                                root
                                installed
                                hostEnv
                        with
                        | Error reason -> return Error reason
                        | Ok running ->
                            let stopProcess = ProcessRuntime.stop settings.Process running
                            let load = Catalog.load work.Catalog

                            match! inExtension (catching (ProcessRuntime.activate running manifest load)) with
                            | Error reason ->
                                let! _ = inExtension stopProcess
                                return Error reason
                            | Ok activation ->
                                let stop =
                                    eff {
                                        let! _ = Eff.catch activation.Deactivate
                                        do! stopProcess
                                    }

                                return Ok(activation, stop)
                | Package.Manifest(manifest, root) ->
                    let runtime =
                        work.Contributions ExtensionRuntimes.contributionType
                        |> List.tryFind (fun c -> c.Id = manifest.Runtime)

                    match runtime with
                    | None -> return Error $"no active extension contributes the runtime {manifest.Runtime}"
                    | Some runtime ->
                        let request =
                            {
                                Manifest = manifest
                                Root = root
                                Load = Catalog.load work.Catalog
                            }

                        // The runtime's code runs in its own extension's environment.
                        let! outcome =
                            Eff.run (runtime.Env.WithScope hostEnv.Scope) (catching (runtime.Value.Activate request))

                        return outcome |> Result.map (fun activation -> activation, activation.Deactivate)
            }

    /// Activates an admitted extension, checks that it provides what it declared, and puts it in effect.
    let private activate<'R when 'R :> IShell and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: HostSettings<'R>)
        (work: Work)
        (phase: Phase)
        (candidate: Candidate)
        (installed: InstalledComponent list)
        : Eff<'R, unit> =
        eff {
            let manifest = Package.manifest candidate.Package
            let env = settings.EnvFor manifest
            let! started = start settings work candidate installed env

            match started with
            | Error reason -> failed work manifest.Id phase reason
            | Ok(activation, stop) ->
                let stopIt =
                    stopLoaded (
                        {
                            Manifest = manifest
                            Source = candidate.Source
                            Env = env
                            Activation = activation
                            Stop = stop
                        }
                        : Loaded
                    )

                let defined =
                    activation.Contributions
                    |> List.choose (Contribution.tryValue ContributionTypes.contributionType)

                let catalog =
                    defined
                    |> List.fold (fun catalog info -> Result.bind (Catalog.add info) catalog) (Ok work.Catalog)

                match catalog with
                | Error reason ->
                    do! stopIt
                    failed work manifest.Id phase reason
                | Ok catalog ->
                    let provided =
                        activation.Contributions
                        |> List.map (fun c -> ({ Type = c.Type; Id = c.Id }: ContributionKey))

                    let methods = activation.Methods |> List.map (fun m -> m.Name)

                    match work.Send(RegistryCommand.Activate(manifest.Id, provided, methods)) with
                    | Error rejection ->
                        do! stopIt
                        failed work manifest.Id phase rejection.Message
                    | Ok _ ->
                        work.Catalog <- catalog

                        work.Loaded[manifest.Id] <-
                            {
                                Manifest = manifest
                                Source = candidate.Source
                                Env = env
                                Activation = activation
                                Stop = stop
                            }

                        report work manifest.Id phase Outcome.Activated
        }

    let private isAdmitted (work: Work) (id: string) =
        match work.State.Entries.TryFind id with
        | Some entry -> entry.Status = Status.Admitted
        | None -> false

    /// The candidates for this host: those for another placement, and those disabled in host config
    /// (policy extensions cannot be), are left out, and reported as skipped if `reporting`.
    let private including (settings: HostSettings<'R>) (work: Work) (reporting: bool) (candidates: Candidate list) =
        candidates
        |> List.filter (fun candidate ->
            let manifest = Package.manifest candidate.Package

            let skipped (reason: string) =
                if reporting then
                    report work manifest.Id (phaseOf manifest) (Outcome.Skipped reason)

                false

            if string manifest.Placement <> settings.Trust.Placement then
                skipped $"it runs in the {manifest.Placement}"
            elif List.contains manifest.Id settings.Disabled && candidate.Source <> Source.Policy then
                skipped "disabled in host config"
            else
                true)

    let private inPhase (phase: Phase) (candidates: Candidate list) =
        candidates
        |> List.filter (fun c -> phaseOf (Package.manifest c.Package) = phase)

    /// The bootstrap's first phase (spec §16.1): the native extensions among the candidates, then the
    /// declarative ones, each admitted, then activated, in the order given. Candidates that are not
    /// for this host are reported as skipped; the rest wait for `bootstrapRest` with the same
    /// candidates, which a host runs once what the components phase needs (grants, the installer) runs
    /// on what this phase loaded.
    let bootstrapInBinary<'R when 'R :> IShell and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: HostSettings<'R>)
        (candidates: Candidate list)
        : Eff<'R, Host> =
        eff {
            let work = Work(empty settings.Catalog)
            let included = including settings work true candidates

            let natives, declaratives =
                inPhase Phase.InBinary included
                |> List.partition (fun c ->
                    match c.Package with
                    | Package.Native _ -> true
                    | Package.Manifest _ -> false)

            for candidate in natives @ declaratives do
                let! admitted = admit settings work Phase.InBinary candidate

                if admitted then
                    do! activate settings work Phase.InBinary candidate []

            return work.Snapshot()
        }

    /// The bootstrap's later phases (spec §16.1), on what `bootstrapInBinary` loaded from the same
    /// candidates: the process and hosted extensions are admitted, then the components each one
    /// requires are installed (`InstallComponents`; if that fails, the extension fails), then
    /// extension processes start (their launches name the installed components' commands), then
    /// extensions whose runtime an extension contributes activate.
    ///
    /// Extension processes stay connected for as long as this effect's scope.
    let bootstrapRest<'R when 'R :> IShell and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: HostSettings<'R>)
        (host: Host)
        (candidates: Candidate list)
        : Eff<'R, Host> =
        eff {
            let work = Work host
            let included = including settings work false candidates

            // 2. Admit the rest, then install the components they require.
            let later = inPhase Phase.Process included @ inPhase Phase.Hosted included

            for candidate in later do
                let! _ = admit settings work (phaseOf (Package.manifest candidate.Package)) candidate
                ()

            let installed = Dictionary<string, InstalledComponent list>(StringComparer.Ordinal)

            for candidate in later do
                let manifest = Package.manifest candidate.Package

                if isAdmitted work manifest.Id && not manifest.Requires.IsEmpty then
                    match! settings.InstallComponents manifest with
                    | Ok components -> installed[manifest.Id] <- components
                    | Error reason -> failed work manifest.Id Phase.Components reason

            // 3. Extension processes. 4. Extensions on contributed runtimes.
            for phase in [ Phase.Process; Phase.Hosted ] do
                for candidate in inPhase phase included do
                    let id = (Package.manifest candidate.Package).Id

                    if isAdmitted work id then
                        let components =
                            match installed.TryGetValue id with
                            | true, components -> components
                            | _ -> []

                        do! activate settings work phase candidate components

            return work.Snapshot()
        }

    /// Loads the candidates in the bootstrap order (spec §16.1): native, then declarative; then the
    /// components the rest require; then extension processes; then extensions whose runtime an
    /// extension contributes. Within a phase, candidates load in the order given. Every candidate is
    /// admitted (trust, pins, versions, conflicts) before any of its code runs. The result records
    /// the registry's events, which replay to its state, and what became of each candidate.
    /// `bootstrapInBinary`, then `bootstrapRest`.
    ///
    /// Extension processes stay connected for as long as this effect's scope.
    let bootstrap<'R when 'R :> IShell and 'R :> IClock and 'R :> IScopeWith<'R>>
        (settings: HostSettings<'R>)
        (candidates: Candidate list)
        : Eff<'R, Host> =
        eff {
            let! host = bootstrapInBinary settings candidates
            return! bootstrapRest settings host candidates
        }

    /// The contributions of a type in effect: active extensions in admission order, each in the order
    /// it provides them. Run hooks over them with `Hooks`.
    let contributions (contributionType: ContributionType<'T>) (host: Host) : Contributed<'T> list =
        Work(host).Contributions contributionType

    /// Calls a method of an active extension's RPC contract, in the extension's environment with the
    /// caller's scope. A method that throws is `Crashed`, as it is out of process.
    let call
        (host: Host)
        (extension: string)
        (name: string)
        (parameters: Json)
        : Eff<#IScope, Result<Json, MethodFailure>> =
        match host.Loaded.TryFind extension with
        | Some loaded when List.contains extension (Registry.active host.State) ->
            match loaded.Activation.Methods |> List.tryFind (fun m -> m.Name = name) with
            | Some m ->
                fun env ->
                    task {
                        match!
                            Eff.run
                                env
                                (Eff.catch (Eff.delay (fun () -> ExtensionEnv.within loaded.Env (m.Invoke parameters))))
                        with
                        | Ok outcome -> return outcome
                        | Error e when
                            e :? OperationCanceledException
                            && env.Scope.Cancellation.IsCancellationRequested
                            ->
                            return raise e
                        | Error e -> return Error(MethodFailure.Crashed(e.GetType().Name))
                    }
            | None -> Eff.ret (Error(MethodFailure.UnknownMethod name))
        | _ -> Eff.ret (Error(MethodFailure.Unreachable $"{extension} is not active"))

    /// Calls a method with its typed definition.
    let callTyped
        (host: Host)
        (extension: string)
        (m: ExtMethod<'P, 'Res, 'E>)
        (parameters: 'P)
        : Eff<#IScope, Result<'Res, ExtMethod.CallError<'E>>> =
        call host extension m.Name (m.Params.Encode parameters)
        |> Eff.map (ExtMethod.decodeOutcome m)

    /// Disables an extension and stops it. Policy extensions cannot be disabled.
    let disable (host: Host) (extension: string) (reason: string) : Eff<#IScope, Result<Host, Rejection>> =
        eff {
            let work = Work host

            match work.Send(RegistryCommand.Disable(extension, reason)) with
            | Error rejection -> return Error rejection
            | Ok events ->
                do! stopDisabled work events
                return Ok(work.Snapshot())
        }

    /// Stops every active extension, the last admitted first. Failures to stop are ignored.
    let stop (host: Host) : Eff<#IScope, unit> =
        eff {
            for id in List.rev (Registry.active host.State) do
                match host.Loaded.TryFind id with
                | Some loaded -> do! stopLoaded loaded
                | None -> ()
        }
