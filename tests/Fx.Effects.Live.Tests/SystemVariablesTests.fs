/// `SystemVariables`: the process's own environment variables pass the host-variables suite.
module Fx.Effects.Live.Tests.SystemVariablesTests

open System
open Expecto
open Fx.Effects.Live
open Fx.Effects.Tests.Conformance

[<Tests>]
let tests =
    HostVariablesConformance.tests "this process" (fun variables ->
        for name, value in variables do
            Environment.SetEnvironmentVariable(name, value)

        SystemVariables.variables)
