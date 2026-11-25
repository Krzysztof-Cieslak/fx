/// Content exclusions on the model-egress hooks (T124): the glob matcher on a table of patterns and
/// paths, a request redacted part by part, and `fx.egress.exclusions` bootstrapped with the Anthropic,
/// OpenAI and canonical wires through the extension host, sending a request rendered from a session
/// whose history quotes excluded files: what leaves for each provider holds placeholders only. The
/// helpers are shared with tests/Fx.Ext.EgressExclusions.Tests.
module Fx.Aot.Scenarios.Egress

open System
open System.Threading
open System.Threading.Tasks
open Fx.Aot.Scenarios.Extensions
open Fx.Aot.Scenarios.Extensions.SampleHost
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Extensions
open Fx.Ext.EgressExclusions
open Fx.Ext.ModelCanonical
open Fx.Models
open Fx.Sdk

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromMinutes 1.0).GetAwaiter().GetResult()

/// Text that only excluded files hold: it must never leave.
let secrets =
    [
        "SECRET-ENV-7731"
        "SECRET-KEY-0419"
        "SECRET-PNG-5523"
        "SECRET-SYSTEM-9907"
    ]

/// The patterns of the tests: host, project and policy lists.
let host = [ "*.env"; "secrets/" ]
let project = [ "/config/private-*.json" ]
let policy = [ "**/*.{pem,key}" ]

/// Settings as the host would read them from variants.
let settings (action: ExclusionAction) (placeholder: PlaceholderStyle) : ExclusionSettings =
    {
        Lists =
            [
                {
                    Origin = ContentExclusions.PolicyOrigin
                    Patterns = policy
                }
                {
                    Origin = ContentExclusions.HostOrigin
                    Patterns = host
                }
                {
                    Origin = ContentExclusions.ProjectOrigin
                    Patterns = project
                }
            ]
        Placeholder = placeholder
        Action = action
    }

let private screenshot (sources: ContentSource list) : MediaPart =
    {
        Kind = MediaKind.Image
        MediaType = "image/png"
        Source = MediaSource.Base64(Convert.ToBase64String(Text.Encoding.UTF8.GetBytes "SECRET-PNG-5523"))
        Name = None
        Sources = sources
    }

/// A request as prompt render (T126) and the CodeAct loop (T127) would make it from a session: the
/// project's instructions and a pinned `.env` in the system prompt, a cell that read an excluded
/// key file and an allowed file, a screenshot of a secret, and a question.
let sessionRequest (model: ModelRef) : ChatRequest =
    { ChatRequest.create
          model
          [
              Message.userText "Why does the deploy fail?"
              Message.assistant
                  [
                      Part.ToolCall
                          {
                              Id = "call_1"
                              Name = "cell"
                              Arguments = """{"code": "await fx.fs.read(\"deploy/keys/prod.key\")"}"""
                              State = None
                          }
                  ]
              Message.user
                  [
                      Part.ToolResult
                          {
                              CallId = "call_1"
                              Name = "cell"
                              Content = [ ToolContent.Text "-----BEGIN KEY----- SECRET-KEY-0419" ]
                              IsError = false
                              Sources = [ ContentSource.file "deploy/keys/prod.key" ]
                          }
                      Part.quoting
                          [
                              {
                                  Path = @"config\public.json"
                                  Lines = Some { First = 1; Last = 3 }
                              }
                          ]
                          """{"region": "eu-central"}"""
                      Part.Media(screenshot [ ContentSource.file "secrets/screen.png" ])
                      Part.text "The deploy log says: permission denied."
                  ]
          ] with
        System =
            [
                Part.quoting [ ContentSource.file "AGENTS.md" ] "Be careful with production."
                Part.quoting [ ContentSource.file ".env" ] "API_TOKEN=SECRET-ENV-7731"
                Part.CacheBreakpoint CacheTtl.Short
                Part.text "SECRET-SYSTEM-9907 is in an unattributed part: exclusions cannot see it."
            ]
    }

/// Every model contribution type, for the host's catalog.
let catalog: Fx.Extensions.Catalog =
    Fx.Extensions.Catalog.create ModelAccess.contributionTypes

