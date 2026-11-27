/// The Seatbelt profiles a policy becomes, and the probe's diagnosis. The profiles' behaviour is
/// the conformance suite's to check, on macOS (tests/Fx.Environments.Tests).
module Fx.Ext.SandboxSeatbelt.Tests.SeatbeltTests

open System
open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Ext.SandboxSeatbelt
open Fx.Sdk

let private policy: SandboxPolicy =
    {
        Workspace = "/Users/u/src/app"
        WorkspaceAccess = WorkspaceAccess.ReadWrite
        Writable = [ "/Users/u/.fx/environments/e1/owned" ]
        Readable = [ "/Users/u/.fx/runtimes" ]
        Protected = [ "/Users/u/src/app/.git/hooks"; "/Users/u/src/app/.git/config" ]
        Denied = [ "/Users/u/.fx"; "/Users/u/.ssh" ]
        Temp = Some "/Users/u/.fx/environments/e1/tmp"
        Network = NetworkAccess.Denied
    }

let private ready: SandboxReady =
    {
        Driver = Seatbelt.Id
        Program = Profile.SandboxExec
        Version = "26.0"
        Features = []
        Files = Map.empty
        Aliases = []
        Notes = []
    }

let private request (policy: SandboxPolicy) : SandboxRequest =
    {
        Ready = ready
        Policy = policy
        Paths = Map.empty
        Shell = ShellSpec.posix
        Process =
            {
                Program = Program.Script "make test"
                Cwd = Some "/Users/u/src/app"
                Env = Inherit []
                Terminal = false
            }
    }

let private param (rendered: Profile.Rendered) (path: string) : string =
    match rendered.Parameters |> List.tryFind (fun (_, p) -> p = path) with
    | Some(name, _) -> $"(param \"{name}\")"
    | None -> failtest $"no parameter for {path}: {rendered.Parameters}"

/// `(require-not (subpath (param "Pn")))` of a path.
let private notUnder (rendered: Profile.Rendered) (path: string) =
    "(require-not (subpath " + param rendered path + "))"

let private having (rendered: Profile.Rendered) (prefix: string) : string list =
    rendered.Rules
    |> List.filter (fun r -> r.StartsWith(prefix, StringComparison.Ordinal))

[<Tests>]
let profiles =
    testList
        "seatbelt: profiles"
        [
            test "deny by default, then what processes need to run" {
                let rendered = Profile.render (request policy)
                Expect.equal (List.take 2 rendered.Rules) [ "(version 1)"; "(deny default)" ] "closed by default"
                Expect.contains rendered.Rules "(allow process-exec)" "programs start"
                Expect.contains rendered.Rules "(allow process-fork)" "and fork"
            }

            test "reads: everything but the denied paths, and readable paths inside them" {
                let rendered = Profile.render (request policy)
                let reads = having rendered "(allow file-read*"
                let root = reads |> List.find (fun r -> r.Contains "(subpath \"/\")")
                Expect.stringContains root (notUnder rendered "/Users/u/.fx") "not the data directory"
                Expect.stringContains root (notUnder rendered "/Users/u/.ssh") "not the keys"
                Expect.stringContains root (notUnder rendered "/Users/u/src/app") "the workspace has its own rule"

                Expect.isTrue
                    (reads
                     |> List.exists (fun r -> r.Contains(param rendered "/Users/u/.fx/runtimes")))
                    "the components are readable"

                Expect.isFalse
                    (reads
                     |> List.exists (fun r -> r = "(allow file-read* (subpath " + param rendered "/Users/u/.ssh" + "))"))
                    "the keys are not"
            }

            test "the denied directories above a readable path show their metadata only" {
                let rendered = Profile.render (request policy)
                let metadata = having rendered "(allow file-read-metadata"

                for path in [ "/Users/u/.fx"; "/Users/u/.fx/environments"; "/Users/u/.fx/environments/e1" ] do
                    Expect.contains metadata $"(allow file-read-metadata (literal {param rendered path}))" path

                Expect.equal metadata.Length 3 "nothing else: not the paths above the keys"
            }

            test "writes: the workspace but its protected paths, the owned and temporary directories" {
                let rendered = Profile.render (request policy)
                let writes = having rendered "(allow file-write*"
                let workspace = param rendered "/Users/u/src/app"

                let ws =
                    writes |> List.find (fun r -> r.Contains $"(require-all (subpath {workspace})")

                Expect.stringContains ws (notUnder rendered "/Users/u/src/app/.git/hooks") "hooks"
                Expect.stringContains ws (notUnder rendered "/Users/u/src/app/.git/config") "config"

                for path in [ "/Users/u/.fx/environments/e1/owned"; "/Users/u/.fx/environments/e1/tmp" ] do
                    Expect.contains writes $"(allow file-write* (subpath {param rendered path}))" path

                Expect.isFalse (writes |> List.exists (fun r -> r.Contains "(subpath \"/\")")) "nothing else"
            }

            test "renaming what holds a protected path, or the path, is refused last" {
                let rendered = Profile.render (request policy)

                let tail =
                    rendered.Rules
                    |> List.rev
                    |> List.takeWhile (fun r -> r.StartsWith "(deny file-write-unlink")

                for path in
                    [
                        "/Users/u/src/app/.git"
                        "/Users/u/src/app/.git/hooks"
                        "/Users/u/src/app/.git/config"
                    ] do
                    Expect.contains tail $"(deny file-write-unlink (literal {param rendered path}))" path
            }

            test "the network only when allowed, without the user's agent or denied sockets" {
                let denied = Profile.render (request policy)
                Expect.isEmpty (having denied "(allow network") "no network"

                let allowed =
                    Profile.render (
                        request
                            { policy with
                                Network = NetworkAccess.Allowed
                            }
                    )

                Expect.contains allowed.Rules "(allow network-outbound)" "the network"

                Expect.contains
                    allowed.Rules
                    ("(deny network-outbound (remote unix-socket (subpath "
                     + param allowed "/Users/u/.ssh"
                     + ")))")
                    "not sockets under denied paths"

                Expect.contains
                    allowed.Rules
                    "(deny network-outbound (remote unix-socket (subpath \"/private/tmp\")))"
                    "not the SSH agent"

                let tail = allowed.Rules |> List.rev |> List.take 2 |> List.rev

                Expect.equal
                    tail
                    [
                        $"(allow network-outbound (remote unix-socket (subpath {param allowed policy.Writable.Head})))"
                        $"(allow network-outbound (remote unix-socket (subpath {param allowed policy.Temp.Value})))"
                    ]
                    "but the environment's own sockets, last (TMPDIR: .NET's named pipes)"
            }

            test "paths under macOS's top-level links name their real paths too" {
                let temp =
                    { policy with
                        Workspace = "/var/folders/ab/T/ws"
                        Protected = []
                    }

                let rendered = Profile.render (request temp)
                Expect.contains (rendered.Parameters |> List.map snd) "/var/folders/ab/T/ws" "as given"
                Expect.contains (rendered.Parameters |> List.map snd) "/private/var/folders/ab/T/ws" "real"

                let forms =
                    Profile.forms
                        { ready with
                            Aliases = [ "/Users/u/link", "/Volumes/data/app" ]
                        }
                        "/Users/u/link/x"

                Expect.equal forms [ "/Users/u/link/x"; "/Volumes/data/app/x" ] "the probe's aliases"
            }

            test "the process runs under sandbox-exec, with paths as parameters" {
                match Profile.wrap (request policy) with
                | Ok {
                         Program = Exec(program, args)
                         Env = env
                     } ->
                    Expect.equal program "/usr/bin/sandbox-exec" "a fixed path"
                    Expect.equal args.Head "-p" "the profile inline"

                    Expect.equal
                        (args |> List.skipWhile ((<>) "--") |> List.tail)
                        [ "/bin/sh"; "-c"; "make test" ]
                        "the program"

                    let definitions = args |> List.filter (fun a -> a.StartsWith "-DP")
                    Expect.isTrue (definitions |> List.exists (fun d -> d.EndsWith "=/Users/u/src/app")) "parameters"
                    Expect.isFalse (args[1].Contains "/Users/u/") "no path in the profile's text"

                    Expect.equal
                        (string env)
                        (string (Inherit [ "TMPDIR", Some "/Users/u/.fx/environments/e1/tmp" ]))
                        "TMPDIR"
                | other -> failtest $"{other}"
            }
        ]

