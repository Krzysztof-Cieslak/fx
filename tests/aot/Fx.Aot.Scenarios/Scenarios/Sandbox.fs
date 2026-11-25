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

let scenarios: Scenario list = [ policies; choosing; bubblewrap; seatbelt; live ]
