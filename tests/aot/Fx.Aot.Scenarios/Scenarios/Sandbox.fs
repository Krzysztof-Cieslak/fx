/// Sandbox drivers (docs/modules/sandbox.md): the pure parts everywhere (policies and their access,
/// overrides, the host's choice of driver and an environment's policy, bubblewrap's command lines and
/// seccomp program, Seatbelt's profiles), and with `SANDBOX_TESTS=1` (CI's sandbox steps) a process
/// confined by this OS's driver.
module Fx.Aot.Scenarios.Sandbox

open System
open System.IO
open System.Threading.Tasks
open Fx.Aot.Scenarios.Show
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Environments
open Fx.Ext.SandboxBubblewrap
open Fx.Ext.SandboxSeatbelt
open Fx.Sdk

let private requested = Environment.GetEnvironmentVariable "SANDBOX_TESTS" = "1"

let private policy: SandboxPolicy =
    {
        Workspace = "/src/app"
        WorkspaceAccess = WorkspaceAccess.ReadWrite
        Writable = [ "/home/u/.fx/environments/e1/owned" ]
        Readable = [ "/home/u/.fx/runtimes" ]
        Protected = [ "/src/app/.git/hooks"; "/src/app/.git/config" ]
        Denied = [ "/home/u/.fx"; "/home/u/.ssh"; "/home/u/.netrc" ]
        Temp = Some "/home/u/.fx/environments/e1/tmp"
        Network = NetworkAccess.Denied
    }

let private ready
    (driver: string)
    (program: string)
    (features: string list)
    (files: (string * string) list)
    : SandboxReady =
    {
        Driver = driver
        Program = program
        Version = "1"
        Features = features
        Files = Map.ofList files
        Aliases = [ "/var/folders/x", "/private/var/folders/x" ]
        Notes = []
    }

let private request (ready: SandboxReady) (policy: SandboxPolicy) (terminal: bool) : SandboxRequest =
    {
        Ready = ready
        Policy = policy
        Paths =
            [
                "/src/app", PathKind.Directory
                "/src/app/.git", PathKind.Directory
                "/src/app/.git/hooks", PathKind.Directory
                "/src/app/.git/config", PathKind.File
                "/home/u/.fx", PathKind.Directory
                "/home/u/.fx/runtimes", PathKind.Directory
                "/home/u/.fx/environments/e1/owned", PathKind.Directory
                "/home/u/.fx/environments/e1/tmp", PathKind.Directory
                "/home/u/.ssh", PathKind.Directory
                "/home/u/.netrc", PathKind.File
            ]
            |> Map.ofList
        Shell = ShellSpec.posix
        Process =
            {
                Program = Program.Script "make test"
                Cwd = Some "/src/app"
                Env = Exactly [ "PATH", "/usr/bin" ]
                Terminal = terminal
            }
    }

let private spec (result: Result<ProcSpec, string>) : string list =
    match result with
    | Ok spec ->
        [
            match spec.Program with
            | Exec(file, args) -> "exec " + escape (String.Join(" ", file :: args))
            | other -> "program " + escape (string other)
            "cwd " + (spec.Cwd |> Option.defaultValue "-")
            "env " + escape (string spec.Env)
        ]
    | Error e -> [ "refused " + escape e ]

let private policies =
    Scenario.lines "sandbox-policy" (fun () ->
        [
            for rule in SandboxPolicy.rules policy do
                $"rule {rule.Root} {rule.Access}"
            for dir in SandboxPolicy.guarded policy do
                $"guarded {dir}"
            for path in
                [
                    "/src/app/a.fs"
                    "/src/app/.git/hooks/x"
                    "/home/u/.fx/runtimes/b"
                    "/home/u/.fx/c"
                    "/etc/hosts"
                ] do
                $"access {path} {SandboxPolicy.access policy path}"

            let reviewer =
                SandboxPolicy.tighten
                    { SandboxOverrides.readOnly with
                        Deny = [ ".env" ]
                        Protect = [ "tests" ]
                    }
                    policy

            for path in [ "/src/app/a.fs"; "/src/app/.env"; "/src/app/tests/x" ] do
                $"reviewer {path} {SandboxPolicy.access reviewer path}"

            "policy " + Json.serializeCanonical (Codec.encode SandboxPolicy.codec reviewer)

            match Json.parse """{"workspace": "read-only", "network": "denied", "protect": ["tests"]}""" with
            | Ok json ->
                match Codec.decode SandboxOverrides.codec json with
                | Ok overrides ->
                    "overrides "
                    + Json.serializeCanonical (Codec.encode SandboxOverrides.codec overrides)
                | Error issues -> "overrides invalid " + escape (Issue.renderAtMost 3 issues)
            | Error e -> $"json {e}"

            match Json.parse """{"workspace": "rw", "protect": ["../x"]}""" with
            | Ok json ->
                match Codec.decode SandboxOverrides.codec json with
                | Ok _ -> "bad overrides decoded"
                | Error issues -> "bad overrides " + escape (Issue.renderAtMost 3 issues)
            | Error e -> $"json {e}"

            let dotted = "/src/app/../../home/u/.ssh/./id"
            $"normalize {SandboxPath.normalize dotted}"
        ])

