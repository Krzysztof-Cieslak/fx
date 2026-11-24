/// The sample extension passes the same suite in the host's process (`native`), behind the process
/// runtime over in-memory pipes, and in a child process (`process`: `Fx.Aot.Scenarios ext-peer`).
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
        ]