/// The extension host with `fx.egress.exclusions` (with these settings) and the given extensions.
let bootstrap (settings: ExclusionSettings) (others: Extension list) : Host =
    let hostSettings =
        { SampleHost.settings with
            Catalog = catalog
        }

    let candidates =
        EgressExclusions.create (Eff.ret settings) :: others
        |> List.map (fun e -> Candidate.create Source.Builtin (Package.Native e))

    wait (Eff.run (hostEnv SampleHost.memoryShell) (ExtensionHost.bootstrap hostSettings candidates))

/// The hooks in effect in a host.
let egressOf (host: Host) : EgressHooks =
    {
        Transforms = ExtensionHost.contributions ModelEgress.transforms host
        Gates = ExtensionHost.contributions ModelEgress.gates host
    }

/// Model access as a composition root builds it from the contributions in effect.
let accessOf (host: Host) (http: Http) : ModelAccess =
    { ModelAccess.create
          (ExtensionHost.contributions ChatWires.contributionType host |> Hooks.collect)
          (ExtensionHost.contributions ProviderDefs.contributionType host |> Hooks.collect)
          http
          (FakeClock.Create()).Clock with
        Egress = egressOf host
    }

/// A provider server that records what reaches it and answers with an error (what is sent is all
/// that matters here).
let recorder () : InMemoryHttp =
    InMemoryHttp((FakeClock.Create()).Clock, fun _ -> Some(Ok(HttpReply.text 503 "{}")))

/// The canonical wire's provider.
let canonicalProvider =
    ProviderDef.create "example" Canonical.Family "https://models.example.test/v1"

/// The models of the scenario: one per wire family.
let models =
    [
        ModelRef.create Fx.Ext.ModelAnthropic.Provider.Id "claude-sonnet-5-5"
        ModelRef.create Fx.Ext.ModelOpenAI.Provider.Id "gpt-6.2-sol"
        ModelRef.create canonicalProvider.Id "echo"
    ]

/// The model wires and providers (the canonical one as an extension of the test's own).
let modelExtensions: Extension list =
    [
        Fx.Ext.ModelAnthropic.Anthropic.extension
        Fx.Ext.ModelOpenAI.OpenAI.extension
        { Canonical.extension with
            Manifest =
                { Canonical.extension.Manifest with
                    Contributions =
                        Canonical.extension.Manifest.Contributions
                        @ [ Declare.contribution ProviderDefs.contributionType canonicalProvider.Id ]
                }
            Activate =
                eff {
                    let! activation = Canonical.extension.Activate

                    return
                        { activation with
                            Contributions =
                                activation.Contributions
                                @ [
                                    Contribution.create
                                        ProviderDefs.contributionType
                                        canonicalProvider.Id
                                        canonicalProvider
                                ]
                        }
                }
        }
    ]

/// What one request did: the events of its stream, and the bodies that reached the provider.
type Sent =
    {
        Events: ChatEvent list
        Bodies: string list
    }

    override this.ToString() =
        $"Sent({this.Events.Length} events, {this.Bodies.Length} bodies)"

/// Reads a stream to its end and closes it.
let drain (stream: ChatStream) : ChatEvent list =
    let events = ResizeArray<ChatEvent>()
    let mutable reading = true

    while reading do
        match wait (stream.Next CancellationToken.None) with
        | Some event -> events.Add event
        | None -> reading <- false

    wait (stream.Close())
    List.ofSeq events

/// Sends a request through model access to a recording provider.
let send (host: Host) (request: ChatRequest) : Sent =
    let http = recorder ()
    let llm = ModelAccess.llm (accessOf host http.Http)
    let events = drain (wait (llm.Stream request CancellationToken.None))

    {
        Events = events
        Bodies = http.Requests |> List.map HttpRequest.bodyText
    }

let private count (text: string) (part: string) : int =
    let mutable n = 0
    let mutable i = text.IndexOf(part, StringComparison.Ordinal)

    while i >= 0 do
        n <- n + 1
        i <- text.IndexOf(part, i + part.Length, StringComparison.Ordinal)

    n

/// Which secrets a body holds.
let leaked (body: string) : string list =
    secrets |> List.filter (fun s -> body.Contains(s, StringComparison.Ordinal))

let private placeholderStart = "[content excluded"

/// The `kind` of an error's JSON (its message differs per adapter).
let private kind (error: ChatError) : string =
    match ChatError.codec.Encode error with
    | Json.Object members ->
        match members |> List.tryFind (fun (name, _) -> name = "kind") with
        | Some(_, Json.String kind) -> kind
        | _ -> "?"
    | _ -> "?"

