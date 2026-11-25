namespace Fx.Components

open Fx.Machines
open Fx.Sdk

/// Where an install is in the pipeline (spec §6.1: resolve → fetch → verify → stage → activate →
/// probe).
[<RequireQualifiedAccess>]
type InstallStep =
    /// Looking for it on disk (another installer may have put it there).
    | Inspecting
    /// Waiting for the answer to the first-install grant (spec §13).
    | AwaitingApproval
    /// Downloading the archive and checking its digest.
    | Fetching
    /// Checking the archive again, unpacking it into a staging directory, probing it there, and
    /// activating it with one atomic rename.
    | Building

    override this.ToString() =
        match this with
        | Inspecting -> "inspecting"
        | AwaitingApproval -> "awaiting approval"
        | Fetching -> "fetching"
        | Building -> "building"

/// Why an install failed. Nothing of a failed install is ever active.
type InstallFailure =
    /// The first install of the component was not approved.
    | Denied of reason: string
    | DownloadFailed of reason: string
    | DigestMismatch of expected: string * actual: string
    /// The archive is corrupt, unsafe, too large, or lacks a file the component declares.
    | BadArchive of reason: string
    /// The probe failed: the program did not start, exited with an error, or printed something else.
    | ProbeFailed of reason: string
    /// The method is not implemented here (T211).
    | Unsupported of reason: string
    | IoFailed of reason: string

    override this.ToString() =
        match this with
        | Denied reason -> $"not approved: {reason}"
        | DownloadFailed reason -> $"download failed: {reason}"
        | DigestMismatch(expected, actual) -> $"digest mismatch: expected {expected}, got {actual}"
        | BadArchive reason -> $"bad archive: {reason}"
        | ProbeFailed reason -> $"probe failed: {reason}"
        | Unsupported reason -> $"unsupported: {reason}"
        | IoFailed reason -> $"I/O failed: {reason}"

/// The answer to an install approval.
type ApprovalAnswer =
    | Approved of by: string
    | Rejected of reason: string

    override this.ToString() =
        match this with
        | Approved by -> $"approved by {by}"
        | Rejected reason -> $"rejected: {reason}"

/// A downloaded archive whose digest matched.
type FetchedArchive =
    {
        Path: string
        Size: int64
    }

    override this.ToString() = $"{this.Path} ({this.Size} bytes)"

/// An active install.
type InstalledInfo =
    {
        /// The install directory (absolute).
        Dir: string
        Files: int
        Bytes: int64
        /// Found on disk (installed by an earlier run or another installer) rather than installed now.
        Reused: bool
    }

    override this.ToString() =
        let how = if this.Reused then "found" else "installed"
        $"{how} in {this.Dir} ({this.Files} files, {this.Bytes} bytes)"

/// An install that is under way.
type InstallProgress =
    {
        Spec: InstallSpec
        Attempt: int
        Step: InstallStep
        /// The verified archive, once fetched.
        Archive: FetchedArchive option
    }

    override this.ToString() =
        $"{this.Spec.Key} attempt {this.Attempt}: {this.Step}"

type InstallStatus =
    | InProgress of InstallProgress
    | Installed of spec: InstallSpec * info: InstalledInfo
    | Failed of spec: InstallSpec * attempt: int * step: InstallStep * failure: InstallFailure

    member this.Spec =
        match this with
        | InProgress progress -> progress.Spec
        | Installed(spec, _)
        | Failed(spec, _, _, _) -> spec

    override this.ToString() =
        match this with
        | InProgress progress -> string progress
        | Installed(spec, info) -> $"{spec.Key}: {info}"
        | Failed(spec, attempt, step, failure) -> $"{spec.Key} attempt {attempt} failed while {step}: {failure}"

