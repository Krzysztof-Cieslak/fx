/// Sandboxes in environments (docs/modules/sandbox.md): the conformance suite on every built-in
/// driver that runs here, how the host chooses a driver and builds an environment's policy, and
/// environments of `fx.env.local` whose processes run in their sandbox (and an agent's tighter one).
module Fx.Environments.Tests.SandboxTests

open System
open System.IO
open System.Threading
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Environments
open Fx.Environments.Tests.Conformance
open Fx.Environments.Tests.Local
open Fx.Ext.EnvLocal
open Fx.Machines.Host
open Fx.Sdk
open Fx.Store

let private subjects () : SandboxConformance.Subject list =
    sandboxes ()
    |> List.map (fun contributed ->
        {
            Name = contributed.Value.Id
            Driver = contributed.Value
            Extension = contributed.Env
            Required =
                contributed.Value.Id = SandboxDrivers.NoSandbox
                || (SandboxConformance.required ()
                    && List.contains SandboxConformance.target.Os contributed.Value.Platforms)
        })

[<Tests>]
let conformance =
    testList "sandbox drivers" (subjects () |> List.map SandboxConformance.tests)

let private drivers () = sandboxes () |> List.map _.Value

let private find (id: string) =
    drivers () |> List.find (fun d -> d.Id = id)

let private chosen (result: Result<SandboxChoice, string>) : string =
    match result with
    | Ok(SandboxChoice.Required d) -> $"required {d.Id}"
    | Ok(SandboxChoice.Preferred d) -> $"preferred {d.Id}"
    | Ok(SandboxChoice.Unconfined _) -> "unconfined"
    | Error _ -> "refused"

let private named (sandbox: string option) =
    { EnvSpec.create "dev" "local" with
        Sandbox = sandbox
    }

let private layout: EnvLayout =
    EnvLayout.create
        {
            Target = TargetTriple.ofRid "linux-x64" |> Option.get
            DataDir = "/home/u/.fx"
            SameHost = true
        }
        {
            Id = "e1"
            Provider = "local"
            Workspace = "/src/app"
            Home = "/home/u/.fx/environments/e1"
            Data = Map.empty
        }

