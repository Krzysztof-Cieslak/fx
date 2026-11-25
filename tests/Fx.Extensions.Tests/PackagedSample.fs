/// The F# sample built from the SDK's NuGet packages alone (`bun scripts/packages.ts` builds it into
/// artifacts/package-consumer/out, outside the solution), as a process extension. Without it the tests
/// are skipped, unless PACKAGE_TESTS=1 (CI), which requires it.
module Fx.Extensions.Tests.PackagedSample

open System
open System.IO
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

/// Its directory (`${root}`).
let root = Repo.path [ "artifacts"; "package-consumer"; "out" ]

let private executable =
    Path.Combine(root, "PackagedSample" + (if OperatingSystem.IsWindows() then ".exe" else ""))

let private require () =
    if not (File.Exists executable) then
        let reason =
            "the sample built from the SDK packages is missing: run bun scripts/packages.ts after a Release build"

        if Environment.GetEnvironmentVariable "PACKAGE_TESTS" = "1" then
            failtest reason
        else
            skiptest reason

/// How the process runtime starts it: as the ext-peer child, from its own directory.
let launch () : LaunchSpec =
    match (processManifest ()).Launch with
    | Some peer ->
        { peer with
            Exe = "${root}/PackagedSample${exeSuffix}"
            Args = []
        }
    | None -> failwith "the ext-peer manifest has no launch"

/// The packaged sample loaded by the host's `process` runtime, for the parity suite.
let load () : Task<Loaded> =
    task {
        require ()

        let env =
            hostEnv (LocalShell.create (LocalShell.defaultShell ()) AppContext.BaseDirectory)

        let manifest =
            { Sample.manifest with
                Runtime = Runtimes.Process
                Launch = Some(launch ())
            }

        let! host =
            within (
                Eff.run
                    env
                    (ExtensionHost.bootstrap
                        settings
                        [ Candidate.create Source.Builtin (Package.Manifest(manifest, Some root)) ])
            )

        return
            {
                Host = host
                Env = env
                Dispose = fun () -> within (Eff.run env (ExtensionHost.stop host)) :> Task
            }
    }

/// A connection to the packaged sample's process, for the server conformance suite.
let connect: ServerConformance.Connect =
    fun members ->
        task {
            require ()
            return! ServerConformance.child root (launch ()) members
        }
