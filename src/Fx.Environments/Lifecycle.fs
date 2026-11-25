namespace Fx.Environments

open Fx.Contracts
open Fx.Envd
open Fx.Machines
open Fx.Sdk

/// Where an environment is in its lifecycle (spec §9.5): `Requested → Provisioning → Bootstrapped
/// → Connected → Ready ⇄ Suspended → Lost | Destroyed`, and `Failed` when a step failed.
[<RequireQualifiedAccess>]
type Phase =
    /// Accepted; the provider is creating it.
    | Requested
    /// The provider holds a lease; envd is being started, or is to be started again (detached).
    | Provisioning
    /// envd runs; the host is connecting.
    | Bootstrapped
    /// The host speaks to envd; components and setup are being installed.
    | Connected
    | Ready
    | Suspended
    /// The provider no longer has it.
    | Lost
    | Destroyed
    /// A step failed (the record says which and why); destroying it releases what it holds.
    | Failed

    override this.ToString() =
        match this with
        | Requested -> "requested"
        | Provisioning -> "provisioning"
        | Bootstrapped -> "bootstrapped"
        | Connected -> "connected"
        | Ready -> "ready"
        | Suspended -> "suspended"
        | Lost -> "lost"
        | Destroyed -> "destroyed"
        | Failed -> "failed"

/// What the host is doing to an environment, if anything (work in flight, recovered at a restart).
[<RequireQualifiedAccess>]
type Step =
    /// `Create` at the provider.
    | Provision
    /// `Platform`, then envd started (in process, or through `Exec`).
    | Bootstrap
    /// The handshake with envd.
    | Connect
    /// Components from the lockfile, then setup.
    | Install
    | Suspend
    | Resume
    | Destroy

    override this.ToString() =
        match this with
        | Provision -> "provision"
        | Bootstrap -> "bootstrap"
        | Connect -> "connect"
        | Install -> "install"
        | Suspend -> "suspend"
        | Resume -> "resume"
        | Destroy -> "destroy"

/// What confines an environment's processes, as the host recorded it when the environment became
/// ready (each attach decides again).
type EnvSandboxInfo =
    {
        /// The sandbox driver's id, e.g. `bubblewrap`, `seatbelt`, `none`.
        Driver: string
        /// Whether the driver confines processes; `false`: the environment runs unsandboxed and
        /// carries a persistent warning (spec §9.1).
        Confines: bool
        /// Why it is so, e.g. why the host's `auto` choice fell back to `none`.
        Notes: string list
    }

    override this.ToString() =
        if this.Confines then
            $"sandbox {this.Driver}"
        else
            $"unsandboxed ({this.Driver})"

/// What the install step gives a ready environment.
type EnvInstallation =
    {
        /// The components installed (`id@version`).
        Components: string list
        Sandbox: EnvSandboxInfo option
    }

    override this.ToString() =
        $"{this.Components.Length} components, {this.Sandbox}"

/// One environment as the host records it: a fold of its events.
type EnvRecord =
    {
        Id: string
        Spec: EnvSpec
        /// When it was requested (Unix microseconds).
        Requested: int64
        Phase: Phase
        /// The step in flight.
        Step: Step option
        /// Counts attempts to provision and attach; results of another attempt are stale.
        Attempt: int
        Lease: Lease option
        Transport: TransportKind option
        Machine: EnvMachine option
        /// What envd announced when the host last connected.
        Platform: Platform option
        /// The components installed for it (`id@version`).
        Components: string list
        /// What confined its processes when it last became ready; `None` before that (and for
        /// environments recorded before sandboxes existed).
        Sandbox: EnvSandboxInfo option
        /// Why it failed, was lost or detached.
        Reason: string option
    }

    /// Whether nothing more happens to it but destroying.
    member this.Terminal =
        match this.Phase with
        | Phase.Lost
        | Phase.Destroyed
        | Phase.Failed -> true
        | _ -> false

    override this.ToString() =
        let step =
            match this.Step with
            | Some step -> $" ({step})"
            | None -> ""

        $"{this.Id} {this.Spec.Name}: {this.Phase}{step}"

/// The environments of a host, by id, and the order they were requested in.
type EnvsState =
    {
        Environments: Map<string, EnvRecord>
        Order: string list
    }

    override this.ToString() =
        $"EnvsState({this.Environments.Count} environments)"

/// An environment was requested.
type EnvRequested =
    {
        Id: string
        Spec: EnvSpec
        /// Unix microseconds.
        At: int64
    }

    override this.ToString() = $"{this.Id} requested ({this.Spec})"