/// Probes with a scripted shell and in-memory files (with `/usr/bin/sandbox-exec` if `present`).
let private probe (target: TargetTriple) (present: bool) (trial: Output) =
    if OperatingSystem.IsWindows() then
        skiptest "Unix paths in the in-memory files"

    let clock = SystemClock.clock
    let fs = InMemoryFs(clock, false)

    if present then
        fs.AddText(Profile.SandboxExec, "")

    let shell =
        ScriptedShell(fun command ->
            match command.Spec.Program with
            | Exec("/bin/sh", "-c" :: script :: _) when script = Seatbelt.discoveryScript ->
                Some(ScriptedShell.ok "version=26.0\nalias=/var/folders/x\t/private/var/folders/x\n")
            | Exec(program, _) when program = Profile.SandboxExec -> Some(Ok trial)
            | _ -> None)

    let env: SandboxEnv =
        {
            Extension = BasicExtensionEnv.forManifest clock Seatbelt.manifest
            FsRead = fs.FsRead
            Fs = fs.Fs
            Shell = shell.Shell
        }

    let site: SandboxSite =
        {
            Target = target
            Shell = ShellSpec.posix
            Dir = "/Users/u/.fx/environments/e1/sandbox"
            Roots = [ "/var/folders/x" ]
        }

    (SandboxEnv.within env (Seatbelt.probe site) env.Extension).GetAwaiter().GetResult()

let private mac = TargetTriple.create Os.MacOs Arch.Arm64 None

[<Tests>]
let probing =
    testList
        "seatbelt: probing"
        [
            test "available: sandbox-exec runs this driver's profile" {
                match probe mac true (ScriptedShell.output 0 "" "") with
                | Ok ready ->
                    Expect.equal ready.Program Profile.SandboxExec "sandbox-exec"
                    Expect.equal ready.Version "26.0" "the macOS version"
                    Expect.equal ready.Aliases [ "/var/folders/x", "/private/var/folders/x" ] "real paths"
                | Error e -> failtest $"{e}"
            }

            test "not on macOS" {
                match
                    probe (TargetTriple.create Os.Linux Arch.X64 (Some Libc.Glibc)) true (ScriptedShell.output 0 "" "")
                with
                | Error e -> Expect.stringContains e.Reason "macOS only" "why"
                | Ok ready -> failtest $"{ready}"
            }

            test "sandbox-exec is missing" {
                match probe mac false (ScriptedShell.output 0 "" "") with
                | Error e -> Expect.stringContains e.Reason "sandbox-exec" "why"
                | Ok ready -> failtest $"{ready}"
            }

            test "sandbox-exec refuses the profile" {
                match probe mac true (ScriptedShell.output 65 "" "sandbox-exec: syntax error") with
                | Error e -> Expect.stringContains e.Reason "syntax error" "what it said"
                | Ok ready -> failtest $"{ready}"
            }
        ]
