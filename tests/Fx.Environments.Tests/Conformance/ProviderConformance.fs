/// The environment provider conformance suite v1 (spec §9.2): a function from a provider, and how a
/// host starts with it, to tests. A provider is done when it passes on Linux, macOS and Windows,
/// for every transport it offers. Everything goes through the host (`EnvsHost`) and envd's
/// capability records, as host code uses them, so the suite does not depend on where the
/// environment is.
module Fx.Environments.Tests.Conformance.ProviderConformance

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Environments
open Fx.Envd
open Fx.Machines.Host
open Fx.Sdk
open Fx.Store
open Fx.Variants

/// The environment the host under test runs in.
type HostEnv =
    {
        Clock: Clock
        Trace: Trace
        Ids: Ids
        Variants: Variants
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface ITrace with
        member env.Trace = env.Trace

    interface IIds with
        member env.Ids = env.Ids

    interface IVariants with
        member env.Variants = env.Variants

    interface IScopeWith<HostEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"HostEnv({env.Scope})"

module HostEnv =
    /// The host's slots: the core's and the sandbox's.
    let slots = Slots.all @ SandboxSlots.all

    /// A host whose variants have these selections.
    let withSelections (selections: (string * Json) list) : HostEnv =
        {
            Clock = SystemClock.clock
            Trace = Trace.none
            Ids = SystemIds.create SystemClock.clock
            Variants = Fx.Variants.Fakes.FakeVariants.create slots selections
            Scope = Scope.root
        }

    let live () : HostEnv = withSelections []

/// Variables the subject's host must add to its own (`EnvsSettings.HostEnvironment`), to check
/// that environments on the host's machine get the host's variables without credentials and
/// injection vectors.
let hygieneVariables: (string * string) list =
    [
        "CONFORMANCE_PLAIN", "visible"
        "CONFORMANCE_API_KEY", "credential"
        "BUN_OPTIONS", "--preload=./evil.ts"
    ]

/// What the suite needs to know about a provider.
[<NoEquality; NoComparison>]
type Subject =
    {
        Name: string
        Provider: EnvProvider
        /// The transport the subject's specs ask for.
        Transport: TransportKind
        /// A spec of this provider, with a fresh workspace that holds `hello.txt` with `hello`.
        Spec: unit -> EnvSpec
        /// A spec on the same workspace as another spec.
        SameWorkspace: EnvSpec -> EnvSpec
        /// A spec the provider must fail to create (e.g. a workspace that does not exist).
        Failing: unit -> EnvSpec
        /// Starts a host with the provider on a log (what the composition root does), with
        /// `hygieneVariables` among the host's variables.
        Start: EventLog<EnvEvent> -> EnvsHost<HostEnv>
        /// The provider's environment, for calls straight to the provider.
        ProviderEnv: unit -> ProviderEnv
        /// Makes the provider lose an environment behind the host's back.
        Lose: Lease -> unit
    }

    override this.ToString() = this.Name

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 120.0).GetAwaiter().GetResult()

let private none = CancellationToken.None

