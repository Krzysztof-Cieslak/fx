/// The bun-ts interpreter's data: the provider conformance suite, the package's manifest and
/// activation, and the declarations and prompt of the core bridge (golden files).
module Fx.Ext.InterpreterBunTs.Tests.BunTsTests

open System
open System.IO
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Ext.InterpreterBunTs
open Fx.Interpreters
open Fx.Interpreters.Tests
open Fx.Interpreters.Tests.Support
open Fx.Sdk

/// Golden files in `golden/` next to these tests; `UPDATE_GOLDEN=1` rewrites them.
module Golden =
    let expect (name: string) (actual: string) =
        Fx.Kernel.Tests.Repo.expectGenerated [ "tests"; "Fx.Ext.InterpreterBunTs.Tests"; "golden"; name ] actual

/// The core bridge a session gets (T127 adds the session's own bindings and its `TurnResult`).
let coreBridge (settings: CoreSettings) : Bridge<Env> =
    Bridge.create (Finishing.create turnCodec [ summaryEnsure ] (fun _ -> Eff.unit))
    |> Bridge.add CoreBindings.groups (CoreBindings.all settings)

let coreSurface = Bridge.surface (coreBridge (CoreSettings.defaults workspace os))

[<Tests>]
let tests =
    testList
        "bun-ts"
        [
            InterpreterConformance.tests "conformance" BunTs.provider

            testCase "the package's data is valid"
            <| fun () -> Expect.isEmpty (BunTs.check () |> List.map string) "issues"

            testCase "its id comes from the product name, and it runs on fx.runtime.bun's runtime"
            <| fun () ->
                Expect.equal BunTs.id (Product.extensionId "interpreter.bun-ts") "id"
                Expect.equal BunTs.provider.Launch.Runtime Fx.Ext.RuntimeBun.RuntimeBun.ComponentId "runtime"

                for required in BunTs.provider.Requires do
                    match VersionRange.parse required.Version with
                    | Ok range ->
                        Expect.isTrue
                            (VersionRange.satisfies range Fx.Ext.RuntimeBun.RuntimeBun.bunComponent.Version)
                            "the pinned Bun satisfies it"
                    | Error e -> failtest e

            testTask "activation provides exactly what the manifest declares" {
                let! (activation: Activation) =
                    Eff.run
                        (BasicExtensionEnv.forManifest Fx.Effects.Live.SystemClock.clock BunTs.manifest)
                        BunTs.extension.Activate

                let provided = activation.Contributions |> List.map (fun c -> c.Type, c.Id)

                let declared = BunTs.manifest.Contributions |> List.map (fun c -> c.Type, c.Id)

                Expect.equal provided declared "contributions"

                match
                    activation.Contributions
                    |> List.tryPick (Contribution.tryValue InterpreterProviders.contributionType)
                with
                | Some provider -> Expect.equal provider.Id BunTs.InterpreterId "the interpreter"
                | None -> failtest "no interpreter"
            }

            testCase "the core bridge's declarations"
            <| fun () -> Golden.expect $"{Product.kernelGlobal}.d.ts" (BunTs.declarations coreSurface)

            testCase "the prompt for the core bridge"
            <| fun () -> Golden.expect "prompt.md" (PromptPack.render BunTs.promptPack (BunTs.bindings coreSurface))

            testCase "the binding file is <global>.d.ts, the text the prompt shows"
            <| fun () ->
                let files = BunTs.bindings coreSurface
                Expect.equal (files.Files |> List.map (fun f -> f.Name)) [ Product.kernelGlobal + ".d.ts" ] "one file"
                Expect.stringEnds files.Files[0].Content files.Declarations "the same text"
                Expect.isFalse (files.Declarations.Contains "Do not edit") "without the header"

            testCase "nothing in the prompt names the product but the global"
            <| fun () ->
                let prompt = PromptPack.render BunTs.promptPack (BunTs.bindings coreSurface)

                for example in BunTs.promptPack.Examples do
                    Expect.stringContains example.Code (Product.kernelGlobal + ".") example.Title

                Expect.stringContains prompt "// intent:" "intent lines"
        ]
