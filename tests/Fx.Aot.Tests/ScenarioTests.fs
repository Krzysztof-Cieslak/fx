module Fx.Aot.Tests.ScenarioTests

open System.Text.RegularExpressions
open Expecto
open Fx.Aot.Scenarios

let private platform = Platform.current ()

let private output (scenario: Scenario) = scenario.Run().GetAwaiter().GetResult()

let private linux =
    {
        Os = Os.Linux
        Arch = System.Runtime.InteropServices.Architecture.X64
        Musl = false
    }

[<Tests>]
let tests =
    testList
        "Scenarios"
        [
            test "names are unique and kebab-case" {
                let names = Registry.all @ Registry.canaries |> List.map (fun s -> s.Name)
                Expect.equal (List.distinct names) names "unique"

                for name in names do
                    Expect.isTrue (Regex.IsMatch(name, "^[a-z0-9]+(-[a-z0-9]+)*$")) $"kebab-case: {name}"
            }

            test "canaries are not part of the diff" {
                for canary in Registry.canaries do
                    Expect.isFalse (List.contains canary.Name (Registry.all |> List.map string)) canary.Name
                    Expect.equal (Registry.tryFind canary.Name |> Option.map string) (Some canary.Name) "findable"
            }

            test "platform gates compose" {
                let s = Scenario.lines "s" (fun () -> [])
                let windows = { linux with Os = Os.Windows }
                let musl = { linux with Musl = true }
                Expect.isTrue (s.RunsOn linux) "everywhere"
                Expect.isFalse ((Scenario.onlyOn [ Os.Windows ] s).RunsOn linux) "onlyOn"
                Expect.isTrue ((Scenario.onlyOn [ Os.Windows ] s).RunsOn windows) "onlyOn"
                Expect.isFalse ((Scenario.except [ Os.Linux ] s).RunsOn linux) "except"

                let glibcOnly =
                    s |> Scenario.onlyOn [ Os.Linux ] |> Scenario.where (fun p -> not p.Musl)

                Expect.isTrue (glibcOnly.RunsOn linux) "glibc"
                Expect.isFalse (glibcOnly.RunsOn musl) "musl"
                Expect.isFalse (glibcOnly.RunsOn windows) "windows"
            }

            test "platform names" {
                Expect.equal (string linux) "linux-x64" "glibc"
                Expect.equal (string { linux with Musl = true }) "linux-musl-x64" "musl"

                Expect.equal
                    (string
                        { linux with
                            Os = Os.MacOS
                            Arch = System.Runtime.InteropServices.Architecture.Arm64
                        })
                    "macos-arm64"
                    "macos"
            }

            test "the canary reports the JIT on the JIT" {
                Expect.equal (output Canary.runtimeMode) [ "code generation = jit" ] "output"
            }

            testList
                "run on the JIT with deterministic output"
                [
                    for scenario in Registry.all do
                        if scenario.RunsOn platform then
                            test scenario.Name {
                                let first = output scenario
                                Expect.isNonEmpty first "output"
                                Expect.equal (output scenario) first "same output when run again"
                            }
                ]
        ]
