/// The extension system (Fx.Sdk, Fx.Extensions): manifests, the hook algebra, the registry's fold, and
/// the sample extension in process, behind the process runtime in memory, and in a child process
/// (`ext-peer`). The three transcripts must be identical, and identical on the JIT and natively.
module Fx.Aot.Scenarios.Extensions.Extensions

open System
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Extensions
open Fx.Machines
open Fx.Sdk
open Fx.Ext.Sample
open Fx.Aot.Scenarios

let private manifest () : string list =
    let sample = Sample.manifest
    let decoded = Manifest.parse (Manifest.serialize sample)

    let invalid =
        """{"id": "Acme", "version": "1.0", "sdk": "0.1", "protocol": "1", "runtime": "process",
            "contributions": [{"type": "gate:x", "id": "a", "value": 1}, {"type": "gate:x", "id": "a"}],
            "launch": {"exe": "x", "envSet": [["NODE_OPTIONS", "--require=evil"]]}, "colour": "red"}"""

    let checkedOnly =
        """{"id": "acme.tools", "version": "1.0.0", "sdk": "0.1", "protocol": "1", "runtime": "process",
            "contributions": [{"type": "gate:x", "id": "a", "value": 1}, {"type": "gate:x", "id": "a"}],
            "launch": {"exe": "x", "envSet": {"NODE_OPTIONS": "--require=evil"}}, "hash": "sha256:00"}"""

    [
        "hash = " + Manifest.hash sample
        "round trip = " + string (decoded = Ok sample)
        yield! (Manifest.serialize sample).Split('\n') |> Array.truncate 12
        yield!
            match Manifest.parse invalid with
            | Ok _ -> [ "invalid: decoded" ]
            | Error issues -> issues |> List.map (fun i -> "invalid: " + string i)
        yield!
            match Manifest.parse checkedOnly with
            | Ok _ -> [ "checked: decoded" ]
            | Error issues -> issues |> List.map (fun i -> "checked: " + string i)
    ]

let private env (id: string) : IExtensionEnv =
    ({
        Extension = id
        Clock = SystemClock.clock
        Capabilities = ExtensionCapabilities.none id
        Scope = Scope.root
    }
    : BasicExtensionEnv)
    :> IExtensionEnv