let private choosing =
    Scenario.lines "sandbox-choose" (fun () ->
        let drivers =
            [ Bubblewrap.driver; Seatbelt.driver; Fx.Ext.SandboxNone.SandboxNone.driver ]

        let named (sandbox: string option) =
            { EnvSpec.create "dev" "local" with
                Sandbox = sandbox
            }

        let machine: EnvMachine =
            {
                Target = TargetTriple.create Os.Linux Arch.X64 (Some Libc.Glibc)
                DataDir = "/home/u/.fx"
                SameHost = true
            }

        let layout =
            EnvLayout.create
                machine
                {
                    Id = "e1"
                    Provider = "local"
                    Workspace = "/src/app"
                    Home = "/home/u/.fx/environments/e1"
                    Data = Map.empty
                }

        [
            for host in [ "auto"; "none"; "bubblewrap" ] do
                for sandbox in [ None; Some "none"; Some "bubblewrap"; Some "seatbelt" ] do
                    for os in [ Os.Linux; Os.MacOs; Os.Windows ] do
                        let choice =
                            match EnvSandbox.choose host (named sandbox) os drivers with
                            | Ok choice -> string choice
                            | Error e -> "refused: " + e

                        let given = sandbox |> Option.defaultValue "-"
                        $"{host} {given} {os}: " + escape choice

            let environment =
                EnvSandbox.policy layout machine (Some "/home/u") [ "~/.cache/pkg" ] [] NetworkAccess.Allowed

            "environment "
            + Json.serializeCanonical (Codec.encode SandboxPolicy.codec environment)
        ])

let private bubblewrap =
    Scenario.lines "sandbox-bubblewrap" (fun () ->
        let full =
            ready
                "bubblewrap"
                "/usr/bin/bwrap"
                [ Bubblewrap.Features.DisableUserns; Bubblewrap.Features.Seccomp ]
                [
                    Bubblewrap.SeccompFile, "/home/u/.fx/environments/e1/sandbox/seccomp-x64.bpf"
                ]

        [
            yield! spec (Bubblewrap.wrap (request full policy false))
            yield! spec (Bubblewrap.wrap (request (ready "bubblewrap" "/usr/bin/bwrap" [] []) policy true))
            yield!
                spec (
                    Bubblewrap.wrap
                        { request full policy false with
                            Paths = (request full policy false).Paths |> Map.add "/home/u/.ssh" PathKind.Symlink
                        }
                )

            for arch in [ Arch.X64; Arch.Arm64 ] do
                let program = Seccomp.program arch
                $"seccomp {arch}: {(Seccomp.instructions arch).Length} instructions, {Seccomp.digest program}"

                for name, args in
                    [
                        "ptrace", []
                        "keyctl", []
                        "clone3", []
                        "unshare", [ Seccomp.CloneNewUser ]
                        "unshare", [ 0x20000u ]
                        "ioctl", [ 0u; 0x5412u ]
                        "ioctl", [ 0u; 0x5401u ]
                    ] do
                    match Seccomp.numberOf arch name with
                    | Some nr ->
                        let action =
                            Seccomp.evaluate (Seccomp.instructions arch) (Seccomp.auditArch arch) nr args

                        let shown = String.Join(",", args |> List.map (fun a -> a.ToString "x"))
                        $"  {name}({shown}) -> {action}"
                    | None -> $"  {name}: none"

            let parsed =
                Bubblewrap.parseDiscovery "bwrap=/usr/bin/bwrap\nversion=bubblewrap 0.9.0\napparmor=1\nalias=/a\t/b\n"

            $"discovery {parsed.Bwrap} {parsed.Version} apparmor={parsed.AppArmorRestricts} aliases={parsed.Aliases.Length}"

            "explain "
            + escape (string (Bubblewrap.explain parsed "loopback: Failed RTM_NEWADDR"))
        ])

