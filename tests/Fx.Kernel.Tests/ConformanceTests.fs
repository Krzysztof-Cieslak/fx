/// The kernel conformance suite against every kernel implementation in the repository.
module Fx.Kernel.Tests.ConformanceTests

open Expecto

[<Tests>]
let tests =
    testList
        "kernel conformance"
        [
            KernelConformance.tests Subjects.scriptInProcess
            KernelConformance.tests Subjects.scriptProcess
            KernelConformance.tests Subjects.bunKernelLazy
        ]