[<Tests>]
let choosing =
    testList
        "choosing a sandbox"
        [
            test "auto picks the first driver that confines on the OS, else none" {
                let all = drivers ()

                Expect.equal
                    (chosen (EnvSandbox.choose "auto" (named None) Os.Linux all))
                    "preferred bubblewrap"
                    "Linux"

                Expect.equal (chosen (EnvSandbox.choose "auto" (named None) Os.MacOs all)) "preferred seatbelt" "macOS"
                Expect.equal (chosen (EnvSandbox.choose "auto" (named None) Os.Windows all)) "unconfined" "Windows"

                match EnvSandbox.choose "auto" (named None) Os.Windows all with
                | Ok(SandboxChoice.Unconfined reason) -> Expect.stringContains reason "T511" "Windows says why"
                | other -> failtest $"{other}"
            }

            test "a spec cannot turn the sandbox off; only the host can" {
                let all = drivers ()

                Expect.equal
                    (chosen (EnvSandbox.choose "auto" (named (Some "none")) Os.Linux all))
                    "refused"
                    "a spec's none"

                Expect.equal
                    (chosen (EnvSandbox.choose "auto" (named (Some "none")) Os.Windows all))
                    "unconfined"
                    "none where nothing confines anyway"

                Expect.equal
                    (chosen (EnvSandbox.choose "none" (named None) Os.Linux all))
                    "unconfined"
                    "the host's none"

                Expect.equal (chosen (EnvSandbox.choose "none" (named (Some "none")) Os.Linux all)) "unconfined" "both"
            }

            test "a spec can always ask for a confining driver, which is then required" {
                let all = drivers ()

                Expect.equal
                    (chosen (EnvSandbox.choose "none" (named (Some "bubblewrap")) Os.Linux all))
                    "required bubblewrap"
                    "tighter than the host"

                Expect.equal
                    (chosen (EnvSandbox.choose "auto" (named (Some "bubblewrap")) Os.Linux all))
                    "required bubblewrap"
                    "named"

                Expect.equal
                    (chosen (EnvSandbox.choose "auto" (named (Some "bubblewrap")) Os.MacOs all))
                    "refused"
                    "not on its OS"

                Expect.equal
                    (chosen (EnvSandbox.choose "auto" (named (Some "nsjail")) Os.Linux all))
                    "refused"
                    "not in effect"
            }

            test "a host that names a driver requires it everywhere" {
                let all = drivers ()

                Expect.equal
                    (chosen (EnvSandbox.choose "bubblewrap" (named None) Os.Linux all))
                    "required bubblewrap"
                    "given"

                Expect.equal
                    (chosen (EnvSandbox.choose "bubblewrap" (named (Some "none")) Os.Linux all))
                    "refused"
                    "a spec's none"

                Expect.equal
                    (chosen (EnvSandbox.choose "bubblewrap" (named (Some "seatbelt")) Os.Linux all))
                    "refused"
                    "another driver"

                Expect.equal
                    (chosen (EnvSandbox.choose "bubblewrap" (named None) Os.Windows all))
                    "refused"
                    "not on Windows"
            }

            test "an environment's policy" {
                let machine: EnvMachine =
                    {
                        Target = TargetTriple.ofRid "linux-x64" |> Option.get
                        DataDir = "/home/u/.fx"
                        SameHost = true
                    }

                let policy =
                    EnvSandbox.policy
                        layout
                        machine
                        (Some "/home/u")
                        [ "~/.cache/pkg" ]
                        [ "/srv/secret" ]
                        NetworkAccess.Denied

                let access = SandboxPolicy.access policy

                Expect.equal (access "/src/app/x.fs") PathAccess.ReadWrite "the workspace"
                Expect.equal (access "/src/app/.git/hooks/pre-commit") PathAccess.ReadOnly "git's hooks"
                Expect.equal (access "/src/app/.git/config") PathAccess.ReadOnly "git's config"
                Expect.equal (access "/src/app/.git/HEAD") PathAccess.ReadWrite "the rest of .git"

                Expect.equal
                    (access $"/src/app/{Product.projectDirName}/agents/x.md")
                    PathAccess.ReadOnly
                    "the project's directory"

                Expect.equal (access "/home/u/.fx/environments/e1/owned/k-1/home") PathAccess.ReadWrite "owned"
                Expect.equal (access "/home/u/.fx/environments/e1/tmp/x") PathAccess.ReadWrite "temporary files"
                Expect.equal (access "/home/u/.fx/environments/e1/cache/bun/x") PathAccess.ReadWrite "the tools' caches"
                Expect.equal (access "/home/u/.fx/environments/e1/envd/blobs") PathAccess.NoAccess "envd's state"
                Expect.equal (access "/home/u/.fx/environments/e1/assets/kernel.ts") PathAccess.ReadOnly "assets"
                Expect.equal (access "/home/u/.fx/runtimes/bun/bin/bun") PathAccess.ReadOnly "components"
                Expect.equal (access "/home/u/.fx/credentials/credentials.json") PathAccess.NoAccess "credentials"
                Expect.equal (access "/home/u/.ssh/id_ed25519") PathAccess.NoAccess "ssh keys"
                Expect.equal (access "/home/u/.aws/credentials") PathAccess.NoAccess "cloud credentials"
                Expect.equal (access "/run/user/1000/bus") PathAccess.NoAccess "the session bus"
                Expect.equal (access "/home/u/.cache/pkg/a") PathAccess.ReadWrite "the host's writable"
                Expect.equal (access "/srv/secret/x") PathAccess.NoAccess "the host's denied"
                Expect.equal (access "/home/u/notes.txt") PathAccess.ReadOnly "the rest of the home"
                Expect.equal (access "/usr/bin/git") PathAccess.ReadOnly "the system"
                Expect.equal policy.Temp (Some "/home/u/.fx/environments/e1/tmp") "TMPDIR"
                Expect.equal policy.Network NetworkAccess.Denied "network"

                let reviewer = SandboxPolicy.tighten SandboxOverrides.readOnly policy

                Expect.equal
                    (SandboxPolicy.access reviewer "/src/app/x.fs")
                    PathAccess.ReadOnly
                    "a reviewer's workspace"

                Expect.equal
                    (SandboxPolicy.access reviewer "/home/u/.fx/environments/e1/owned/k-1/home")
                    PathAccess.ReadWrite
                    "a reviewer's kernel still writes its own directories"
            }

            test "tools get their caches and homes in the environment, and the user's NuGet packages to read" {
                let machine: EnvMachine =
                    {
                        Target = TargetTriple.ofRid "linux-x64" |> Option.get
                        DataDir = "/home/u/.fx"
                        SameHost = true
                    }

                let policyWith (writable: string list) (denied: string list) =
                    EnvSandbox.policy layout machine (Some "/home/u") writable denied NetworkAccess.Allowed

                let policy = policyWith [] []
                let packages = "/home/u/.nuget/packages"
                let defaults = EnvSandbox.defaults layout policy (Some packages)

                let value name =
                    defaults |> List.find (fun (n, _) -> n = name) |> snd

                let cache = "/home/u/.fx/environments/e1/cache"

                Expect.equal (value "DOTNET_CLI_HOME") $"{cache}/dotnet" "the .NET CLI's home"
                Expect.equal (value "NUGET_PACKAGES") $"{cache}/nuget/packages" "NuGet's packages"
                Expect.equal (value "NUGET_HTTP_CACHE_PATH") $"{cache}/nuget/http-cache" "NuGet's HTTP cache"
                Expect.equal (value "NUGET_PLUGINS_CACHE_PATH") $"{cache}/nuget/plugins-cache" "NuGet's plugins cache"
                Expect.equal (value "NUGET_FALLBACK_PACKAGES") packages "the user's packages, to read"
                Expect.equal (value "CARGO_HOME") $"{cache}/cargo" "Cargo's home"
                Expect.equal (value "XDG_RUNTIME_DIR") "/home/u/.fx/environments/e1/tmp" "the runtime directory"
                Expect.equal (value "XDG_CACHE_HOME") cache "XDG's caches"

                for name, path in defaults do
                    let expected =
                        if name = "NUGET_FALLBACK_PACKAGES" then
                            PathAccess.ReadOnly
                        else
                            PathAccess.ReadWrite

                    Expect.equal (SandboxPolicy.access policy path) expected $"{name} ({path})"

                Expect.equal
                    (SandboxPolicy.access policy "/home/u/.nuget/NuGet/NuGet.Config")
                    PathAccess.NoAccess
                    "the user's NuGet config stays denied"

                Expect.equal
                    (SandboxPolicy.access policy $"{cache}/dotnet/.nuget/NuGet/NuGet.Config")
                    PathAccess.ReadWrite
                    "NuGet writes the environment's own"

                Expect.equal
                    (SandboxPolicy.access policy "/home/u/.cargo/credentials.toml")
                    PathAccess.NoAccess
                    "Cargo's credentials stay denied"

                let names (defaults: (string * string) list) = defaults |> List.map fst

                Expect.isFalse
                    (EnvSandbox.defaults layout policy None
                     |> names
                     |> List.contains "NUGET_FALLBACK_PACKAGES")
                    "no fallback without the user's folder"

                let shared = policyWith [ "~/.nuget/packages" ] []
                let sharedDefaults = EnvSandbox.defaults layout shared (Some packages)

                Expect.equal
                    (sharedDefaults |> List.find (fun (n, _) -> n = "NUGET_PACKAGES") |> snd)
                    packages
                    "the user's folder itself, where the host made it writable"

                Expect.isFalse (sharedDefaults |> names |> List.contains "NUGET_FALLBACK_PACKAGES") "and no fallback"

                Expect.isFalse
                    (EnvSandbox.defaults layout (policyWith [] [ "~/.nuget/packages" ]) (Some packages)
                     |> names
                     |> List.contains "NUGET_FALLBACK_PACKAGES")
                    "no fallback the sandbox would not let NuGet read"

                Expect.equal
                    (EnvSandbox.nugetPackages (Some "/home/u") [])
                    (Some packages)
                    "the default packages folder"

                Expect.equal
                    (EnvSandbox.nugetPackages (Some "/home/u") [ "NUGET_PACKAGES", "/ci/packages/" ])
                    (Some "/ci/packages")
                    "the host's NUGET_PACKAGES"

                Expect.equal
                    (EnvSandbox.nugetPackages (Some "/home/u") [ "NUGET_PACKAGES", "packages" ])
                    (Some packages)
                    "a relative one is not the host's"

                Expect.equal (EnvSandbox.nugetPackages None []) None "no home"
            }
        ]

