module Fx.Components.Tests.InstallerTests

open Expecto
open Fx.Components
open Fx.Components.Tests.Support
open Fx.Machines
open Fx.Sdk

let private spec = specOf target (toolComponent "1.2.3" (toolArchive "1.2.3"))
let private key = spec.Key
let private other = specOf target (toolComponent "2.0.0" (toolArchive "2.0.0"))

let private archive: FetchedArchive =
    {
        Path = "/root/.downloads/x"
        Size = 10L
    }

let private info: InstalledInfo =
    {
        Dir = "/root/tool/x"
        Files = 2
        Bytes = 10L
        Reused = false
    }

let private step (state: InstallerState) (command: InstallerCommand) =
    match Machine.step Installer.machine state command with
    | Ok step -> step
    | Error rejection -> failtest $"{command}: {rejection}"

let private rejected (state: InstallerState) (command: InstallerCommand) =
    match Installer.machine.Handle state command with
    | Error rejection -> rejection.Code
    | Ok events -> failtest $"{command} was accepted: {events}"

/// Runs commands from the initial state, checking that each is accepted.
let private run (commands: InstallerCommand list) =
    commands
    |> List.fold
        (fun (state, _) command -> let s = step state command in s.State, s)
        (Installer.initial, Unchecked.defaultof<_>)

[<Tests>]
let tests =
    testList
        "Installer machine"
        [
            test "ensuring starts an attempt: hold, publish the roots, then inspect" {
                let step = step Installer.initial (InstallerCommand.Ensure("h", spec))

                Expect.equal
                    (step.Events |> List.map string)
                    [ $"held by h: [{key}]"; $"requested {spec} for h (attempt 1)" ]
                    "events"

                Expect.equal
                    step.Intents
                    [ InstallerIntent.Publish [ key ]; InstallerIntent.Inspect spec ]
                    "intents, in order"
            }

            test "a missing install asks for approval once per component; approval goes on to fetch" {
                let state, _ =
                    run [ InstallerCommand.Ensure("h", spec); InstallerCommand.Ensure("h", other) ]

                let missing = step state (InstallerCommand.Inspected(key, None))

                Expect.equal
                    missing.Intents
                    [
                        InstallerIntent.Ask
                            {
                                Spec = spec
                                Holder = "h"
                                Attempt = 1
                            }
                    ]
                    "ask, saying who asks and which attempt"

                let state = (step missing.State (InstallerCommand.Inspected(other.Key, None))).State
                let approved = step state (InstallerCommand.Answered(key, Approved "alice"))
                Expect.equal (approved.Events |> List.map string) [ "approved tool by alice" ] "approved"

                Expect.equal
                    (approved.Intents |> List.map string |> List.sort)
                    ([ InstallerIntent.Fetch spec; InstallerIntent.Fetch other ]
                     |> List.map string
                     |> List.sort)
                    "both installs of the tool go on"

                Expect.equal
                    (rejected approved.State (InstallerCommand.Answered(other.Key, Approved "bob")))
                    "installer.stale"
                    "answered already"

                // A later install of the tool needs no approval.
                let third = specOf target (toolComponent "3.0.0" (toolArchive "3.0.0"))
                let state = (step approved.State (InstallerCommand.Ensure("h", third))).State

                Expect.equal
                    (step state (InstallerCommand.Inspected(third.Key, None))).Intents
                    [ InstallerIntent.Fetch third ]
                    "fetch at once"
            }

            test "results for another step are stale, and change nothing" {
                let state, _ = run [ InstallerCommand.Ensure("h", spec) ]

                Expect.equal
                    (rejected state (InstallerCommand.Fetched(key, Ok archive)))
                    "installer.stale"
                    "not fetching"

                Expect.equal (rejected state (InstallerCommand.Built(key, Ok info))) "installer.stale" "not building"

                Expect.equal
                    (rejected state (InstallerCommand.Answered(key, Approved "x")))
                    "installer.stale"
                    "not asking"

                Expect.equal (rejected state (InstallerCommand.Swept(1, []))) "installer.stale" "no prune"
            }

            test "a failed attempt is retried by the next ensure" {
                let state, _ =
                    run
                        [
                            InstallerCommand.Ensure("h", spec)
                            InstallerCommand.Inspected(key, None)
                            InstallerCommand.Answered(key, ApprovalAnswer.Rejected "no")
                        ]

                match state.Installs[key] with
                | Failed(_, 1, InstallStep.AwaitingApproval, Denied "no") -> ()
                | other -> failtest $"{other}"

                let retry = step state (InstallerCommand.Ensure("h", spec))
                Expect.equal (retry.Events |> List.map string) [ $"requested {spec} for h (attempt 2)" ] "attempt 2"
            }

            test "ensuring what is installed or in progress appends nothing" {
                let state, _ =
                    run
                        [
                            InstallerCommand.Ensure("h", spec)
                            InstallerCommand.Inspected(key, Some { info with Reused = true })
                        ]

                Expect.equal (step state (InstallerCommand.Ensure("h", spec))).Events [] "installed"

                Expect.equal
                    (step state (InstallerCommand.Ensure("g", spec))).Events
                    [ InstallerEvent.Held("g", [ key ]) ]
                    "another holder"

                let state, _ = run [ InstallerCommand.Ensure("h", other) ]
                Expect.equal (step state (InstallerCommand.Ensure("h", other))).Events [] "in progress"
                Expect.equal (step state (InstallerCommand.Hold("h", [ other.Key ]))).Events [] "the same holding"
            }

            test "recovery picks every install up from its step, after republishing the roots" {
                let state, _ =
                    run
                        [
                            InstallerCommand.Ensure("h", spec)
                            InstallerCommand.Inspected(key, None)
                            InstallerCommand.Answered(key, Approved "x")
                            InstallerCommand.Fetched(key, Ok archive)
                            InstallerCommand.Ensure("h", other)
                            InstallerCommand.Prune
                        ]

                Expect.equal
                    (Installer.recover state)
                    [
                        InstallerIntent.Publish(List.sort [ key; other.Key ])
                        InstallerIntent.Build(spec, archive)
                        InstallerIntent.Inspect other
                        InstallerIntent.Sweep 1
                    ]
                    "intents"

                Expect.equal (Installer.recover Installer.initial) [] "nothing to do"
            }

            test "the state is the fold of the events (replay)" {
                let commands =
                    [
                        InstallerCommand.Ensure("h", spec)
                        InstallerCommand.Inspected(key, None)
                        InstallerCommand.Answered(key, Approved "x")
                        InstallerCommand.Fetched(key, Ok archive)
                        InstallerCommand.Built(key, Ok info)
                        InstallerCommand.Hold("h", [])
                        InstallerCommand.Prune
                        InstallerCommand.Swept(1, [ $"tool/{key.Dir}" ])
                    ]

                let state, events = Machine.run Installer.machine Installer.initial commands
                Expect.equal (Machine.replay Installer.machine events) state "replayed"
                Expect.isEmpty state.Installs "pruned"
                Expect.isEmpty state.Holders "released"
                Expect.equal state.Approved (set [ "tool" ]) "approval kept"
                Expect.equal events.Length 10 "every command appended"
            }

            test "methods other than archive are refused at once" {
                let system =
                    InstallSpec.create
                        "git"
                        (semver "2.0.0")
                        target
                        (InstallMethod.System { Probe = [ "git" ]; Constraint = "2" })
                        { Executables = [] }

                Expect.equal
                    (rejected Installer.initial (InstallerCommand.Ensure("h", system)))
                    "installer.unsupported-method"
                    "refused"
            }
        ]
