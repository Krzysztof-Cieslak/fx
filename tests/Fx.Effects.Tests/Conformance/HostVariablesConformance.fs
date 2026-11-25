/// The host-variables conformance suite: what every `HostVariables` interpreter must do. Also compiled
/// into Fx.Effects.Live.Tests, where `SystemVariables` runs it on the process's own variables.
module Fx.Effects.Tests.Conformance.HostVariablesConformance

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects

let private sync (task: Task<'T>) =
    task.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

let private reveal (value: SecretText option) = value |> Option.map _.Reveal()

/// Tests that `create` conforms: it gets variables to set (name, value) and returns the interpreter
/// that sees them. Every name it gets is unique to its test.
let tests (name: string) (create: (string * string) list -> HostVariables) : Test =
    let unique (label: string) =
        "FX_CONFORMANCE_"
        + label
        + "_"
        + Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant()

    testList
        $"HostVariables conformance ({name})"
        [
            testCase "a set variable reads as its value, which never prints" (fun () ->
                let name = unique "SET"
                let variables = create [ name, "sk-test 1=2;\"x\"" ]
                let value = sync (variables.Read name CancellationToken.None)
                Expect.equal (reveal value) (Some "sk-test 1=2;\"x\"") "value"
                Expect.equal (string value.Value) "(redacted)" "never prints")

            testCase "unset and empty variables read as None" (fun () ->
                let empty = unique "EMPTY"
                let variables = create [ empty, "" ]
                Expect.isNone (sync (variables.Read (unique "UNSET") CancellationToken.None)) "unset"
                Expect.isNone (sync (variables.Read empty CancellationToken.None)) "empty")

            testCase "names that are not variables' read as None" (fun () ->
                let variables = create []

                for bad in [ ""; "A=B"; "1ABC"; "A B"; String('A', 129) ] do
                    Expect.isNone (sync (variables.Read bad CancellationToken.None)) bad)

            testCase "a cancelled read is cancelled" (fun () ->
                let variables = create []
                use cancelled = new CancellationTokenSource()
                cancelled.Cancel()

                Expect.isTrue (variables.Read "PATH" cancelled.Token).IsCanceled "cancelled")
        ]
