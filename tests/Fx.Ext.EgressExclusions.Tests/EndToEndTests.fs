/// `fx.egress.exclusions` as the host runs it: bootstrapped through the extension host with the
/// Anthropic, OpenAI and canonical wires, requests built from a session leave for every provider with
/// placeholders only (or not at all); and its hooks behave the same over the extension protocol.
module Fx.Ext.EgressExclusions.Tests.EndToEndTests

open System
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Extensions
open Fx.Models
open Fx.Sdk
open Fx.Aot.Scenarios
open Fx.Aot.Scenarios.Extensions
open Fx.Aot.Scenarios.Extensions.SampleHost
open Fx.Ext.EgressExclusions

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 60.0).GetAwaiter().GetResult()

let private redact = Egress.settings ExclusionAction.Redact PlaceholderStyle.Plain

let private blocked (sent: Egress.Sent) : EgressBlock option =
    match List.tryLast sent.Events with
    | Some(ChatEvent.Failed(EgressBlocked block)) -> Some block
    | _ -> None

/// The content exclusions' hooks alone, as an extension any runtime can serve.
let private hooksOnly (settings: ExclusionSettings) : Extension =
    {
        Manifest =
            { Manifest.create "test.exclusions" "1.0.0" Runtimes.Native with
                Contributions =
                    [
                        Declare.contribution ModelEgress.transforms EgressExclusions.HookId
                        Declare.contribution ModelEgress.gates EgressExclusions.HookId
                    ]
            }
        Activate =
            eff {
                let transform, gate = ContentExclusions.hooks (Eff.ret settings)

                return
                    Activation.create
                        [
                            Contribution.create ModelEgress.transforms EgressExclusions.HookId transform
                            Contribution.create ModelEgress.gates EgressExclusions.HookId gate
                        ]
                        []
            }
    }

/// The hooks in a host: natively, or behind the process runtime over the extension protocol.
let private loadHooks (overProtocol: bool) (settings: ExclusionSettings) : Host * HostEnv =
    let extension = hooksOnly settings
    let env = hostEnv (SampleHost.memoryShellOf [ extension ])

    let candidate =
        if overProtocol then
            let exe, args = SampleHost.peerCommand ()

            Candidate.create
                Source.Builtin
                (Package.Manifest(
                    { extension.Manifest with
                        Runtime = Runtimes.Process
                        Launch =
                            Some
                                {
                                    Exe = exe
                                    Args = args
                                    EnvAllowlist = []
                                    EnvSet = []
                                    EnvDeny = []
                                    Cwd = LaunchCwd.Owned
                                    UntrustedOverrides = []
                                }
                    },
                    Some AppContext.BaseDirectory
                ))
        else
            Candidate.create Source.Builtin (Package.Native extension)

    let host =
        wait (
            Eff.run
                env
                (ExtensionHost.bootstrap
                    { SampleHost.settings with
                        Catalog = Egress.catalog
                    }
                    [ candidate ])
        )

    host, env

