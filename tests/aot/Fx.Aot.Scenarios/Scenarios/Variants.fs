/// Fx.Variants (T106, docs/modules/variants.md): resolving slots from config layers under policies,
/// issues, profiles and their ids, eval matrices, and reading slots through the capability with
/// recording and replay.
module Fx.Aot.Scenarios.Variants

open System
open System.Threading.Tasks
open Fx.Aot.Scenarios.Show
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Replay
open Fx.Variants

type Speed =
    | Slow
    | Fast

    override this.ToString() =
        match this with
        | Slow -> "slow"
        | Fast -> "fast"

let private speed: Slot<Speed> =
    Slot.enum "demo.speed" "How fast." [ Slow, "slow"; Fast, "fast" ] Slow
    |> Slot.ownedBy "acme.demo"

let private budget: Slot<decimal> =
    Slot.create "demo.budget" "A budget in dollars." (Codec.decimal |> Codec.between 0m 100m) 2.50m

let private isolated: Slot<bool> =
    Slot.create "demo.isolated" "Whether to isolate." Codec.bool true
    |> Slot.privileged

let private registry =
    SlotRegistry.ofSlots (Slots.all @ [ Slot.info speed; Slot.info budget; Slot.info isolated ])

let private parse (text: string) : Json =
    match Json.parse text with
    | Ok json -> json
    | Error error -> failwith (string error)

let private config (layer: Layer) (origin: string) (text: string) : Selections =
    match Selections.ofConfig (Source.create layer origin) (parse text) with
    | Ok selections -> selections
    | Error issues -> failwith (Issue.render issues)

let private policy (origin: string) (text: string) : Policy =
    match Policy.ofJson origin (parse text) with
    | Ok policy -> policy
    | Error issues -> failwith (Issue.render issues)

let private argument (text: string) : string * Json =
    match Selections.parseArgument text with
    | Ok selection -> selection
    | Error reason -> failwith reason

let private host =
    config
        Layer.Host
        "host.json"
        """{"theme": "dark", "variants": {"edit.guard": "require", "demo.budget": 7.50, "demo.isolated": false, "model.role.main": "anthropic/claude-opus"}}"""

let private project =
    config Layer.Project "project.json" """{"variants": {"agent.actionSurface": "hybrid", "demo.speed": "fast"}}"""

let private org =
    policy "org.json" """{"pin": {"shell.windows": "pwsh"}, "forbid": {"goal.context": ["continue"]}}"""

let private resolve (layers: Selections list) : Resolution =
    match Resolution.resolve registry [ org ] layers with
    | Ok resolution -> resolution
    | Error issues -> failwith (VariantIssue.render issues)

type private Env =
    {
        Variants: Variants
        Scope: Scope
    }

    interface IVariants with
        member env.Variants = env.Variants

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "Env"

