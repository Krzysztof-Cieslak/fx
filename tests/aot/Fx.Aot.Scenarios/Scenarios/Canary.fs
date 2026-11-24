/// Scenarios that must differ between the JIT and Native AOT: the diff harness's self-test.
module Fx.Aot.Scenarios.Canary

open System.Diagnostics.CodeAnalysis

type private Marker = class end

/// A native binary has no managed assembly files, so `Assembly.Location` is empty there and a path on
/// the JIT. (`RuntimeFeature.IsDynamicCodeCompiled` is not a canary: the RID-specific JIT build runs
/// with the same feature switches as the native binary.)
[<UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "The empty location is the point.")>]
let private codeGeneration () =
    if System.String.IsNullOrEmpty typeof<Marker>.Assembly.Location then
        "native"
    else
        "jit"

let runtimeMode =
    Scenario.lines "canary-runtime-mode" (fun () -> [ $"code generation = {codeGeneration ()}" ])

let scenarios = [ runtimeMode ]
