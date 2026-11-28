/// The registry machine: admission and trust decisions, activation checks, and its state as a fold
/// of its events (replay).
module Fx.Extensions.Tests.RegistryTests

open Expecto
open Fx.Machines
open Fx.Sdk
open Fx.Extensions

let trust: TrustContext =
    {
        ProjectTrusted = false
        RequirePins = false
        Placement = "host"
        Sdk = Versions.Sdk
        Protocols = [ Versions.Protocol ]
    }

let key (kind: string) (id: string) : ContributionKey = { Type = kind; Id = id }

let request (id: string) (source: Source) (keys: ContributionKey list) : AdmitRequest =
    {
        Id = id
        Version = "1.0.0"
        Hash = "sha256:" + id
        Runtime = Runtimes.Native
        Placement = "host"
        Sdk = Versions.Sdk
        Protocol = Versions.Protocol
        Source = source
        Contributions =
            keys
            |> List.map (fun k ->
                {
                    Key = k
                    Known = true
                    Privileged = false
                })
        Capabilities = []
        Methods = []
        Replaces = []
        Pin = None
        Trust = trust
    }

/// Runs commands from the empty registry; the state, the events, and each command's outcome.
let runAll (commands: RegistryCommand list) =
    let mutable state = Registry.empty
    let events = ResizeArray<RegistryEvent>()

    let outcomes =
        [
            for command in commands do
                match Machine.step Registry.machine state command with
                | Ok step ->
                    state <- step.State
                    events.AddRange step.Events
                    Ok step.Events
                | Error rejection -> Error rejection.Code
        ]

    state, List.ofSeq events, outcomes

let private lastOutcome (commands: RegistryCommand list) =
    let _, _, outcomes = runAll commands
    List.last outcomes

let private rejected (code: string) (commands: RegistryCommand list) =
    Expect.equal (lastOutcome commands) (Error code) code

let private admitted (id: string) (keys: ContributionKey list) =
    [
        RegistryCommand.Admit(request id Source.Builtin keys)
        RegistryCommand.Activate(id, keys, [])
    ]