let private seatbelt =
    Scenario.lines "sandbox-seatbelt" (fun () ->
        let sb = ready "seatbelt" Profile.SandboxExec [] []

        let rendered =
            Profile.render (
                request
                    sb
                    { policy with
                        Workspace = "/var/folders/x/ws"
                        Network = NetworkAccess.Allowed
                    }
                    false
            )

        [
            for rule in rendered.Rules do
                "rule " + escape rule
            for name, path in rendered.Parameters do
                $"param {name}={path}"
            yield!
                spec (Profile.wrap (request sb policy false))
                |> List.map (fun line ->
                    if line.Length > 160 then
                        line.Substring(0, 160) + "…"
                    else
                        line)
        ])

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 60.0).GetAwaiter().GetResult()

/// A process confined by this OS's driver: it reads the workspace, cannot write outside it, cannot
/// read a denied file, and its exit code passes through.
let private live =
    Scenario.lines "sandbox-live" (fun () ->
        let driver, manifest =
            if OperatingSystem.IsMacOS() then
                Seatbelt.driver, Seatbelt.manifest
            else
                Bubblewrap.driver, Bubblewrap.manifest

        let root =
            Path.Combine(Path.GetTempPath(), "sandbox-live-" + Guid.NewGuid().ToString("N").Substring(0, 8))

        let workspace = Path.Combine(root, "ws")
        let secret = Path.Combine(root, "secret")
        let outside = Path.Combine(root, "outside")

        for dir in [ workspace; secret; outside ] do
            Directory.CreateDirectory dir |> ignore

        File.WriteAllText(Path.Combine(workspace, "hello.txt"), "hello\n")
        File.WriteAllText(Path.Combine(secret, "key"), "key\n")

        try
            let shell = LocalShell.create ShellSpec.posix workspace

            let env: SandboxEnv =
                {
                    Extension = BasicExtensionEnv.forManifest SystemClock.clock manifest
                    FsRead = LocalFs.read
                    Fs = LocalFs.write
                    Shell = shell
                }

            let target =
                TargetTriple.ofRid Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier
                |> Option.get

            let site: SandboxSite =
                {
                    Target = target
                    Shell = ShellSpec.posix
                    Dir = Path.Combine(root, "driver")
                    Roots = [ root ]
                }

            match wait (SandboxEnv.within env (driver.Probe site) env.Extension) with
            | Error unavailable -> failwith $"{driver.Id} is not available: {unavailable}"
            | Ok ready ->
                let policy: SandboxPolicy =
                    {
                        Workspace = workspace
                        WorkspaceAccess = WorkspaceAccess.ReadWrite
                        Writable = []
                        Readable = []
                        Protected = []
                        Denied = [ secret ]
                        Temp = None
                        Network = NetworkAccess.Denied
                    }

                let kind (path: string) =
                    if Directory.Exists path then PathKind.Directory
                    elif File.Exists path then PathKind.File
                    else PathKind.Missing

                let request: SandboxRequest =
                    {
                        Ready = ready
                        Policy = policy
                        Paths = Sandbox.pathsToStat ready policy |> List.map (fun p -> p, kind p) |> Map.ofList
                        Shell = ShellSpec.posix
                        Process =
                            {
                                Program =
                                    Program.Script
                                        $"cat hello.txt; echo x > '{outside}/x' 2>/dev/null; cat '{secret}/key' 2>/dev/null || echo hidden; exit 7"
                                Cwd = Some workspace
                                Env = Inherit []
                                Terminal = false
                            }
                    }

                // T135: a confined process lives until it ends, also when the thread that started it
                // ends first (bubblewrap's --die-with-parent ties it to that thread).
                let outlives () : string =
                    let talker =
                        { request with
                            Process =
                                { request.Process with
                                    Program = Program.Script "echo ready; read line; echo \"got $line\""
                                }
                        }

                    match driver.Wrap talker with
                    | Error e -> failwith e
                    | Ok spec ->
                        let output = System.Text.StringBuilder()

                        let readUntil (proc: Proc) (stop: string -> bool) =
                            let mutable reading = true

                            while reading do
                                match wait (proc.Read Threading.CancellationToken.None) with
                                | Some(Stdout bytes) ->
                                    output.Append(System.Text.Encoding.UTF8.GetString bytes) |> ignore
                                    reading <- not (stop (output.ToString()))
                                | Some(Stderr _) -> ()
                                | None -> reading <- false

                        let mutable spawned = None

                        let asker =
                            Threading.Thread(fun () ->
                                match wait (shell.Spawn spec Threading.CancellationToken.None) with
                                | Ok proc ->
                                    readUntil proc (fun text -> text.Contains "ready")
                                    spawned <- Some proc
                                | Error e -> failwith (string e))

                        asker.Start()

                        if not (asker.Join(TimeSpan.FromSeconds 60.0)) then
                            failwith "the process did not start"

                        Threading.Thread.Sleep 500

                        match spawned with
                        | None -> failwith "nothing was spawned"
                        | Some proc ->
                            wait (
                                proc.Write (System.Text.Encoding.UTF8.GetBytes "x\n") Threading.CancellationToken.None
                            )

                            wait (proc.CloseStdin())
                            readUntil proc (fun _ -> false)
                            let code = wait (proc.Wait Threading.CancellationToken.None)
                            $"outlives the thread that started it: {escape (output.ToString())} exit {code}"

                match driver.Wrap request with
                | Error e -> failwith e
                | Ok spec ->
                    match wait (shell.Run (Command.ofSpec spec) Threading.CancellationToken.None) with
                    | Error e -> failwith (string e)
                    | Ok output ->
                        let written = File.Exists(Path.Combine(outside, "x"))

                        [
                            $"driver {driver.Id}"
                            "stdout " + escape (Output.stdoutText output)
                            $"exit {output.ExitCode}"
                            $"outside written {written}"
                            outlives ()
                        ]
        finally
            try
                Directory.Delete(root, true)
            with _ ->
                ())
    |> Scenario.where (fun p ->
        requested
        && (p.Os = Fx.Aot.Scenarios.Os.Linux || p.Os = Fx.Aot.Scenarios.Os.MacOS)
        && not p.Musl)

