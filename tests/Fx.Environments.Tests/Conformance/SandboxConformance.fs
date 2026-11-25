/// The sandbox driver conformance suite (docs/modules/sandbox.md): a function from a driver to the
/// tests every driver must pass on the machines it runs on. It builds a scratch machine (a workspace,
/// a home with credentials, the product's data directory with an environment's home, a directory
/// outside them all), probes the driver there, and runs real processes through its wrap on this
/// machine's shell and terminals, checking from outside what they could and could not do. A driver
/// that confines nothing (`none`) must say so and leave processes as they are.
///
/// Linked into the drivers' test projects; `Fx.Environments.Tests` runs it on `none`.
module Fx.Environments.Tests.Conformance.SandboxConformance

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx
open Fx.Effects
open Fx.Effects.Live
open Fx.Pty
open Fx.Sdk

/// What the suite needs to know about a driver.
[<NoEquality; NoComparison>]
type Subject =
    {
        Name: string
        Driver: SandboxDriver
        /// The driver's extension environment (its probe runs in it).
        Extension: IExtensionEnv
        /// The driver must be available here: `false` skips the live tests where its probe says it
        /// cannot run (a developer's machine without bwrap); CI requires it (`SANDBOX_TESTS=1`).
        Required: bool
    }

    override this.ToString() = this.Name

/// `SANDBOX_TESTS=1`: the confining drivers of this OS must be available (CI provides them).
let required () =
    Environment.GetEnvironmentVariable "SANDBOX_TESTS" = "1"

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 120.0).GetAwaiter().GetResult()

let private windows = OperatingSystem.IsWindows()

/// This machine's target.
let target: TargetTriple =
    match TargetTriple.ofRid RuntimeInformation.RuntimeIdentifier with
    | Some target -> target
    | None -> failwith ("unknown runtime identifier " + RuntimeInformation.RuntimeIdentifier)

let private shellSpec = LocalShell.defaultShell ()

/// A scratch machine for one subject: every fixture is a directory under one root, which the probe
/// resolves once (its real path, e.g. `/private/var/…` on macOS).
type Machine(name: string) =
    let root =
        Path.Combine(Path.GetTempPath(), $"sandbox-{name}-" + Guid.NewGuid().ToString("N").Substring(0, 8))

    do
        Directory.CreateDirectory root |> ignore

        AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
            try
                if Directory.Exists root then
                    for file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) do
                        File.SetAttributes(file, FileAttributes.Normal)

                    Directory.Delete(root, true)
            with _ ->
                ())

    member _.Root = root

    member _.Fresh() =
        let dir = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0, 8))
        Directory.CreateDirectory dir |> ignore
        dir

/// One fixture: the directories of a scratch machine and the policy over them.
type Fixture =
    {
        Dir: string
        Workspace: string
        Home: string
        Data: string
        EnvHome: string
        Owned: string
        Temp: string
        Outside: string
        Policy: SandboxPolicy
    }

    override this.ToString() = $"fixture {this.Dir}"

