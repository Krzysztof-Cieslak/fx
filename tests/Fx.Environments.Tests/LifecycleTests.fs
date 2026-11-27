/// The lifecycle machine (spec §9.5): its rules, what a restart recovers, its JSON, and replay.
module Fx.Environments.Tests.LifecycleTests

open System
open System.IO
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Environments
open Fx.Envd
open Fx.Machines
open Fx.Sdk

/// Golden files, found from the test binaries (CI maps `__SOURCE_DIRECTORY__` to `/_/`).
module Golden =
    let private root =
        let rec up (dir: DirectoryInfo | null) =
            match dir with
            | null -> failwith $"no Fx.slnx above {AppContext.BaseDirectory}"
            | dir when File.Exists(Path.Combine(dir.FullName, "Fx.slnx")) -> dir.FullName
            | dir -> up dir.Parent

        up (DirectoryInfo AppContext.BaseDirectory)

    let path (name: string) =
        Path.Combine(root, "tests", "Fx.Environments.Tests", "golden", name)

    /// Expects `actual` to equal the golden file; `UPDATE_GOLDEN=1` writes a missing one.
    let expect (name: string) (actual: string) =
        let file = path name

        if
            Environment.GetEnvironmentVariable "UPDATE_GOLDEN" = "1"
            && not (File.Exists file)
        then
            Directory.CreateDirectory(Path.GetDirectoryName file |> nonNull) |> ignore
            File.WriteAllText(file, actual)
        else
            Expect.isTrue (File.Exists file) $"missing golden file {name}; run the tests with UPDATE_GOLDEN=1"
            Expect.equal (File.ReadAllText file) actual $"{name}: events are kept forever; add an upcaster instead"

let spec: EnvSpec = EnvSpec.create "dev" "local" |> EnvSpec.withWorkspace "/src/app"

let lease (id: string) : Lease =
    {
        Id = id
        Provider = "local"
        Workspace = "/src/app"
        Home = "/home/u/.fx/environments/" + id
        Data = Map.empty
    }

let machine: EnvMachine =
    {
        Target = TargetTriple.create Os.Linux Arch.X64 (Some Libc.Glibc)
        DataDir = "/home/u/.fx"
        SameHost = true
    }

let platform: Platform =
    {
        Os = "linux"
        Arch = "x64"
        Libc = Some "glibc"
        Shell = ShellSpec.posix
        PathSeparator = "/"
        CaseSensitive = true
        Workspace = "/src/app"
        Version = "0.0.0"
        Features = [ Feature.Pty; Feature.Watch; Feature.Snapshots ]
    }

let request (id: string) =
    EnvCommand.Request
        {
            Id = id
            Spec = spec
            At = 1_767_225_600_000_000L
        }

/// The commands that take an environment to ready.
let toReady (id: string) =
    [
        request id
        EnvCommand.Provisioned(id, 1, Ok(lease id))
        EnvCommand.Bootstrapped(id, 1, Ok(TransportKind.InProcess, machine, []))
        EnvCommand.Connected(id, 1, Ok platform)
        EnvCommand.Installed(
            id,
            1,
            Ok
                {
                    Components = [ "bun@1.4.5" ]
                    Sandbox = None
                }
        )
    ]

let run (commands: EnvCommand list) : EnvsState * EnvEvent list =
    Machine.run Lifecycle.machine Lifecycle.initial commands

let after (commands: EnvCommand list) : EnvsState = fst (run commands)

let record (id: string) (state: EnvsState) : EnvRecord = state.Environments[id]

let handle (state: EnvsState) (command: EnvCommand) = Lifecycle.handle state command

let rejected (code: string) (result: Result<EnvEvent list, Rejection>) =
    match result with
    | Error rejection -> Expect.equal rejection.Code code "rejection"
    | Ok events -> failtest $"expected {code}, got {events}"

/// A scripted history with every kind of event, as version 1 writes it.
let history: EnvEvent list =
    let _, events =
        run (
            toReady "a"
            @ [
                EnvCommand.Disconnected("a", 1, "the connection to envd ended (peer)")
                EnvCommand.Attach "a"
                EnvCommand.Bootstrapped("a", 2, Ok(TransportKind.Piped, machine, []))
                EnvCommand.Connected("a", 2, Ok platform)
                EnvCommand.Installed("a", 2, Ok { Components = []; Sandbox = None })
                EnvCommand.Suspend "a"
                EnvCommand.Suspended("a", Ok())
                EnvCommand.Resume "a"
                EnvCommand.Resumed("a", Ok())
                request "b"
                EnvCommand.Provisioned("b", 1, Error "the workspace /x does not exist")
                request "c"
                EnvCommand.Provisioned("c", 1, Ok(lease "c"))
                EnvCommand.Reconciled([], [ "c", "the local provider no longer has it" ])
                EnvCommand.Destroy "a"
                EnvCommand.Destroyed("a", Ok())
            ]
        )

    events