/// A build tool started as the host starts an agent's processes (T136): an environment's policy and
/// variables (`EnvSandbox.policy`, `EnvSandbox.defaults`) with the user's real home, so NuGet's user
/// config is denied as it is for agents, this OS's driver's probe and wrap, and `dotnet build` of a
/// small project in it, then the program it built.
let private buildTools =
    Scenario.lines "sandbox-build-tools" (fun () ->
        let driver, manifest =
            if OperatingSystem.IsMacOS() then
                Seatbelt.driver, Seatbelt.manifest
            else
                Bubblewrap.driver, Bubblewrap.manifest

        let root =
            Path.Combine(Path.GetTempPath(), "sandbox-build-" + Guid.NewGuid().ToString("N").Substring(0, 8))

        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

        let target =
            TargetTriple.ofRid Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier
            |> Option.get

        let machine: EnvMachine =
            {
                Target = target
                DataDir = Path.Combine(root, "data")
                SameHost = true
            }

        let layout =
            EnvLayout.create
                machine
                {
                    Id = "e1"
                    Provider = "local"
                    Workspace = Path.Combine(root, "ws")
                    Home = Path.Combine(root, "data", "environments", "e1")
                    Data = Map.empty
                }

        for dir in [ layout.Workspace; layout.Temp; layout.Cache; layout.Owned; layout.Sandbox ] do
            Directory.CreateDirectory dir |> ignore

        File.WriteAllText(
            Path.Combine(layout.Workspace, "tiny.csproj"),
            String.Join(
                "\n",
                [
                    "<Project Sdk=\"Microsoft.NET.Sdk\">"
                    "  <PropertyGroup>"
                    "    <OutputType>Exe</OutputType>"
                    "    <TargetFramework>net$(BundledNETCoreAppTargetFrameworkVersion)</TargetFramework>"
                    "    <OutputPath>out/</OutputPath>"
                    "    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>"
                    "    <NuGetAudit>false</NuGetAudit>"
                    "  </PropertyGroup>"
                    "</Project>"
                ]
            )
        )

        File.WriteAllText(
            Path.Combine(layout.Workspace, "Program.cs"),
            "System.Console.WriteLine(\"built in the sandbox\");"
        )

        try
            let shell = LocalShell.create ShellSpec.posix layout.Workspace

            let env: SandboxEnv =
                {
                    Extension = BasicExtensionEnv.forManifest SystemClock.clock manifest
                    FsRead = LocalFs.read
                    Fs = LocalFs.write
                    Shell = shell
                }

            let policy =
                EnvSandbox.policy layout machine (Some home) [] [] NetworkAccess.Allowed

            let site = EnvSandbox.site layout machine ShellSpec.posix (Some home) policy

            match wait (SandboxEnv.within env (driver.Probe site) env.Extension) with
            | Error unavailable -> failwith $"{driver.Id} is not available: {unavailable}"
            | Ok ready ->
                let variables =
                    [
                        for entry in Environment.GetEnvironmentVariables() |> Seq.cast<Collections.DictionaryEntry> do
                            match entry.Key, entry.Value with
                            | (:? string as name), (:? string as value) -> name, value
                            | _ -> ()
                    ]

                let userPackages =
                    EnvSandbox.nugetPackages (Some home) variables |> Option.filter Directory.Exists

                // What envd's stat says: links as links, not followed.
                let kind (path: string) =
                    match FileInfo(path).LinkTarget with
                    | null when Directory.Exists path -> PathKind.Directory
                    | null when File.Exists path -> PathKind.File
                    | null -> PathKind.Missing
                    | _ -> PathKind.Symlink

                let paths =
                    Sandbox.pathsToStat ready policy
                    |> List.map (fun p -> SandboxPath.trim p, kind p)
                    |> Map.ofList

                let userConfig = Path.Combine(home, ".nuget", "NuGet", "NuGet.Config")

                let script =
                    String.Join(
                        " && ",
                        [
                            "dotnet build -nologo -v q > build.log 2>&1 || { cat build.log; exit 1; }"
                            "dotnet out/tiny.dll"
                            "dotnet nuget config paths"
                            $"(cat '{userConfig}' > /dev/null 2>&1 && echo user config read || echo user config hidden)"
                        ]
                    )

                let request: SandboxRequest =
                    {
                        Ready = ready
                        Policy = policy
                        Paths = paths
                        Shell = ShellSpec.posix
                        Process =
                            {
                                Program = Program.Script script
                                Cwd = Some layout.Workspace
                                Env =
                                    EnvSandbox.defaults layout policy userPackages
                                    |> List.fold
                                        (fun env (name, value) -> Sandbox.withDefault name value env)
                                        (Inherit [])
                                Terminal = false
                            }
                    }

                match driver.Wrap request with
                | Error e -> failwith e
                | Ok spec ->
                    let output =
                        match
                            (shell.Run (Command.ofSpec spec) Threading.CancellationToken.None)
                                .WaitAsync(TimeSpan.FromMinutes 5.0)
                                .GetAwaiter()
                                .GetResult()
                        with
                        | Error e -> failwith (string e)
                        | Ok output -> output

                    let lines =
                        (Output.stdoutText output).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        |> Array.map _.Trim()
                        |> List.ofArray

                    [
                        $"driver {driver.Id}"
                        $"exit {output.ExitCode}"
                        if output.ExitCode <> 0 then
                            "stdout " + escape (Output.stdoutText output)
                            "stderr " + escape (Output.stderrText output)
                        for line in lines do
                            if line.StartsWith(layout.Cache, StringComparison.Ordinal) then
                                "nuget config in the environment's cache"
                            elif line = userConfig then
                                "nuget config of the user"
                            elif not (line.StartsWith '/') then
                                line
                    ]
        finally
            try
                Directory.Delete(root, true)
            with _ ->
                ())
    |> Scenario.where (fun p ->
        requested
        && (p.Os = Fx.Aot.Scenarios.Os.Linux || p.Os = Fx.Aot.Scenarios.Os.MacOS)
        && not p.Musl)

let scenarios: Scenario list =
    [ policies; choosing; bubblewrap; seatbelt; live; buildTools ]
