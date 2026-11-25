/// The sample extension passes the same suites in the host's process (`native`), behind the process
/// runtime over in-memory pipes, in a child process (`process`: `Fx.Aot.Scenarios ext-peer`, served by
/// the F# SDK's `ExtensionProcess.run`), and written in TypeScript against `@fx/sdk`, as a process on
/// the pinned Bun.
module Fx.Extensions.Tests.ParityTests

open Expecto
open Fx.Aot.Scenarios.Extensions

[<Tests>]
let tests =
    testList
        "Transport parity"
        [
            ParitySuite.tests "native" SampleHost.native
            ParitySuite.tests "process (in memory)" SampleHost.inMemoryProcess
            ParitySuite.tests "process (child)" SampleHost.childProcess
            ParitySuite.tests "process (TypeScript on Bun)" TypeScriptSample.load
        ]

[<Tests>]
let servers =
    let peer =
        match (SampleHost.processManifest ()).Launch with
        | Some launch -> launch
        | None -> failwith "the ext-peer manifest has no launch"

    testList
        "Extension protocol servers"
        [
            ServerConformance.tests "F# (in process)" ServerConformance.inProcess
            ServerConformance.tests "F# (child)" (ServerConformance.child System.AppContext.BaseDirectory peer)
            ServerConformance.tests "TypeScript on Bun" TypeScriptSample.connect
        ]
