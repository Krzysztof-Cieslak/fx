/// The installer conformance suite (v1): what an installer environment (filesystem, install-file
/// operations, fetch, shell, clock) must do with the installer machine on top. The in-memory fakes
/// and the live interpreters on a real disk both pass it. Archives are built in the tests and
/// served by `FakeFetch`; probes "run" files by printing them (`Support.probeShell`).
/// Also compiled into Fx.Components.Live.Tests.
module Fx.Components.Tests.Conformance.InstallerConformance

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Components
open Fx.Components.Fakes
open Fx.Components.Tests.Support
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Sdk

/// An installer environment under test, with an install root that does not exist yet.
type Fixture =
    {
        Env: InstallerEnv
        Fetch: FakeFetch
        /// The install root: `<Outside>/runtimes`.
        Root: string
        /// The root's parent, where nothing may be written.
        Outside: string
        /// Lets time pass: advances a fake clock, or sleeps a little on a real one.
        Advance: unit -> unit
        Release: unit -> unit
    }

    override this.ToString() = $"Fixture({this.Root})"

/// The process crashed while it unpacked.
type SimulatedCrash() =
    inherit Exception("simulated crash")

/// One installer (an actor on its own log) in a fixture.
type private Installer
    (fixture: Fixture, name: string, approve: InstallSpec -> ApprovalAnswer, log: InMemoryEventLog<InstallerEvent>) =
    let approvals = ResizeArray<string>()
    let mutable next = 0

    let settings: InstallerSettings<InstallerEnv> =
        { InstallerSettings.create fixture.Root name (fun request _ ->
              lock approvals (fun () -> approvals.Add request.Spec.Key.Name)
              Task.FromResult(approve request.Spec)) with
            RetryDelay = TimeSpan.FromSeconds 1.0
            LockTimeout = TimeSpan.FromMinutes 5.0
        }

    new(fixture, name) =
        Installer(fixture, name, (fun _ -> ApprovalAnswer.Approved "tester"), InMemoryEventLog<InstallerEvent>())

    member _.Log = log
    member _.Settings = settings
    member _.Approvals = lock approvals (fun () -> List.ofSeq approvals)

    member _.Start(env: InstallerEnv) : ComponentInstaller.Installer =
        runIn env (ComponentInstaller.start settings log.Log)

    member _.Key() =
        next <- next + 1
        $"{name}-{next}"

    member _.Events: string list =
        log.Events(ComponentInstaller.stream name)
        |> List.map (fun recorded ->
            match recorded.Event with
            | InstallerEvent.Requested _ -> "requested"
            | InstallerEvent.Held _ -> "held"
            | InstallerEvent.Found _ -> "found"
            | InstallerEvent.Missing _ -> "missing"
            | InstallerEvent.ApprovalRequested _ -> "approval-requested"
            | InstallerEvent.Approved _ -> "approved"
            | InstallerEvent.Fetched _ -> "fetched"
            | InstallerEvent.Installed _ -> "installed"
            | InstallerEvent.Failed(_, step, _) -> $"failed while {step}"
            | InstallerEvent.PruneStarted _ -> "prune-started"
            | InstallerEvent.Pruned _ -> "pruned")