[<Tests>]
let tests =
    testList
        "Registry"
        [
            test "admit, then activate with exactly the declared contributions" {
                let state, events, _ = runAll (admitted "a" [ key "word" "one" ])
                Expect.equal (Registry.active state) [ "a" ] "active"

                Expect.equal
                    (events |> List.map string)
                    [ "admitted a 1.0.0 (native, builtin)"; "activated a" ]
                    "events"

                Expect.equal state.Owners (Map.ofList [ key "word" "one", "a" ]) "owners"
            }

            test "an activation must provide what the manifest declares, methods included" {
                let admit =
                    RegistryCommand.Admit(
                        { request "a" Source.Builtin [ key "word" "one" ] with
                            Methods = [ "echo" ]
                        }
                    )

                rejected "extension.mismatch" [ admit; RegistryCommand.Activate("a", [ key "word" "two" ], [ "echo" ]) ]
                rejected "extension.mismatch" [ admit; RegistryCommand.Activate("a", [ key "word" "one" ], []) ]

                rejected
                    "extension.mismatch"
                    [
                        admit
                        RegistryCommand.Activate("a", [ key "word" "one"; key "word" "one" ], [ "echo" ])
                    ]

                rejected "extension.not-admitted" [ RegistryCommand.Activate("x", [], []) ]
                rejected "extension.not-admitted" (admitted "a" [] @ [ RegistryCommand.Activate("a", [], []) ])

                match Machine.step Registry.machine Registry.empty (RegistryCommand.Fail("x", "no")) with
                | Error rejection -> Expect.equal rejection.Code "extension.not-admitted" "fail"
                | Ok _ -> failtest "fail of nothing"
            }

            test "a failed activation releases its contributions" {
                let state, _, _ =
                    runAll
                        [
                            RegistryCommand.Admit(request "a" Source.Builtin [ key "word" "one" ])
                            RegistryCommand.Fail("a", "crashed")
                            yield! admitted "b" [ key "word" "one" ]
                        ]

                Expect.equal (Registry.active state) [ "b" ] "b holds it"
                Expect.equal state.Entries["a"].Status (Status.Failed "crashed") "a failed"
            }

            test "duplicates and conflicts are refused" {
                rejected
                    "extension.duplicate"
                    (admitted "a" [] @ [ RegistryCommand.Admit(request "a" Source.Builtin []) ])

                rejected
                    "contribution.conflict"
                    (admitted "a" [ key "word" "one" ]
                     @ [ RegistryCommand.Admit(request "b" Source.Builtin [ key "word" "one" ]) ])

                // Same id, another type: no conflict.
                Expect.isOk
                    (lastOutcome (
                        admitted "a" [ key "word" "one" ]
                        @ [ RegistryCommand.Admit(request "b" Source.Builtin [ key "other" "one" ]) ]
                    ))
                    "keys are type and id"
            }

            test "nothing project-local is admitted before the project is trusted" {
                rejected "trust.untrusted-project" [ RegistryCommand.Admit(request "p" Source.Project []) ]

                let trusted =
                    { request "p" Source.Project [] with
                        Trust = { trust with ProjectTrusted = true }
                    }

                Expect.isOk (lastOutcome [ RegistryCommand.Admit trusted ]) "trusted"
            }

            test "privileged types come only from the binary, host config or policy" {
                let privileged (source: Source) =
                    { request "p" source [] with
                        Contributions =
                            [
                                {
                                    Key = key "extension-runtime" "bun"
                                    Known = true
                                    Privileged = true
                                }
                            ]
                        Trust = { trust with ProjectTrusted = true }
                    }

                rejected "trust.privileged" [ RegistryCommand.Admit(privileged Source.Project) ]

                for source in [ Source.Builtin; Source.HostConfig; Source.Policy ] do
                    Expect.isOk (lastOutcome [ RegistryCommand.Admit(privileged source) ]) (string source)
            }

            test "pins: a pinned hash must match; policy can require pins outside the binary" {
                let pinned pin =
                    { request "h" Source.HostConfig [] with
                        Pin = pin
                    }

                rejected "trust.hash-mismatch" [ RegistryCommand.Admit(pinned (Some "sha256:other")) ]
                Expect.isOk (lastOutcome [ RegistryCommand.Admit(pinned (Some "sha256:h")) ]) "matching pin"

                let strict = { trust with RequirePins = true }

                rejected "trust.unpinned" [ RegistryCommand.Admit({ pinned None with Trust = strict }) ]

                Expect.isOk
                    (lastOutcome
                        [
                            RegistryCommand.Admit(
                                { request "b" Source.Builtin [] with
                                    Trust = strict
                                }
                            )
                        ])
                    "built-ins come with the binary"
            }

            test "versions, placement and unknown types" {
                rejected
                    "extension.sdk"
                    [
                        RegistryCommand.Admit(
                            { request "a" Source.Builtin [] with
                                Sdk = "9.0"
                            }
                        )
                    ]

                rejected
                    "extension.protocol"
                    [
                        RegistryCommand.Admit(
                            { request "a" Source.Builtin [] with
                                Protocol = "9"
                            }
                        )
                    ]

                rejected
                    "extension.placement"
                    [
                        RegistryCommand.Admit(
                            { request "a" Source.Builtin [] with
                                Placement = "environment"
                            }
                        )
                    ]

                let unknown =
                    { request "a" Source.Builtin [] with
                        Contributions =
                            [
                                {
                                    Key = key "nope" "x"
                                    Known = false
                                    Privileged = false
                                }
                            ]
                    }

                rejected "contribution.unknown-type" [ RegistryCommand.Admit unknown ]

                let unknownCapability =
                    { request "a" Source.Builtin [] with
                        Capabilities = [ { Name = "variants"; Known = true }; { Name = "fs.read"; Known = false } ]
                    }

                rejected "extension.capability" [ RegistryCommand.Admit unknownCapability ]

                let knownCapabilities =
                    { request "a" Source.Builtin [] with
                        Capabilities = [ { Name = "variants"; Known = true } ]
                    }

                let _, events, _ = runAll [ RegistryCommand.Admit knownCapabilities ]
                Expect.equal events.Length 1 "known capabilities are admitted"
            }

            test "replacing disables the replaced extension and takes over its contributions" {
                let replacing =
                    { request "fork" Source.HostConfig [ key "word" "one" ] with
                        Replaces = [ "a" ]
                    }

                let state, events, _ =
                    runAll (admitted "a" [ key "word" "one" ] @ [ RegistryCommand.Admit replacing ])

                Expect.equal
                    (events |> List.skip 2 |> List.map string)
                    [ "disabled a (replaced by fork)"; "admitted fork 1.0.0 (native, host-config)" ]
                    "events"

                Expect.equal state.Owners[key "word" "one"] "fork" "owner"

                // The replaced extension stays out.
                rejected
                    "extension.replaced"
                    (admitted "a" [ key "word" "one" ]
                     @ [
                         RegistryCommand.Admit replacing
                         RegistryCommand.Admit(request "a" Source.Builtin [])
                     ])
            }

            test "policy extensions cannot be disabled or replaced; others only by sources trusted as much" {
                let policy = RegistryCommand.Admit(request "pol" Source.Policy [])
                rejected "trust.policy" [ policy; RegistryCommand.Disable("pol", "no") ]

                rejected
                    "trust.replace"
                    [
                        policy
                        RegistryCommand.Admit(
                            { request "x" Source.HostConfig [] with
                                Replaces = [ "pol" ]
                            }
                        )
                    ]

                let project = { trust with ProjectTrusted = true }

                rejected
                    "trust.replace"
                    [
                        RegistryCommand.Admit(request "b" Source.Builtin [])
                        RegistryCommand.Admit(
                            { request "p" Source.Project [] with
                                Replaces = [ "b" ]
                                Trust = project
                            }
                        )
                    ]

                Expect.isOk
                    (lastOutcome
                        [
                            RegistryCommand.Admit(request "b" Source.Builtin [])
                            RegistryCommand.Admit(
                                { request "h" Source.HostConfig [] with
                                    Replaces = [ "b" ]
                                }
                            )
                        ])
                    "host config overrides built-ins"
            }

            test "disabling releases the contributions; only admitted or active extensions" {
                let state, _, outcomes =
                    runAll (
                        admitted "a" [ key "word" "one" ]
                        @ [ RegistryCommand.Disable("a", "user"); RegistryCommand.Disable("a", "again") ]
                    )

                Expect.equal (List.last outcomes) (Error "extension.not-enabled") "twice"
                Expect.equal (Registry.active state) [] "inactive"
                Expect.isEmpty state.Owners "released"
            }

            testProperty
                "the registry is the fold of its events, and its invariants hold"
                (fun (inputs: (byte * byte * byte) list) ->
                    let ids = [| "a"; "b"; "c"; "d" |]
                    let keys = [| key "word" "one"; key "word" "two"; key "runtime" "x" |]

                    let command (op: byte, a: byte, b: byte) =
                        let id = ids[int a % ids.Length]

                        let ks =
                            [ keys[int b % keys.Length]; keys[int (b / 7uy) % keys.Length] ]
                            |> List.distinct

                        match op % 6uy with
                        | 0uy
                        | 1uy ->
                            RegistryCommand.Admit(
                                { request id (if a % 5uy = 0uy then Source.Policy else Source.Builtin) ks with
                                    Replaces =
                                        (if b % 4uy = 0uy then
                                             [ ids[int b % ids.Length] ] |> List.filter ((<>) id)
                                         else
                                             [])
                                }
                            )
                        | 2uy
                        | 3uy -> RegistryCommand.Activate(id, ks, [])
                        | 4uy -> RegistryCommand.Fail(id, "failed")
                        | _ -> RegistryCommand.Disable(id, "user")

                    let state, events, _ = runAll (inputs |> List.map command)
                    let replayed = Registry.replay events

                    // Owners are exactly the contributions of admitted or active extensions, one owner each.
                    let held =
                        state.Entries
                        |> Map.toList
                        |> List.collect (fun (id, entry) ->
                            match entry.Status with
                            | Status.Admitted
                            | Status.Active -> entry.Admission.Contributions |> List.map (fun k -> k, id)
                            | _ -> [])

                    replayed = state
                    && Map.ofList held = state.Owners
                    && List.length held = state.Owners.Count
                    && List.sort state.Order = List.sort (state.Entries |> Map.toList |> List.map fst))
        ]
