/// The TypeScript sample extension (`packages/sample-extension`, written against `@fx/sdk`) as a process
/// extension on the pinned Bun: bundled into one file with it, launched with `fx.runtime.bun`'s hardened
/// launch (its flags, untrusted overrides, files and environment, with the owned directories under the
/// package directory) by the host's `process` runtime from the F# sample's manifest: the TypeScript SDK's
/// server on its own (BunRuntime.fs loads the sample on the `bun` runtime). Without the pinned Bun
/// (offline) its tests are skipped locally and fail in CI.
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
open Fx.Ext.RuntimeBun
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

/// Bundles a module of the sample's package into one file with the pinned Bun (`bun build --target=bun`).
let bundle (bun: string) (entry: string) (output: string) : Task<unit> =
    task {
        let package = Repo.path [ "packages"; "sample-extension" ]

        let info =
            ProcessStartInfo(bun, [ "build"; entry; "--target=bun"; $"--outfile={output}" ])

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
    }

let private prepared =
    lazy
        (task {
            match! PinnedBun.executable () with
            | Error reason -> return Error reason
            | Ok bun ->
                try
                    let bundled =
                        Repo.path [ "artifacts"; "sample-extension"; $"sample-{Environment.ProcessId}.js" ]

                    do! bundle bun (Repo.path [ "packages"; "sample-extension"; "src"; "main.ts" ]) bundled
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

/// `fx.runtime.bun`'s placeholders for a launch whose owned directories are under the package
/// directory (`${root}`), its files in `${root}/config`.
let private ownedPlaceholders =
    [
        "${owned.root}", "${root}"
        "${owned.cwd}", "${root}"
        "${owned.home}", "${root}/home"
        "${owned.config}", "${root}/config"
        "${owned.cache}", "${root}/cache"
        "${owned.temp}", "${root}/tmp"
        yield!
            RuntimeBun.runtime.Files
            |> List.map (fun file -> "${file." + file.Name + "}", "${root}/config/" + file.Name)
    ]

let private owned (text: string) =
    ownedPlaceholders
    |> List.fold (fun (t: string) (placeholder, value) -> t.Replace(placeholder, value, StringComparison.Ordinal)) text

/// A package directory of its own for one process (`${root}`): the bundle, `fx.runtime.bun`'s files,
/// and the home, cache and temporary directories the launch points Bun at.
let private ownedRoot (prepared: Prepared) : string =
    let n = Interlocked.Increment counter

    let root =
        Repo.path [ "artifacts"; "sample-extension"; "owned"; $"{Environment.ProcessId}-{n}" ]

    for dir in [ "home"; "tmp"; "cache"; "config" ] do
        Directory.CreateDirectory(Path.Combine(root, dir)) |> ignore

    File.Copy(prepared.Bundle, Path.Combine(root, "sample.js"), true)

    for file in RuntimeBun.runtime.Files do
        File.WriteAllText(Path.Combine(root, "config", file.Name), file.Content)

    root

/// How the process runtime starts the sample: `fx.runtime.bun`'s hardened launch (data, T114) on the
/// pinned Bun, running the bundle in its package directory. Its untrusted overrides apply: the tests'
/// project is not trusted.
let launch (prepared: Prepared) : LaunchSpec =
    let bun = RuntimeBun.launch

    { bun with
        Exe = prepared.Bun
        Args = (bun.Args |> List.map owned) @ [ "${root}/sample.js" ]
        UntrustedOverrides = bun.UntrustedOverrides |> List.map owned
        EnvSet = bun.EnvSet |> List.map (fun (name, value) -> name, owned value)
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