/// Waits for a task, letting the fixture's time pass while it waits.
let private await (fixture: Fixture) (task: Task<'T>) : 'T =
    let deadline = DateTime.UtcNow.AddMinutes 2.0

    while not task.IsCompleted && DateTime.UtcNow < deadline do
        fixture.Advance()

    if not task.IsCompleted then
        failtest "the installer did not finish"

    task.GetAwaiter().GetResult()

let private ensure
    (fixture: Fixture)
    (installer: Installer)
    (actor: ComponentInstaller.Installer)
    (holder: string)
    (spec: InstallSpec)
    =
    await fixture (Eff.run fixture.Env (ComponentInstaller.ensure actor (installer.Key()) holder spec))

let private send
    (fixture: Fixture)
    (installer: Installer)
    (actor: ComponentInstaller.Installer)
    (command: InstallerCommand)
    =
    await fixture (Eff.run fixture.Env (ComponentInstaller.send actor (installer.Key()) command))

/// Sends `Prune` and waits for its round to end; returns what it removed.
let private prune (fixture: Fixture) (installer: Installer) (actor: ComponentInstaller.Installer) : string list =
    match send fixture installer actor InstallerCommand.Prune with
    | Outcome.Accepted _ -> ()
    | other -> failtest $"prune: {other}"

    let rec wait (tries: int) =
        match (Actor.snapshot actor).State.Pruning with
        | None -> ()
        | Some _ when tries > 0 ->
            fixture.Advance()
            wait (tries - 1)
        | Some _ -> failtest "the prune did not finish"

    wait 10_000

    installer.Log.Events(ComponentInstaller.stream (installer.Settings.Name))
    |> List.choose (fun r ->
        match r.Event with
        | InstallerEvent.Pruned(_, removed) -> Some removed
        | _ -> None)
    |> List.last

let private ok (result: Result<'T, ComponentError>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failtest $"expected success, got {e}"

let private failure (result: Result<InstalledInfo, ComponentError>) : InstallFailure =
    match result with
    | Error(InstallFailed(_, failure)) -> failure
    | other -> failtest $"expected an install failure, got {other}"

let private tree (fixture: Fixture) (dir: string) = listTree fixture.Env.FsRead dir

let private exists (fixture: Fixture) (path: string) =
    match (fixture.Env.FsRead.Stat path CancellationToken.None).GetAwaiter().GetResult() with
    | Ok _ -> true
    | Error _ -> false

/// What the root's working directories hold: they must be empty between installs.
let private leftovers (fixture: Fixture) =
    [ ".staging"; ".downloads"; ".trash" ]
    |> List.collect (fun dir ->
        tree fixture (Path.Combine(fixture.Root, dir))
        |> List.map (fun f -> dir + "/" + f))

let private serveTool (fixture: Fixture) (version: string) : InstallSpec =
    let archive = toolArchive version
    fixture.Fetch.Serve(toolUrl version, archive)
    specOf target (toolComponent version archive)

/// A component whose archive is these entries, packed as tar.gz.
let private serveBad (fixture: Fixture) (name: string) (entries: ArchiveItem list) : InstallSpec =
    let archive = tarGz entries
    let url = $"https://example.test/bad/{name}.tar.gz"
    fixture.Fetch.Serve(url, archive)

    specOf
        target
        (Component.create
            "bad"
            (semver "1.0.0")
            [
                {
                    Platforms = []
                    Method =
                        Archive
                            {
                                Url = url
                                Digest = digestOf archive
                                Format = ArchiveFormat.TarGz
                                Strip = 0
                            }
                }
            ])

/// Tests that the environments made by `create` (one per test) conform.
let tests (name: string) (create: unit -> Fixture) : Test =
    let test (title: string) (body: Fixture -> unit) =
        testCase title (fun () ->
            let fixture = create ()

            try
                body fixture
            finally
                fixture.Release())

    testList
        $"Installer conformance ({name})"
        [
            test "installs an archive: fetch, verify, stage, probe, activate"
            <| fun fixture ->
                let spec = serveTool fixture "1.2.3"
                let installer = Installer(fixture, "a")
                let actor = installer.Start fixture.Env
                let info = ok (ensure fixture installer actor "h" spec)

                Expect.equal info.Dir (InstallRoot.installDir fixture.Root spec.Key) "the install directory"
                Expect.isFalse info.Reused "installed now"
                Expect.equal info.Files 2 "two files"

                Expect.equal
                    (tree fixture info.Dir)
                    (List.sort [ InstallRoot.receiptName; "README"; "bin/"; "bin/tool" ])
                    "the archive's files, stripped, and the receipt"

                Expect.equal (leftovers fixture) [] "no staging, download or trash left"
                Expect.equal fixture.Fetch.Requests [ toolUrl "1.2.3" ] "one download"
                Expect.equal installer.Approvals [ spec.Key.Name ] "the first install was approved"

                Expect.equal
                    installer.Events
                    [
                        "held"
                        "requested"
                        "missing"
                        "approval-requested"
                        "approved"
                        "fetched"
                        "installed"
                    ]
                    "the journal"

            test "a digest mismatch is rejected, and nothing is installed"
            <| fun fixture ->
                let spec = serveTool fixture "1.2.3"
                fixture.Fetch.Serve(toolUrl "1.2.3", toolArchive "6.6.6")
                let installer = Installer(fixture, "a")
                let actor = installer.Start fixture.Env

                match failure (ensure fixture installer actor "h" spec) with
                | DigestMismatch(expected, actual) ->
                    Expect.equal expected (string (digestOf (toolArchive "1.2.3"))) "the pinned digest"
                    Expect.equal actual (string (digestOf (toolArchive "6.6.6"))) "what was served"
                | other -> failtest $"expected a digest mismatch, got {other}"

                Expect.isFalse (exists fixture (InstallRoot.installDir fixture.Root spec.Key)) "not installed"
                Expect.equal (leftovers fixture) [] "the download is gone"
                Expect.equal fixture.Fetch.Requests [ toolUrl "1.2.3" ] "not retried"

            test "unsafe archives are rejected, and nothing is written outside the root"
            <| fun fixture ->
                let installer = Installer(fixture, "a")
                let actor = installer.Start fixture.Env

                let cases =
                    [
                        "traversal", [ File("../evil", "x", false) ]
                        "nested-traversal", [ Dir "a"; File("a/../../evil", "x", false) ]
                        "absolute", [ File("/evil", "x", false) ]
                        "device", [ Device "dev" ]
                        "escaping-link", [ Symlink("link", "../../evil") ]
                        "absolute-link", [ Symlink("link", "/etc/passwd") ]
                        "write-through-link", [ Symlink("link", "sub"); File("link/evil", "x", false) ]
                        "windows-name", [ File("aux.txt", "x", false) ]
                        "case-collision", [ File("Tool", "x", false); File("tool", "y", false) ]
                    ]

                for case, entries in cases do
                    let spec = serveBad fixture case entries

                    match failure (ensure fixture installer actor case spec) with
                    | BadArchive _ -> ()
                    | other -> failtest $"{case}: expected a bad archive, got {other}"

                    Expect.isFalse
                        (exists fixture (InstallRoot.installDir fixture.Root spec.Key))
                        $"{case}: not installed"

                    Expect.equal (leftovers fixture) [] $"{case}: staging cleared"

                Expect.isFalse (exists fixture (Path.Combine(fixture.Outside, "evil"))) "nothing outside the root"

            test "a failing probe rolls back, and the active version stays"
            <| fun fixture ->
                let v1 = serveTool fixture "1.0.0"
                let installer = Installer(fixture, "a")
                let actor = installer.Start fixture.Env
                let first = ok (ensure fixture installer actor "h" v1)
                let before = tree fixture first.Dir

                // Version 2's tool says it is version 9.
                let archive =
                    tarGz
                        [
                            File("tool-2.0.0/bin/tool", "tool 9.0.0", true)
                            File("tool-2.0.0/README", "read me", false)
                        ]

                fixture.Fetch.Serve(toolUrl "2.0.0", archive)
                let v2 = specOf target (toolComponent "2.0.0" archive)

                match failure (ensure fixture installer actor "h" v2) with
                | ProbeFailed reason -> Expect.stringContains reason "tool 9.0.0" "what it printed"
                | other -> failtest $"expected a probe failure, got {other}"

                Expect.isFalse (exists fixture (InstallRoot.installDir fixture.Root v2.Key)) "version 2 is not active"
                Expect.equal (tree fixture first.Dir) before "version 1 is untouched"
                Expect.equal (leftovers fixture |> List.filter (fun f -> f.StartsWith ".staging")) [] "staging cleared"

                // A probe that exits with an error fails too.
                let failing = tarGz [ File("tool-3.0.0/bin/tool", "exit 3", true) ]
                fixture.Fetch.Serve(toolUrl "3.0.0", failing)

                match failure (ensure fixture installer actor "h" (specOf target (toolComponent "3.0.0" failing))) with
                | ProbeFailed reason -> Expect.stringContains reason "exited with 3" "the exit code"
                | other -> failtest $"expected a probe failure, got {other}"

            test "a crash mid-install leaves nothing active, and a restart finishes it"
            <| fun fixture ->
                let spec = serveTool fixture "1.2.3"
                let installer = Installer(fixture, "a")

                let crashing =
                    { fixture.Env.InstallFiles with
                        Extract =
                            fun request cancellation ->
                                task {
                                    let! _ = fixture.Env.InstallFiles.Extract request cancellation
                                    return raise (SimulatedCrash())
                                }
                    }

                let crashed =
                    installer.Start
                        { fixture.Env with
                            InstallFiles = crashing
                        }

                match ensure fixture installer crashed "h" spec with
                | Error InstallerStopped -> ()
                | other -> failtest $"expected the installer to stop, got {other}"

                Expect.throwsT<SimulatedCrash> (fun () -> crashed.Completion.GetAwaiter().GetResult()) "it crashed"

                let final = InstallRoot.installDir fixture.Root spec.Key
                Expect.isFalse (exists fixture final) "nothing is active after the crash"

                Expect.isTrue
                    (exists fixture (InstallRoot.staging fixture.Root spec.Key))
                    "the crash left staging behind"

                // A new installer on the same journal picks the install up where it was.
                let restarted = installer.Start fixture.Env
                let info = ok (ensure fixture installer restarted "h" spec)

                Expect.equal info.Dir final "installed"

                Expect.equal
                    (tree fixture final)
                    (List.sort [ InstallRoot.receiptName; "README"; "bin/"; "bin/tool" ])
                    "complete"

                Expect.equal (leftovers fixture) [] "the crash's staging is gone"
                Expect.equal fixture.Fetch.Requests [ toolUrl "1.2.3" ] "the verified archive was reused"
                Expect.equal installer.Approvals [ spec.Key.Name ] "approved once"

            test "installing again is idempotent, and another installer finds the install"
            <| fun fixture ->
                let spec = serveTool fixture "1.2.3"
                let a = Installer(fixture, "a")
                let actorA = a.Start fixture.Env
                let first = ok (ensure fixture a actorA "h" spec)
                let events = a.Events
                let again = ok (ensure fixture a actorA "h" spec)

                Expect.equal again first "the same install"
                Expect.equal a.Events events "nothing new in the journal"

                let b = Installer(fixture, "b")
                let actorB = b.Start fixture.Env
                let found = ok (ensure fixture b actorB "g" spec)

                Expect.equal found.Dir first.Dir "the same directory"
                Expect.isTrue found.Reused "found on disk"
                Expect.equal b.Approvals [] "finding an install needs no approval"
                Expect.equal b.Events [ "held"; "requested"; "found" ] "found"
                Expect.equal fixture.Fetch.Requests [ toolUrl "1.2.3" ] "downloaded once"

            test "an upgrade keeps the old version until it is pruned, and prune keeps what anyone uses"
            <| fun fixture ->
                let v1 = serveTool fixture "1.0.0"
                let v2 = serveTool fixture "2.0.0"
                let a = Installer(fixture, "a")
                let actorA = a.Start fixture.Env
                let b = Installer(fixture, "b")
                let actorB = b.Start fixture.Env

                let old = ok (ensure fixture a actorA "h" v1)
                ok (ensure fixture b actorB "g" v1) |> ignore

                // `h` moves to version 2.
                send fixture a actorA (InstallerCommand.Hold("h", [ v2.Key ])) |> ignore
                let upgraded = ok (ensure fixture a actorA "h" v2)
                Expect.isTrue (exists fixture old.Dir) "version 1 stays until a prune"

                Expect.equal (prune fixture a actorA) [] "b's holder still uses version 1"
                Expect.isTrue (exists fixture old.Dir) "kept"

                send fixture b actorB (InstallerCommand.Hold("g", [])) |> ignore

                Expect.equal (prune fixture a actorA) [ $"tool/{v1.Key.Dir}" ] "version 1 is pruned"
                Expect.isFalse (exists fixture old.Dir) "removed"
                Expect.isTrue (exists fixture upgraded.Dir) "version 2 stays"
                Expect.equal (leftovers fixture) [] "the trash is emptied"

                Expect.isFalse
                    ((Actor.snapshot actorA).State.Installs.ContainsKey v1.Key)
                    "the installer forgets the pruned install"

            test "concurrent installers share one download and one install"
            <| fun fixture ->
                let spec = serveTool fixture "1.2.3"
                let held = fixture.Fetch.Hold(toolUrl "1.2.3")
                let a = Installer(fixture, "a")
                let actorA = a.Start fixture.Env
                let b = Installer(fixture, "b")
                let actorB = b.Start fixture.Env

                let first =
                    Eff.run fixture.Env (ComponentInstaller.ensure actorA (a.Key()) "h" spec)

                let second =
                    Eff.run fixture.Env (ComponentInstaller.ensure actorB (b.Key()) "g" spec)

                // Both are under way; a's download holds the install's lock.
                for _ in 1..20 do
                    fixture.Advance()

                Expect.isFalse first.IsCompleted "a waits for its download"
                Expect.isFalse second.IsCompleted "b waits for a"
                Expect.isFalse held.Task.IsCompleted "still held"
                fixture.Fetch.Release(toolUrl "1.2.3")

                let first = ok (await fixture first)
                let second = ok (await fixture second)

                Expect.equal second.Dir first.Dir "one install"
                Expect.notEqual first.Reused second.Reused "one installed it, the other found it"
                Expect.equal fixture.Fetch.Requests [ toolUrl "1.2.3" ] "one download"
                Expect.equal (leftovers fixture) [] "nothing left over"

            test "installs from a lockfile, dependencies first"
            <| fun fixture ->
                let baseArchive = tarGz [ File("lib/base.txt", "base", false) ]
                fixture.Fetch.Serve("https://example.test/base-1.4.0.tar.gz", baseArchive)

                let baseComponent =
                    Component.create
                        "base"
                        (semver "1.4.0")
                        [
                            {
                                Platforms = [ target ]
                                Method =
                                    Archive
                                        {
                                            Url = "https://example.test/base-${version}.tar.gz"
                                            Digest = digestOf baseArchive
                                            Format = ArchiveFormat.TarGz
                                            Strip = 0
                                        }
                            }
                        ]

                let toolArchive = toolArchive "1.2.3"
                fixture.Fetch.Serve(toolUrl "1.2.3", toolArchive)

                let tool =
                    { toolComponent "1.2.3" toolArchive with
                        DependsOn = [ { Id = "base"; Version = "^1.2" } ]
                    }

                let lockfile =
                    match
                        Planner.plan
                            PlanSettings.defaults
                            [ tool; baseComponent ]
                            [
                                {
                                    Ref = { Id = "tool"; Version = "1" }
                                    By = "test"
                                }
                            ]
                            [ target ]
                    with
                    | Ok lockfile -> lockfile
                    | Error errors -> failtest (errors |> List.map string |> String.concat "; ")

                let installer = Installer(fixture, "a")
                let actor = installer.Start fixture.Env
                let mutable keys = 0

                let components =
                    ComponentInstaller.components actor lockfile target (fun () ->
                        keys <- keys + 1
                        $"components-{keys}")

                let request =
                    {
                        Holder = "extension:acme"
                        Requires = [ { Id = "tool"; Version = ">=1.2" } ]
                    }

                let installed =
                    ok (await fixture (components.Ensure request CancellationToken.None))

                Expect.equal (installed |> List.map (fun c -> c.Id)) [ "base"; "tool" ] "dependencies first"
                let tool = installed |> List.find (fun c -> c.Id = "tool")
                Expect.equal tool.Bin [ "tool", InstallRoot.file tool.Dir "bin/tool" ] "the command's path"
                Expect.equal tool.Env [ "TOOL_HOME", tool.Dir ] "${dir} expanded"

                Expect.equal
                    (Installer.held (Actor.snapshot actor).State |> List.map (fun k -> k.Id))
                    [ "base"; "tool" ]
                    "the holder uses both"

                match
                    await
                        fixture
                        (components.Ensure
                            { request with
                                Requires = [ { Id = "tool"; Version = "^2" } ]
                            }
                            CancellationToken.None)
                with
                | Error(NotLocked reason) -> Expect.stringContains reason "plan it again" "outside the lockfile"
                | other -> failtest $"expected NotLocked, got {other}"

            test "a first install needs approval, and a rejection installs nothing"
            <| fun fixture ->
                let spec = serveTool fixture "1.2.3"

                let installer =
                    Installer(fixture, "a", (fun _ -> ApprovalAnswer.Rejected "not now"), InMemoryEventLog())

                let actor = installer.Start fixture.Env

                match failure (ensure fixture installer actor "h" spec) with
                | Denied reason -> Expect.equal reason "not now" "the reason"
                | other -> failtest $"expected a denial, got {other}"

                Expect.equal fixture.Fetch.Requests [] "nothing downloaded"
                Expect.equal installer.Approvals [ spec.Key.Name ] "asked"

            test "transient download failures are retried, others are not"
            <| fun fixture ->
                let spec = serveTool fixture "1.2.3"
                fixture.Fetch.FailNext(toolUrl "1.2.3", Network(toolUrl "1.2.3", "reset"))
                fixture.Fetch.FailNext(toolUrl "1.2.3", HttpStatus(toolUrl "1.2.3", 503))
                let installer = Installer(fixture, "a")
                let actor = installer.Start fixture.Env
                ok (ensure fixture installer actor "h" spec) |> ignore
                Expect.equal fixture.Fetch.Requests.Length 3 "two retries"

                let other = serveTool fixture "2.0.0"
                fixture.Fetch.FailNext(toolUrl "2.0.0", HttpStatus(toolUrl "2.0.0", 404))

                match failure (ensure fixture installer actor "h" other) with
                | DownloadFailed reason -> Expect.stringContains reason "404" "the status"
                | other -> failtest $"expected a download failure, got {other}"

                Expect.equal fixture.Fetch.Requests.Length 4 "not retried"

            test "methods other than archive are refused with a clear error"
            <| fun fixture ->
                let installer = Installer(fixture, "a")
                let actor = installer.Start fixture.Env

                let spec =
                    InstallSpec.create
                        "system-git"
                        (semver "2.0.0")
                        target
                        (InstallMethod.System
                            {
                                Probe = [ "git"; "--version" ]
                                Constraint = ">=2"
                            })
                        { Executables = [] }

                match ensure fixture installer actor "h" spec with
                | Error(Refused reason) -> Expect.stringContains reason "T211" "names the task that adds it"
                | other -> failtest $"expected a refusal, got {other}"
        ]
