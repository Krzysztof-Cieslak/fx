/// The pinned Bun for real: downloaded from GitHub through the planner, the lockfile and the
/// installer, then launched with the hardened `LaunchSpec`. Each hardening measure has a positive
/// control: the injection works on a plain launch of the same binary and fails on the hardened one
/// (ported from the T009 spike's `hardening.test.ts`). Needs the network, so it runs only with
/// NETWORK_TESTS=1 (CI: its own step on Linux, macOS and Windows, and in the musl targets' Alpine
/// containers).
module Fx.Ext.RuntimeBun.Tests.LiveTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Threading
open Expecto
open Fx.Components
open Fx.Components.Fakes
open Fx.Components.Live
open Fx.Components.Live.Tests.LiveEnv
open Fx.Components.Tests.Support
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Ext.RuntimeBun
open Fx.Ext.RuntimeBun.Tests.InstallTests
open Fx.Sdk

let private enabled () =
    Environment.GetEnvironmentVariable "NETWORK_TESTS" = "1"

let private requireNetwork () =
    if not (enabled ()) then
        skiptest "set NETWORK_TESTS=1 to download Bun"

type private Fixture =
    {
        Outside: string
        Target: TargetTriple
        Installed: InstalledComponent
        Bun: string
    }

/// Installs Bun once per test run, into a temporary directory removed when the process exits.
let private fixture =
    lazy
        (let target = machine ()

         let lockfile =
             match
                 Planner.plan
                     PlanSettings.defaults
                     [ RuntimeBun.bunComponent ]
                     [
                         {
                             Ref = { Id = "bun"; Version = "^1.4" }
                             By = "test"
                         }
                     ]
                     [ target ]
             with
             | Ok lockfile -> lockfile
             | Error errors -> failtest (errors |> List.map string |> String.concat "; ")

         let outside = tempDirectory ()
         AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> removeDirectory outside)
         use client = HttpFetch.client ()

         let env =
             { create (FakeFetch(LocalFs.write)) None with
                 Fetch = HttpFetch.create client
                 Shell = LocalShell.create (LocalShell.defaultShell ()) outside
             }

         let installed = install env (Path.Combine(outside, "runtimes")) lockfile target

         {
             Outside = outside
             Target = target
             Installed = installed
             Bun = installed.Bin |> List.find (fun (name, _) -> name = "bun") |> snd
         })

let private shell =
    LocalShell.create (LocalShell.defaultShell ()) (Path.GetTempPath())

let private within (task: Tasks.Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromMinutes 2.0).GetAwaiter().GetResult()

let private run (spec: ProcSpec) : Output =
    match within (shell.Run { Spec = spec; Stdin = [||] } CancellationToken.None) with
    | Ok output -> output
    | Error e -> failtest (string e)

/// The launcher's own environment, as a composition root reads it.
let private processEnvironment () : (string * string) list =
    [
        for entry in Environment.GetEnvironmentVariables() |> Seq.cast<Collections.DictionaryEntry> do
            match entry.Key, entry.Value with
            | (:? string as name), (:? string as value) -> name, value
            | _ -> ()
    ]

/// A new directory under the fixture's.
let private directory (f: Fixture) (name: string) : string =
    let dir =
        Path.Combine(f.Outside, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8))

    Directory.CreateDirectory dir |> ignore
    dir

/// The hardened launch of `program`, with owned directories of its own, written before.
let private hardened
    (f: Fixture)
    (runtime: RuntimeSpec)
    (configure: LaunchSession -> LaunchSession)
    (program: string list)
    : ProcSpec * OwnedDirs =
    let owned = OwnedDirs.under (directory f "owned")
    Expect.equal (runIn localFs (RuntimeSpec.prepare runtime owned)) (Ok()) "prepared"

    let session =
        configure
            {
                Target = f.Target
                Owned = owned
                Workspace = None
                ProjectTrusted = false
                HostEnvironment = processEnvironment ()
            }

    match RuntimeSpec.launch runtime f.Installed session program with
    | Ok proc -> proc, owned
    | Error issues -> failtest (Issue.render issues)

/// A plain launch of the same binary: the launcher's environment plus `extra`, in `cwd`.
let private plain (f: Fixture) (cwd: string) (extra: (string * string) list) (program: string list) : ProcSpec =
    {
        Program = Exec(f.Bun, program)
        Cwd = Some cwd
        Env = Inherit(extra |> List.map (fun (name, value) -> name, Some value))
    }

let private marker (dir: string) (name: string) = Path.Combine(dir, "ran-" + name)