/// The installer's state: a fold of its events.
type InstallerState =
    {
        Installs: Map<InstallKey, InstallStatus>
        /// Components whose first install was approved (spec §13).
        Approved: Set<string>
        /// What each holder (an extension, a lockfile's user) uses: the references that keep installs
        /// from being pruned.
        Holders: Map<string, Set<InstallKey>>
        /// The prune in progress, if any: its round.
        Pruning: int option
        /// Prunes so far.
        Rounds: int
    }

    override this.ToString() =
        $"InstallerState({this.Installs.Count} installs, {this.Holders.Count} holders)"

[<RequireQualifiedAccess>]
type InstallerCommand =
    /// Installs the spec unless it is installed or being installed, and records that the holder
    /// uses it.
    | Ensure of holder: string * spec: InstallSpec
    /// Sets what a holder uses, exactly (an empty list releases it).
    | Hold of holder: string * keys: InstallKey list
    /// Removes installs no holder of any installer sharing the root uses.
    | Prune
    // Results of intents.
    /// Whether it is on disk. Also the result of a fetch or build that found it installed by
    /// another installer meanwhile.
    | Inspected of key: InstallKey * found: InstalledInfo option
    | Answered of key: InstallKey * answer: ApprovalAnswer
    | Fetched of key: InstallKey * result: Result<FetchedArchive, InstallFailure>
    | Built of key: InstallKey * result: Result<InstalledInfo, InstallFailure>
    | Swept of round: int * removed: string list

    override this.ToString() =
        match this with
        | Ensure(holder, spec) -> $"ensure {spec} for {holder}"
        | Hold(holder, keys) ->
            let keys = keys |> List.map string |> String.concat ", "
            $"hold [{keys}] for {holder}"
        | Prune -> "prune"
        | Inspected(key, found) ->
            match found with
            | Some info -> $"inspected {key}: {info}"
            | None -> $"inspected {key}: missing"
        | Answered(key, answer) -> $"answered {key}: {answer}"
        | Fetched(key, Ok archive) -> $"fetched {key}: {archive}"
        | Fetched(key, Error failure) -> $"fetching {key} failed: {failure}"
        | Built(key, Ok info) -> $"built {key}: {info}"
        | Built(key, Error failure) -> $"building {key} failed: {failure}"
        | Swept(round, removed) ->
            let removed = String.concat ", " removed
            $"swept (round {round}): [{removed}]"

/// What happened to the installs. Events are facts kept forever: add cases, never change one.
[<RequireQualifiedAccess>]
type InstallerEvent =
    /// An install attempt started.
    | Requested of holder: string * spec: InstallSpec * attempt: int
    /// What a holder uses now.
    | Held of holder: string * keys: InstallKey list
    /// It was on disk already.
    | Found of key: InstallKey * info: InstalledInfo
    /// It was not on disk.
    | Missing of key: InstallKey
    | ApprovalRequested of key: InstallKey
    /// The component's first install was approved; later installs of it need no approval.
    | Approved of id: string * by: string
    | Fetched of key: InstallKey * archive: FetchedArchive
    /// Verified, unpacked, probed and activated.
    | Installed of key: InstallKey * info: InstalledInfo
    | Failed of key: InstallKey * step: InstallStep * failure: InstallFailure
    | PruneStarted of round: int
    /// Install directories (`<id>/<version>-<hash>`) removed by a prune.
    | Pruned of round: int * removed: string list

    override this.ToString() =
        match this with
        | Requested(holder, spec, attempt) -> $"requested {spec} for {holder} (attempt {attempt})"
        | Held(holder, keys) ->
            let keys = keys |> List.map string |> String.concat ", "
            $"held by {holder}: [{keys}]"
        | Found(key, info) -> $"found {key}: {info}"
        | Missing key -> $"missing {key}"
        | ApprovalRequested key -> $"approval requested for {key}"
        | Approved(id, by) -> $"approved {id} by {by}"
        | Fetched(key, archive) -> $"fetched {key}: {archive}"
        | Installed(key, info) -> $"installed {key}: {info}"
        | Failed(key, step, failure) -> $"failed {key} while {step}: {failure}"
        | PruneStarted round -> $"prune {round} started"
        | Pruned(round, removed) ->
            let removed = String.concat ", " removed
            $"prune {round} removed [{removed}]"

