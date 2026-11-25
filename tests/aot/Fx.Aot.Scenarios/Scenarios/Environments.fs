/// Environments (T112, docs/modules/environments.md): environment specs from config and capability
/// negotiation, the host's variables, the layout, the lifecycle machine and its JSON (pure), and a
/// scripted session with `fx.env.local` through the environments host: envd in this process
/// (embedded mode), and as this program's `envd` mode on stdio (piped), with a restart that
/// reconciles the leases. Paths, ids and times are never printed.
module Fx.Aot.Scenarios.Environments

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Aot.Scenarios.Show
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Environments
open Fx.Envd
open Fx.Envd.Live
open Fx.Ext.EnvLocal
open Fx.Machines
open Fx.Machines.Host
open Fx.Sdk
open Fx.Store
open Fx.Variants

let private none = CancellationToken.None

let private config =
    """{ "variants": {},
         "environments": {
           "dev": { "provider": "local", "transport": "piped", "workspace": { "path": "~/src/app" } },
           "cloud": { "provider": "e2b", "config": { "template": "node" }, "workspace": { "source": "git", "ref": "HEAD" },
                      "components": { "placement": "prebaked", "fallback": "host-push" },
                      "setup": { "run": ["bun install"], "secrets": ["npm"] },
                      "policy": { "idle": "suspend:15m", "ttl": "8h" } },
           "Bad": { "provider": "local", "sandbx": "none" } } }"""

let private specs () : string list =
    [
        match EnvSpecs.parse config with
        | Ok specs -> $"parsed {specs.Length}"
        | Error issues ->
            for issue in issues do
                "issue " + escape (JsonPath.render issue.Path + ": " + issue.Message)

        let fixedConfig =
            config.Replace("\"Bad\"", "\"good\"").Replace("\"sandbx\"", "\"sandbox\"")

        match EnvSpecs.parse fixedConfig with
        | Error issues -> $"unexpected: {Issue.render issues}"
        | Ok specs ->
            for spec in specs do
                let json = Json.serializeCanonical (EnvSpec.codec.Encode spec)
                $"spec {escape json}"

                match Codec.decode EnvSpec.codec (EnvSpec.codec.Encode spec) with
                | Ok back when back = spec -> "  round trip ok"
                | Ok _ -> "  round trip changed the spec"
                | Error issues -> $"  round trip failed: {Issue.render issues}"

                for issue in EnvSpecs.check EnvLocal.provider spec do
                    "  local refuses " + escape (JsonPath.render issue.Path + ": " + issue.Message)

        let scrubbed =
            HostEnvironment.scrub
                [
                    "PATH", "/bin"
                    "OPENAI_API_KEY", "x"
                    "GITHUB_TOKEN", "x"
                    "BUN_OPTIONS", "x"
                    "DB_PASSWORD", "x"
                    "LANG", "C"
                ]

        "scrubbed: " + String.Join(",", scrubbed |> List.map fst)

        let lease: Lease =
            {
                Id = "e1"
                Provider = "local"
                Workspace = "C:\\src"
                Home = "C:\\data\\environments\\e1"
                Data = Map.ofList [ "b", "2"; "a", "1" ]
            }

        let machine: EnvMachine =
            {
                Target = TargetTriple.create Os.Windows Arch.Arm64 None
                DataDir = "C:\\data"
                SameHost = true
            }

        let layout = EnvLayout.create machine lease
        let owned = EnvLayout.owned layout "k"

        "layout: "
        + escape $"{layout.EnvdState} {layout.Assets} {layout.Components} {owned.Temp}"

        "lease: " + escape (Json.serializeCanonical (EnvSpec.lease.Encode lease))
        "machine: " + escape (Json.serializeCanonical (EnvSpec.machine.Encode machine))
        "caps: " + escape (Json.serializeCanonical (EnvSpec.caps.Encode EnvLocal.caps))
        $"local valid: {(EnvLocal.check ()).IsEmpty}"
    ]