/// A workspace that tries every project-local and environment injection we know of, and the
/// variables that try the rest. `probe.ts` prints what it sees as JSON.
let private hostileWorkspace (f: Fixture) : string * (string * string) list =
    let dir = directory f "hostile"

    let write (name: string) (text: string) =
        File.WriteAllText(Path.Combine(dir, name), text)

    let writeMarker (name: string) =
        $"require(\"node:fs\").writeFileSync({Json.quote (marker dir name)}, \"x\");\n"

    Directory.CreateDirectory(Path.Combine(dir, "node_path", "probe-node-path"))
    |> ignore

    write ".env" "HOSTILE_DOTENV=loaded\n"
    write "bunfig.toml" "preload = [\"./evil-bunfig.ts\"]\n"
    write "evil-bunfig.ts" (writeMarker "bunfig")
    write "evil-options.ts" (writeMarker "bun-options")
    write "tsconfig.json" """{ "compilerOptions": { "paths": { "probe-paths": ["./evil-paths.ts"] } } }"""
    write "evil-paths.ts" "export const who = \"tsconfig paths\";\n"

    File.WriteAllText(
        Path.Combine(dir, "node_path", "probe-node-path", "index.js"),
        "module.exports = \"NODE_PATH\";\n"
    )

    write
        "probe.ts"
        """import os from "node:os";
const out: Record<string, unknown> = {
  dotenv: process.env.HOSTILE_DOTENV ?? null,
  nodeOptions: process.env.NODE_OPTIONS ?? null,
};
try { out.paths = (await import("probe-paths")).who; } catch { out.paths = null; }
try { out.nodePath = require("probe-node-path"); } catch { out.nodePath = null; }
out.cwd = process.cwd();
out.home = os.homedir();
out.tmp = os.tmpdir();
out.env = Object.keys(process.env).sort();
console.log(JSON.stringify(out));
"""

    dir,
    [
        "BUN_OPTIONS", "--preload=./evil-options.ts"
        "NODE_OPTIONS", "--require=./evil-options.ts"
        "NODE_PATH", Path.Combine(dir, "node_path")
        "BUN_JSC_dumpOptions", "1"
        "BUN_CONFIG_VERBOSE_FETCH", "curl"
        "NPM_CONFIG_REGISTRY", "https://registry.invalid/"
        "SECRET_TOKEN", "do-not-leak"
    ]

/// What `probe.ts` printed: its last line of output, as JSON.
let private probed (output: Output) : Json =
    let text = (Output.stdoutText output).Trim()

    Expect.equal output.ExitCode 0 $"the probe ran: {Output.stderrText output}"

    let last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.last

    match Json.parse last with
    | Ok json -> json
    | Error e -> failtest $"{e}: {text}"

let private field (name: string) (json: Json) : Json =
    Json.tryField name json |> Option.defaultValue Json.Null

let private text (name: string) (json: Json) : string =
    match field name json with
    | Json.String value -> value
    | other -> failtest $"{name}: {other}"

let private ignoreCase (f: Fixture) =
    if f.Target.Os = Os.Windows then
        StringComparison.OrdinalIgnoreCase
    else
        StringComparison.Ordinal

/// A runtime whose launch runs in the workspace, to show that the flags, not only the owned
/// working directory, keep the project's files out.
let private inWorkspace: RuntimeSpec =
    { RuntimeBun.runtime with
        Launch =
            { RuntimeBun.runtime.Launch with
                Cwd = LaunchCwd.Workspace
            }
    }

/// Reads a process's standard output until it contains `text`, for at most 30 seconds.
let private waitFor (proc: Proc) (text: string) =
    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
    let seen = StringBuilder()

    while not (seen.ToString().Contains text) do
        match within (proc.Read timeout.Token) with
        | Some(Stdout bytes) -> seen.Append(Encoding.UTF8.GetString bytes) |> ignore
        | Some(Stderr _) -> ()
        | None -> failtest $"the process ended before it printed {text}: {seen}"

let private connects (port: int) : bool =
    try
        use client = new TcpClient()
        client.Connect(IPAddress.Loopback, port)
        true
    with :? SocketException ->
        false

let private freePort () : int =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

/// Runs a spec that prints "ready" and sleeps, and says whether `port` accepts a connection within
/// five seconds after that (the debugger's listener starts on its own thread).
let private inspectorListens (spec: ProcSpec) (port: int) : bool =
    match within (shell.Spawn spec CancellationToken.None) with
    | Error e -> failtest (string e)
    | Ok proc ->
        try
            waitFor proc "ready"
            let mutable connected = connects port
            let mutable tries = 0

            while not connected && tries < 50 do
                Thread.Sleep 100
                tries <- tries + 1
                connected <- connects port

            connected
        finally
            within (proc.Kill())
            within (proc.Wait CancellationToken.None) |> ignore