let private write (path: string) (text: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
    File.WriteAllText(path, text)

/// A fresh fixture: `hello.txt`, protected `.git/hooks`, `.git/config` and the project directory in
/// the workspace; `.ssh/id` in the home; the data directory with an environment's home and a
/// component; a directory outside everything. The policy is what the host gives an environment.
let fixture (machine: Machine) : Fixture =
    let dir = machine.Fresh()

    let at (parts: string list) =
        Path.Combine(Array.ofList (dir :: parts))

    let workspace = at [ "workspace" ]
    let home = at [ "home" ]
    let data = at [ "home"; Product.homeDirName ]
    let envHome = at [ "home"; Product.homeDirName; "environments"; "e1" ]
    let owned = Path.Combine(envHome, "owned")
    let temp = Path.Combine(envHome, "tmp")
    let outside = at [ "outside" ]

    write (Path.Combine(workspace, "hello.txt")) "hello\n"
    write (Path.Combine(workspace, ".git", "config")) "[core]\n"
    Directory.CreateDirectory(Path.Combine(workspace, ".git", "hooks")) |> ignore
    write (Path.Combine(workspace, Product.projectDirName, "agents.md")) "agents\n"
    write (Path.Combine(home, ".ssh", "id")) "secret key\n"
    write (Path.Combine(data, "credentials", "credentials.json")) "{}\n"
    write (Path.Combine(data, "runtimes", "tool", "version.txt")) "1.0\n"

    for d in [ owned; temp; outside; Path.Combine(envHome, "sandbox") ] do
        Directory.CreateDirectory d |> ignore

    {
        Dir = dir
        Workspace = workspace
        Home = home
        Data = data
        EnvHome = envHome
        Owned = owned
        Temp = temp
        Outside = outside
        Policy =
            {
                Workspace = workspace
                WorkspaceAccess = WorkspaceAccess.ReadWrite
                Writable = [ owned ]
                Readable = [ Path.Combine(data, "runtimes") ]
                Protected = SandboxPolicy.protectedInWorkspace |> List.map (SandboxPath.under workspace)
                Denied = data :: SandboxPolicy.secrets target.Os (Some home)
                Temp = Some temp
                Network = NetworkAccess.Denied
            }
    }

/// What a path is now, as the host's `fs/stat` would say (links not followed).
let kindOf (path: string) : PathKind =
    let info = FileInfo path

    if not (isNull info.LinkTarget) then PathKind.Symlink
    elif Directory.Exists path then PathKind.Directory
    elif File.Exists path then PathKind.File
    else PathKind.Missing

/// The request for a process under a policy, with the paths as they are now.
let request (ready: SandboxReady) (policy: SandboxPolicy) (proc: SandboxProcess) : SandboxRequest =
    {
        Ready = ready
        Policy = policy
        Paths =
            Sandbox.pathsToStat ready policy
            |> List.map (fun p -> SandboxPath.trim p, kindOf p)
            |> Map.ofList
        Shell = shellSpec
        Process = proc
    }

/// Probes the driver on the scratch machine, in its extension's environment, with this machine's
/// files and processes.
let probe (subject: Subject) (machine: Machine) : Result<SandboxReady, SandboxUnavailable> =
    let env: SandboxEnv =
        {
            Extension = subject.Extension
            FsRead = LocalFs.read
            Fs = LocalFs.write
            Shell = LocalShell.create shellSpec machine.Root
        }

    let site: SandboxSite =
        {
            Target = target
            Shell = shellSpec
            Dir = Path.Combine(machine.Root, "driver")
            Roots = [ machine.Root ]
        }

    wait (SandboxEnv.within env (subject.Driver.Probe site) subject.Extension)

/// Runs a script in the sandbox under a policy, in the workspace.
let run (subject: Subject) (ready: SandboxReady) (policy: SandboxPolicy) (script: string) : Output =
    let proc =
        {
            Program = Program.Script script
            Cwd = Some policy.Workspace
            Env = Inherit []
            Terminal = false
        }

    match subject.Driver.Wrap(request ready policy proc) with
    | Error e -> failtest $"the driver refused the process: {e}"
    | Ok spec ->
        let shell = LocalShell.create shellSpec policy.Workspace

        match wait (shell.Run (Command.ofSpec spec) CancellationToken.None) with
        | Ok output -> output
        | Error e -> failtest $"the sandboxed process did not start: {e}"

/// Whether a program is on `PATH`.
let onPath (program: string) : bool =
    (Environment.GetEnvironmentVariable "PATH"
     |> Option.ofObj
     |> Option.defaultValue "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
    |> Array.exists (fun dir -> File.Exists(Path.Combine(dir, program)))

let private stdout (output: Output) = (Output.stdoutText output).Trim()

let private lines (output: Output) =
    (Output.stdoutText output).Split('\n', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun l -> l.Trim())
    |> List.ofArray

/// Runs a test with a fresh fixture and the driver's probe; skips it where the driver cannot run
/// and is not required.
let private live
    (subject: Subject)
    (machine: Lazy<Machine>)
    (ready: Lazy<Result<SandboxReady, SandboxUnavailable>>)
    (name: string)
    (body: SandboxReady -> Fixture -> unit)
    =
    test name {
        if not (List.contains target.Os subject.Driver.Platforms) then
            skiptest $"{subject.Driver.Id} does not run on {target.Os}"

        match ready.Force() with
        | Error unavailable when subject.Required ->
            failtest $"{subject.Driver.Id} must be available here: {unavailable}"
        | Error unavailable -> skiptest $"{subject.Driver.Id} is not available here: {unavailable}"
        | Ok ready -> body ready (fixture (machine.Force()))
    }

/// The tests every sandbox driver passes.
let tests (subject: Subject) : Test =
    let machine = lazy (Machine subject.Driver.Id)
    let ready = lazy (probe subject (machine.Force()))
    let live = live subject machine ready

    let confining (name: string) (body: SandboxReady -> Fixture -> unit) =
        live name (fun ready f ->
            if not subject.Driver.Confines then
                skiptest "the driver confines nothing"

            body ready f)

    testSequenced
    <| testList
        $"sandbox driver conformance ({subject})"
        [
            test "the driver's data is valid" {
                Expect.isEmpty (SandboxDrivers.validate subject.Driver) "issues"

                Expect.equal
                    (subject.Driver.Id = SandboxDrivers.NoSandbox)
                    (not subject.Driver.Confines)
                    "only none confines nothing"
            }

            live "the probe says it can sandbox here, and how" (fun ready _ ->
                Expect.equal ready.Driver subject.Driver.Id "the ready driver is the driver"

                if subject.Driver.Confines then
                    Expect.isTrue (SandboxPath.isAbsolute ready.Program) "the program is an absolute path"

                for KeyValue(_, file) in ready.Files do
                    Expect.isTrue (File.Exists file) "the probe's files exist")

            live "normal commands run, with their exit codes and output" (fun ready f ->
                let output = run subject ready f.Policy "echo hi"
                Expect.equal (output.ExitCode, stdout output) (0, "hi") "echo"
                let output = run subject ready f.Policy "exit 3"
                Expect.equal output.ExitCode 3 "the exit code passes through"

                if not windows then
                    let output = run subject ready f.Policy "cat hello.txt"
                    Expect.equal (stdout output) "hello" "in the workspace")

            live "a driver that confines nothing leaves processes as they are" (fun ready f ->
                if subject.Driver.Confines then
                    skiptest "the driver confines"

                let proc =
                    {
                        Program = Program.Script "echo hi"
                        Cwd = Some f.Workspace
                        Env = Inherit [ "A", Some "b" ]
                        Terminal = false
                    }

                match subject.Driver.Wrap(request ready f.Policy proc) with
                | Ok spec ->
                    Expect.equal (string spec.Program) (string proc.Program) "the same program"
                    Expect.equal spec.Cwd proc.Cwd "the same directory"
                    Expect.equal (string spec.Env) (string proc.Env) "the same variables"
                | Error e -> failtest e

                let target = Path.Combine(f.Outside, "landed.txt")

                let script =
                    if windows then
                        $"Set-Content -Path '{target}' -Value x"
                    else
                        $"echo x > '{target}'"

                run subject ready f.Policy script |> ignore
                Expect.isTrue (File.Exists target) "unconfined: a write outside the workspace lands")

            confining "writes land in the workspace, the owned directories and the temporary directory" (fun ready f ->
                let owned = Path.Combine(f.Owned, "o.txt")

                let script =
                    String.Join(
                        "\n",
                        [
                            "echo w > new.txt"
                            $"echo o > '{owned}'"
                            "echo t > \"$TMPDIR/t.txt\""
                            "echo \"$TMPDIR\""
                        ]
                    )

                let output = run subject ready f.Policy script
                Expect.equal output.ExitCode 0 $"the writes worked: {Output.stderrText output}"
                Expect.equal (File.ReadAllText(Path.Combine(f.Workspace, "new.txt"))) "w\n" "the workspace"
                Expect.equal (File.ReadAllText(Path.Combine(f.Owned, "o.txt"))) "o\n" "the owned directory"

                Expect.equal
                    (File.ReadAllText(Path.Combine(f.Temp, "t.txt")))
                    "t\n"
                    "TMPDIR is the policy's temporary directory"

                Expect.equal (List.last (lines output)) f.Temp "TMPDIR")

            confining "the directories above a writable path inside a denied one can be looked up" (fun ready f ->
                let deep = Path.Combine(f.Owned, "a", "b")

                let output =
                    run subject ready f.Policy $"mkdir -p '{deep}' && cd '{f.Owned}' && echo made"

                Expect.equal output.ExitCode 0 $"mkdir -p reached the owned directory: {Output.stderrText output}"
                Expect.equal (stdout output) "made" "made"
                Expect.isTrue (Directory.Exists deep) "the directories were made")

            confining "writes outside the allowed paths never land" (fun ready f ->
                let targets =
                    [
                        Path.Combine(f.Outside, "x.txt")
                        Path.Combine(f.Home, "x.txt")
                        Path.Combine(f.Data, "x.txt")
                        Path.Combine(f.EnvHome, "x.txt")
                        Path.Combine(f.Data, "runtimes", "tool", "x.txt")
                    ]

                let script =
                    targets
                    |> List.map (fun t -> $"echo x > '{t}' 2>/dev/null; true")
                    |> fun lines -> String.Join("\n", lines)

                run subject ready f.Policy script |> ignore

                for t in targets do
                    Expect.isFalse (File.Exists t) $"{t} was not written"

                Expect.equal
                    (File.ReadAllText(Path.Combine(f.Data, "runtimes", "tool", "version.txt")))
                    "1.0\n"
                    "a component is unchanged")

            confining "denied paths cannot be read" (fun ready f ->
                let key = Path.Combine(f.Home, ".ssh", "id")
                let credentials = Path.Combine(f.Data, "credentials", "credentials.json")
                let ssh = Path.Combine(f.Home, ".ssh")

                let script =
                    String.Join(
                        "\n",
                        [
                            $"cat '{key}' 2>/dev/null || echo no-key"
                            $"cat '{credentials}' 2>/dev/null || echo no-credentials"
                            $"ls '{ssh}' 2>/dev/null"
                        ]
                    )

                let output = run subject ready f.Policy script
                let seen = Output.stdoutText output
                Expect.isFalse (seen.Contains "secret key") "the key stays hidden"
                Expect.isFalse (seen.Contains "{}") "the product's credentials stay hidden"
                Expect.isFalse (seen.Contains "id") "the directory's entries stay hidden"
                Expect.contains (lines output) "no-key" "reading the key failed"
                Expect.contains (lines output) "no-credentials" "reading the credentials failed")

            confining "readable paths inside denied ones can be read" (fun ready f ->
                let version = Path.Combine(f.Data, "runtimes", "tool", "version.txt")
                let output = run subject ready f.Policy $"cat '{version}'"

                Expect.equal (stdout output) "1.0" "a component under the data directory")

            confining "protected paths stay read-only inside a writable workspace" (fun ready f ->
                let script =
                    String.Join(
                        "\n",
                        [
                            "echo evil >> .git/config 2>/dev/null; true"
                            "echo evil > .git/hooks/pre-commit 2>/dev/null; true"
                            $"echo evil >> {Product.projectDirName}/agents.md 2>/dev/null; true"
                            "mv .git moved-git 2>/dev/null; true"
                            $"mv {Product.projectDirName} moved-project 2>/dev/null; true"
                            "echo ok"
                        ]
                    )

                let output = run subject ready f.Policy script
                Expect.equal (stdout output) "ok" "the script ran"
                Expect.equal (File.ReadAllText(Path.Combine(f.Workspace, ".git", "config"))) "[core]\n" "git's config"
                Expect.isFalse (File.Exists(Path.Combine(f.Workspace, ".git", "hooks", "pre-commit"))) "git's hooks"

                Expect.equal
                    (File.ReadAllText(Path.Combine(f.Workspace, Product.projectDirName, "agents.md")))
                    "agents\n"
                    "the project's directory"

                Expect.isTrue (Directory.Exists(Path.Combine(f.Workspace, ".git"))) ".git was not moved away"
                Expect.isFalse (Directory.Exists(Path.Combine(f.Workspace, "moved-git"))) "no moved .git")

            confining "a read-only workspace blocks writes, not reads" (fun ready f ->
                let policy = SandboxPolicy.tighten SandboxOverrides.readOnly f.Policy

                let output =
                    run subject ready policy "echo x > ro.txt 2>/dev/null; rm -f hello.txt 2>/dev/null; cat hello.txt"

                Expect.equal (stdout output) "hello" "reading works"
                Expect.isFalse (File.Exists(Path.Combine(f.Workspace, "ro.txt"))) "nothing was written"
                Expect.isTrue (File.Exists(Path.Combine(f.Workspace, "hello.txt"))) "nothing was deleted")

            confining "overrides deny and protect more" (fun ready f ->
                write (Path.Combine(f.Workspace, ".env")) "TOKEN=1\n"
                write (Path.Combine(f.Workspace, "tests", "a.txt")) "a\n"

                let policy =
                    SandboxPolicy.tighten
                        { SandboxOverrides.none with
                            Deny = [ ".env" ]
                            Protect = [ "tests" ]
                        }
                        f.Policy

                let output =
                    run subject ready policy "cat .env 2>/dev/null; echo b > tests/a.txt 2>/dev/null; echo done"

                Expect.isFalse ((Output.stdoutText output).Contains "TOKEN") "the denied file is hidden"
                Expect.equal (File.ReadAllText(Path.Combine(f.Workspace, "tests", "a.txt"))) "a\n" "the protected file")

            confining "a denied network refuses connections, an allowed one makes them" (fun ready f ->
                if not (File.Exists "/bin/bash") then
                    skiptest "needs /bin/bash for /dev/tcp"

                use listener = new TcpListener(IPAddress.Loopback, 0)
                listener.Start()
                let port = (listener.LocalEndpoint :?> IPEndPoint).Port

                let accepting =
                    task {
                        while true do
                            let! client = listener.AcceptTcpClientAsync()
                            client.Dispose()
                    }

                ignore accepting

                let connect =
                    $"/bin/bash -c 'exec 3<>/dev/tcp/127.0.0.1/{port}' 2>/dev/null && echo connected || echo refused"

                let denied = run subject ready f.Policy connect
                Expect.equal (stdout denied) "refused" "no network"

                let allowed =
                    run
                        subject
                        ready
                        { f.Policy with
                            Network = NetworkAccess.Allowed
                        }
                        connect

                Expect.equal (stdout allowed) "connected" "the network"
                listener.Stop())

            confining "a process on a terminal keeps it" (fun ready f ->
                let proc =
                    {
                        Program = Program.Script "[ -t 0 ] && [ -t 1 ] && echo on-a-terminal; exit 0"
                        Cwd = Some f.Workspace
                        Env = Inherit []
                        Terminal = true
                    }

                match subject.Driver.Wrap(request ready f.Policy proc) with
                | Error e -> failtest e
                | Ok spec ->
                    let terminal = LocalTerminal.create shellSpec f.Workspace

                    match
                        wait (
                            terminal.Spawn
                                {
                                    Program = spec.Program
                                    Cwd = spec.Cwd
                                    Env = spec.Env
                                    Size = PtySize.standard
                                }
                                CancellationToken.None
                        )
                    with
                    | Error e -> failtest $"{e}"
                    | Ok pty ->
                        let output = StringBuilder()
                        let mutable reading = true

                        while reading do
                            match wait (pty.Read CancellationToken.None) with
                            | Some bytes -> output.Append(Encoding.UTF8.GetString bytes) |> ignore
                            | None -> reading <- false

                        let code = wait (pty.Wait CancellationToken.None)
                        wait (pty.Close())
                        Expect.equal code 0 "it exited cleanly"
                        Expect.stringContains (string output) "on-a-terminal" "it has its terminal")

            confining "git works in the workspace" (fun ready f ->
                if not (onPath "git") then
                    skiptest "git is not installed"

                let fresh = Path.Combine(f.Dir, "repo")
                Directory.CreateDirectory fresh |> ignore
                File.WriteAllText(Path.Combine(fresh, "a.txt"), "a\n")
                let policy = { f.Policy with Workspace = fresh }
                let init = run subject ready policy "git init -q && echo initialized"
                Expect.equal (stdout init) "initialized" $"git init: {Output.stderrText init}"

                let commit =
                    run
                        subject
                        ready
                        policy
                        "git add -A && git -c user.name=t -c user.email=t@example.com commit -q -m first && git log --oneline | wc -l"

                Expect.equal (stdout commit) "1" $"git commit: {Output.stderrText commit}")
        ]