let private lifecycle () : string list =
    let spec = EnvSpec.create "dev" "local" |> EnvSpec.withWorkspace "/src"

    let lease (id: string) : Lease =
        {
            Id = id
            Provider = "local"
            Workspace = "/src"
            Home = "/data/environments/" + id
            Data = Map.empty
        }

    let machine: EnvMachine =
        {
            Target = TargetTriple.create Os.Linux Arch.X64 (Some Libc.Musl)
            DataDir = "/data"
            SameHost = true
        }

    let platform: Platform =
        {
            Os = "linux"
            Arch = "x64"
            Libc = Some "musl"
            Shell = ShellSpec.posix
            PathSeparator = "/"
            CaseSensitive = true
            Workspace = "/src"
            Version = "0.0.0"
            Features = [ Feature.Pty ]
        }

    let request (id: string) =
        EnvCommand.Request { Id = id; Spec = spec; At = 1L }

    let commands =
        [
            request "a"
            EnvCommand.Provisioned("a", 1, Ok(lease "a"))
            EnvCommand.Bootstrapped("a", 1, Ok(TransportKind.InProcess, machine))
            EnvCommand.Connected("a", 1, Ok platform)
            EnvCommand.Installed("a", 1, Ok [ "bun@1.4.5" ])
            EnvCommand.Bootstrapped("a", 1, Ok(TransportKind.Piped, machine))
            EnvCommand.Disconnected("a", 1, "gone")
            EnvCommand.Attach "a"
            EnvCommand.Attach "a"
            request "b"
            EnvCommand.Provisioned("b", 1, Error "no workspace")
            EnvCommand.Attach "b"
            request "c"
            EnvCommand.Destroy "c"
            EnvCommand.Provisioned("c", 1, Ok(lease "c"))
            EnvCommand.Destroyed("c", Ok())
            EnvCommand.Suspend "zz"
        ]

    let mutable state = Lifecycle.initial

    [
        for command in commands do
            match Machine.step Lifecycle.machine state command with
            | Error rejection -> $"{command} -> rejected {rejection.Code}"
            | Ok step ->
                state <- step.State
                let intents = String.Join("; ", step.Intents |> List.map string)
                $"{command} -> {step.Events.Length} events [{intents}]"

                for event in step.Events do
                    "  " + escape (Json.serializeCanonical (EnvLog.schema.Codec.Encode event))

        for record in Lifecycle.records state do
            $"record {record}"

        "recover: " + String.Join("; ", Lifecycle.recover state |> List.map string)
    ]

/// The host's environment.
type private HostEnv =
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

    override env.ToString() = "HostEnv"

let private temporary () =
    let dir =
        Path.Combine(Path.GetTempPath(), "environments-scenario-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private remove (dir: string) =
    try
        if Directory.Exists dir then
            for file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) do
                File.SetAttributes(file, FileAttributes.Normal)

            Directory.Delete(dir, true)
    with _ ->
        ()

let private variables () : (string * string) list =
    [
        for entry in Environment.GetEnvironmentVariables() |> Seq.cast<Collections.DictionaryEntry> do
            match entry.Key, entry.Value with
            | (:? string as name), (:? string as value) -> name, value
            | _ -> ()
    ]
    |> List.sortBy fst

/// This program, to run its `envd` mode: itself natively, or `dotnet` and its assembly on the JIT.
let private self () : string * string list =
    let path =
        match Environment.ProcessPath with
        | null -> ""
        | path -> path

    if String.Equals(Path.GetFileNameWithoutExtension path, "dotnet", StringComparison.OrdinalIgnoreCase) then
        path, [ Path.Combine(AppContext.BaseDirectory, "Fx.Aot.Scenarios.dll") ]
    else
        let suffix = if OperatingSystem.IsWindows() then ".exe" else ""
        Path.Combine(AppContext.BaseDirectory, "Fx.Aot.Scenarios" + suffix), []