let private contributed (id: string) (value: 'T) : Contributed<'T> =
    {
        Extension = "x"
        Type = "t"
        Id = id
        Value = value
        Env = env "x"
    }

type private Env =
    {
        Scope: Scope
    }

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { Scope = scope }

    override env.ToString() = "Env"

let private hooks () : Task<string list> =
    task {
        let run (effect: Eff<Env, 'T>) =
            SampleHost.within (Eff.run ({ Scope = Scope.root }: Env) effect)

        let ask = Verdict.ofDecision "a" (Decision.Ask "sure?")
        let deny = Verdict.ofDecision "b" (Decision.Deny "no")
        let deny2 = Verdict.ofDecision "c" (Decision.Deny "never")

        let gate (decision: Decision) : Gate<int> = { Check = fun _ -> Eff.ret decision }

        let broken: Gate<int> = { Check = fun _ -> invalidOp "broken" }

        let! all =
            run (
                Hooks.decide
                    [
                        contributed "allow" (gate Decision.Allow)
                        contributed "ask" (gate (Decision.Ask "why?"))
                        contributed "broken" broken
                    ]
                    1
            )

        let! none = run (Hooks.decide [] 1)

        let transforms =
            [
                contributed "late" (Transform.ofFunction 5 (fun s -> s + "-late"))
                contributed "early" (Transform.ofFunction 1 (fun s -> s + "-early"))
                contributed "tie" (Transform.ofFunction 5 (fun s -> s + "-tie"))
            ]

        let! transformed = run (Hooks.transform transforms "x")

        let observers: Contributed<Observer<string>> list =
            [
                contributed "ok" { Observe = fun _ -> Eff.unit }
                contributed "bad" { Observe = fun _ -> invalidOp "bad" }
            ]

        let! failures = run (Hooks.observe observers "event")

        let log = Collections.Generic.List<string>()

        let middleware name priority : Middleware<string -> string> =
            {
                Priority = priority
                Wrap =
                    fun inner input ->
                        log.Add name
                        inner (input + name)
            }

        let wrapped =
            Hooks.interpret
                [
                    contributed "inner" (middleware "inner" 9)
                    contributed "outer" (middleware "outer" 1)
                ]
                (fun input -> input + "!")

        let interpreted = wrapped ">"

        return
            [
                $"ask+deny = {Verdict.combine ask deny}"
                $"deny+ask = {Verdict.combine deny ask}"
                $"deny+deny2 = {Verdict.combine deny deny2}"
                $"deny2+deny = {Verdict.combine deny2 deny}"
                $"allow+allow = {Verdict.combine Verdict.Allow Verdict.Allow}"
                $"decide = {all}"
                $"decide none = {none}"
                $"transform = {transformed}"
                $"""observe failures = {String.Join(", ", failures |> List.map string)}"""
                $"""interpret = {interpreted} via {String.Join(" ", log)}"""
            ]
    }

let private registry () : string list =
    let request (id: string) (source: Source) (contributions: (string * string * bool) list) : AdmitRequest =
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
                contributions
                |> List.map (fun (kind, cid, privileged) ->
                    {
                        Key = ({ Type = kind; Id = cid }: ContributionKey)
                        Known = kind <> "unknown"
                        Privileged = privileged
                    })
            Capabilities = []
            Methods = []
            Replaces = []
            Pin = None
            Trust = SampleHost.trust
        }

    let commands =
        [
            RegistryCommand.Admit(request "a" Source.Builtin [ "word", "one", false ])
            RegistryCommand.Activate("a", [ ({ Type = "word"; Id = "one" }: ContributionKey) ], [])
            RegistryCommand.Admit(request "b" Source.HostConfig [ "word", "one", false ])
            RegistryCommand.Admit(request "c" Source.Project [ "word", "two", false ])
            RegistryCommand.Admit(
                { request "c" Source.Project [ "runtime", "x", true ] with
                    Trust =
                        { SampleHost.trust with
                            ProjectTrusted = true
                        }
                }
            )
            RegistryCommand.Admit(request "d" Source.Policy [ "unknown", "u", false ])
            RegistryCommand.Admit(request "d" Source.Policy [ "word", "two", false ])
            RegistryCommand.Activate("d", [ ({ Type = "word"; Id = "three" }: ContributionKey) ], [])
            RegistryCommand.Fail("d", "mismatch")
            RegistryCommand.Admit(
                { request "e" Source.HostConfig [ "word", "one", false ] with
                    Replaces = [ "a" ]
                }
            )
            RegistryCommand.Disable("e", "user")
            RegistryCommand.Admit(
                { request "f" Source.HostConfig [] with
                    Pin = Some "sha256:other"
                }
            )
        ]

    let mutable state = Registry.empty
    let events = ResizeArray<RegistryEvent>()

    [
        for command in commands do
            match Machine.step Registry.machine state command with
            | Ok step ->
                state <- step.State
                events.AddRange step.Events
                let shown = String.Join("; ", step.Events |> List.map string)
                $"{command} -> {shown}"
            | Error rejection -> $"{command} -> rejected {rejection.Code}"
        let replayed = Registry.replay (List.ofSeq events)
        $"replay = {replayed = state}"
        $"""active = {String.Join(", ", Registry.active state)}"""
    ]

let private transcript (load: unit -> Task<SampleHost.Loaded>) () : Task<string list> =
    task {
        let! loaded = load ()

        try
            return! SampleHost.transcript loaded
        finally
            (loaded.Dispose()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
    }

let scenarios: Scenario list =
    [
        Scenario.lines "extensions-manifest" manifest
        Scenario.task "extensions-hooks" hooks
        Scenario.lines "extensions-registry" registry
        Scenario.task "extensions-native" (transcript SampleHost.native)
        Scenario.task "extensions-process-memory" (transcript SampleHost.inMemoryProcess)
        Scenario.task "extensions-process-child" (transcript SampleHost.childProcess)
    ]