let private globs =
    Scenario.lines "egress-globs" (fun () ->
        let cases =
            [
                [ "*.env" ], [ ".env"; "a/b.env"; @"C:\p\.env"; "a/env"; "/home/u/x.env"; "PROD.ENV" ]
                [ "secrets/" ], [ "secrets/a"; "a/secrets/b"; "secrets"; @"Secrets\b" ]
                [ "/config/*.json"; "config\\keys\\*" ], [ "config/a.json"; "x/config/a.json"; "config/keys/k" ]
                [ "a/**/b"; "logs/**" ], [ "a/b"; "a/x/y/b"; "logs"; "logs/1/2" ]
                [ "**/*.{pem,key}"; "id_[rd]sa" ], [ "k.pem"; "d/e/k.key"; "id_rsa"; "id_dsa"; "id_ecdsa" ]
                [ "Docs/[!a-c]*.MD"; "a[b"; "x{y" ], [ "Docs/d.MD"; "docs/D.md"; "Docs/a.MD"; "a[b"; "x{y" ]
            ]

        [
            for patterns, paths in cases do
                match Exclusions.create [ { Origin = "t"; Patterns = patterns } ] with
                | Error issues -> yield $"{patterns}: {issues}"
                | Ok e ->
                    for path in paths do
                        let verdict =
                            match Exclusions.test e path with
                            | Some m -> $"excluded by {m.Pattern}"
                            | None -> "kept"

                        yield $"""{String.Join(" ", patterns)}: {path}: {verdict}"""

            for pattern in
                [
                    ""
                    "!keep.env"
                    "/"
                    "./"
                    "[z-a]"
                    String.replicate 9 "{a,b}"
                    "# comment"
                    "ok/"
                ] do
                match Glob.parse pattern with
                | Ok None -> yield $"{pattern}: a comment"
                | Ok(Some glob) -> yield $"{pattern}: ok (directories {glob.DirectoryOnly})"
                | Error message -> yield $"{pattern}: {message}"

            for path in [ @"src\a\..\b\.\c.txt"; "./x//y/"; @"C:\..\x"; @"\\srv\share\f"; "../../up" ] do
                yield $"normalize {path} -> {WorkspacePath.normalize path}"
        ])

let private redaction =
    Scenario.lines "egress-redaction" (fun () ->
        let request = sessionRequest (ModelRef.create "example" "echo")

        [
            for style in [ PlaceholderStyle.Plain; PlaceholderStyle.WithPath ] do
                match ContentExclusions.compile (settings ExclusionAction.Redact style) with
                | Error issues -> yield string issues
                | Ok e ->
                    let redacted, found = ExclusionRedaction.redact (Placeholder.text style) e request

                    for f in found do
                        yield $"{style}: {f}"

                    yield $"{style}: left {ExclusionRedaction.find e redacted |> List.length}"

                    yield $"{style}: {Json.serialize (ChatRequest.codec.Encode(ChatRequest.withoutSources redacted))}"
        ])

let private modelAccess =
    Scenario.lines "egress-model-access" (fun () ->
        [
            for action in [ ExclusionAction.Redact; ExclusionAction.Deny ] do
                let host = bootstrap (settings action PlaceholderStyle.Plain) modelExtensions

                for entry in host.Report do
                    yield $"{action}: report {entry.Id} {entry.Outcome}"

                for model in models do
                    let sent = send host (sessionRequest model)

                    let outcome =
                        match List.tryLast sent.Events with
                        | Some(ChatEvent.Failed(EgressBlocked block)) -> $"blocked ({block})"
                        | Some(ChatEvent.Failed error) -> $"sent, provider answered {kind error}"
                        | other -> $"unexpected {other}"

                    yield $"{action} {model}: {outcome}"

                    for body in sent.Bodies do
                        yield
                            $"""{action} {model}: leaked [{String.Join(", ", leaked body)}], placeholders {count body placeholderStart}, sources in body {body.Contains("\"sources\"")}"""

            let broken =
                { settings ExclusionAction.Redact PlaceholderStyle.Plain with
                    Lists =
                        [
                            {
                                Origin = "host"
                                Patterns = [ "[z-a]" ]
                            }
                        ]
                }

            let host = bootstrap broken modelExtensions
            let sent = send host (sessionRequest (List.last models))

            yield $"invalid settings: {sent.Events}, bodies {sent.Bodies.Length}"
        ])

let scenarios = [ globs; redaction; modelAccess ]