let private newLog () =
    EnvLog.typed (InMemoryEventLog<Payload>().Log)

/// Creates an environment with a host whose variants have these selections, runs the body on it,
/// and destroys it.
let private withEnvironment
    (selections: (string * Json) list)
    (change: EnvSpec -> EnvSpec)
    (body: Result<EnvRecord, EnvError> -> Envs -> unit)
    =
    let dataDir = Scratch.directory ()
    let host = startWith selections (settings dataDir) (newLog ())

    try
        let envs = EnvsHost.envs host

        let created =
            wait (envs.Create (change (spec TransportKind.InProcess)) CancellationToken.None)

        body created envs

        match created with
        | Ok record -> wait (envs.Destroy record.Id CancellationToken.None) |> ignore
        | Error(EnvError.NotReady record) -> wait (envs.Destroy record.Id CancellationToken.None) |> ignore
        | Error _ -> ()
    finally
        wait (EnvsHost.stop host)

let private confining () =
    drivers ()
    |> List.filter (fun d -> d.Confines && List.contains SandboxConformance.target.Os d.Platforms)

let private shellRun (handle: EnvHandle) (script: string) : Output =
    match
        wait (
            handle.Client.Shell.Run
                (Command.script script |> Command.inDir handle.Layout.Workspace)
                CancellationToken.None
        )
    with
    | Ok output -> output
    | Error e -> failtest $"{e}"

