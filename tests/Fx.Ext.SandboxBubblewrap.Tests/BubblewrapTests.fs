/// The bwrap command lines a policy becomes, and the probe's diagnosis.
module Fx.Ext.SandboxBubblewrap.Tests.BubblewrapTests

open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Ext.SandboxBubblewrap
open Fx.Sdk

let private policy: SandboxPolicy =
    {
        Workspace = "/src/app"
        WorkspaceAccess = WorkspaceAccess.ReadWrite
        Writable = [ "/home/u/.fx/environments/e1/owned" ]
        Readable = [ "/home/u/.fx/runtimes" ]
        Protected = [ "/src/app/.git/hooks"; "/src/app/.git/config"; "/src/app/.fx" ]
        Denied = [ "/home/u/.fx"; "/home/u/.ssh"; "/home/u/.netrc"; "/home/u/.aws" ]
        Temp = Some "/home/u/.fx/environments/e1/tmp"
        Network = NetworkAccess.Denied
    }

let private ready: SandboxReady =
    {
        Driver = Bubblewrap.Id
        Program = "/usr/bin/bwrap"
        Version = "0.9.0"
        Features = [ Bubblewrap.Features.DisableUserns; Bubblewrap.Features.Seccomp ]
        Files =
            Map.ofList
                [
                    Bubblewrap.SeccompFile, "/home/u/.fx/environments/e1/sandbox/seccomp-x64.bpf"
                ]
        Aliases = []
        Notes = []
    }

/// What exists: everything but `.aws` and `.fx` in the workspace; `.netrc` is a file.
let private paths: Map<string, PathKind> =
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

let private request (proc: SandboxProcess) : SandboxRequest =
    {
        Ready = ready
        Policy = policy
        Paths = paths
        Shell = ShellSpec.posix
        Process = proc
    }

let private script (text: string) : SandboxProcess =
    {
        Program = Program.Script text
        Cwd = None
        Env = Inherit []
        Terminal = false
    }

let private argv (request: SandboxRequest) : string list =
    match Bubblewrap.wrap request with
    | Ok { Program = Exec(file, args) } -> file :: args
    | Ok other -> failtest $"{other}"
    | Error e -> failtest e

