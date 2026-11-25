/// Every scenario, as values. Add a new scenario module's `scenarios` list here.
module Fx.Aot.Scenarios.Registry

/// The scenarios the JIT-vs-native diff runs. Each must produce identical output both ways.
let all: Scenario list =
    List.concat
        [
            Language.scenarios
            Equality.scenarios
            Generics.scenarios
            Exceptions.scenarios
            Collections.scenarios
            Concurrency.scenarios
            Effects.scenarios
            Machines.scenarios
            Text.scenarios
            Runtime.scenarios
            Kestrel.Http.scenarios
            Kestrel.Access.scenarios
            Kestrel.WebSockets.scenarios
            Kestrel.EventStreams.scenarios
            Kestrel.Transports.scenarios
            Kestrel.Builders.scenarios
            Contracts.scenarios
            ContractsLarge.scenarios
            Sqlite.scenarios
            Pty.scenarios
            Store.scenarios
            Variants.scenarios
            Rpc.Wire.scenarios
            Rpc.Transports.scenarios
            Extensions.Extensions.scenarios
            TypeScript.scenarios
            Components.scenarios
            Kernel.Kernel.scenarios
        ]

/// Scenarios that must differ between the JIT and Native AOT. Never listed with `all`: the diff
/// harness runs them only in its self-test, to prove that it detects a difference.
let canaries: Scenario list = Canary.scenarios

let tryFind (name: string) : Scenario option =
    List.append all canaries |> List.tryFind (fun s -> s.Name = name)