type EnvProvisioned =
    {
        Id: string
        Attempt: int
        Lease: Lease
    }

    override this.ToString() = $"{this.Id} provisioned ({this.Lease})"

/// An environment and an attempt.
type EnvAttempt =
    {
        Id: string
        Attempt: int
    }

    override this.ToString() = $"{this.Id} attempt {this.Attempt}"

type EnvBootstrapped =
    {
        Id: string
        Attempt: int
        Transport: TransportKind
        Machine: EnvMachine
    }

    override this.ToString() =
        $"{this.Id} bootstrapped ({this.Transport}, {this.Machine})"

type EnvConnected =
    {
        Id: string
        Attempt: int
        Platform: Platform
    }

    override this.ToString() =
        $"{this.Id} connected ({this.Platform})"

type EnvReadied =
    {
        Id: string
        Attempt: int
        Components: string list
        /// What confines its processes (absent in events written before sandboxes existed).
        Sandbox: EnvSandboxInfo option
    }

    override this.ToString() = $"{this.Id} ready"

/// An environment and why something happened to it.
type EnvNote =
    {
        Id: string
        Reason: string
    }

    override this.ToString() = $"{this.Id}: {this.Reason}"

/// An environment and a step.
type EnvStep =
    {
        Id: string
        Step: Step
    }

    override this.ToString() = $"{this.Id} {this.Step}"

type EnvStepFailed =
    {
        Id: string
        Step: Step
        Reason: string
    }

    override this.ToString() =
        $"{this.Id} {this.Step} failed: {this.Reason}"

/// An environment.
type EnvRef =
    {
        Id: string
    }

    override this.ToString() = this.Id

/// What happened to the host's environments. Facts kept forever: add cases, never change one.
[<RequireQualifiedAccess>]
type EnvEvent =
    | Requested of EnvRequested
    | Provisioned of EnvProvisioned
    /// A new attempt to start envd for a provisioned environment (after it detached).
    | Attaching of EnvAttempt
    | Bootstrapped of EnvBootstrapped
    | Connected of EnvConnected
    | Readied of EnvReadied
    /// The host lost its connection to envd (it ended, or the host restarted); the lease is kept.
    | Detached of EnvNote
    /// A suspend, resume or destroy started.
    | Started of EnvStep
    | Suspended of EnvRef
    | Resumed of EnvRef
    | Destroyed of EnvRef
    | Lost of EnvNote
    | Failed of EnvStepFailed

    member this.Id =
        match this with
        | Requested e -> e.Id
        | Provisioned e -> e.Id
        | Attaching e -> e.Id
        | Bootstrapped e -> e.Id
        | Connected e -> e.Id
        | Readied e -> e.Id
        | Detached e -> e.Id
        | Started e -> e.Id
        | Suspended e -> e.Id
        | Resumed e -> e.Id
        | Destroyed e -> e.Id
        | Lost e -> e.Id
        | Failed e -> e.Id

    override this.ToString() =
        match this with
        | Requested e -> string e
        | Provisioned e -> string e
        | Attaching e -> $"{e.Id} attaching (attempt {e.Attempt})"
        | Bootstrapped e -> string e
        | Connected e -> string e
        | Readied e -> string e
        | Detached e -> $"{e.Id} detached: {e.Reason}"
        | Started e -> $"{e.Id} {e.Step} started"
        | Suspended e -> $"{e.Id} suspended"
        | Resumed e -> $"{e.Id} resumed"
        | Destroyed e -> $"{e.Id} destroyed"
        | Lost e -> $"{e.Id} lost: {e.Reason}"
        | Failed e -> string e

