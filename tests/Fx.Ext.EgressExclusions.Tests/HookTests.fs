/// The content exclusions' settings and hooks (Fx.Sdk `ContentExclusions`), run directly.
module Fx.Ext.EgressExclusions.Tests.HookTests

open System
open System.Threading
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Models
open Fx.Sdk
open Fx.Variants
open Fx.Variants.Fakes
open Fx.Aot.Scenarios

/// An extension's environment for hooks.
type ExtEnv =
    {
        Extension: string
        Clock: Clock
        Scope: Scope
    }

    interface IExtensionEnv with
        member env.Extension = env.Extension

    interface IClock with
        member env.Clock = env.Clock

    interface IScopeWith<IExtensionEnv> with
        member env.Scope = env.Scope

        member env.WithScope scope =
            { env with Scope = scope } :> IExtensionEnv

let private env: IExtensionEnv =
    {
        Extension = "test"
        Clock = (FakeClock.Create()).Clock
        Scope = Scope.root
    }

let contributed (extension: string) (id: string) (value: 'T) : Contributed<'T> =
    {
        Extension = extension
        Type = "hook"
        Id = id
        Value = value
        Env =
            { (env :?> ExtEnv) with
                Extension = extension
            }
    }

let private run (effect: Eff<IExtensionEnv, 'T>) : 'T =
    (Eff.run env effect).WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

let private hooks (settings: ExclusionSettings) =
    ContentExclusions.hooks (Eff.ret settings)

let private redact = Egress.settings ExclusionAction.Redact PlaceholderStyle.Plain
let private request = Egress.sessionRequest (ModelRef.create "example" "echo")

let private texts (request: ChatRequest) : string list =
    (request.System @ (request.Messages |> List.collect (fun m -> m.Parts)))
    |> List.collect (fun part ->
        match part with
        | Part.Text t -> [ t.Text ]
        | Part.ToolResult r ->
            r.Content
            |> List.choose (fun c ->
                match c with
                | ToolContent.Text t -> Some t
                | ToolContent.Media _ -> None)
        | _ -> [])

let private resolve (layers: Selections list) (policies: Policy list) : Variants =
    let registry = SlotRegistry.ofSlots ContentExclusions.slots

    match Resolution.resolve registry policies layers with
    | Ok resolution -> Variants.ofResolution resolution
    | Error issues -> failtest (String.Join("\n", issues |> List.map string))

let private layer (layer: Layer) (selections: (string * Json) list) =
    Selections.create (Source.create layer (string layer)) selections

let private read (variants: Variants) : ExclusionSettings =
    run (ContentExclusions.ofVariants variants)

[<Tests>]
let tests =
    testList
        "Content exclusions"
        [
            testList
                "settings"
                [
                    test "every origin's patterns add up; a project cannot touch the host's or policy's" {
                        let variants =
                            resolve
                                [
                                    layer Layer.Host [ Selections.select ContentExclusions.hostPatterns [ "*.env" ] ]
                                    layer
                                        Layer.Project
                                        [ Selections.select ContentExclusions.projectPatterns [ "!*.env"; "tmp/" ] ]
                                ]
                                [
                                    Policy.empty "org.json"
                                    |> Policy.pin ContentExclusions.policyPatterns [ "*.pem" ]
                                ]

                        let settings = read variants

                        Expect.equal
                            settings.Lists
                            [
                                {
                                    Origin = "policy"
                                    Patterns = [ "*.pem" ]
                                }
                                {
                                    Origin = "host"
                                    Patterns = [ "*.env" ]
                                }
                                {
                                    Origin = "project"
                                    Patterns = [ "!*.env"; "tmp/" ]
                                }
                            ]
                            "three lists"

                        match ContentExclusions.compile PathCase.Insensitive settings with
                        | Ok e ->
                            Expect.isSome (Exclusions.test e ".env") "the project's ! cannot re-include the host's"
                            Expect.isSome (Exclusions.test e "k.pem") "policy"
                            Expect.isSome (Exclusions.test e "tmp/x") "project"
                        | Error issues -> failtest (string issues)

                        let registry = SlotRegistry.ofSlots ContentExclusions.slots

                        for slot in [ ContentExclusions.hostPatterns; ContentExclusions.policyPatterns ] do
                            match
                                Resolution.resolve registry [] [ layer Layer.Project [ Selections.select slot [] ] ]
                            with
                            | Ok _ -> failtest $"a project set {slot.Id}"
                            | Error issues -> Expect.stringContains (string issues) "privileged" "refused"

                        match
                            Resolution.resolve
                                registry
                                []
                                [
                                    layer
                                        Layer.Host
                                        [ ContentExclusions.hostPatterns.Id.Value, Json.Array [ Json.String "[bad" ] ]
                                ]
                        with
                        | Ok _ -> failtest "an invalid pattern was accepted"
                        | Error issues -> Expect.stringContains (string issues) "unclosed [" "the pattern's issue"
                    }

                    test "without the slots registered (the extension disabled), nothing is excluded" {
                        let settings = read (FakeVariants.core [])
                        Expect.equal settings ContentExclusions.none "none"
                    }

                    test "placeholder and action are variants" {
                        let settings =
                            read (
                                FakeVariants.create
                                    ContentExclusions.slots
                                    [
                                        Selections.select ContentExclusions.placeholder PlaceholderStyle.WithPath
                                        Selections.select ContentExclusions.action ExclusionAction.Deny
                                    ]
                            )

                        Expect.equal settings.Placeholder PlaceholderStyle.WithPath "placeholder"
                        Expect.equal settings.Action ExclusionAction.Deny "action"
                        Expect.isEmpty settings.Lists "no patterns"
                    }

                    test "case follows the environment's filesystem" {
                        Expect.equal (ContentExclusions.caseOf Fx.Sdk.Os.Linux) PathCase.Sensitive "Linux"
                        Expect.equal (ContentExclusions.caseOf Fx.Sdk.Os.MacOs) PathCase.Insensitive "macOS"
                        Expect.equal (ContentExclusions.caseOf Fx.Sdk.Os.Windows) PathCase.Insensitive "Windows"
                    }
                ]

            testList
                "hooks"
                [
                    test "the transform replaces excluded parts; the gate then allows" {
                        let transform, gate = hooks redact
                        let redacted = run (transform.Apply request)

                        for secret in Egress.secrets |> List.filter (fun s -> s <> "SECRET-SYSTEM-9907") do
                            for text in texts redacted do
                                Expect.isFalse (text.Contains secret) $"{secret} is gone"

                        Expect.contains
                            (texts redacted)
                            "SECRET-SYSTEM-9907 is in an unattributed part: exclusions cannot see it."
                            "parts without sources are left alone"

                        Expect.contains (texts redacted) """{"region": "eu-central"}""" "a re-included file stays"
                        Expect.equal (run (gate.Check redacted)) Decision.Allow "nothing left"
                        Expect.equal transform.Priority ContentExclusions.Priority "runs first"

                        match run (gate.Check request) with
                        | Decision.Deny reason ->
                            Expect.stringContains
                                reason
                                "content of .env, excluded by host (*.env), would leave in system[1]"
                                "names the first"

                            Expect.stringContains reason "(and 2 more)" "and counts the rest"
                        | other -> failtest (string other)
                    }

                    test "with the deny action, the transform changes nothing and the gate denies" {
                        let transform, gate =
                            hooks
                                { redact with
                                    Action = ExclusionAction.Deny
                                }

                        Expect.equal (run (transform.Apply request)) request "unchanged"

                        match run (gate.Check request) with
                        | Decision.Deny reason -> Expect.stringContains reason "excluded by" "why"
                        | other -> failtest (string other)
                    }

                    test "settings that do not compile fail the transform and deny at the gate" {
                        let broken =
                            { redact with
                                Lists =
                                    [
                                        {
                                            Origin = "host"
                                            Patterns = [ "[bad" ]
                                        }
                                    ]
                            }

                        let transform, gate = hooks broken

                        Expect.throwsT<ContentExclusions.InvalidExclusionsException>
                            (fun () -> run (transform.Apply request) |> ignore)
                            "the pipeline fails"

                        match run (gate.Check request) with
                        | Decision.Deny reason -> Expect.stringContains reason "invalid" "why"
                        | other -> failtest (string other)
                    }

                    test "settings are read for each request" {
                        let mutable current = redact

                        let transform, _ =
                            ContentExclusions.hooks (fun _ -> Threading.Tasks.Task.FromResult current)

                        let before = run (transform.Apply request)
                        current <- ContentExclusions.none
                        let after = run (transform.Apply request)
                        Expect.notEqual before request "redacted"
                        Expect.equal after request "nothing excluded any more"
                    }

                    test
                        "other transforms run by priority: before the exclusions they see excluded content, after them placeholders" {
                        let transform, gate = hooks redact
                        let seen = Collections.Concurrent.ConcurrentDictionary<string, bool>()

                        let watcher (name: string) (priority: int) : Transform<ChatRequest> =
                            {
                                Priority = priority
                                Apply =
                                    fun r ->
                                        seen[name] <- texts r |> List.exists (fun t -> t.Contains "SECRET-KEY-0419")
                                        Eff.ret r
                            }

                        // A transform after the exclusions that puts excluded content back, with its source.
                        let reinsert: Transform<ChatRequest> =
                            {
                                Priority = 10
                                Apply =
                                    fun r ->
                                        Eff.ret
                                            { r with
                                                Messages =
                                                    r.Messages
                                                    @ [
                                                        Message.user
                                                            [
                                                                Part.quoting
                                                                    [ ContentSource.file "prod.env" ]
                                                                    "SECRET-ENV-7731"
                                                            ]
                                                    ]
                                            }
                            }

                        let transforms =
                            [
                                contributed "acme.redaction" "late" (watcher "late" 0)
                                contributed Fx.Ext.EgressExclusions.EgressExclusions.id "exclusions" transform
                                contributed "acme.early" "early" (watcher "early" (ContentExclusions.Priority - 1))
                            ]

                        let result = run (Hooks.transform transforms request)
                        Expect.isTrue seen["early"] "the early transform saw the excluded content"
                        Expect.isFalse seen["late"] "the late one saw placeholders"

                        let withReinsert =
                            run (Hooks.transform (transforms @ [ contributed "acme.bad" "reinsert" reinsert ]) request)

                        Expect.equal (run (gate.Check result)) Decision.Allow "clean"

                        match run (gate.Check withReinsert) with
                        | Decision.Deny reason ->
                            Expect.stringContains reason "prod.env" "the gate catches what came back"
                        | other -> failtest (string other)
                    }
                ]
        ]