let private settings (dataDir: string) : EnvsSettings =
    let clock = SystemClock.clock
    let extensionEnv = BasicExtensionEnv.forManifest clock EnvLocal.manifest

    let activation =
        (Eff.run extensionEnv EnvLocal.extension.Activate).GetAwaiter().GetResult()

    let provider =
        activation.Contributions
        |> List.pick (Contribution.tryValue EnvProviders.contributionType)

    let binary, binaryArgs = self ()

    {
        Stream = EnvsHost.stream "scenario"
        Providers =
            [
                {
                    Extension = EnvLocal.manifest.Id
                    Type = EnvProviders.contributionType.Name
                    Id = EnvLocal.Kind
                    Value = provider
                    Env = extensionEnv
                }
            ]
        FsRead = LocalFs.read
        Fs = LocalFs.write
        Shell = LocalShell.create (LocalShell.defaultShell ()) dataDir
        HostInfo =
            {
                Target =
                    TargetTriple.ofRid RuntimeInformation.RuntimeIdentifier
                    |> Option.defaultValue (TargetTriple.create Os.Linux Arch.X64 (Some Libc.Glibc))
                DataDir = dataDir
                Home = dataDir
            }
        Binary = binary
        BinaryArgs = binaryArgs
        HostEnvironment = variables () @ [ "SCENARIO_PLAIN", "plain"; "SCENARIO_API_KEY", "secret" ]
        Embed =
            Some(fun launch ->
                task {
                    let options =
                        { EnvdOptions.defaults with
                            Workspace = Some launch.Workspace
                            State = Some launch.State
                            Shell = launch.Shell
                            Environment = Some launch.Environment
                        }

                    match! EnvdMain.embed options with
                    | Error e -> return Error e
                    | Ok embedded ->
                        let connection: Embedded =
                            {
                                Transport = embedded.Transport
                                Stop = embedded.Stop
                            }

                        return Ok connection
                })
        Components = EnvComponents.none
        StopTimeout = TimeSpan.FromSeconds 10.0
        Limits = Fx.Rpc.ConnectionLimits.defaults
    }

let private hostEnv () : HostEnv =
    {
        Clock = SystemClock.clock
        Trace = Trace.none
        Ids = SystemIds.create SystemClock.clock
        Variants = Fx.Variants.Fakes.FakeVariants.core []
        Scope = Scope.root
    }

let private reason (record: EnvRecord) =
    record.Reason |> Option.defaultValue "no reason"

let private text (read: Result<byte array, FsError>) =
    match read with
    | Ok bytes -> Encoding.UTF8.GetString bytes
    | Error e -> $"error {e}"

