/// The TypeScript sample extension (`packages/sample-extension`, written against `@fx/sdk`) as a process
/// extension on the pinned Bun: bundled into one file with it, launched with the Bun kernel spike's
/// hardened flags in an owned directory (T114 owns the real launch), and loaded by the host's `process`
/// runtime from the F# sample's manifest. Without the pinned Bun (offline) its tests are skipped
/// locally and fail in CI.
module Fx.Extensions.Tests.TypeScriptSample

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Live
open Fx.Extensions
open Fx.Sdk
open Fx.Ext.Sample
open Fx.Kernel.Tests
open Fx.Aot.Scenarios.Extensions
open Fx.Aot.Scenarios.Extensions.SampleHost

/// The pinned Bun and the sample bundled with it.
type Prepared =
    {
        Bun: string
        Bundle: string
    }

    override this.ToString() = $"{this.Bun} {this.Bundle}"

let private bundle (bun: string) : Task<string> =
    task {
        let package = Repo.path [ "packages"; "sample-extension" ]

        let output =
            Repo.path [ "artifacts"; "sample-extension"; $"sample-{Environment.ProcessId}.js" ]

        let info =
            ProcessStartInfo(
                bun,
                [
                    "build"
                    Path.Combine(package, "src", "main.ts")
                    "--target=bun"
                    $"--outfile={output}"
                ]
            )

        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.UseShellExecute <- false
        info.WorkingDirectory <- package
        info.Environment["DO_NOT_TRACK"] <- "1"

        use proc =
            match Process.Start info with
            | null -> failwith $"{bun} did not start"
            | p -> p

        let out = proc.StandardOutput.ReadToEndAsync()
        let err = proc.StandardError.ReadToEndAsync()
        do! proc.WaitForExitAsync()
        let! log = out
        let! errors = err

        if proc.ExitCode <> 0 then
            failwith $"bundling the TypeScript sample failed: {log}{errors}"

        return output
    }

let private prepared =
    lazy
        (task {
            match! PinnedBun.executable () with
            | Error reason -> return Error reason
            | Ok bun ->
                try
                    let! bundled = bundle bun
                    return Ok { Bun = bun; Bundle = bundled }
                with e ->
                    return Error(e.GetType().Name + ": " + e.Message)
        })

/// The pinned Bun and the bundle, or a skipped test (a failed one in CI).
let prepare () : Task<Prepared> =
    task {
        match! prepared.Force() with
        | Ok p -> return p
        | Error reason when Environment.GetEnvironmentVariable "CI" = "true" ->
            return failtest $"the pinned Bun is not available: {reason}"
        | Error reason -> return skiptest $"the pinned Bun is not available: {reason}"
    }

let private counter = ref 0

/// The owned bunfig (docs/spikes/bun-kernel.md): the same switches as the flags.
let private bunfig =
    "telemetry = false\nenv = false\n\n[install]\nauto = \"disable\"\n\n[run]\nnoOrphans = true\n"

/// What a process on Bun needs from the host's environment (Windows basics); nothing else.
let private allowlist =
    [
        "PATH"
        "SystemRoot"
        "SystemDrive"
        "windir"
        "ComSpec"
        "PATHEXT"
        "NUMBER_OF_PROCESSORS"
        "PROCESSOR_ARCHITECTURE"
        "OS"
        "LANG"
        "TZ"
    ]

/// A package directory of its own for one process (`${root}`): the bundle, the bunfig, and the
/// home, cache and temporary directories the launch points Bun at.
let private ownedRoot (prepared: Prepared) : string =
    let n = Interlocked.Increment counter

    let root =
        Repo.path [ "artifacts"; "sample-extension"; "owned"; $"{Environment.ProcessId}-{n}" ]

    for dir in [ "home"; "tmp"; "cache" ] do
        Directory.CreateDirectory(Path.Combine(root, dir)) |> ignore

    File.Copy(prepared.Bundle, Path.Combine(root, "sample.js"), true)
    File.WriteAllText(Path.Combine(root, "bunfig.toml"), bunfig)
    root

/// How the process runtime starts the sample: the pinned Bun, hardened, running the bundle in its
/// package directory.
let launch (prepared: Prepared) : LaunchSpec =
    {
        Exe = prepared.Bun
        Args =
            [
                "--config=${root}/bunfig.toml"
                "--no-env-file"
                "--no-install"
                "--no-orphans"
                "--no-macros"
                "${root}/sample.js"
            ]
        EnvAllowlist = allowlist
        EnvSet =
            [
                "DO_NOT_TRACK", "1"
                "NO_COLOR", "1"
                "HOME", "${root}/home"
                "USERPROFILE", "${root}/home"
                "XDG_CONFIG_HOME", "${root}/home/config"
                "XDG_CACHE_HOME", "${root}/cache"
                "TMPDIR", "${root}/tmp"
                "TEMP", "${root}/tmp"
                "TMP", "${root}/tmp"
                "BUN_RUNTIME_TRANSPILER_CACHE_PATH", "${root}/cache/transpiler"
                "BUN_INSTALL_CACHE_DIR", "${root}/cache/install"
            ]
        EnvDeny =
            [
                "BUN_INSPECT*"
                "BUN_CONFIG_*"
                "BUN_JSC_*"
                "BUN_FEATURE_FLAG_*"
                "BUN_DEBUG*"
                "NODE_PATH"
                "NODE_CHANNEL_*"
                "LD_LIBRARY_PATH"
                "DYLD_*"
            ]
        Cwd = LaunchCwd.Owned
    }

let private remove (root: string) =
    try
        Directory.Delete(root, true)
    with _ ->
        ()

/// The F# sample's manifest, as the process extension the TypeScript sample implements.
let manifest (prepared: Prepared) : Manifest =
    { Sample.manifest with
        Runtime = Runtimes.Process
        Launch = Some(launch prepared)
    }

/// The TypeScript sample loaded by the host's `process` runtime, for the parity suite.
let load () : Task<Loaded> =
    task {
        let! prepared = prepare ()
        let root = ownedRoot prepared

        let env =
            hostEnv (LocalShell.create (LocalShell.defaultShell ()) AppContext.BaseDirectory)

        let! host =
            within (
                Eff.run
                    env
                    (ExtensionHost.bootstrap
                        settings
                        [
                            Candidate.create Source.Builtin (Package.Manifest(manifest prepared, Some root))
                        ])
            )

        return
            {
                Host = host
                Env = env
                Dispose =
                    fun () ->
                        task {
                            do! within (Eff.run env (ExtensionHost.stop host))
                            remove root
                        }
            }
    }

/// A connection to the TypeScript sample's process, for the server conformance suite.
let connect: ServerConformance.Connect =
    fun members ->
        task {
            let! prepared = prepare ()
            let root = ownedRoot prepared

            match! ServerConformance.child root (launch prepared) members with
            | Ok connection ->
                return
                    Ok
                        { connection with
                            Close =
                                fun () ->
                                    task {
                                        do! connection.Close()
                                        remove root
                                    }
                        }
            | Error e ->
                remove root
                return Error e
        }