[<Tests>]
let wrapping =
    testList
        "bubblewrap: wrapping"
        [
            test "a script becomes bwrap with the seccomp program on descriptor 3" {
                Expect.equal
                    (argv (request (script "make test")))
                    [
                        "/bin/sh"
                        "-c"
                        Bubblewrap.SeccompLauncher
                        "/home/u/.fx/environments/e1/sandbox/seccomp-x64.bpf"
                        "/usr/bin/bwrap"
                        "--seccomp"
                        "3"
                        "--die-with-parent"
                        "--unshare-all"
                        "--unshare-user"
                        "--disable-userns"
                        "--new-session"
                        "--ro-bind"
                        "/"
                        "/"
                        "--dev"
                        "/dev"
                        "--proc"
                        "/proc"
                        "--tmpfs"
                        "/tmp"
                        // Shortest first; what does not exist (.aws, the workspace's .fx) is left out.
                        "--bind"
                        "/src/app"
                        "/src/app"
                        "--tmpfs"
                        "/home/u/.fx"
                        "--tmpfs"
                        "/home/u/.ssh"
                        // .git, between the workspace and its protected paths, bound onto itself.
                        "--bind"
                        "/src/app/.git"
                        "/src/app/.git"
                        "--ro-bind"
                        "/dev/null"
                        "/home/u/.netrc"
                        "--ro-bind"
                        "/src/app/.git/hooks"
                        "/src/app/.git/hooks"
                        "--ro-bind"
                        "/home/u/.fx/runtimes"
                        "/home/u/.fx/runtimes"
                        "--ro-bind"
                        "/src/app/.git/config"
                        "/src/app/.git/config"
                        "--bind"
                        "/home/u/.fx/environments/e1/tmp"
                        "/home/u/.fx/environments/e1/tmp"
                        "--bind"
                        "/home/u/.fx/environments/e1/owned"
                        "/home/u/.fx/environments/e1/owned"
                        "--remount-ro"
                        "/home/u/.fx"
                        "--remount-ro"
                        "/home/u/.ssh"
                        "--"
                        "/bin/sh"
                        "-c"
                        "make test"
                    ]
                    "the command line"
            }

            test "TMPDIR is the policy's temporary directory; the working directory is kept" {
                match
                    Bubblewrap.wrap (
                        request
                            { script "x" with
                                Cwd = Some "/src/app/sub"
                            }
                    )
                with
                | Ok spec ->
                    Expect.equal spec.Cwd (Some "/src/app/sub") "cwd"

                    Expect.equal
                        (string spec.Env)
                        (string (Inherit [ "TMPDIR", Some "/home/u/.fx/environments/e1/tmp" ]))
                        "TMPDIR"

                    match spec.Program with
                    | Exec(_, args) ->
                        let at = args |> List.findIndex ((=) "--chdir")
                        Expect.equal args[at + 1] "/src/app/sub" "--chdir"
                    | other -> failtest $"{other}"
                | Error e -> failtest e
            }

            test "the network, terminals, and programs without a shell" {
                let allowed =
                    argv
                        { request (script "x") with
                            Policy =
                                { policy with
                                    Network = NetworkAccess.Allowed
                                }
                        }

                Expect.contains allowed "--share-net" "the network is shared when allowed"
                Expect.isFalse (List.contains "--share-net" (argv (request (script "x")))) "and not otherwise"

                let terminal = argv (request { script "x" with Terminal = true })
                Expect.isFalse (List.contains "--new-session" terminal) "a terminal keeps its session"

                let exec =
                    argv (
                        request
                            { script "x" with
                                Program = Exec("git", [ "status" ])
                            }
                    )

                Expect.equal
                    (exec |> List.skipWhile ((<>) "--") |> List.tail)
                    [ "git"; "status" ]
                    "the program and its arguments"
            }

            test "without seccomp or --disable-userns, bwrap runs directly" {
                let plain =
                    argv
                        { request (script "x") with
                            Ready =
                                { ready with
                                    Features = []
                                    Files = Map.empty
                                }
                        }

                Expect.equal plain.Head "/usr/bin/bwrap" "bwrap"
                Expect.isFalse (List.contains "--disable-userns" plain) "no --disable-userns"
                Expect.isFalse (List.contains "--unshare-user" plain) "no forced user namespace"
            }

            test "a read-only workspace is bound read-only" {
                let ro =
                    argv
                        { request (script "x") with
                            Policy = SandboxPolicy.tighten SandboxOverrides.readOnly policy
                        }

                let at = ro |> List.findIndex ((=) "/src/app")
                Expect.equal ro[at - 1] "--ro-bind" "--ro-bind"
                Expect.isFalse (List.contains "/src/app/.git" ro) "nothing writable to guard"
            }

            test "real paths from the probe" {
                let aliased =
                    { request (script "x") with
                        Ready =
                            { ready with
                                Aliases = [ "/src/app", "/data/app" ]
                            }
                        Paths = paths |> Map.add "/data/app" PathKind.Directory
                    }

                let args = argv aliased
                Expect.contains args "/data/app" "the workspace's real path"
                Expect.isFalse (List.contains "/src/app" args) "not the link"
            }

            test "a denied or protected path that is an unresolved link refuses the start" {
                let linked =
                    { request (script "x") with
                        Paths = paths |> Map.add "/home/u/.ssh" PathKind.Symlink
                    }

                match Bubblewrap.wrap linked with
                | Error e -> Expect.stringContains e "/home/u/.ssh" "names it"
                | Ok spec -> failtest $"{spec}"

                let writable =
                    { request (script "x") with
                        Paths = paths |> Map.add "/home/u/.fx/environments/e1/owned" PathKind.Symlink
                    }

                let args = argv writable
                Expect.isFalse (List.contains "/home/u/.fx/environments/e1/owned" args) "a writable link is not granted"
            }
        ]

let private site: SandboxSite =
    {
        Target = TargetTriple.create Os.Linux Arch.X64 (Some Libc.Glibc)
        Shell = ShellSpec.posix
        Dir = "/home/u/.fx/environments/e1/sandbox"
        Roots = [ "/src/app" ]
    }

let private discovery (bwrap: string) (apparmor: string) =
    $"bwrap={bwrap}\nversion=bubblewrap 0.9.0\napparmor={apparmor}\nuserns_clone=\nmax_userns=31646\nalias=/src/app\t/data/app\n"