/// What the installer asks its interpreter to do. Intents run one at a time, in order.
[<RequireQualifiedAccess>]
type InstallerIntent =
    /// Writes the installs this installer's holders use to its roots file, so that no installer
    /// sharing the root prunes them.
    | Publish of keys: InstallKey list
    | Inspect of spec: InstallSpec
    | Ask of spec: InstallSpec
    | Fetch of spec: InstallSpec
    | Build of spec: InstallSpec * archive: FetchedArchive
    | Sweep of round: int

    override this.ToString() =
        match this with
        | Publish keys -> $"publish {keys.Length} roots"
        | Inspect spec -> $"inspect {spec.Key}"
        | Ask spec -> $"ask to install {spec.Key}"
        | Fetch spec -> $"fetch {spec.Key}"
        | Build(spec, _) -> $"build {spec.Key}"
        | Sweep round -> $"sweep (round {round})"

/// The installer machine (spec §6.1, environment side). One actor per install root and process;
/// installers in other processes that share the root coordinate through the root itself (locks,
/// roots files, atomic renames; see `Pipeline`).
module Installer =

    let initial: InstallerState =
        {
            Installs = Map.empty
            Approved = Set.empty
            Holders = Map.empty
            Pruning = None
            Rounds = 0
        }

    /// Every install some holder uses, sorted.
    let held (state: InstallerState) : InstallKey list =
        state.Holders
        |> Map.toList
        |> List.collect (fun (_, keys) -> Set.toList keys)
        |> List.distinct
        |> List.sort

    let private reject code message = Error(Rejection.create code message)

    let private progress (state: InstallerState) (key: InstallKey) : InstallProgress option =
        match state.Installs.TryFind key with
        | Some(InProgress progress) -> Some progress
        | _ -> None

    let private update (key: InstallKey) (change: InstallProgress -> InstallProgress) (state: InstallerState) =
        match progress state key with
        | Some p ->
            { state with
                Installs = state.Installs.Add(key, InProgress(change p))
            }
        | None -> state

    let evolve (state: InstallerState) (event: InstallerEvent) : InstallerState =
        match event with
        | InstallerEvent.Requested(_, spec, attempt) ->
            { state with
                Installs =
                    state.Installs.Add(
                        spec.Key,
                        InProgress
                            {
                                Spec = spec
                                Attempt = attempt
                                Step = InstallStep.Inspecting
                                Archive = None
                            }
                    )
            }
        | InstallerEvent.Held(holder, keys) ->
            { state with
                Holders =
                    if keys.IsEmpty then
                        state.Holders.Remove holder
                    else
                        state.Holders.Add(holder, Set.ofList keys)
            }
        | InstallerEvent.Found(key, info)
        | InstallerEvent.Installed(key, info) ->
            match state.Installs.TryFind key with
            | Some status ->
                { state with
                    Installs = state.Installs.Add(key, Installed(status.Spec, info))
                }
            | None -> state
        | InstallerEvent.Missing key ->
            let approved =
                match progress state key with
                | Some p -> state.Approved.Contains p.Spec.Id
                | None -> false

            update
                key
                (fun p ->
                    { p with
                        Step =
                            if approved then
                                InstallStep.Fetching
                            else
                                InstallStep.AwaitingApproval
                    })
                state
        | InstallerEvent.ApprovalRequested key ->
            update
                key
                (fun p ->
                    { p with
                        Step = InstallStep.AwaitingApproval
                    })
                state
        | InstallerEvent.Approved(id, _) ->
            let installs =
                state.Installs
                |> Map.map (fun _ status ->
                    match status with
                    | InProgress p when p.Spec.Id = id && p.Step = InstallStep.AwaitingApproval ->
                        InProgress { p with Step = InstallStep.Fetching }
                    | other -> other)

            { state with
                Approved = state.Approved.Add id
                Installs = installs
            }
        | InstallerEvent.Fetched(key, archive) ->
            update
                key
                (fun p ->
                    { p with
                        Step = InstallStep.Building
                        Archive = Some archive
                    })
                state
        | InstallerEvent.Failed(key, step, failure) ->
            match progress state key with
            | Some p ->
                { state with
                    Installs = state.Installs.Add(key, Failed(p.Spec, p.Attempt, step, failure))
                }
            | None -> state
        | InstallerEvent.PruneStarted round ->
            { state with
                Pruning = Some round
                Rounds = max state.Rounds round
            }
        | InstallerEvent.Pruned(_, removed) ->
            let removed = Set.ofList removed

            { state with
                Pruning = None
                Installs =
                    state.Installs
                    |> Map.filter (fun key status ->
                        match status with
                        | Installed _ -> not (removed.Contains(key.Id + "/" + key.Dir))
                        | _ -> true)
            }

    let private stale (key: InstallKey) (what: string) =
        reject "installer.stale" $"{key} is not {what}; this result is from an earlier attempt"

    let private stepIs (state: InstallerState) (key: InstallKey) (step: InstallStep) =
        match progress state key with
        | Some p when p.Step = step -> Some p
        | _ -> None

    let handle (state: InstallerState) (command: InstallerCommand) : Result<InstallerEvent list, Rejection> =
        match command with
        | InstallerCommand.Ensure(holder, spec) ->
            match spec.Method with
            | Archive _ ->
                let keys = state.Holders.TryFind holder |> Option.defaultValue Set.empty

                let hold =
                    if keys.Contains spec.Key then
                        []
                    else
                        [ InstallerEvent.Held(holder, Set.toList (keys.Add spec.Key)) ]

                match state.Installs.TryFind spec.Key with
                | Some(Installed _)
                | Some(InProgress _) -> Ok hold
                | Some(Failed(_, attempt, _, _)) -> Ok(hold @ [ InstallerEvent.Requested(holder, spec, attempt + 1) ])
                | None -> Ok(hold @ [ InstallerEvent.Requested(holder, spec, 1) ])
            | other ->
                reject
                    "installer.unsupported-method"
                    $"{spec.Key}: the {other.Kind} install method is not implemented yet (T211); only archive is"
        | InstallerCommand.Hold(holder, keys) ->
            let keys = keys |> List.distinct |> List.sort

            let current =
                state.Holders.TryFind holder |> Option.map Set.toList |> Option.defaultValue []

            if keys = current then
                Ok []
            else
                Ok [ InstallerEvent.Held(holder, keys) ]
        | InstallerCommand.Prune ->
            match state.Pruning with
            | Some _ -> Ok []
            | None -> Ok [ InstallerEvent.PruneStarted(state.Rounds + 1) ]
        | InstallerCommand.Inspected(key, found) ->
            match progress state key, found with
            // Another installer may finish it while this one fetches or builds it.
            | Some _, Some info -> Ok [ InstallerEvent.Found(key, info) ]
            | _ when (stepIs state key InstallStep.Inspecting).IsNone -> stale key "being inspected"
            | Some p, None when state.Approved.Contains p.Spec.Id -> Ok [ InstallerEvent.Missing key ]
            | _ -> Ok [ InstallerEvent.Missing key; InstallerEvent.ApprovalRequested key ]
        | InstallerCommand.Answered(key, answer) ->
            match stepIs state key InstallStep.AwaitingApproval, answer with
            | None, _ -> stale key "awaiting approval"
            | Some p, Approved by -> Ok [ InstallerEvent.Approved(p.Spec.Id, by) ]
            | Some _, Rejected reason -> Ok [ InstallerEvent.Failed(key, InstallStep.AwaitingApproval, Denied reason) ]
        | InstallerCommand.Fetched(key, result) ->
            match stepIs state key InstallStep.Fetching, result with
            | None, _ -> stale key "being fetched"
            | Some _, Ok archive -> Ok [ InstallerEvent.Fetched(key, archive) ]
            | Some _, Error failure -> Ok [ InstallerEvent.Failed(key, InstallStep.Fetching, failure) ]
        | InstallerCommand.Built(key, result) ->
            match stepIs state key InstallStep.Building, result with
            | None, _ -> stale key "being built"
            | Some _, Ok info -> Ok [ InstallerEvent.Installed(key, info) ]
            | Some _, Error failure -> Ok [ InstallerEvent.Failed(key, InstallStep.Building, failure) ]
        | InstallerCommand.Swept(round, removed) ->
            match state.Pruning with
            | Some current when current = round -> Ok [ InstallerEvent.Pruned(round, List.sort removed) ]
            | _ -> reject "installer.stale" $"prune {round} is not in progress"

    let react (state: InstallerState) (event: InstallerEvent) : InstallerIntent list =
        match event with
        | InstallerEvent.Held _ -> [ InstallerIntent.Publish(held state) ]
        | InstallerEvent.Requested(_, spec, _) -> [ InstallerIntent.Inspect spec ]
        | InstallerEvent.Missing key ->
            match stepIs state key InstallStep.Fetching with
            | Some p -> [ InstallerIntent.Fetch p.Spec ]
            | None -> []
        | InstallerEvent.ApprovalRequested key ->
            match stepIs state key InstallStep.AwaitingApproval with
            | Some p -> [ InstallerIntent.Ask p.Spec ]
            | None -> []
        | InstallerEvent.Approved(id, _) ->
            // Every install of the component that waited for this approval goes on.
            state.Installs
            |> Map.toList
            |> List.choose (fun (_, status) ->
                match status with
                | InProgress p when p.Spec.Id = id && p.Step = InstallStep.Fetching ->
                    Some(InstallerIntent.Fetch p.Spec)
                | _ -> None)
        | InstallerEvent.Fetched(key, archive) ->
            match stepIs state key InstallStep.Building with
            | Some p -> [ InstallerIntent.Build(p.Spec, archive) ]
            | None -> []
        | InstallerEvent.PruneStarted round -> [ InstallerIntent.Sweep round ]
        | InstallerEvent.Found _
        | InstallerEvent.Installed _
        | InstallerEvent.Failed _
        | InstallerEvent.Pruned _ -> []

    /// What to pick up when an installer starts on a log with work in flight (intents are not
    /// durable): republish the roots, then go on with every install from the step it was in. Every
    /// step is safe to run again, and the build step starts by clearing what a crash left.
    let recover (state: InstallerState) : InstallerIntent list =
        let publish =
            if state.Holders.IsEmpty then
                []
            else
                [ InstallerIntent.Publish(held state) ]

        let resume =
            state.Installs
            |> Map.toList
            |> List.choose (fun (_, status) ->
                match status with
                | InProgress p ->
                    match p.Step, p.Archive with
                    | InstallStep.Inspecting, _ -> Some(InstallerIntent.Inspect p.Spec)
                    | InstallStep.AwaitingApproval, _ -> Some(InstallerIntent.Ask p.Spec)
                    | InstallStep.Fetching, _ -> Some(InstallerIntent.Fetch p.Spec)
                    | InstallStep.Building, Some archive -> Some(InstallerIntent.Build(p.Spec, archive))
                    // `Fetched` records the archive with the step, so this does not happen.
                    | InstallStep.Building, None -> None
                | _ -> None)

        let sweep =
            match state.Pruning with
            | Some round -> [ InstallerIntent.Sweep round ]
            | None -> []

        publish @ resume @ sweep

    let machine: Machine<InstallerCommand, InstallerEvent, InstallerState, InstallerIntent> =
        {
            Initial = initial
            Evolve = evolve
            Handle = handle
            React = react
        }
