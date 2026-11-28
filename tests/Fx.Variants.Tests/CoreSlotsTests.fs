module Fx.Variants.Tests.CoreSlotsTests

open Expecto
open Fx.Contracts
open Fx.Variants
open Fx.Variants.Fakes
open Fx.Variants.Tests.Support

let private core = SlotRegistry.ofSlots Slots.all

[<Tests>]
let tests =
    testList
        "Core slots"
        [
            test "the core slots form a registry" {
                Expect.equal
                    (core.Slots |> List.map (fun s -> s.Id.Value))
                    [
                        "agent.actionSurface"
                        "edit.guard"
                        "values.previewLimits"
                        "values.inlineMaxBytes"
                        "values.argumentMaxBytes"
                        "shell.windows"
                        "goal.context"
                        "model.role.main"
                        "model.role.fast"
                        "model.role.summarizer"
                        "model.role.review"
                        "model.role.judge"
                        "model.role.embed"
                    ]
                    "ids"
            }

            test "their defaults" {
                let defaults = Resolution.defaults core
                Expect.equal (Resolution.value Slots.actionSurface defaults) ActionSurface.CellsOnly "action surface"
                Expect.equal (Resolution.value Slots.editGuard defaults) EditGuard.Warn "edit guard"

                Expect.equal
                    (Resolution.value Slots.previewLimits defaults)
                    {
                        MaxChars = 4000
                        MaxItems = 50
                        MaxDepth = 4
                        MaxStringChars = 1000
                    }
                    "preview limits"

                Expect.equal (Resolution.value Slots.inlineValueMaxBytes defaults) 16384 "inline values"

                Expect.equal (Resolution.value Slots.argumentMaxBytes defaults) (8 * 1024 * 1024) "host-call arguments"
                Expect.equal (Resolution.value Slots.windowsShell defaults) WindowsShell.Pwsh "windows shell"
                Expect.equal (Resolution.value Slots.goalContext defaults) GoalContext.FreshHandoff "goal context"
                Expect.equal (Resolution.value (Slots.modelRole "main") defaults) None "main model"
            }

            test "model roles take provider/model references" {
                let variants =
                    FakeVariants.core [ Selections.select (Slots.modelRole "review") (Some "openai/gpt-5") ]

                Expect.equal
                    (runIn (env variants) (Variants.get (Slots.modelRole "review")))
                    (Some "openai/gpt-5")
                    "selected"

                match Resolution.resolve core [] [ layer Layer.Host "h" [ "model.role.main", str "no-provider" ] ] with
                | Ok _ -> failtest "resolved"
                | Error issues ->
                    Expect.equal
                        (issues |> List.map string)
                        [
                            "host (h): model.role.main: expected a string matching /^[A-Za-z0-9][A-Za-z0-9._-]*/[^\\s]+$/, got string \"no-provider\""
                        ]
                        "issues"
            }

            test "the core slots, as fx lists them" {
                Golden.expect "slots.json" (Json.serializeIndented (SlotRegistry.describe core))
            }

            test "the JSON Schema of a config file's variants" {
                Golden.expect
                    "config.schema.json"
                    (Json.serializeIndented (Schema.toJsonSchema (SlotRegistry.configSchema core)))
            }
        ]