/// The session: the same calls, whatever the transport.
let private session (transport: TransportKind) : Task<string list> =
    task {
        let lines = ResizeArray<string>()
        let say (line: string) = lines.Add(escape line)
        let dataDir = temporary ()
        let workspace = temporary ()

        try
            File.WriteAllText(Path.Combine(workspace, "hello.txt"), "hello\n")
            let settings = settings dataDir
            let log = EnvLog.typed (InMemoryEventLog<Payload>().Log)
            let! host = Eff.run (hostEnv ()) (EnvsHost.start settings log)
            let envs = EnvsHost.envs host

            let spec =
                { (EnvSpec.create "dev" EnvLocal.Kind |> EnvSpec.withWorkspace workspace) with
                    Transport = Some transport
                }

            match! envs.Create spec none with
            | Error e -> say $"create failed: {e}"
            | Ok record ->
                say $"created: {record.Phase} {record.Transport.Value} attempt {record.Attempt}"
                say $"warnings: {(Envs.warnings record).Length}"
                let lease = record.Lease.Value
                say $"lease: id matches {lease.Id = record.Id}, home under data {lease.Home.StartsWith dataDir}"

                match! envs.Open record.Id none with
                | Error e -> say $"open failed: {e}"
                | Ok handle ->
                    let platform = handle.Client.Platform
                    say $"platform: {platform.Os} {platform.Arch} same host {handle.Machine.SameHost}"
                    say $"workspace announced: {platform.Workspace = workspace}"
                    let at name = Path.Combine(workspace, name)
                    let! read = handle.Client.FsRead.ReadFile (at "hello.txt") none

                    match read with
                    | Ok bytes -> say $"read: {Encoding.UTF8.GetString bytes}"
                    | Error e -> say $"read failed: {e}"

                    let! _ =
                        handle.Client.Fs.WriteFile (at "out.txt") (Encoding.UTF8.GetBytes "out") none

                    let line =
                        if OperatingSystem.IsWindows() then
                            "Write-Output \"$env:SCENARIO_PLAIN|$env:SCENARIO_API_KEY\"; Get-Content out.txt"
                        else
                            "echo \"$SCENARIO_PLAIN|$SCENARIO_API_KEY\"; cat out.txt"

                    let! output =
                        handle.Client.Shell.Run (Command.script line |> Command.inDir workspace) none

                    match output with
                    | Ok output ->
                        let printed = (Output.stdoutText output).Replace("\r\n", "\n").Trim()
                        say $"run: exit {output.ExitCode}: {printed}"
                    | Error e -> say $"run failed: {e}"

                    // The connection ends without the host: detached, then attached again.
                    let! _ = EnvClient.close handle.Client
                    let mutable current = record

                    for _ in 1..400 do
                        if current.Phase = Phase.Ready then
                            do! Task.Delay 25
                            let! got = envs.Get record.Id none
                            current <- got.Value

                    say $"after the connection ended: {current.Phase} ({reason current})"

                    match! envs.Open record.Id none with
                    | Ok again ->
                        let! read = again.Client.FsRead.ReadFile (at "out.txt") none
                        say $"attached again: {text read}"
                    | Error e -> say $"attach failed: {e}"

                // A restart: the next host finds the environment detached and attaches it again.
                do! EnvsHost.stop host
                let! host = Eff.run (hostEnv ()) (EnvsHost.start settings log)
                let envs = EnvsHost.envs host
                let mutable current = record

                for _ in 1..400 do
                    let! got = envs.Get record.Id none

                    if got.Value.Phase = Phase.Ready || got.Value.Phase = Phase.Requested then
                        do! Task.Delay 25
                    else
                        current <- got.Value

                say $"after a restart: {current.Phase} ({reason current})"

                match! envs.Open record.Id none with
                | Ok handle ->
                    let! read = handle.Client.FsRead.ReadFile (Path.Combine(workspace, "out.txt")) none
                    say $"reopened: {text read}"
                | Error e -> say $"reopen failed: {e}"

                match! envs.Destroy record.Id none with
                | Ok destroyed -> say $"destroyed: {destroyed.Phase}, home gone {not (Directory.Exists lease.Home)}"
                | Error e -> say $"destroy failed: {e}"

                let kept = File.Exists(Path.Combine(workspace, "out.txt"))
                say $"workspace kept: {kept}"
                do! EnvsHost.stop host
        finally
            remove workspace
            remove dataDir

        return List.ofSeq lines
    }

/// `envd --stdio --workspace <dir> --state <dir> [--shell <name>]`: what the piped transport starts.
let serveEnvd (args: string list) : int =
    let rec parse (options: EnvdOptions) (args: string list) =
        match args with
        | "--workspace" :: dir :: rest -> parse { options with Workspace = Some dir } rest
        | "--state" :: dir :: rest -> parse { options with State = Some dir } rest
        | "--shell" :: name :: rest -> parse { options with Shell = Some name } rest
        | [] -> Some options
        | _ -> None

    match parse EnvdOptions.defaults args with
    | None ->
        stderr.WriteLine "usage: envd --stdio --workspace <dir> --state <dir> [--shell <name>]"
        2
    | Some options ->
        EnvdMain.runStdio options (Console.OpenStandardInput()) (Console.OpenStandardOutput()) (fun line ->
            stderr.WriteLine line)

let private bounded (run: unit -> Task<string list>) () : Task<string list> =
    run().WaitAsync(TimeSpan.FromSeconds 180.0)

let scenarios: Scenario list =
    [
        Scenario.lines "environments-specs" specs
        Scenario.lines "environments-lifecycle" lifecycle
        Scenario.task "environments-local-in-process" (bounded (fun () -> session TransportKind.InProcess))
        Scenario.task "environments-local-piped" (bounded (fun () -> session TransportKind.Piped))
    ]