let private ok (what: string) (result: Result<'T, 'E>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failtest $"{what}: {e}"

let private newLog () : EventLog<EnvEvent> =
    EnvLog.typed (InMemoryEventLog<Payload>().Log)

/// Runs a test with a started host, stopped afterwards.
let private withHost (subject: Subject) (log: EventLog<EnvEvent>) (body: EnvsHost<HostEnv> -> unit) =
    let host = subject.Start log

    try
        body host
    finally
        wait (EnvsHost.stop host)

let private create (host: EnvsHost<HostEnv>) (spec: EnvSpec) : EnvRecord =
    wait ((EnvsHost.envs host).Create spec none) |> ok "create"

let private openEnv (host: EnvsHost<HostEnv>) (id: string) : EnvHandle =
    wait ((EnvsHost.envs host).Open id none) |> ok "open"

let private destroy (host: EnvsHost<HostEnv>) (id: string) : EnvRecord =
    wait ((EnvsHost.envs host).Destroy id none) |> ok "destroy"

let private path (handle: EnvHandle) (name: string) =
    EnvLayout.join handle.Layout.Os handle.Layout.Workspace name

let private windows (handle: EnvHandle) = handle.Client.Platform.Os = "windows"

let private readText (handle: EnvHandle) (name: string) : string =
    wait (handle.Client.FsRead.ReadFile (path handle name) none)
    |> ok $"read {name}"
    |> Encoding.UTF8.GetString

let private writeText (handle: EnvHandle) (name: string) (text: string) =
    wait (handle.Client.Fs.WriteFile (path handle name) (Encoding.UTF8.GetBytes text) none)
    |> ok $"write {name}"

/// Runs a command line with the environment's shell, in the workspace.
let private script (handle: EnvHandle) (posix: string) (powershell: string) : Output =
    let line = if windows handle then powershell else posix
    let command = Command.script line |> Command.inDir handle.Layout.Workspace
    wait (handle.Client.Shell.Run command none) |> ok $"run {line}"

let private stdout (output: Output) = (Output.stdoutText output).Trim()

let private leases (subject: Subject) : Lease list =
    wait (ProviderEnv.within (subject.ProviderEnv()) subject.Provider.List (HostEnv.live ()))
    |> ok "list"

let private issuePaths (error: EnvError) : string list =
    match error with
    | EnvError.Invalid issues -> issues |> List.map (fun i -> JsonPath.render i.Path)
    | other -> failtest $"expected an invalid spec, got {other}"

/// The suite.
let tests (subject: Subject) : Test =
    testList
        $"environment provider conformance ({subject})"
        [
            test "the provider's data is valid" {
                Expect.isEmpty (EnvProviders.validate subject.Provider) "issues"
                Expect.contains subject.Provider.Caps.Transports subject.Transport "the subject's transport is offered"
            }

            test "creates a ready environment and leases it" {
                withHost subject (newLog ()) (fun host ->
                    let spec = subject.Spec()
                    let record = create host spec
                    Expect.equal record.Phase Phase.Ready "ready"
                    Expect.equal record.Step None "nothing in flight"
                    Expect.equal record.Transport (Some subject.Transport) "transport"
                    let lease = record.Lease |> Option.get
                    Expect.equal lease.Id record.Id "the lease is named after the environment"
                    Expect.equal lease.Provider subject.Provider.Kind "the lease's provider"
                    let sandbox = record.Sandbox |> Option.get

                    Expect.equal
                        (Envs.warnings record).Length
                        (if sandbox.Confines then 0 else 1)
                        "the record says what sandboxes it; an unsandboxed environment carries one warning"

                    let listed = wait ((EnvsHost.envs host).List none)
                    Expect.equal (listed |> List.map (fun r -> r.Id)) [ record.Id ] "listed"
                    Expect.equal (wait ((EnvsHost.envs host).Get record.Id none)) (Some record) "get"
                    Expect.isTrue (leases subject |> List.exists (fun l -> l.Id = record.Id)) "the provider lists it"
                    destroy host record.Id |> ignore)
            }

            test "reports the machine, envd's platform and the layout" {
                withHost subject (newLog ()) (fun host ->
                    let record = create host (subject.Spec())
                    let handle = openEnv host record.Id
                    let lease = record.Lease.Value
                    let platform = handle.Client.Platform

                    Expect.equal
                        platform.Os
                        (string handle.Machine.Target.Os)
                        "envd runs on the machine the provider reported"

                    Expect.equal platform.Arch (string handle.Machine.Target.Arch) "architecture"
                    Expect.equal platform.Workspace lease.Workspace "envd's workspace is the lease's"
                    Expect.equal record.Platform (Some platform) "the record keeps the platform"
                    Expect.equal record.Machine (Some handle.Machine) "the record keeps the machine"

                    for dir in [ handle.Layout.EnvdState; handle.Layout.Assets; handle.Layout.Owned ] do
                        Expect.stringStarts dir lease.Home "under the environment's home"

                    destroy host record.Id |> ignore)
            }

            test "runs processes in the workspace" {
                withHost subject (newLog ()) (fun host ->
                    let record = create host (subject.Spec())
                    let handle = openEnv host record.Id
                    let output = script handle "echo hi" "Write-Output hi"
                    Expect.equal (output.ExitCode, stdout output) (0, "hi") "a command ran"
                    let output = script handle "cat hello.txt" "Get-Content hello.txt"
                    Expect.equal (stdout output) "hello" "in the workspace"
                    destroy host record.Id |> ignore)
            }

            test "keeps the host's credentials and injection vectors out" {
                withHost subject (newLog ()) (fun host ->
                    let record = create host (subject.Spec())
                    let handle = openEnv host record.Id

                    if handle.Machine.SameHost then
                        let output =
                            script
                                handle
                                "echo \"$CONFORMANCE_PLAIN|$CONFORMANCE_API_KEY|$BUN_OPTIONS\""
                                "Write-Output \"$env:CONFORMANCE_PLAIN|$env:CONFORMANCE_API_KEY|$env:BUN_OPTIONS\""

                        Expect.equal (stdout output) "visible||" "plain variables pass, credentials and vectors do not"

                    destroy host record.Id |> ignore)
            }

            test "reads and writes the workspace's files" {
                withHost subject (newLog ()) (fun host ->
                    let record = create host (subject.Spec())
                    let handle = openEnv host record.Id
                    Expect.equal (readText handle "hello.txt") "hello\n" "the workspace's file"
                    writeText handle "written.txt" "from the host"
                    let output = script handle "cat written.txt" "Get-Content written.txt"
                    Expect.equal (stdout output) "from the host" "processes see it"

                    let entries =
                        wait (handle.Client.FsOps.StatMany [ path handle "hello.txt"; path handle "missing.txt" ] none)

                    match entries with
                    | [ Ok entry; Error(NotFound _) ] -> Expect.equal entry.Kind EntryKind.File "a file"
                    | other -> failtest $"unexpected stat: {other}"

                    destroy host record.Id |> ignore)
            }

            test "stores blobs" {
                withHost subject (newLog ()) (fun host ->
                    let record = create host (subject.Spec())
                    let handle = openEnv host record.Id
                    let content = Encoding.UTF8.GetBytes "a blob in the environment"
                    let blob = wait (handle.Client.Blobs.Put content none)
                    Expect.equal blob.Size (int64 content.Length) "size"
                    let back = wait (handle.Client.Blobs.Get blob.Hash none) |> ok "get"
                    Expect.equal back content "content"
                    destroy host record.Id |> ignore)
            }

            test "takes workspace snapshots where envd has them" {
                withHost subject (newLog ()) (fun host ->
                    let record = create host (subject.Spec())
                    let handle = openEnv host record.Id

                    if Platform.has Feature.Snapshots handle.Client.Platform then
                        let snapshots = handle.Client.Snapshots
                        let first = wait (snapshots.Take "before" none) |> ok "take"
                        writeText handle "hello.txt" "changed\n"
                        let second = wait (snapshots.Take "after" none) |> ok "take"
                        let changes = wait (snapshots.Diff first second none) |> ok "diff"
                        Expect.equal (changes |> List.map (fun c -> c.Path)) [ "hello.txt" ] "the change"
                        wait (snapshots.Restore first None none) |> ok "restore"
                        Expect.equal (readText handle "hello.txt") "hello\n" "restored"

                    destroy host record.Id |> ignore)
            }

            test "destroys it, and keeps an existing workspace" {
                withHost subject (newLog ()) (fun host ->
                    let spec = subject.Spec()
                    let record = create host spec
                    let handle = openEnv host record.Id
                    writeText handle "kept.txt" "kept"
                    let destroyed = destroy host record.Id
                    Expect.equal destroyed.Phase Phase.Destroyed "destroyed"
                    Expect.isFalse (leases subject |> List.exists (fun l -> l.Id = record.Id)) "the provider let it go"
                    Expect.equal (destroy host record.Id).Phase Phase.Destroyed "destroying again succeeds"

                    match wait ((EnvsHost.envs host).Open record.Id none) with
                    | Error(EnvError.NotReady r) -> Expect.equal r.Phase Phase.Destroyed "cannot be opened"
                    | other -> failtest $"expected not ready, got {other}"

                    if spec.Workspace.Source = MaterializeKind.Existing then
                        let again = create host (subject.SameWorkspace spec)
                        let handle = openEnv host again.Id
                        Expect.equal (readText handle "kept.txt") "kept" "the workspace is still there"
                        destroy host again.Id |> ignore)
            }

            test "reconciles leases after a restart" {
                let log = newLog ()

                let kept, lost, spec =
                    let host = subject.Start log

                    try
                        let spec = subject.Spec()
                        let kept = create host spec
                        let handle = openEnv host kept.Id
                        writeText handle "before.txt" "before the restart"
                        let lost = create host (subject.Spec())
                        kept, lost, spec
                    finally
                        wait (EnvsHost.stop host)

                subject.Lose lost.Lease.Value

                withHost subject log (fun host ->
                    let envs = EnvsHost.envs host

                    let settled (id: string) =
                        let mutable record = wait (envs.Get id none)

                        for _ in 1..100 do
                            if record.IsSome && record.Value.Phase = Phase.Ready then
                                Thread.Sleep 50
                                record <- wait (envs.Get id none)

                        record.Value

                    let keptAfter = settled kept.Id

                    Expect.equal
                        keptAfter.Phase
                        Phase.Provisioning
                        "detached: the host has no connection after a restart"

                    Expect.equal keptAfter.Reason (Some Lifecycle.Restarted) "why"
                    Expect.equal keptAfter.Lease kept.Lease "the lease is kept"
                    let lostAfter = settled lost.Id
                    Expect.equal lostAfter.Phase Phase.Lost "the provider no longer has it"

                    let handle = openEnv host kept.Id
                    Expect.equal (readText handle "before.txt") "before the restart" "the same workspace"
                    let reopened = wait (envs.Get kept.Id none) |> Option.get
                    Expect.equal reopened.Phase Phase.Ready "ready again"
                    Expect.isGreaterThan reopened.Attempt kept.Attempt "a new attempt"

                    Expect.equal (destroy host lost.Id).Phase Phase.Destroyed "a lost environment can be destroyed"
                    destroy host kept.Id |> ignore
                    ignore spec)
            }

            test "attaches again after the connection is lost" {
                withHost subject (newLog ()) (fun host ->
                    let record = create host (subject.Spec())
                    let handle = openEnv host record.Id
                    writeText handle "before.txt" "before"
                    wait (EnvClient.close handle.Client) |> ignore

                    let envs = EnvsHost.envs host
                    let mutable current = wait (envs.Get record.Id none) |> Option.get

                    for _ in 1..200 do
                        if current.Phase = Phase.Ready then
                            Thread.Sleep 25
                            current <- wait (envs.Get record.Id none) |> Option.get

                    Expect.equal current.Phase Phase.Provisioning "detached"
                    let again = openEnv host record.Id
                    Expect.equal (readText again "before.txt") "before" "the same environment"
                    destroy host record.Id |> ignore)
            }

            test "negotiates capabilities before anything is created" {
                withHost subject (newLog ()) (fun host ->
                    let envs = EnvsHost.envs host
                    let spec = subject.Spec()
                    let caps = subject.Provider.Caps

                    let refused (change: EnvSpec -> EnvSpec) =
                        match wait (envs.Create (change spec) none) with
                        | Error e -> issuePaths e
                        | Ok record -> failtest $"expected a refusal, got {record}"

                    let notOffered =
                        [
                            TransportKind.InProcess
                            TransportKind.Piped
                            TransportKind.Direct
                            TransportKind.Dialback
                        ]
                        |> List.tryFind (fun t -> not (List.contains t caps.Transports))

                    match notOffered with
                    | Some transport ->
                        Expect.contains
                            (refused (fun s -> { s with Transport = Some transport }))
                            "$.transport"
                            "a transport it does not offer"
                    | None -> ()

                    let notMaterialized =
                        [
                            MaterializeKind.Existing
                            MaterializeKind.Mount
                            MaterializeKind.Git
                            MaterializeKind.Upload
                            MaterializeKind.Snapshot
                        ]
                        |> List.tryFind (fun m -> not (List.contains m caps.Materialize))

                    match notMaterialized with
                    | Some source ->
                        Expect.contains
                            (refused (fun s ->
                                { s with
                                    Workspace = { s.Workspace with Source = source }
                                }))
                            "$.workspace.source"
                            "a materialization it does not do"
                    | None -> ()

                    let unknownSetting = Json.Object [ "conformance-unknown-setting", Json.Bool true ]

                    Expect.isNonEmpty
                        (refused (fun s -> { s with Config = unknownSetting }))
                        "unknown settings are refused"

                    Expect.contains
                        (refused (fun s ->
                            { s with
                                Sandbox = Some "conformance-no-such-driver"
                            }))
                        "$.sandbox"
                        "a sandbox driver that does not exist"

                    match
                        wait (
                            envs.Create
                                { spec with
                                    Provider = "conformance-no-such-provider"
                                }
                                none
                        )
                    with
                    | Error(EnvError.UnknownProvider kind) -> Expect.equal kind "conformance-no-such-provider" "kind"
                    | other -> failtest $"expected an unknown provider, got {other}"

                    Expect.isEmpty (wait (envs.List none)) "nothing was created")
            }

            test "reports a failed creation, which can be destroyed" {
                withHost subject (newLog ()) (fun host ->
                    let envs = EnvsHost.envs host

                    match wait (envs.Create (subject.Failing()) none) with
                    | Error(EnvError.NotReady record) ->
                        Expect.equal record.Phase Phase.Failed "failed"
                        Expect.isSome record.Reason "with a reason"
                        Expect.equal (destroy host record.Id).Phase Phase.Destroyed "destroyed"
                    | other -> failtest $"expected a failure, got {other}")
            }

            test "creating is idempotent per id, and so is destroying" {
                let providerEnv = subject.ProviderEnv()

                let request =
                    {
                        Id = "conformance-" + Guid.NewGuid().ToString("N")
                        Spec = subject.Spec()
                    }

                let run (effect: Eff<IProviderEnv, 'T>) : 'T =
                    wait (ProviderEnv.within providerEnv effect (HostEnv.live ()))

                let first = run (subject.Provider.Create request) |> ok "create"
                let second = run (subject.Provider.Create request) |> ok "create again"
                Expect.equal second first "the same lease"
                let listed = leases subject |> List.filter (fun l -> l.Id = request.Id)
                Expect.equal listed [ first ] "listed once"
                run (subject.Provider.Destroy first) |> ok "destroy"
                run (subject.Provider.Destroy first) |> ok "destroy again"
                Expect.isEmpty (leases subject |> List.filter (fun l -> l.Id = request.Id)) "gone"
            }
        ]