[<Tests>]
let tests =
    testList
        "Bun (network)"
        [
            test "the pins match the release's SHASUMS256.txt" {
                requireNetwork ()
                use client = new HttpClient()

                let url =
                    $"https://github.com/oven-sh/bun/releases/download/bun-v{Pins.Version}/SHASUMS256.txt"

                // GitHub's release downloads answer 503 now and then: retry for about a minute.
                let rec fetch (attempt: int) : string =
                    use response = within (client.GetAsync url)

                    if response.IsSuccessStatusCode || attempt >= 6 then
                        response.EnsureSuccessStatusCode() |> ignore
                        within (response.Content.ReadAsStringAsync())
                    else
                        Thread.Sleep(TimeSpan.FromSeconds(2.0 * float (pown 2 (attempt - 1))))
                        fetch (attempt + 1)

                let sums = fetch 1

                let listed =
                    sums.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun line -> line.Trim())
                    |> Set.ofArray

                for asset in Pins.assets do
                    Expect.isTrue (listed.Contains $"{asset.Sha256}  {asset.Name}.zip") $"{asset.Name}.zip"
            }

            test "the installer installs the pinned build, and its executable is the pinned one" {
                requireNetwork ()
                let f = fixture.Force()
                let asset = Pins.find (Pins.assetFor f.Target)
                use stream = File.OpenRead f.Bun
                Expect.equal (Convert.ToHexStringLower(SHA256.HashData stream)) asset.Executable asset.Name
            }

            test "bun --version through the hardened launch" {
                requireNetwork ()
                let f = fixture.Force()
                let output = run (fst (hardened f RuntimeBun.runtime id [ "--version" ]))
                Expect.equal output.ExitCode 0 (Output.stderrText output)
                Expect.equal ((Output.stdoutText output).Trim()) Pins.Version "version"
            }

            test "every hardening flag exists in the pinned Bun (unknown flags are ignored silently)" {
                requireNetwork ()
                let f = fixture.Force()
                let output = run (fst (hardened f RuntimeBun.runtime id [ "--help" ]))
                let help = Output.stdoutText output

                for flag in RuntimeBun.launch.Args @ RuntimeBun.launch.UntrustedOverrides do
                    let name = flag.Split('=').[0]
                    Expect.isTrue (help.Contains(name + " ") || help.Contains(name + "=")) $"{name} in bun --help"
            }

            test "positive control: project files and injected variables work on a plain launch" {
                requireNetwork ()
                let f = fixture.Force()
                let dir, injected = hostileWorkspace f
                let output = run (plain f dir injected [ "probe.ts" ])
                let seen = probed output
                Expect.equal (text "dotenv" seen) "loaded" ".env"
                Expect.equal (text "paths" seen) "tsconfig paths" "tsconfig.json paths"
                Expect.equal (text "nodePath" seen) "NODE_PATH" "NODE_PATH"
                Expect.equal (text "nodeOptions" seen) "--require=./evil-options.ts" "NODE_OPTIONS reaches the runtime"
                Expect.isTrue (File.Exists(marker dir "bun-options")) "BUN_OPTIONS preloads"
                Expect.isTrue (File.Exists(marker dir "bunfig")) "bunfig.toml preloads"
                Expect.stringContains (Output.stderrText output) "Modified JSC options" "BUN_JSC_*"
            }

            test "the hardened launch ignores project files and injected variables, even in the workspace" {
                requireNetwork ()
                let f = fixture.Force()
                let dir, injected = hostileWorkspace f

                let spec, _ =
                    hardened
                        f
                        inWorkspace
                        (fun s ->
                            { s with
                                Workspace = Some dir
                                HostEnvironment = s.HostEnvironment @ injected
                            })
                        [ Path.Combine(dir, "probe.ts") ]

                let output = run spec
                let seen = probed output

                for name in [ "dotenv"; "paths"; "nodePath"; "nodeOptions" ] do
                    Expect.equal (field name seen) Json.Null name

                Expect.isFalse (File.Exists(marker dir "bun-options")) "no BUN_OPTIONS preload"
                Expect.isFalse (File.Exists(marker dir "bunfig")) "no project bunfig"
                Expect.isFalse ((Output.stderrText output).Contains "Modified JSC options") "no BUN_JSC_*"
                Expect.isTrue ((text "cwd" seen).EndsWith(Path.GetFileName dir, ignoreCase f)) "in the workspace"

                let names =
                    match field "env" seen with
                    | Json.Array items ->
                        items
                        |> List.map (function
                            | Json.String name -> name
                            | other -> failtest (string other))
                    | other -> failtest (string other)

                let allowed (name: string) =
                    RuntimeBun.launch.EnvAllowlist
                    |> List.exists (fun pattern -> EnvPattern.matches (f.Target.Os = Os.Windows) pattern name)
                    || RuntimeBun.launch.EnvSet |> List.exists (fun (set, _) -> set = name)
                    // Bun sets it for --no-orphans on Linux and macOS, so that its children inherit it.
                    || name = "BUN_FEATURE_FLAG_NO_ORPHANS"

                for name in names do
                    Expect.isTrue (allowed name) $"{name} reached the runtime"

                Expect.isFalse (List.contains "SECRET_TOKEN" names) "no secret"
                Expect.isFalse (List.contains "HOSTILE_DOTENV" names) "no .env"
            }

            test "the project's tsconfig.json is overridden only until the project is trusted" {
                requireNetwork ()
                let f = fixture.Force()
                let dir, _ = hostileWorkspace f

                let spec, _ =
                    hardened
                        f
                        inWorkspace
                        (fun s ->
                            { s with
                                Workspace = Some dir
                                ProjectTrusted = true
                            })
                        [ Path.Combine(dir, "probe.ts") ]

                let seen = probed (run spec)
                Expect.equal (text "paths" seen) "tsconfig paths" "trusted: the project's paths apply"
                Expect.equal (field "dotenv" seen) Json.Null "but never its .env"
                Expect.isFalse (File.Exists(marker dir "bunfig")) "nor its bunfig.toml"
            }

            test "the hardened launch runs in owned directories: cwd, home and temp" {
                requireNetwork ()
                let f = fixture.Force()
                let dir, _ = hostileWorkspace f
                let spec, owned = hardened f RuntimeBun.runtime id [ Path.Combine(dir, "probe.ts") ]
                let seen = probed (run spec)

                Expect.isTrue
                    ((text "cwd" seen).EndsWith(Path.Combine(Path.GetFileName owned.Root, "cwd"), ignoreCase f))
                    "owned cwd"

                Expect.equal (text "home" seen) owned.Home "owned home"
                Expect.equal ((text "tmp" seen).TrimEnd(Path.DirectorySeparatorChar)) owned.Temp "owned temp"
            }

            test "positive control: BUN_INSPECT opens a debugger on a plain launch, not on the hardened one" {
                requireNetwork ()
                let f = fixture.Force()
                let dir = directory f "inspect"
                let script = Path.Combine(dir, "ready.ts")
                File.WriteAllText(script, "console.log(\"ready\");\nawait Bun.sleep(20000);\n")

                let port = freePort ()
                let inspect = [ "BUN_INSPECT", $"127.0.0.1:{port}" ]
                Expect.isTrue (inspectorListens (plain f dir inspect [ script ]) port) "plain: a debugger listens"

                let port = freePort ()
                let inspect = [ "BUN_INSPECT", $"127.0.0.1:{port}" ]

                let spec, _ =
                    hardened
                        f
                        RuntimeBun.runtime
                        (fun s ->
                            { s with
                                HostEnvironment = s.HostEnvironment @ inspect
                            })
                        [ script ]

                Expect.isFalse (inspectorListens spec port) "hardened: nothing listens"
            }

            test "the hardened environment is enough to work: crypto, sockets, temp files, child processes" {
                requireNetwork ()
                let f = fixture.Force()
                let dir = directory f "smoke"
                let script = Path.Combine(dir, "smoke.ts")

                File.WriteAllText(
                    script,
                    """import os from "node:os";
import { join } from "node:path";
import { writeFileSync, readFileSync } from "node:fs";
const checks: Record<string, unknown> = {};
checks.uuid = crypto.randomUUID().length === 36;
const file = join(os.tmpdir(), "smoke.txt");
writeFileSync(file, "x");
checks.temp = readFileSync(file, "utf8") === "x";
const server = Bun.listen({ hostname: "127.0.0.1", port: 0, socket: { data(s, d) { s.write(d); } } });
const echoed = await new Promise<string>((resolve) => {
  Bun.connect({ hostname: "127.0.0.1", port: server.port, socket: {
    open(s) { s.write("ping"); }, data(s, d) { resolve(d.toString()); s.end(); } } });
});
server.stop(true);
checks.socket = echoed === "ping";
const child = Bun.spawnSync([process.execPath, "--version"]);
checks.child = child.exitCode === 0;
checks.intl = typeof new Intl.DateTimeFormat().resolvedOptions().timeZone === "string";
console.log(JSON.stringify(checks));
"""
                )

                let seen = probed (run (fst (hardened f RuntimeBun.runtime id [ script ])))

                for name in [ "uuid"; "temp"; "socket"; "child"; "intl" ] do
                    Expect.equal (field name seen) (Json.Bool true) name
            }
        ]