let private run (variants: Variants) (effect: Eff<Env, 'T>) : 'T =
    (Eff.run
        {
            Variants = variants
            Scope = Scope.root
        }
        effect)
        .WaitAsync(TimeSpan.FromSeconds 30.0)
        .GetAwaiter()
        .GetResult()

let private outcome (variants: Variants) (effect: Eff<Env, 'T>) : string =
    try
        string (run variants effect)
    with e ->
        e.GetType().Name

let scenarios: Scenario list =
    [
        Scenario.lines "variants-resolve" (fun () ->
            let explicit =
                Selections.create
                    (Source.create Layer.Explicit "--variant")
                    [ argument "demo.speed=slow"; argument "values.inlineMaxBytes=4096" ]

            let session =
                Selections.create
                    (Source.create Layer.Session "/surface")
                    [ argument "agent.actionSurface=cells-only" ]

            let resolution = resolve [ explicit; host; session; project ]

            let bad =
                Resolution.resolve
                    registry
                    [
                        org
                        policy
                            "lab.json"
                            """{"pin": {"shell.windows": "powershell"}, "forbid": {"demo.speed": ["fast"]}}"""
                    ]
                    [
                        config
                            Layer.Project
                            "project.json"
                            """{"variants": {"edit.gaurd": "off", "demo.isolated": false, "demo.budget": 101, "goal.context": "continue"}}"""
                        config
                            Layer.Session
                            "/x"
                            """{"variants": {"demo.speed": "fast", "values.previewLimits": {"maxChars": -1}}}"""
                    ]

            [
                yield! Resolution.explain resolution |> List.map escape
                kv "value speed" (string (Resolution.value speed resolution))
                kv "value budget" (string (Resolution.value budget resolution))
                kv "value limits" (string (Resolution.value Slots.previewLimits resolution))
                match bad with
                | Ok _ -> "bad: resolved"
                | Error issues ->
                    yield! issues |> List.map (fun issue -> kv "issue" (string issue))

                    yield!
                        issues
                        |> List.collect VariantIssue.toIssues
                        |> List.map (fun issue -> kv "model issue" (string issue))
                kv "config schema" (Json.serialize (Schema.toJsonSchema (SlotRegistry.configSchema registry)))
            ])

        Scenario.lines "variants-profile" (fun () ->
            let profile =
                resolve [ host; project ]
                |> Profile.ofResolution "0.1.0"
                |> Profile.withExtension "acme.demo" { Version = "1.0.0"; Hash = None }
                |> Profile.withExtension
                    "acme.tools"
                    {
                        Version = "2.1.0"
                        Hash = Some("sha256:" + String('c', 64))
                    }
                |> Profile.withConformance "acme.interpreter" "kernel" { Passed = 41; Failed = 0; Skipped = 1 }
                |> Profile.withConformance "acme.interpreter" "previews" { Passed = 9; Failed = 1; Skipped = 0 }
                |> Profile.withLockfile ("sha256:" + String('d', 64))

            let reordered =
                resolve [ project; host ]
                |> Profile.ofResolution "0.1.0"
                |> Profile.withLockfile ("sha256:" + String('d', 64))
                |> Profile.withConformance "acme.interpreter" "previews" { Passed = 9; Failed = 1; Skipped = 0 }
                |> Profile.withExtension
                    "acme.tools"
                    {
                        Version = "2.1.0"
                        Hash = Some("sha256:" + String('c', 64))
                    }
                |> Profile.withConformance "acme.interpreter" "kernel" { Passed = 41; Failed = 0; Skipped = 1 }
                |> Profile.withExtension "acme.demo" { Version = "1.0.0"; Hash = None }

            let id = Profile.id profile

            let again =
                { (resolve [ Profile.layer profile ] |> Profile.ofResolution "0.1.0") with
                    Extensions = profile.Extensions
                    Conformance = profile.Conformance
                    Lockfile = profile.Lockfile
                }

            let changed =
                resolve [ host; project; Matrix.expand "m" [ Matrix.axis speed [ Slow ] ] |> List.head ]

            let matrix =
                Matrix.expand
                    "smoke"
                    [
                        (Matrix.choices (Slot.info Slots.actionSurface)).Value
                        Matrix.axis budget [ 1m; 3.5m ]
                    ]

            [
                kv "canonical" (Profile.canonicalText profile)
                kv "id" (string id)
                kv "short" id.Short
                kv "reordered id" (string (Profile.id reordered))
                kv "read back" (string (Profile.ofBytes id (Profile.bytes profile) = Ok profile))
                kv
                    "wrong id"
                    (string (Result.isError (Profile.ofBytes (ProfileId.ofBytes [| 1uy |]) (Profile.bytes profile))))
                kv "layer again" (string (Profile.id again = id))
                yield!
                    Profile.diff profile (Profile.ofResolution "0.2.0" changed)
                    |> List.map (fun d -> kv "diff" (string d))
                yield!
                    matrix
                    |> List.map (fun combination ->
                        let p = resolve [ host; combination ] |> Profile.ofResolution "0.1.0"
                        kv combination.Source.Origin (Profile.id p).Short)
                match Codec.decodeText Profile.codec (Json.serializeIndented (Codec.encode Profile.codec profile)) with
                | Ok back -> kv "decoded id" (string (Profile.id back))
                | Error issues -> kv "decode" (Issue.render issues)
            ])

        Scenario.lines "variants-capability" (fun () ->
            let resolution = resolve [ host; project ]
            let reads = VariantReads()
            let variants = Variants.record reads (Variants.ofResolution resolution)

            let work: Eff<Env, string> =
                eff {
                    let! surface = Variants.get Slots.actionSurface
                    let! s = Variants.get speed
                    let! b, source = Variants.read budget
                    let! all = Eff.all [ for _ in 1..8 -> Variants.get Slots.editGuard ]
                    let! again = Variants.get speed
                    return $"{surface} {s} {b} {source} {all.Length} {List.head all} {again}"
                }

            let first = run variants work
            let recorded = reads.Reads
            let json = Codec.encode Variants.readsCodec recorded

            let decoded =
                match Codec.decode Variants.readsCodec json with
                | Ok back -> back
                | Error issues -> failwith (Issue.render issues)

            let unknown = Slot.create "demo.unknown" "Not registered." Codec.int 0
            let mistyped = Slot.create "demo.speed" "Speed as a number." Codec.int 0

            [
                kv "first" first
                kv "reads" (Json.serialize json)
                kv "replayed" (run (Variants.replay decoded) work)
                kv "replay other slot" (outcome (Variants.replay decoded) (Variants.get Slots.goalContext))
                kv "unregistered" (outcome variants (Variants.get unknown))
                kv "mismatch" (outcome variants (Variants.get mistyped))
                kv "defaults" (outcome (Variants.ofResolution (Resolution.defaults registry)) work)
            ])
    ]
