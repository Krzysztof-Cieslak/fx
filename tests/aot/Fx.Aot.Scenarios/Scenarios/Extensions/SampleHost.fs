/// The sample extension (extensions/Fx.Ext.Sample) loaded into a host three ways: in process
/// (`native`), in a child process (`process`: this program's `ext-peer` command, on the JIT and
/// natively), and through a fake process in memory; and one transcript of hook calls that must read
/// the same every way. tests/Fx.Extensions.Tests runs its parity suite on these loaders.
module Fx.Aot.Scenarios.Extensions.SampleHost

open System
open System.Diagnostics
open System.IO
open System.IO.Pipelines
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Extensions
open Fx.Rpc
open Fx.Sdk
open Fx.Sdk.Process
open Fx.Ext.Sample

/// The host's environment: processes, a clock and a scope.
type HostEnv =
    {
        Clock: Clock
        Shell: Shell
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IShell with
        member env.Shell = env.Shell

    interface IScopeWith<HostEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"HostEnv({env.Scope})"

/// Fails instead of hanging.
let within (work: Task<'T>) : Task<'T> =
    work.WaitAsync(TimeSpan.FromSeconds 60.0)

let trust: TrustContext =
    {
        ProjectTrusted = false
        RequirePins = false
        Placement = "host"
        Sdk = Versions.Sdk
        Protocols = ExtensionProtocol.versions
    }

let info: PeerInfo =
    {
        Name = Product.binaryName
        Version = Product.Version
    }

/// What a host needs to start extension processes here.
let processSettings: ProcessRuntime.ProcessSettings =
    {
        Limits = ConnectionLimits.defaults
        Info = info
        HostEnvironment =
            [
                for entry in Environment.GetEnvironmentVariables() |> Seq.cast<Collections.DictionaryEntry> do
                    match entry.Key, entry.Value with
                    | (:? string as name), (:? string as value) -> name, value
                    | _ -> ()
            ]
            |> List.sortBy fst
        Workspace = None
        IsWindows = OperatingSystem.IsWindows()
        StopTimeout = TimeSpan.FromSeconds 10.0
        StderrBytes = 4096
    }

/// The sample's slot as the test host resolves it: `fancy`, from the host's config.
let styleValue: Fx.Variants.Resolved =
    {
        Slot = Sample.Slots.style.Id
        Value = Json.String "fancy"
        Source = Fx.Variants.Source.create Fx.Variants.Layer.Host "test"
    }

/// A grants log for the sample's transcript: resources under `ok/` are allowed by grant `g1`, others
/// need a person; an ask is denied at once with its key and requester as the reason, so the transcript
/// shows what the host received.
let grants: Grants =
    {
        Check =
            fun query _ ->
                Task.FromResult(
                    Ok(
                        if query.Resource.StartsWith "ok/" then
                            Fx.Grants.GrantCheck.Allowed(Fx.Grants.GrantId "g1")
                        else
                            Fx.Grants.GrantCheck.MustAsk [ "a person decides" ]
                    )
                )
        Request =
            fun ask _ ->
                Task.FromResult(
                    Ok(Fx.Grants.AskOutcome.Settled(Fx.Grants.Resolution.Denied [ $"{ask.Key} by {ask.Requester}" ]))
                )
        Await = fun id _ -> Task.FromResult(Error(Fx.Grants.GrantError.UnknownRequest id))
        Withdraw = fun id _ _ -> Task.FromResult(Error(Fx.Grants.GrantError.UnknownRequest id))
    }

/// What the test host offers extensions: the sample's slot, the packages of `current`, `grants`.
let services (current: unit -> Host) : HostServices =
    {
        Slots = fun extension -> if extension = Sample.id then [ styleValue ] else []
        Packages = ExtensionHost.packages current
        Grants = grants
    }

/// Settings with the sample's hook points in the catalog, whose extensions' capabilities see `current`.
let settingsFor (current: unit -> Host) : HostSettings<HostEnv> =
    let catalog = Catalog.create Sample.Points.all

    { ExtensionHost.settings trust (BasicExtensionEnv.attenuated SystemClock.clock (services current)) processSettings with
        Catalog = catalog
    }

/// Settings with the sample's hook points in the catalog (the packages capability sees no extensions).
let settings: HostSettings<HostEnv> =
    settingsFor (fun () -> ExtensionHost.empty (Catalog.create Sample.Points.all))

let hostEnv (shell: Shell) : HostEnv =
    {
        Clock = SystemClock.clock
        Shell = shell
        Scope = Scope.root
    }

/// A host with the sample loaded, and how to release it.
type Loaded =
    {
        Host: Host
        Env: HostEnv
        Dispose: unit -> Task
    }

    override this.ToString() = string this.Host

let private bootstrap (env: HostEnv) (candidate: Candidate) : Task<Loaded> =
    task {
        let current = ref (ExtensionHost.empty (Catalog.create Sample.Points.all))

        let! host =
            within (Eff.run env (ExtensionHost.bootstrap (settingsFor (fun () -> current.Value)) [ candidate ]))

        current.Value <- host

        return
            {
                Host = host
                Env = env
                Dispose = fun () -> within (Eff.run env (ExtensionHost.stop host)) :> Task
            }
    }

/// The sample in the host's process.
let native () : Task<Loaded> =
    bootstrap
        (hostEnv (LocalShell.create (LocalShell.defaultShell ()) AppContext.BaseDirectory))
        (Candidate.create Source.Builtin (Package.Native Sample.extension))

/// The program and arguments that run this program's `ext-peer` command: itself (natively or on the
/// JIT), or, from a test host that references it, its apphost or `dotnet` and its assembly.
let peerCommand () : string * string list =
    let self =
        match Environment.ProcessPath with
        | null -> ""
        | path -> path

    let directory = AppContext.BaseDirectory

    if String.Equals(Path.GetFileNameWithoutExtension self, "dotnet", StringComparison.OrdinalIgnoreCase) then
        self, [ Path.Combine(directory, "Fx.Aot.Scenarios.dll"); "ext-peer" ]
    else
        let suffix = if OperatingSystem.IsWindows() then ".exe" else ""
        Path.Combine(directory, "Fx.Aot.Scenarios" + suffix), [ "ext-peer" ]

/// What a .NET program needs from the host's environment to start.
let private allowlist =
    [
        "PATH"
        "HOME"
        "USERPROFILE"
        "DOTNET_ROOT*"
        "TMPDIR"
        "TEMP"
        "TMP"
        "SystemRoot"
        "SystemDrive"
        "windir"
        "LOCALAPPDATA"
        "APPDATA"
        "ProgramData"
        "ProgramFiles"
        "COMSPEC"
    ]

/// The sample's manifest as a process extension started with `ext-peer`.
let processManifest () : Manifest =
    let exe, args = peerCommand ()

    { Sample.manifest with
        Runtime = Runtimes.Process
        Launch =
            Some
                {
                    Exe = exe
                    Args = args
                    EnvAllowlist = allowlist
                    EnvSet = []
                    EnvDeny = []
                    Cwd = LaunchCwd.Owned
                    UntrustedOverrides = []
                }
    }

/// The sample in a child process: `ext-peer`, started by the process runtime with the real shell.
let childProcess () : Task<Loaded> =
    bootstrap
        (hostEnv (LocalShell.create (LocalShell.defaultShell ()) AppContext.BaseDirectory))
        (Candidate.create Source.Builtin (Package.Manifest(processManifest (), Some AppContext.BaseDirectory)))

/// The `ext-peer` command: serves the sample on standard input and output until the host closes it,
/// as any F# extension process does (`ExtensionProcess.run` from the SDK).
let servePeer () : int =
    ExtensionProcess.run "Fx.Ext.Sample" Sample.manifest.Version [ Sample.extension ]

/// A process in memory: its standard input and output are pipes to an `ExtensionServer` serving the
/// extensions in this process. The process runtime and `ProcTransport` run exactly as for a child.
let memoryProcOf (extensions: Extension list) : Proc =
    let toServer = Pipe()
    let toHost = Pipe()

    let serverTransport =
        StreamTransport.create
            (ConnectionLimits.frames ConnectionLimits.defaults)
            (toServer.Reader.AsStream())
            (toHost.Writer.AsStream())

    let serverEnv =
        {
            Clock = SystemClock.clock
            Shell = LocalShell.create (LocalShell.defaultShell ()) AppContext.BaseDirectory
            Scope = Scope.root
        }

    let exited =
        task {
            match!
                Eff.run
                    serverEnv
                    (ExtensionServer.serve info extensions (BasicExtensionEnv.create SystemClock.clock) serverTransport)
            with
            | Ok peer ->
                let! _ = peer.Completion
                return 0
            | Error _ -> return 1
        }

    let output = toHost.Reader.AsStream()
    let input = toServer.Writer.AsStream()
    let buffer = Array.zeroCreate<byte> 16384

    {
        Pid = 0
        Write =
            fun bytes cancellation ->
                task {
                    do! input.WriteAsync(ReadOnlyMemory bytes, cancellation)
                    do! input.FlushAsync cancellation
                }
        CloseStdin = fun () -> task { do! toServer.Writer.CompleteAsync() }
        Read =
            fun cancellation ->
                task {
                    let! count = output.ReadAsync(buffer.AsMemory(), cancellation)

                    if count = 0 then
                        return None
                    else
                        return Some(Stdout(Array.sub buffer 0 count))
                }
        Wait = fun cancellation -> exited.WaitAsync cancellation
        Kill =
            fun () ->
                serverTransport.Abort()
                Task.FromResult()
    }

/// A shell whose `Spawn` starts an in-memory process serving the extensions, whatever it is asked
/// to start.
let memoryShellOf (extensions: Extension list) : Shell =
    {
        Run = fun _ _ -> invalidOp "the in-memory shell only spawns"
        Spawn = fun _ _ -> Task.FromResult(Ok(memoryProcOf extensions))
    }

/// The in-memory shell serving the sample.
let memoryShell: Shell = memoryShellOf [ Sample.extension ]

/// The sample behind the process runtime, served in this process over in-memory pipes.
let inMemoryProcess () : Task<Loaded> =
    bootstrap
        (hostEnv memoryShell)
        (Candidate.create Source.Builtin (Package.Manifest(processManifest (), Some AppContext.BaseDirectory)))

// ---- The transcript ---------------------------------------------------------------------------

let private ids (items: Contributed<'T> list) =
    String.Join(", ", items |> List.map (fun c -> c.By))

/// Uses every contribution and method of the sample through the host and describes what happened.
/// Failure details (exception types, remote messages) differ between runtimes, so they are left out.
/// The report is the sample's: a runtime's host (`fx.exthost.bun`) may be loaded with it.
let transcript (loaded: Loaded) : Task<string list> =
    task {
        let host = loaded.Host
        let env = loaded.Env
        let run (effect: Eff<HostEnv, 'T>) = within (Eff.run env effect)
        let lines = ResizeArray<string>()

        for entry in host.Report |> List.filter (fun e -> e.Id = Sample.id) do
            lines.Add $"report: {entry.Id} {entry.Outcome}"

        let gates = ExtensionHost.contributions Sample.Points.greeting host
        let transforms = ExtensionHost.contributions Sample.Points.text host
        let words = ExtensionHost.contributions Sample.Points.words host
        let observers = ExtensionHost.contributions Sample.Points.notes host
        lines.Add $"gates: {ids gates}"
        lines.Add $"transforms: {ids transforms}"
        lines.Add $"""words: {String.Join(", ", Hooks.collect words)}"""
        lines.Add $"observers: {ids observers}"

        for name, language in [ "Ada", "en"; "", "en"; "Ada", "fr"; "crash", "en"; "", "fr" ] do
            let! verdict =
                run (Hooks.decide gates ({ Name = name; Language = language }: Sample.Greeting))

            lines.Add $"decide {Json.quote name} {language}: {verdict}"

        for text in [ "  hi "; "fine"; "please explode" ] do
            let! outcome = run (Eff.catch (Hooks.transform transforms text))

            match outcome with
            | Ok result -> lines.Add $"transform {Json.quote text}: {Json.quote result}"
            | Error(:? ContributionFailedException as e) -> lines.Add $"transform {Json.quote text}: {e.Address} failed"
            | Error e -> lines.Add $"transform {Json.quote text}: unexpected {e.GetType().Name}"

        for text in [ "one"; "boom"; "two" ] do
            let! failures = run (Hooks.observe observers ({ Text = text }: Sample.Note))
            let failed = String.Join(", ", failures |> List.map (fun f -> f.By))
            lines.Add $"observe {text}: failures [{failed}]"

        let call (m: ExtMethod<'P, 'R, 'E>) (parameters: 'P) =
            run (ExtensionHost.callTyped host Sample.id m parameters)

        let show (outcome: Result<'T, ExtMethod.CallError<'E>>) =
            match outcome with
            | Ok value -> "ok " + string value
            | Error(ExtMethod.CallError.Failed e) -> "typed error " + string e
            | Error(ExtMethod.CallError.Other(MethodFailure.Crashed _)) -> "crashed"
            | Error(ExtMethod.CallError.Other other) -> string other

        let! observed = call Sample.Methods.observed ()

        lines.Add(
            "observed: "
            + (match observed with
               | Ok notes -> String.Join(", ", notes)
               | Error e -> string e)
        )

        let! echoed = call Sample.Methods.echo ({ Text = "hello" }: Sample.Note)
        lines.Add $"echo: {show echoed}"

        let! quotient =
            call Sample.Methods.divide ({ Dividend = 7; Divisor = 2 }: Sample.Division)

        lines.Add $"divide 7 2: {show quotient}"

        let! byZero =
            call Sample.Methods.divide ({ Dividend = 1; Divisor = 0 }: Sample.Division)

        lines.Add $"divide 1 0: {show byZero}"
        let! failed = call Sample.Methods.fail ()
        lines.Add $"fail: {show failed}"

        let! invalid =
            run (ExtensionHost.call host Sample.id Sample.Methods.divide.Name (Json.Object [ "dividend", Json.int 1 ]))

        lines.Add(
            "divide without a divisor: "
            + (match invalid with
               | Error(MethodFailure.InvalidParams issues) -> "invalid params " + Issue.render issues
               | other -> string other)
        )

        let! unknown = run (ExtensionHost.call host Sample.id "nope" Json.Null)

        lines.Add(
            "unknown method: "
            + (match unknown with
               | Error failure -> string failure
               | Ok _ -> "ok")
        )

        // The host capabilities its manifest lists: its slot, the packages in effect, grants.
        let! style = call Sample.Methods.style ()
        lines.Add $"style: {show style}"

        let! packages = call Sample.Methods.packages ()

        lines.Add(
            "packages: "
            + (match packages with
               | Ok ids ->
                   "the sample is in effect: "
                   + (if List.contains Sample.id ids then "yes" else "no")
               | Error e -> string e)
        )

        for contributionType in [ Sample.Points.words.Name; "nope"; Sample.Points.greeting.Name ] do
            let! data = call Sample.Methods.data contributionType

            lines.Add(
                $"data {contributionType}: "
                + (match data with
                   | Ok values -> "ok " + String.Join(", ", values |> List.map Json.serialize)
                   | other -> show other)
            )

        for resource in [ "ok/a"; "secret" ] do
            let query: Fx.Grants.GrantQuery =
                {
                    Action = Fx.Grants.ActionName "file.read"
                    Resource = resource
                    Context = Fx.Grants.GrantContext.none
                }

            let! check = call Sample.Methods.check (Fx.Grants.Codecs.query.Encode query)

            lines.Add(
                $"check {resource}: "
                + (match check |> Result.map Fx.Grants.Codecs.check.Decode with
                   | Ok(Ok check) -> "ok " + string check
                   | Ok(Error issues) -> "invalid " + Issue.render issues
                   | Error e -> show (Error e))
            )

        let ask: Fx.Grants.GrantAsk =
            {
                Key = "k1"
                Action = Fx.Grants.ActionName "file.read"
                Resource = "secret"
                Context = Fx.Grants.GrantContext.none
                Requester =
                    {
                        Principal = "user:spoofed:1"
                        Agent = Some "agent"
                        Extension = Some "someone.else"
                    }
                Summary = "Read the secret"
                Evidence = []
                Suggestions = []
                Scopes = []
                Expires = None
            }

        let! asked = call Sample.Methods.ask (Fx.Grants.Codecs.ask.Encode ask)

        lines.Add(
            "ask: "
            + (match asked |> Result.map Fx.Grants.Codecs.askOutcome.Decode with
               | Ok(Ok outcome) -> "ok " + string outcome
               | Ok(Error issues) -> "invalid " + Issue.render issues
               | Error e -> show (Error e))
        )

        return List.ofSeq lines
    }