/// What the host and the interpreters of intents tell the machine. Results carry the attempt they
/// belong to; a result for another step or attempt is stale and rejected.
[<RequireQualifiedAccess>]
type EnvCommand =
    | Request of EnvRequested
    /// Start envd again for a provisioned environment the host has no connection to.
    | Attach of id: string
    | Suspend of id: string
    | Resume of id: string
    | Destroy of id: string
    | Provisioned of id: string * attempt: int * result: Result<Lease, string>
    | Bootstrapped of id: string * attempt: int * result: Result<TransportKind * EnvMachine, string>
    | Connected of id: string * attempt: int * result: Result<Platform, string>
    | Installed of id: string * attempt: int * result: Result<EnvInstallation, string>
    | Suspended of id: string * result: Result<unit, string>
    | Resumed of id: string * result: Result<unit, string>
    | Destroyed of id: string * result: Result<unit, string>
    /// The connection of an attempt ended without the host closing it.
    | Disconnected of id: string * attempt: int * reason: string
    /// What the providers still hold, after a restart: ids present (with the attempt recovery saw:
    /// an environment attached again since is not detached), and ids missing with why.
    | Reconciled of present: (string * int) list * missing: (string * string) list

    override this.ToString() =
        let result (r: Result<'T, string>) =
            match r with
            | Ok _ -> "ok"
            | Error e -> "error: " + e

        match this with
        | Request r -> $"request {r.Id}"
        | Attach id -> $"attach {id}"
        | Suspend id -> $"suspend {id}"
        | Resume id -> $"resume {id}"
        | Destroy id -> $"destroy {id}"
        | Provisioned(id, attempt, r) -> $"provisioned {id}#{attempt}: {result r}"
        | Bootstrapped(id, attempt, r) -> $"bootstrapped {id}#{attempt}: {result r}"
        | Connected(id, attempt, r) -> $"connected {id}#{attempt}: {result r}"
        | Installed(id, attempt, r) -> $"installed {id}#{attempt}: {result r}"
        | Suspended(id, r) -> $"suspended {id}: {result r}"
        | Resumed(id, r) -> $"resumed {id}: {result r}"
        | Destroyed(id, r) -> $"destroyed {id}: {result r}"
        | Disconnected(id, attempt, reason) -> $"disconnected {id}#{attempt}: {reason}"
        | Reconciled(present, missing) -> $"reconciled ({present.Length} present, {missing.Length} missing)"

/// What the host does for the machine, as data (`EnvsHost` interprets them).
[<RequireQualifiedAccess>]
type EnvIntent =
    /// `Create` at the provider.
    | Provision of id: string * attempt: int * spec: EnvSpec
    /// Earlier attempts released, `Platform`, then envd started for the attempt.
    | Bootstrap of id: string * attempt: int * spec: EnvSpec * lease: Lease
    /// The handshake with the attempt's envd.
    | Connect of id: string * attempt: int
    /// Components from the lockfile, then setup.
    | Install of id: string * attempt: int * spec: EnvSpec
    | Suspend of id: string * spec: EnvSpec * lease: Lease
    | Resume of id: string * spec: EnvSpec * lease: Lease
    /// Releases every connection, then `Destroy` at the provider (finding the lease by id if none
    /// was recorded).
    | Destroy of id: string * spec: EnvSpec * lease: Lease option
    /// Closes the host's connections of attempts up to this one (and any started later for them).
    | Release of id: string * upTo: int
    /// Asks the providers which of these leases they still hold (with each environment's attempt).
    | Reconcile of leases: (string * int * EnvSpec * Lease) list

    override this.ToString() =
        match this with
        | Provision(id, attempt, _) -> $"provision {id}#{attempt}"
        | Bootstrap(id, attempt, _, _) -> $"bootstrap {id}#{attempt}"
        | Connect(id, attempt) -> $"connect {id}#{attempt}"
        | Install(id, attempt, _) -> $"install {id}#{attempt}"
        | Suspend(id, _, _) -> $"suspend {id}"
        | Resume(id, _, _) -> $"resume {id}"
        | Destroy(id, _, _) -> $"destroy {id}"
        | Release(id, upTo) -> $"release {id} up to #{upTo}"
        | Reconcile leases -> $"reconcile {leases.Length} leases"

/// The environment lifecycle machine (spec §9.5): one actor per host, one stream for all its
/// environments, so listing and reconciling them is a fold. Pure; everything from the world comes
/// in commands. Module spec: docs/modules/environments.md.
[<RequireQualifiedAccess>]
module Lifecycle =
    /// The stream kind of a host's environments.
    [<Literal>]
    let StreamKind = "environments"

    /// Why the host detaches every environment it finds at a restart.
    [<Literal>]
    let Restarted = "the host restarted"

    let initial: EnvsState = { Environments = Map.empty; Order = [] }

    let private update (id: string) (change: EnvRecord -> EnvRecord) (state: EnvsState) : EnvsState =
        match state.Environments.TryFind id with
        | Some record ->
            { state with
                Environments = state.Environments.Add(id, change record)
            }
        | None -> state

    let evolve (state: EnvsState) (event: EnvEvent) : EnvsState =
        match event with
        | EnvEvent.Requested e ->
            let record =
                {
                    Id = e.Id
                    Spec = e.Spec
                    Requested = e.At
                    Phase = Phase.Requested
                    Step = Some Step.Provision
                    Attempt = 1
                    Lease = None
                    Transport = None
                    Machine = None
                    Platform = None
                    Components = []
                    Sandbox = None
                    Reason = None
                }

            {
                Environments = state.Environments.Add(e.Id, record)
                Order = state.Order @ [ e.Id ]
            }
        | EnvEvent.Provisioned e ->
            state
            |> update e.Id (fun r ->
                { r with
                    Phase = Phase.Provisioning
                    Step = Some Step.Bootstrap
                    Attempt = e.Attempt
                    Lease = Some e.Lease
                    Reason = None
                })
        | EnvEvent.Attaching e ->
            state
            |> update e.Id (fun r ->
                { r with
                    Phase = Phase.Provisioning
                    Step = Some Step.Bootstrap
                    Attempt = e.Attempt
                    Reason = None
                })
        | EnvEvent.Bootstrapped e ->
            state
            |> update e.Id (fun r ->
                { r with
                    Phase = Phase.Bootstrapped
                    Step = Some Step.Connect
                    Transport = Some e.Transport
                    Machine = Some e.Machine
                })
        | EnvEvent.Connected e ->
            state
            |> update e.Id (fun r ->
                { r with
                    Phase = Phase.Connected
                    Step = Some Step.Install
                    Platform = Some e.Platform
                })
        | EnvEvent.Readied e ->
            state
            |> update e.Id (fun r ->
                { r with
                    Phase = Phase.Ready
                    Step = None
                    Components = e.Components
                    Sandbox = e.Sandbox
                })
        | EnvEvent.Detached e ->
            state
            |> update e.Id (fun r ->
                { r with
                    Phase = Phase.Provisioning
                    Step = None
                    Reason = Some e.Reason
                })
        | EnvEvent.Started e -> state |> update e.Id (fun r -> { r with Step = Some e.Step })
        | EnvEvent.Suspended e ->
            state
            |> update e.Id (fun r ->
                { r with
                    Phase = Phase.Suspended
                    Step = None
                    Reason = None
                })
        | EnvEvent.Resumed e ->
            state
            |> update e.Id (fun r ->
                { r with
                    Phase = Phase.Provisioning
                    Step = None
                    Reason = None
                })
        | EnvEvent.Destroyed e ->
            state
            |> update e.Id (fun r ->
                { r with
                    Phase = Phase.Destroyed
                    Step = None
                    Reason = None
                })
        | EnvEvent.Lost e ->
            state
            |> update e.Id (fun r ->
                { r with
                    Phase = Phase.Lost
                    Step = None
                    Reason = Some e.Reason
                })
        | EnvEvent.Failed e ->
            state
            |> update e.Id (fun r ->
                match e.Step with
                | Step.Suspend
                | Step.Resume ->
                    { r with
                        Step = None
                        Reason = Some e.Reason
                    }
                | _ ->
                    { r with
                        Phase = Phase.Failed
                        Step = None
                        Reason = Some e.Reason
                    })

    let private reject (code: string) (message: string) : Result<EnvEvent list, Rejection> =
        Error(Rejection.create code message)

    let private unknown (id: string) =
        reject "environment.unknown" $"there is no environment {id}"

    let private stale (record: EnvRecord) (what: string) =
        reject "environment.stale" $"{what} for {record.Id} arrived after it moved on ({record})"

    /// Handles a result for a step of an attempt: only while that step of that attempt runs.
    let private result
        (state: EnvsState)
        (id: string)
        (attempt: int)
        (step: Step)
        (onOk: EnvRecord -> EnvEvent list)
        (outcome: Result<'T, string>)
        : Result<EnvEvent list, Rejection> =
        match state.Environments.TryFind id with
        | None -> unknown id
        | Some record when record.Step <> Some step || record.Attempt <> attempt -> stale record (string step)
        | Some record ->
            match outcome with
            | Ok _ -> Ok(onOk record)
            | Error reason ->
                Ok
                    [
                        EnvEvent.Failed
                            {
                                Id = id
                                Step = step
                                Reason = reason
                            }
                    ]

    let private withRecord (state: EnvsState) (id: string) (decide: EnvRecord -> Result<EnvEvent list, Rejection>) =
        match state.Environments.TryFind id with
        | None -> unknown id
        | Some record -> decide record

    let private busy (record: EnvRecord) =
        reject "environment.busy" $"{record.Id} is busy ({record})"

    let private inPhase (record: EnvRecord) (wanted: string) =
        reject "environment.state" $"{record.Id} is {record.Phase}; {wanted}"

    let handle (state: EnvsState) (command: EnvCommand) : Result<EnvEvent list, Rejection> =
        match command with
        | EnvCommand.Request request ->
            if state.Environments.ContainsKey request.Id then
                reject "environment.exists" $"there is already an environment {request.Id}"
            else
                Ok [ EnvEvent.Requested request ]
        | EnvCommand.Attach id ->
            withRecord state id (fun record ->
                match record.Phase, record.Step, record.Lease with
                | _, Some(Step.Bootstrap | Step.Connect | Step.Install), _ -> Ok []
                | _, Some _, _ -> busy record
                | (Phase.Provisioning | Phase.Bootstrapped | Phase.Connected | Phase.Ready), None, Some _ ->
                    Ok
                        [
                            EnvEvent.Attaching
                                {
                                    Id = id
                                    Attempt = record.Attempt + 1
                                }
                        ]
                | _ -> inPhase record "only a provisioned environment can be attached")
        | EnvCommand.Suspend id ->
            withRecord state id (fun record ->
                match record.Phase, record.Step with
                | Phase.Ready, None -> Ok [ EnvEvent.Started { Id = id; Step = Step.Suspend } ]
                | _, Some _ -> busy record
                | _ -> inPhase record "only a ready environment can be suspended")
        | EnvCommand.Resume id ->
            withRecord state id (fun record ->
                match record.Phase, record.Step with
                | Phase.Suspended, None -> Ok [ EnvEvent.Started { Id = id; Step = Step.Resume } ]
                | _, Some _ -> busy record
                | _ -> inPhase record "only a suspended environment can be resumed")
        | EnvCommand.Destroy id ->
            withRecord state id (fun record ->
                match record.Phase, record.Step with
                | Phase.Destroyed, _
                | _, Some Step.Destroy -> Ok []
                | _ -> Ok [ EnvEvent.Started { Id = id; Step = Step.Destroy } ])
        | EnvCommand.Provisioned(id, attempt, outcome) ->
            outcome
            |> result state id attempt Step.Provision (fun _ ->
                match outcome with
                | Ok lease ->
                    [
                        EnvEvent.Provisioned
                            {
                                Id = id
                                Attempt = attempt
                                Lease = lease
                            }
                    ]
                | Error _ -> [])
        | EnvCommand.Bootstrapped(id, attempt, outcome) ->
            outcome
            |> result state id attempt Step.Bootstrap (fun _ ->
                match outcome with
                | Ok(transport, machine) ->
                    [
                        EnvEvent.Bootstrapped
                            {
                                Id = id
                                Attempt = attempt
                                Transport = transport
                                Machine = machine
                            }
                    ]
                | Error _ -> [])
        | EnvCommand.Connected(id, attempt, outcome) ->
            outcome
            |> result state id attempt Step.Connect (fun _ ->
                match outcome with
                | Ok platform ->
                    [
                        EnvEvent.Connected
                            {
                                Id = id
                                Attempt = attempt
                                Platform = platform
                            }
                    ]
                | Error _ -> [])
        | EnvCommand.Installed(id, attempt, outcome) ->
            outcome
            |> result state id attempt Step.Install (fun _ ->
                match outcome with
                | Ok installation ->
                    [
                        EnvEvent.Readied
                            {
                                Id = id
                                Attempt = attempt
                                Components = installation.Components
                                Sandbox = installation.Sandbox
                            }
                    ]
                | Error _ -> [])
        | EnvCommand.Suspended(id, outcome) ->
            withRecord state id (fun record ->
                outcome
                |> result state id record.Attempt Step.Suspend (fun _ -> [ EnvEvent.Suspended { Id = id } ]))
        | EnvCommand.Resumed(id, outcome) ->
            withRecord state id (fun record ->
                outcome
                |> result state id record.Attempt Step.Resume (fun _ -> [ EnvEvent.Resumed { Id = id } ]))
        | EnvCommand.Destroyed(id, outcome) ->
            withRecord state id (fun record ->
                outcome
                |> result state id record.Attempt Step.Destroy (fun _ -> [ EnvEvent.Destroyed { Id = id } ]))
        | EnvCommand.Disconnected(id, attempt, reason) ->
            withRecord state id (fun record ->
                match record.Phase, record.Step with
                | _, Some(Step.Destroy | Step.Suspend | Step.Resume) -> stale record "a disconnection"
                | (Phase.Bootstrapped | Phase.Connected | Phase.Ready), _ when record.Attempt = attempt ->
                    Ok [ EnvEvent.Detached { Id = id; Reason = reason } ]
                | _ -> stale record "a disconnection")
        | EnvCommand.Reconciled(present, missing) ->
            let detached =
                present
                |> List.choose (fun (id, attempt) ->
                    match state.Environments.TryFind id with
                    | Some record when
                        record.Attempt = attempt
                        && (match record.Phase with
                            | Phase.Provisioning
                            | Phase.Bootstrapped
                            | Phase.Connected
                            | Phase.Ready -> true
                            | _ -> false)
                        && (match record.Step with
                            | None
                            | Some(Step.Bootstrap | Step.Connect | Step.Install) -> true
                            | Some _ -> false)
                        ->
                        Some(EnvEvent.Detached { Id = id; Reason = Restarted })
                    | _ -> None)

            let lost =
                missing
                |> List.choose (fun (id, reason) ->
                    match state.Environments.TryFind id with
                    | Some record when not record.Terminal && record.Phase <> Phase.Requested ->
                        Some(EnvEvent.Lost { Id = id; Reason = reason })
                    | _ -> None)

            Ok(detached @ lost)

    let react (state: EnvsState) (event: EnvEvent) : EnvIntent list =
        match state.Environments.TryFind event.Id with
        | None -> []
        | Some record ->
            match event with
            | EnvEvent.Requested e -> [ EnvIntent.Provision(e.Id, record.Attempt, e.Spec) ]
            | EnvEvent.Provisioned e -> [ EnvIntent.Bootstrap(e.Id, e.Attempt, record.Spec, e.Lease) ]
            | EnvEvent.Attaching e ->
                match record.Lease with
                | Some lease -> [ EnvIntent.Bootstrap(e.Id, e.Attempt, record.Spec, lease) ]
                | None -> []
            | EnvEvent.Bootstrapped e -> [ EnvIntent.Connect(e.Id, e.Attempt) ]
            | EnvEvent.Connected e -> [ EnvIntent.Install(e.Id, e.Attempt, record.Spec) ]
            | EnvEvent.Readied _ -> []
            | EnvEvent.Detached e
            | EnvEvent.Lost e -> [ EnvIntent.Release(e.Id, record.Attempt) ]
            | EnvEvent.Failed e ->
                match e.Step with
                | Step.Suspend
                | Step.Resume -> []
                | _ -> [ EnvIntent.Release(e.Id, record.Attempt) ]
            | EnvEvent.Started e ->
                match e.Step, record.Lease with
                | Step.Destroy, lease -> [ EnvIntent.Destroy(e.Id, record.Spec, lease) ]
                | Step.Suspend, Some lease -> [ EnvIntent.Suspend(e.Id, record.Spec, lease) ]
                | Step.Resume, Some lease -> [ EnvIntent.Resume(e.Id, record.Spec, lease) ]
                | _ -> []
            | EnvEvent.Suspended e -> [ EnvIntent.Release(e.Id, record.Attempt) ]
            | EnvEvent.Resumed _
            | EnvEvent.Destroyed _ -> []

    /// What a host that starts on a log with environments picks up (intents are not durable): the
    /// steps in flight that are safe to run again (provisioning, suspending, resuming, destroying),
    /// and a reconciliation of every other lease with its provider.
    let recover (state: EnvsState) : EnvIntent list =
        let records = state.Order |> List.choose state.Environments.TryFind

        let steps =
            records
            |> List.choose (fun record ->
                match record.Step, record.Lease with
                | Some Step.Provision, _ -> Some(EnvIntent.Provision(record.Id, record.Attempt, record.Spec))
                | Some Step.Destroy, lease -> Some(EnvIntent.Destroy(record.Id, record.Spec, lease))
                | Some Step.Suspend, Some lease -> Some(EnvIntent.Suspend(record.Id, record.Spec, lease))
                | Some Step.Resume, Some lease -> Some(EnvIntent.Resume(record.Id, record.Spec, lease))
                | _ -> None)

        let leases =
            records
            |> List.choose (fun record ->
                match record.Phase, record.Step, record.Lease with
                | (Phase.Provisioning | Phase.Bootstrapped | Phase.Connected | Phase.Ready | Phase.Suspended),
                  (None | Some(Step.Bootstrap | Step.Connect | Step.Install)),
                  Some lease -> Some(record.Id, record.Attempt, record.Spec, lease)
                | _ -> None)

        if leases.IsEmpty then
            steps
        else
            steps @ [ EnvIntent.Reconcile leases ]

    let machine: Machine<EnvCommand, EnvEvent, EnvsState, EnvIntent> =
        {
            Initial = initial
            Evolve = evolve
            Handle = handle
            React = react
        }

    /// The environments in the order they were requested.
    let records (state: EnvsState) : EnvRecord list =
        state.Order |> List.choose state.Environments.TryFind

    // ---- Codecs ---------------------------------------------------------------------------------

    let private nonEmpty = Codec.string |> Codec.nonEmpty
    let private attempt = Codec.int |> Codec.atLeast 1

    let step: Codec<Step> =
        Codec.enum
            [
                Step.Provision, "provision"
                Step.Bootstrap, "bootstrap"
                Step.Connect, "connect"
                Step.Install, "install"
                Step.Suspend, "suspend"
                Step.Resume, "resume"
                Step.Destroy, "destroy"
            ]
        |> Codec.named "EnvStepKind"

    let phase: Codec<Phase> =
        Codec.enum
            [
                Phase.Requested, "requested"
                Phase.Provisioning, "provisioning"
                Phase.Bootstrapped, "bootstrapped"
                Phase.Connected, "connected"
                Phase.Ready, "ready"
                Phase.Suspended, "suspended"
                Phase.Lost, "lost"
                Phase.Destroyed, "destroyed"
                Phase.Failed, "failed"
            ]
        |> Codec.describe "Where an environment is in its lifecycle."
        |> Codec.named "EnvPhase"

    let private requested: Codec<EnvRequested> =
        Codec.record {
            let! id = Codec.field "id" (fun (e: EnvRequested) -> e.Id) nonEmpty
            and! spec = Codec.field "spec" (fun e -> e.Spec) EnvSpec.codec
            and! at = Codec.field "at" (fun e -> e.At) Codec.int64
            return { Id = id; Spec = spec; At = at }
        }

    let private provisioned: Codec<EnvProvisioned> =
        Codec.record {
            let! id = Codec.field "id" (fun (e: EnvProvisioned) -> e.Id) nonEmpty
            and! attempt = Codec.field "attempt" (fun e -> e.Attempt) attempt
            and! lease = Codec.field "lease" (fun e -> e.Lease) EnvSpec.lease

            return
                {
                    Id = id
                    Attempt = attempt
                    Lease = lease
                }
        }

    let private envAttempt: Codec<EnvAttempt> =
        Codec.record {
            let! id = Codec.field "id" (fun (e: EnvAttempt) -> e.Id) nonEmpty
            and! attempt = Codec.field "attempt" (fun e -> e.Attempt) attempt
            return { Id = id; Attempt = attempt }
        }

    let private bootstrapped: Codec<EnvBootstrapped> =
        Codec.record {
            let! id = Codec.field "id" (fun (e: EnvBootstrapped) -> e.Id) nonEmpty
            and! attempt = Codec.field "attempt" (fun e -> e.Attempt) attempt
            and! transport = Codec.field "transport" (fun e -> e.Transport) EnvSpec.transportKind
            and! machine = Codec.field "machine" (fun e -> e.Machine) EnvSpec.machine

            return
                {
                    Id = id
                    Attempt = attempt
                    Transport = transport
                    Machine = machine
                }
        }

    let private connected: Codec<EnvConnected> =
        Codec.record {
            let! id = Codec.field "id" (fun (e: EnvConnected) -> e.Id) nonEmpty
            and! attempt = Codec.field "attempt" (fun e -> e.Attempt) attempt
            and! platform = Codec.field "platform" (fun e -> e.Platform) EnvCodecs.platform

            return
                {
                    Id = id
                    Attempt = attempt
                    Platform = platform
                }
        }

    /// What confines an environment's processes: `{"driver", "confines", "notes"?}`.
    let sandboxInfo: Codec<EnvSandboxInfo> =
        Codec.record {
            let! driver = Codec.field "driver" (fun (s: EnvSandboxInfo) -> s.Driver) nonEmpty
            and! confines = Codec.field "confines" (fun s -> s.Confines) Codec.bool

            and! notes =
                Codec.defaultField "notes" (fun s -> s.Notes) [] (Codec.list Codec.string)

            return
                {
                    Driver = driver
                    Confines = confines
                    Notes = notes
                }
        }
        |> Codec.describe "What confines an environment's processes: a sandbox driver, or none."
        |> Codec.named "EnvSandboxInfo"

    let private readied: Codec<EnvReadied> =
        Codec.record {
            let! id = Codec.field "id" (fun (e: EnvReadied) -> e.Id) nonEmpty
            and! attempt = Codec.field "attempt" (fun e -> e.Attempt) attempt

            and! components =
                Codec.defaultField "components" (fun e -> e.Components) [] (Codec.list nonEmpty)

            // Added with the sandbox drivers (T113): optional, so what version 1 wrote reads the same.
            and! sandbox = Codec.optionalField "sandbox" (fun e -> e.Sandbox) sandboxInfo

            return
                {
                    Id = id
                    Attempt = attempt
                    Components = components
                    Sandbox = sandbox
                }
        }

    let private note: Codec<EnvNote> =
        Codec.record {
            let! id = Codec.field "id" (fun (e: EnvNote) -> e.Id) nonEmpty
            and! reason = Codec.field "reason" (fun e -> e.Reason) Codec.string
            return { Id = id; Reason = reason }
        }

    let private envStep: Codec<EnvStep> =
        Codec.record {
            let! id = Codec.field "id" (fun (e: EnvStep) -> e.Id) nonEmpty
            and! step = Codec.field "step" (fun e -> e.Step) step
            return { Id = id; Step = step }
        }

    let private stepFailed: Codec<EnvStepFailed> =
        Codec.record {
            let! id = Codec.field "id" (fun (e: EnvStepFailed) -> e.Id) nonEmpty
            and! step = Codec.field "step" (fun e -> e.Step) step
            and! reason = Codec.field "reason" (fun e -> e.Reason) Codec.string

            return
                {
                    Id = id
                    Step = step
                    Reason = reason
                }
        }

    let private envRef: Codec<EnvRef> =
        Codec.record {
            let! id = Codec.field "id" (fun (e: EnvRef) -> e.Id) nonEmpty
            return { Id = id }
        }

    /// The JSON of the lifecycle's events: `{"kind": "requested", "id": …, …}`.
    let event: Codec<EnvEvent> =
        Codec.union "kind" {
            let! requested' = Codec.caseOf "requested" EnvEvent.Requested requested
            and! provisioned' = Codec.caseOf "provisioned" EnvEvent.Provisioned provisioned
            and! attaching = Codec.caseOf "attaching" EnvEvent.Attaching envAttempt
            and! bootstrapped' = Codec.caseOf "bootstrapped" EnvEvent.Bootstrapped bootstrapped
            and! connected' = Codec.caseOf "connected" EnvEvent.Connected connected
            and! readied' = Codec.caseOf "readied" EnvEvent.Readied readied
            and! detached = Codec.caseOf "detached" EnvEvent.Detached note
            and! started = Codec.caseOf "started" EnvEvent.Started envStep
            and! suspended = Codec.caseOf "suspended" EnvEvent.Suspended envRef
            and! resumed = Codec.caseOf "resumed" EnvEvent.Resumed envRef
            and! destroyed = Codec.caseOf "destroyed" EnvEvent.Destroyed envRef
            and! lost = Codec.caseOf "lost" EnvEvent.Lost note
            and! failed = Codec.caseOf "failed" EnvEvent.Failed stepFailed

            return
                fun value ->
                    match value with
                    | EnvEvent.Requested e -> requested' e
                    | EnvEvent.Provisioned e -> provisioned' e
                    | EnvEvent.Attaching e -> attaching e
                    | EnvEvent.Bootstrapped e -> bootstrapped' e
                    | EnvEvent.Connected e -> connected' e
                    | EnvEvent.Readied e -> readied' e
                    | EnvEvent.Detached e -> detached e
                    | EnvEvent.Started e -> started e
                    | EnvEvent.Suspended e -> suspended e
                    | EnvEvent.Resumed e -> resumed e
                    | EnvEvent.Destroyed e -> destroyed e
                    | EnvEvent.Lost e -> lost e
                    | EnvEvent.Failed e -> failed e
        }
        |> Codec.describe "What happened to one of the host's environments."
        |> Codec.named "EnvEvent"

/// How a host's environments are stored: the event schema of the stream.
[<RequireQualifiedAccess>]
module EnvLog =
    /// Version 1. A change to the JSON of an event is a new version with an upcaster
    /// (`EventSchema.evolve`), and a test that reads what version 1 wrote.
    let schema: Fx.Store.EventSchema<EnvEvent> =
        Fx.Store.EventSchema.create Lifecycle.event

    /// The lifecycle events of a store's log.
    let typed (log: Fx.Machines.Host.EventLog<Fx.Store.Payload>) : Fx.Machines.Host.EventLog<EnvEvent> =
        Fx.Store.EventLog.typed schema log