[<Tests>]
let tests =
    testList
        "lifecycle"
        [
            test "an environment goes from requested to ready, one step at a time" {
                let state, events = run (toReady "a")

                Expect.equal
                    (events
                     |> List.map (fun e -> (Lifecycle.event.Encode e |> Json.tryField "kind").Value))
                    ([ "requested"; "provisioned"; "bootstrapped"; "connected"; "readied" ]
                     |> List.map Json.String)
                    "events"

                let r = record "a" state
                Expect.equal (r.Phase, r.Step, r.Attempt) (Phase.Ready, None, 1) "ready"
                Expect.equal r.Lease (Some(lease "a")) "lease"
                Expect.equal r.Platform (Some platform) "platform"
                Expect.equal r.Components [ "bun@1.4.5" ] "components"
            }

            test "each event asks for the next step" {
                let intents (commands: EnvCommand list) (next: EnvCommand) =
                    let state = after commands

                    match Machine.step Lifecycle.machine state next with
                    | Ok step -> step.Intents |> List.map string
                    | Error rejection -> failtest (string rejection)

                let steps = toReady "a"
                Expect.equal (intents [] steps[0]) [ "provision a#1" ] "provision"
                Expect.equal (intents steps[..0] steps[1]) [ "bootstrap a#1" ] "bootstrap"
                Expect.equal (intents steps[..1] steps[2]) [ "connect a#1" ] "connect"
                Expect.equal (intents steps[..2] steps[3]) [ "install a#1" ] "install"
                Expect.equal (intents steps[..3] steps[4]) [] "ready: nothing"

                Expect.equal
                    (intents steps (EnvCommand.Disconnected("a", 1, "gone")))
                    [ "release a up to #1" ]
                    "a detached environment releases its connection"

                Expect.equal
                    (intents (steps @ [ EnvCommand.Disconnected("a", 1, "gone") ]) (EnvCommand.Attach "a"))
                    [ "bootstrap a#2" ]
                    "attaching: a new attempt"

                Expect.equal (intents steps (EnvCommand.Destroy "a")) [ "destroy a" ] "destroy"
            }

            test "results of another attempt or step are stale" {
                let ready = after (toReady "a")

                rejected
                    "environment.stale"
                    (handle ready (EnvCommand.Bootstrapped("a", 1, Ok(TransportKind.Piped, machine, []))))

                rejected "environment.stale" (handle ready (EnvCommand.Disconnected("a", 7, "old")))

                let reattaching =
                    after (toReady "a" @ [ EnvCommand.Disconnected("a", 1, "gone"); EnvCommand.Attach "a" ])

                Expect.equal (record "a" reattaching).Attempt 2 "a new attempt"

                rejected
                    "environment.stale"
                    (handle reattaching (EnvCommand.Bootstrapped("a", 1, Ok(TransportKind.Piped, machine, []))))

                Expect.isOk
                    (handle reattaching (EnvCommand.Bootstrapped("a", 2, Ok(TransportKind.Piped, machine, []))))
                    "current"

                rejected "environment.unknown" (handle reattaching (EnvCommand.Attach "zzz"))
                rejected "environment.exists" (handle reattaching (request "a"))
            }

            test "a failed step fails the environment; destroying it is allowed" {
                let state =
                    after [ request "a"; EnvCommand.Provisioned("a", 1, Error "no workspace") ]

                let r = record "a" state
                Expect.equal (r.Phase, r.Reason) (Phase.Failed, Some "no workspace") "failed"
                rejected "environment.state" (handle state (EnvCommand.Attach "a"))

                let destroyed =
                    after
                        [
                            request "a"
                            EnvCommand.Provisioned("a", 1, Error "no workspace")
                            EnvCommand.Destroy "a"
                            EnvCommand.Destroyed("a", Ok())
                        ]

                Expect.equal (record "a" destroyed).Phase Phase.Destroyed "destroyed"
            }

            test "destroying is idempotent, and wins over a step in flight" {
                let destroying = after [ request "a"; EnvCommand.Destroy "a" ]
                Expect.equal (record "a" destroying).Step (Some Step.Destroy) "destroying while provisioning"
                rejected "environment.stale" (handle destroying (EnvCommand.Provisioned("a", 1, Ok(lease "a"))))
                Expect.equal (handle destroying (EnvCommand.Destroy "a")) (Ok []) "again: nothing"

                let destroyed =
                    after [ request "a"; EnvCommand.Destroy "a"; EnvCommand.Destroyed("a", Ok()) ]

                Expect.equal (handle destroyed (EnvCommand.Destroy "a")) (Ok []) "destroyed: nothing"
            }

            test "suspend and resume follow the provider; a failure keeps the phase" {
                let ready = toReady "a"
                rejected "environment.busy" (handle (after [ request "a" ]) (EnvCommand.Suspend "a"))

                let suspended =
                    after (ready @ [ EnvCommand.Suspend "a"; EnvCommand.Suspended("a", Ok()) ])

                Expect.equal (record "a" suspended).Phase Phase.Suspended "suspended"
                rejected "environment.state" (handle suspended (EnvCommand.Attach "a"))

                let failed =
                    after (ready @ [ EnvCommand.Suspend "a"; EnvCommand.Suspended("a", Error "busy") ])

                Expect.equal
                    ((record "a" failed).Phase, (record "a" failed).Reason)
                    (Phase.Ready, Some "busy")
                    "still ready"

                let resumed =
                    after (
                        ready
                        @ [
                            EnvCommand.Suspend "a"
                            EnvCommand.Suspended("a", Ok())
                            EnvCommand.Resume "a"
                            EnvCommand.Resumed("a", Ok())
                        ]
                    )

                Expect.equal
                    ((record "a" resumed).Phase, (record "a" resumed).Step)
                    (Phase.Provisioning, None)
                    "resumed: to be attached"
            }

            test "a restart recovers steps in flight and reconciles leases" {
                let state =
                    after (
                        toReady "ready"
                        @ [
                            request "creating"
                            request "going"
                            EnvCommand.Provisioned("going", 1, Ok(lease "going"))
                            EnvCommand.Destroy "going"
                        ]
                        @ [
                            request "attaching"
                            EnvCommand.Provisioned("attaching", 1, Ok(lease "attaching"))
                        ]
                    )

                Expect.equal
                    (Lifecycle.recover state |> List.map string)
                    [ "provision creating#1"; "destroy going"; "reconcile 2 leases" ]
                    "recovered"

                match Lifecycle.recover state |> List.last with
                | EnvIntent.Reconcile leases ->
                    Expect.equal
                        (leases |> List.map (fun (id, attempt, _, _) -> id, attempt))
                        [ "ready", 1; "attaching", 1 ]
                        "leases, with their attempts"
                | other -> failtest (string other)

                let _, events =
                    Machine.run
                        Lifecycle.machine
                        state
                        [ EnvCommand.Reconciled([ "ready", 1 ], [ "attaching", "gone" ]) ]

                Expect.equal
                    (events |> List.map string)
                    [ $"ready detached: {Lifecycle.Restarted}"; "attaching lost: gone" ]
                    "detached and lost"
            }

            test "an environment attached again before the reconciliation arrives stays attached" {
                // After a restart, a session may open its environment (an attach, attempt 2) before
                // the providers have answered: their answer is about attempt 1.
                let state =
                    after (
                        toReady "ready"
                        @ [
                            EnvCommand.Attach "ready"
                            EnvCommand.Bootstrapped("ready", 2, Ok(TransportKind.Piped, machine, []))
                        ]
                    )

                let attaching, events =
                    Machine.run Lifecycle.machine state [ EnvCommand.Reconciled([ "ready", 1 ], []) ]

                Expect.isEmpty events "not detached"

                Expect.equal
                    ((record "ready" attaching).Attempt, (record "ready" attaching).Phase)
                    (2, Phase.Bootstrapped)
                    "attaching"

                let _, events =
                    Machine.run Lifecycle.machine state [ EnvCommand.Reconciled([ "ready", 2 ], []) ]

                Expect.equal
                    (events |> List.map string)
                    [ $"ready detached: {Lifecycle.Restarted}" ]
                    "the same attempt is"
            }

            test "every event round-trips through its JSON" {
                for event in history do
                    let json = Lifecycle.event.Encode event
                    Expect.equal (Codec.decode Lifecycle.event json) (Ok event) (Json.serialize json)
            }

            test "version 1's events are read as they were written, and replay to the same state" {
                let lines =
                    history
                    |> List.map (fun e -> Json.serializeCanonical (EnvLog.schema.Codec.Encode e))

                Golden.expect "events-v1.jsonl" (String.Join("\n", lines) + "\n")

                let read =
                    File.ReadAllLines(Golden.path "events-v1.jsonl")
                    |> Array.filter (fun line -> line <> "")
                    |> Array.map (fun line ->
                        match Codec.decodeText EnvLog.schema.Codec line with
                        | Ok event -> event
                        | Error issues -> failtest (Issue.renderAtMost 5 issues))
                    |> List.ofArray

                Expect.equal (Machine.replay Lifecycle.machine read) (Machine.replay Lifecycle.machine history) "replay"

                let summary =
                    Machine.replay Lifecycle.machine read |> Lifecycle.records |> List.map string

                Expect.equal summary [ "a dev: destroyed"; "b dev: failed"; "c dev: lost" ] "the replayed state"
            }
        ]