/// The repository these tests were built from: the directory above them with the solution.
let private repositoryRoot () : string option =
    let rec up (dir: string) =
        if File.Exists(Path.Combine(dir, "Fx.slnx")) then
            Some dir
        else
            match Path.GetDirectoryName dir with
            | null -> None
            | parent -> up parent

    up (Path.TrimEndingDirectorySeparator AppContext.BaseDirectory)

/// A project of the repository (its own files, not its subdirectories) and the repository's build
/// settings, copied into a workspace.
let private copyProject (root: string) (project: string) (workspace: string) =
    let copy (relative: string) =
        let target = Path.Combine(workspace, relative)

        Directory.CreateDirectory(Path.Combine(workspace, Path.GetDirectoryName relative |> string))
        |> ignore

        File.Copy(Path.Combine(root, relative), target, true)

    for file in
        [
            "global.json"
            "nuget.config"
            "Directory.Build.props"
            "Directory.Build.targets"
            "Directory.Packages.props"
        ] do
        copy file

    for dir in [ "native"; project ] do
        let options =
            if dir = project then
                SearchOption.TopDirectoryOnly
            else
                SearchOption.AllDirectories

        for file in Directory.EnumerateFiles(Path.Combine(root, dir), "*", options) do
            copy (Path.GetRelativePath(root, file))