/// What the hooks do to some requests, as text.
let private transcript (overProtocol: bool) (settings: ExclusionSettings) : string list =
    let host, env = loadHooks overProtocol settings
    let hooks = Egress.egressOf host
    let run (effect: Eff<HostEnv, 'T>) = wait (Eff.run env effect)

    try
        [
            for entry in host.Report do
                $"report: {entry.Outcome}"

            let model = ModelRef.create "example" "echo"

            for request in
                [
                    Egress.sessionRequest model
                    ChatRequest.create model [ Message.userText "nothing to exclude" ]
                ] do
                match run (Eff.catch (Hooks.transform hooks.Transforms request)) with
                | Ok transformed ->
                    Json.serialize (ChatRequest.codec.Encode transformed)
                    string (run (Hooks.decide hooks.Gates transformed))
                    string (run (Hooks.decide hooks.Gates request))
                | Error(:? ContributionFailedException) -> "the transform failed"
                | Error e -> $"unexpected {e.GetType().Name}"
        ]
    finally
        wait (Eff.run env (ExtensionHost.stop host))

[<Tests>]
let tests =
    testList
        "fx.egress.exclusions"
        [
            test "it contributes exactly what its manifest declares, and activates through the host" {
                let host = Egress.bootstrap redact []

                Expect.equal
                    (host.Report |> List.map (fun r -> r.Id, string r.Outcome))
                    [ EgressExclusions.id, "activated" ]
                    "activated"

                Expect.equal
                    (ExtensionHost.contributions VariantSlots.contributionType host
                     |> List.map (fun c -> c.Id))
                    (ContentExclusions.slots |> List.map (fun s -> string s.Id))
                    "its slots"

                let egress = Egress.egressOf host

                Expect.equal
                    (egress.Transforms |> List.map (fun c -> c.By))
                    [ EgressExclusions.id + "/exclusions" ]
                    "the transform"

                Expect.equal
                    (egress.Gates |> List.map (fun c -> c.By))
                    [ EgressExclusions.id + "/exclusions" ]
                    "the gate"

                Expect.isTrue (ModelEgress.transforms.Privileged && ModelEgress.gates.Privileged) "privileged"
            }

            test "a request built from a session leaves for every provider with placeholders, never excluded content" {
                let host = Egress.bootstrap redact Egress.modelExtensions

                for model in Egress.models do
                    let sent = Egress.send host (Egress.sessionRequest model)
                    Expect.isNone (blocked sent) $"{model}: sent"
                    Expect.equal sent.Bodies.Length 1 $"{model}: one request"

                    for body in sent.Bodies do
                        Expect.equal
                            (Egress.leaked body)
                            [ "SECRET-SYSTEM-9907" ]
                            $"{model}: only the unattributed text, which no exclusion can see"

                        Expect.stringContains body "[content excluded" $"{model}: placeholders"
                        Expect.stringContains body "eu-central" $"{model}: a re-included file's content"
                        Expect.isFalse (body.Contains "\"sources\"") $"{model}: provenance never leaves"
                        Expect.isFalse (body.Contains "screen.png") $"{model}: plain placeholders name no file"
            }

            test "with the path placeholder, the excluded files are named and nothing else" {
                let host =
                    Egress.bootstrap
                        (Egress.settings ExclusionAction.Redact PlaceholderStyle.WithPath)
                        Egress.modelExtensions

                let sent = Egress.send host (Egress.sessionRequest (List.last Egress.models))

                for body in sent.Bodies do
                    Expect.stringContains body "deploy/keys/prod.key is kept out of model requests" "the path"
                    Expect.equal (Egress.leaked body) [ "SECRET-SYSTEM-9907" ] "no content"
            }

            test "with the deny action, nothing is sent and the request is blocked with the reason" {
                let host =
                    Egress.bootstrap
                        (Egress.settings ExclusionAction.Deny PlaceholderStyle.Plain)
                        Egress.modelExtensions

                for model in Egress.models do
                    let sent = Egress.send host (Egress.sessionRequest model)

                    match blocked sent with
                    | Some block ->
                        Expect.isFalse block.Ask "denied"

                        Expect.stringContains
                            (String.Join(" ", block.Reasons))
                            $"{EgressExclusions.id}/exclusions: content of .env, excluded by host (*.env), would leave in system[1] (and 2 more)"
                            "why"
                    | None -> failtest $"{model}: not blocked: {sent.Events}"

                    Expect.isEmpty sent.Bodies $"{model}: nothing left the process"

                let clean =
                    Egress.send host (ChatRequest.create (List.last Egress.models) [ Message.userText "hi" ])

                Expect.isNone (blocked clean) "a request without excluded content goes"
            }

            test "invalid settings stop every request" {
                let host =
                    Egress.bootstrap
                        { redact with
                            Lists =
                                [
                                    {
                                        Origin = "host"
                                        Patterns = [ "[z-a]" ]
                                    }
                                ]
                        }
                        Egress.modelExtensions

                let sent =
                    Egress.send host (ChatRequest.create (List.last Egress.models) [ Message.userText "hi" ])

                Expect.isSome (blocked sent) "blocked"
                Expect.isEmpty sent.Bodies "nothing sent"
            }

            test
                "the prompt render records what it quotes, so the egress hooks redact files excluded after they were recorded" {
                // A log recorded before `prod.env` and the key were excluded (the render withholds only
                // what the exclusions it is given name; here none).
                let context = global.Fx.Machines.StreamId "context/e1"

                let view (path: string) (text: string) : Fx.Prompt.Piece =
                    Fx.Prompt.Piece.File
                        {
                            Path = path
                            Digest = "sha256:00"
                            Lines = [ LineRange.create 1 2 ]
                            Text = text
                        }

                let sections =
                    [
                        Fx.Prompt.Block.create "instructions" [ Fx.Prompt.Piece.Text "Be careful." ]
                        Fx.Prompt.Block.create "env" [ view "prod.env" "API_TOKEN=SECRET-ENV-7731" ]
                    ]

                let transcript =
                    Fx.Prompt.Transcript.ofItems
                        [
                            Fx.Prompt.LogItem.Context(
                                global.Fx.Machines.EventRef.create context 1L,
                                Fx.Prompt.ContextEvent.Framed sections
                            )
                            Fx.Prompt.LogItem.Context(
                                global.Fx.Machines.EventRef.create context 2L,
                                Fx.Prompt.ContextEvent.Returned
                                    {
                                        Turn = 1
                                        Step = 0
                                        Call = None
                                        Name = "cell"
                                        Pieces =
                                            [
                                                Fx.Prompt.Piece.Text "read a key"
                                                view "deploy/keys/prod.key" "SECRET-KEY-0419"
                                            ]
                                        IsError = false
                                        Restarted = None
                                    }
                            )
                            Fx.Prompt.LogItem.Context(
                                global.Fx.Machines.EventRef.create context 3L,
                                Fx.Prompt.ContextEvent.Returned
                                    {
                                        Turn = 1
                                        Step = 1
                                        Call = None
                                        Name = "cell"
                                        Pieces = [ view "README.md" "hello" ]
                                        IsError = false
                                        Restarted = None
                                    }
                            )
                        ]

                let caching: CachingCaps =
                    {
                        Mode = CacheMode.Breakpoints
                        MaxBreakpoints = 4
                        Key = false
                        LongTtl = false
                    }

                let cfg =
                    Fx.Prompt.PromptSettings.config Fx.Prompt.PromptSettings.defaults sections Exclusions.none caching

                let model = List.last Egress.models

                let request =
                    Fx.Prompt.Render.request
                        cfg
                        (ChatRequest.create model [])
                        (Fx.Prompt.Render.render cfg transcript Fx.Prompt.Live.none)

                Expect.contains
                    (ChatRequest.sources request |> List.map (fun s -> s.Path))
                    "deploy/keys/prod.key"
                    "the receipt's file is recorded"

                let host = Egress.bootstrap redact Egress.modelExtensions
                let sent = Egress.send host request

                for body in sent.Bodies do
                    Expect.isEmpty (Egress.leaked body) "no excluded content leaves"
                    Expect.stringContains body "hello" "the rest does"
                    Expect.stringContains body "[content excluded" "placeholders"
            }

            test "the hooks behave the same over the extension protocol as in process" {
                for settings in
                    [
                        redact
                        Egress.settings ExclusionAction.Deny PlaceholderStyle.WithPath
                        { redact with
                            Lists =
                                [
                                    {
                                        Origin = "host"
                                        Patterns = [ "[z-a]" ]
                                    }
                                ]
                        }
                    ] do
                    let native = transcript false settings
                    let remote = transcript true settings
                    Expect.isNonEmpty native "something happened"
                    Expect.sequenceEqual remote native $"the same with {settings}"
            }
        ]