/// Probes with a scripted shell: the discovery script answers `discovered`, bwrap's trials `trial`.
let private probe (target: TargetTriple) (discovered: string) (trial: Program -> Output) =
    // The in-memory files have the path syntax of the machine the tests run on.
    if System.OperatingSystem.IsWindows() then
        skiptest "Unix paths in the in-memory files"

    let clock = SystemClock.clock
    let fs = InMemoryFs(clock, false)

    let shell =
        ScriptedShell(fun command ->
            match command.Spec.Program with
            | Exec("/bin/sh", "-c" :: script :: _) when script = Bubblewrap.discoveryScript ->
                Some(ScriptedShell.ok discovered)
            | program -> Some(Ok(trial program)))

    let env: SandboxEnv =
        {
            Extension = BasicExtensionEnv.forManifest clock Bubblewrap.manifest
            FsRead = fs.FsRead
            Fs = fs.Fs
            Shell = shell.Shell
        }

    let result =
        (SandboxEnv.within env (Bubblewrap.probe { site with Target = target }) env.Extension).GetAwaiter().GetResult()

    result, fs, shell

[<Tests>]
let probing =
    testList
        "bubblewrap: probing"
        [
            test "available: bwrap, --disable-userns and the seccomp program" {
                match probe site.Target (discovery "/usr/bin/bwrap" "0") (fun _ -> ScriptedShell.output 0 "" "") with
                | Ok ready, fs, shell ->
                    Expect.equal ready.Program "/usr/bin/bwrap" "bwrap"
                    Expect.equal ready.Version "0.9.0" "version"

                    Expect.equal
                        ready.Features
                        [ Bubblewrap.Features.DisableUserns; Bubblewrap.Features.Seccomp ]
                        "features"

                    Expect.equal ready.Aliases [ "/src/app", "/data/app" ] "aliases"
                    let file = ready.Files[Bubblewrap.SeccompFile]
                    Expect.stringStarts file site.Dir "in the site's directory"

                    match
                        (fs.FsRead.ReadFile file System.Threading.CancellationToken.None).GetAwaiter().GetResult()
                    with
                    | Ok bytes -> Expect.equal bytes (Seccomp.program Arch.X64) "the x64 program"
                    | Error e -> failtest $"{e}"

                    Expect.equal shell.Calls.Length 4 "discovery and three trials"
                | Error e, _, _ -> failtest $"{e}"
            }

            test "not on Linux" {
                match probe (TargetTriple.create Os.MacOs Arch.Arm64 None) "" (fun _ -> failwith "no trial") with
                | Error unavailable, _, _ -> Expect.stringContains unavailable.Reason "Linux only" "why"
                | other -> failtest $"{other}"
            }

            test "bwrap is not installed" {
                match probe site.Target (discovery "" "") (fun _ -> failwith "no trial") with
                | Error unavailable, _, _ ->
                    Expect.stringContains unavailable.Reason "not installed" "why"
                    Expect.stringContains (unavailable.Remedy |> Option.defaultValue "") "apt install bubblewrap" "how"
                | other -> failtest $"{other}"
            }

            test "AppArmor restricts user namespaces (Ubuntu 24.04)" {
                let failing _ =
                    ScriptedShell.output 1 "" "bwrap: loopback: Failed RTM_NEWADDR: Operation not permitted"

                match probe site.Target (discovery "/usr/bin/bwrap" "1") failing with
                | Error unavailable, _, _ ->
                    Expect.stringContains unavailable.Reason "AppArmor" "why"
                    Expect.stringContains unavailable.Reason "RTM_NEWADDR" "what bwrap said"
                    let remedy = unavailable.Remedy |> Option.defaultValue ""
                    Expect.stringContains remedy "userns," "the AppArmor profile"
                    Expect.stringContains remedy "kernel.apparmor_restrict_unprivileged_userns=0" "or the sysctl"
                | other -> failtest $"{other}"
            }

            test "an optional feature that fails is left out, with a note" {
                let trial (program: Program) =
                    match program with
                    | Exec(_, args) when List.contains "--seccomp" args ->
                        ScriptedShell.output 1 "" "bwrap: seccomp: Invalid argument"
                    | _ -> ScriptedShell.output 0 "" ""

                match probe site.Target (discovery "/usr/bin/bwrap" "0") trial with
                | Ok ready, _, _ ->
                    Expect.equal ready.Features [ Bubblewrap.Features.DisableUserns ] "no seccomp"
                    Expect.stringContains (String.concat " " ready.Notes) "seccomp" "says so"
                | other -> failtest $"{other}"
            }

            test "versions" {
                Expect.isTrue (Bubblewrap.atLeast 0 8 "0.9.0") "0.9"
                Expect.isFalse (Bubblewrap.atLeast 0 8 "0.6.3") "0.6"
                Expect.isTrue (Bubblewrap.atLeast 0 8 "1.0") "1.0"
                Expect.isFalse (Bubblewrap.atLeast 0 8 "") "unknown"
            }
        ]