[<Tests>]
let environments =
    testSequenced
    <| testList
        "sandboxed environments"
        [
            test "a host that turns sandboxing off runs environments unsandboxed, with a warning" {
                withEnvironment [ "sandbox.driver", Json.String "none" ] id (fun created _ ->
                    match created with
                    | Ok record ->
                        let sandbox = record.Sandbox |> Option.get
                        Expect.equal (sandbox.Driver, sandbox.Confines) ("none", false) "none"
                        Expect.stringContains (List.head sandbox.Notes) "sandbox.driver = none" "why"

                        match Envs.warnings record with
                        | [ warning ] -> Expect.stringContains warning "unsandboxed" "the persistent warning"
                        | other -> failtest $"{other}"
                    | Error e -> failtest (string e))
            }

            test "auto confines with this OS's driver where it can, and says why where it cannot" {
                withEnvironment [] id (fun created envs ->
                    match created with
                    | Error e -> failtest (string e)
                    | Ok record ->
                        let sandbox = record.Sandbox |> Option.get

                        match confining () with
                        | [] ->
                            Expect.equal sandbox.Driver "none" "nothing confines here"
                            Expect.equal (Envs.warnings record).Length 1 "the unsandboxed warning"

                            if OperatingSystem.IsWindows() then
                                Expect.stringContains (String.Join(" ", sandbox.Notes)) "Windows" "Windows says why"
                        | driver :: _ when sandbox.Confines ->
                            Expect.equal sandbox.Driver driver.Id "the OS's driver"
                            Expect.isEmpty (Envs.warnings record) "no warning"
                        | driver :: _ ->
                            if SandboxConformance.required () then
                                failtest $"{driver.Id} must confine here: {sandbox.Notes}"

                            Expect.stringContains
                                (String.Join(" ", sandbox.Notes))
                                driver.Id
                                "says why the driver could not"

                        let handle =
                            wait (envs.Open record.Id CancellationToken.None)
                            |> Result.toOption
                            |> Option.get

                        Expect.equal handle.Sandbox.Info sandbox "the handle's sandbox is the record's")
            }

            test "processes of a sandboxed environment run in its sandbox, an agent's in a tighter one" {
                withEnvironment [] id (fun created envs ->
                    match created with
                    | Error e -> failtest (string e)
                    | Ok record when not (record.Sandbox |> Option.exists _.Confines) ->
                        skiptest "nothing confines processes here"
                    | Ok record ->
                        let handle =
                            wait (envs.Open record.Id CancellationToken.None)
                            |> Result.toOption
                            |> Option.get

                        let outside = Path.Combine(Scratch.directory (), "x.txt")

                        let output =
                            shellRun
                                handle
                                $"echo w > in.txt; echo x > '{outside}' 2>/dev/null; echo \"$TMPDIR\"; echo \"$XDG_CACHE_HOME\"; mkdir -p \"$BUN_INSTALL_CACHE_DIR\" && echo cached"

                        Expect.equal output.ExitCode 0 $"the command ran: {Output.stderrText output}"

                        Expect.isTrue
                            (File.Exists(Path.Combine(handle.Layout.Workspace, "in.txt")))
                            "the workspace is writable"

                        Expect.isFalse (File.Exists outside) "outside is not"

                        Expect.equal
                            ((Output.stdoutText output).Trim().Split('\n') |> List.ofArray)
                            [ handle.Layout.Temp; handle.Layout.Cache; "cached" ]
                            "TMPDIR, and the tools' caches in the environment's cache directory"

                        let reviewer = EnvHandle.attenuate SandboxOverrides.readOnly handle
                        let output = shellRun reviewer "echo r > ro.txt 2>/dev/null; cat hello.txt"
                        Expect.equal ((Output.stdoutText output).Trim()) "hello" "the reviewer reads"

                        Expect.isFalse
                            (File.Exists(Path.Combine(handle.Layout.Workspace, "ro.txt")))
                            "and cannot write"

                        let output = shellRun handle "echo again > in.txt && echo ok"

                        Expect.equal
                            ((Output.stdoutText output).Trim())
                            "ok"
                            "the environment's own sandbox is unchanged")
            }

            test "dotnet restores and builds this repository's smallest project in the sandbox, offline" {
                let root = repositoryRoot ()

                let userPackages =
                    EnvSandbox.nugetPackages
                        (Some(Environment.GetFolderPath Environment.SpecialFolder.UserProfile))
                        (hostEnvironment ())
                    |> Option.filter Directory.Exists

                match root, userPackages with
                | None, _ -> skiptest "not run from the repository's artifacts"
                | _, None -> skiptest "no NuGet packages folder to read FSharp.Core from offline"
                | _, Some packages when not (Directory.Exists(Path.Combine(packages, "fsharp.core"))) ->
                    skiptest "the NuGet packages folder has no FSharp.Core: the restore would download it"
                | Some root, Some packages ->
                    withEnvironment [] id (fun created envs ->
                        match created with
                        | Error e -> failtest (string e)
                        | Ok record when not (record.Sandbox |> Option.exists _.Confines) ->
                            skiptest "nothing confines processes here"
                        | Ok record ->
                            let handle =
                                wait (envs.Open record.Id CancellationToken.None)
                                |> Result.toOption
                                |> Option.get

                            copyProject root "src/Fx.Prelude" handle.Layout.Workspace

                            let userConfig =
                                Path.Combine(
                                    Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                                    ".nuget",
                                    "NuGet",
                                    "NuGet.Config"
                                )

                            let script =
                                String.Join(
                                    " && ",
                                    [
                                        "dotnet restore src/Fx.Prelude --locked-mode -p:NuGetAudit=false"
                                        "dotnet build src/Fx.Prelude --no-restore -c Release -nologo"
                                        "dotnet nuget config paths"
                                        "echo \"fallback=$NUGET_FALLBACK_PACKAGES\""
                                        $"(cat '{userConfig}' > /dev/null 2>&1 && echo user-config-read || echo user-config-hidden)"
                                    ]
                                )

                            let output =
                                match
                                    (handle.Client.Shell.Run
                                        (Command.script script |> Command.inDir handle.Layout.Workspace)
                                        CancellationToken.None)
                                        .WaitAsync(TimeSpan.FromMinutes 5.0)
                                        .GetAwaiter()
                                        .GetResult()
                                with
                                | Ok output -> output
                                | Error e -> failtest $"{e}"

                            let stdout = Output.stdoutText output

                            Expect.equal
                                output.ExitCode
                                0
                                $"restored and built in the sandbox:\n{stdout}\n{Output.stderrText output}"

                            Expect.isTrue
                                (File.Exists(
                                    Path.Combine(
                                        handle.Layout.Workspace,
                                        "artifacts",
                                        "bin",
                                        "Fx.Prelude",
                                        "release",
                                        "Fx.Prelude.dll"
                                    )
                                ))
                                "the assembly"

                            let lines = stdout.Split('\n') |> Array.map _.Trim() |> List.ofArray

                            Expect.isTrue
                                (lines |> List.exists (fun line -> line.StartsWith handle.Layout.Cache))
                                $"NuGet's user config is the environment's: {stdout}"

                            Expect.isFalse (List.contains userConfig lines) "not the user's"
                            Expect.contains lines $"fallback={packages}" "the user's packages, read in place"

                            if File.Exists userConfig then
                                Expect.contains lines "user-config-hidden" "the user's NuGet config stays denied"

                            Expect.isFalse
                                (Directory.Exists(
                                    Path.Combine(handle.Layout.Cache, "nuget", "packages", "fsharp.core")
                                ))
                                "nothing downloaded")
            }

            test "a spec cannot turn off the sandbox the host gives" {
                withEnvironment [] (fun s -> { s with Sandbox = Some "none" }) (fun created _ ->
                    match confining (), created with
                    | [], Ok record ->
                        Expect.equal (record.Sandbox |> Option.map _.Driver) (Some "none") "nothing confines here"
                    | _ :: _, Error(EnvError.NotReady record) ->
                        Expect.equal record.Phase Phase.Failed "failed"

                        Expect.stringContains
                            (record.Reason |> Option.defaultValue "")
                            "sandbox.driver"
                            "only the host can"
                    | _, other -> failtest $"{other}")
            }

            test "no network needs a sandbox that confines" {
                withEnvironment
                    [ "sandbox.driver", Json.String "none" ]
                    (fun s -> { s with Network = Some "none" })
                    (fun created _ ->
                        match created with
                        | Error(EnvError.NotReady record) ->
                            Expect.stringContains (record.Reason |> Option.defaultValue "") "network" "why"
                        | other -> failtest $"{other}")
            }

            test "a spec's driver that is not in effect is refused before anything is created" {
                withEnvironment [] (fun s -> { s with Sandbox = Some "nsjail" }) (fun created envs ->
                    match created with
                    | Error(EnvError.Invalid issues) ->
                        Expect.equal
                            (issues |> List.map (fun i -> JsonPath.render i.Path))
                            [ "$.sandbox" ]
                            "the spec's sandbox"

                        Expect.isEmpty (wait (envs.List CancellationToken.None)) "nothing was created"
                    | other -> failtest $"{other}")
            }

            test "readied events record the sandbox, and read back" {
                let event =
                    EnvEvent.Readied
                        {
                            Id = "a"
                            Attempt = 1
                            Components = []
                            Sandbox =
                                Some
                                    {
                                        Driver = "none"
                                        Confines = false
                                        Notes = [ "the host turned sandboxing off (sandbox.driver = none)" ]
                                    }
                        }

                let json = Codec.encode Lifecycle.event event

                Expect.equal
                    (Json.serializeCanonical json)
                    """{"attempt":1,"components":[],"id":"a","kind":"readied","sandbox":{"confines":false,"driver":"none","notes":["the host turned sandboxing off (sandbox.driver = none)"]}}"""
                    "the JSON"

                Expect.equal (Codec.decode Lifecycle.event json) (Ok event) "round trip"

                match
                    Codec.decode
                        Lifecycle.event
                        (Json.parse """{"attempt":1,"id":"a","kind":"readied"}"""
                         |> Result.toOption
                         |> Option.get)
                with
                | Ok(EnvEvent.Readied readied) -> Expect.isNone readied.Sandbox "what version 1 wrote has no sandbox"
                | other -> failtest $"{other}"
            }
        ]
