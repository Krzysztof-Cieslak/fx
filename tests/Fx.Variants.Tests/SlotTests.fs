module Fx.Variants.Tests.SlotTests

open System
open Expecto
open Fx.Contracts
open Fx.Variants
open Fx.Variants.Tests.Support

let private issues (validation: Validation<'T, Issue>) : string list =
    match validation with
    | Ok value -> failtest $"valid: {value}"
    | Error issues -> issues |> List.map string

let private registryIssues (slots: SlotInfo list) : string list =
    match SlotRegistry.create slots with
    | Ok registry -> failtest $"created {registry}"
    | Error issues -> issues |> List.map string

[<Tests>]
let tests =
    testList
        "Slots"
        [
            test "slot ids are dotted segments" {
                for id in [ "agent.actionSurface"; "model.role.main"; "acme.tools-x.mode_2" ] do
                    Expect.isOk (SlotId.tryCreate id) id

                for id in
                    [
                        ""
                        "single"
                        "Agent.mode"
                        "agent..mode"
                        "agent.Mode"
                        "agent.mode."
                        "agent.1mode"
                        "a.b c"
                    ] do
                    Expect.isError (SlotId.tryCreate id) id

                Expect.isError (SlotId.tryCreate ("a." + String('b', 127))) "too long"
                Expect.throwsT<ArgumentException> (fun () -> SlotId.create "nodots" |> ignore) "defect"
            }

            test "the id pattern agrees with the parser" {
                let regex = Text.RegularExpressions.Regex SlotId.Pattern

                for id in
                    [
                        "agent.actionSurface"
                        "model.role.main"
                        "a.b"
                        "single"
                        "Agent.mode"
                        "a.b-"
                        "a.-b"
                    ] do
                    Expect.equal (regex.IsMatch id) (Result.isOk (SlotId.tryCreate id)) id
            }

            test "a slot's check validates, applies choices and canonicalizes" {
                let info = Slot.info width
                Expect.equal (info.Check(num "100.0")) (Ok(num "100")) "canonical"

                Expect.equal
                    (issues (info.Check(num "90")))
                    [ "$: expected one of 80, 100, 120, got number 90" ]
                    "not a choice"

                Expect.equal (issues (info.Check(str "wide"))) [ "$: expected integer, got string \"wide\"" ] "type"

                Expect.equal
                    (issues ((Slot.info depth).Check(num "11")))
                    [ "$: expected an integer from 1 to 10, got number 11" ]
                    "refinement"

                Expect.equal ((Slot.info ratio).Check(num "0.50")) (Ok(num "0.5")) "decimal scale"
                Expect.equal (Slot.info ratio).Default (num "0.5") "canonical default"
            }

            test "an enumerated slot lists its choices" {
                let info = Slot.info mode
                Expect.equal info.Choices (Some [ str "fast"; str "thorough"; str "careful" ]) "choices"
                Expect.equal info.Default (str "fast") "default"
                Expect.isFalse info.Privileged "not privileged"
                Expect.isTrue (Slot.info sandbox).Privileged "privileged"
                Expect.equal info.Owner SlotOwner.Core "core"

                Expect.equal
                    (Slot.info (mode |> Slot.ownedBy "acme.tools")).Owner
                    (SlotOwner.Extension "acme.tools")
                    "owned by an extension"
            }

            test "a declared slot takes only its choices" {
                let decl: SlotDecl =
                    {
                        Id = "acme.tools.level"
                        Description = "How loud."
                        Default = num "2"
                        Choices = Some [ num "1"; num "2.0"; num "3" ]
                        Privileged = false
                    }

                match SlotInfo.ofDecl (SlotOwner.Extension "acme.tools") decl with
                | Error issues -> failtest (Issue.render issues)
                | Ok info ->
                    Expect.equal info.Choices (Some [ num "1"; num "2"; num "3" ]) "canonical choices"
                    Expect.equal (info.Check(num "3.0")) (Ok(num "3")) "a choice"
                    Expect.equal (issues (info.Check(num "4"))) [ "$: expected one of 1, 2, 3, got number 4" ] "other"
                    Expect.equal (SlotInfo.decl info).Id decl.Id "back to its declaration"
                    Expect.isOk (SlotRegistry.create [ info ]) "registers"

                let named =
                    { decl with
                        Default = str "low"
                        Choices = Some [ str "low"; str "high" ]
                    }

                match SlotInfo.ofDecl SlotOwner.Core named with
                | Ok info -> Expect.equal (Schema.render info.Schema) "\"low\" | \"high\"" "string choices are an enum"
                | Error issues -> failtest (Issue.render issues)
            }

            test "an invalid declaration reports every problem" {
                let decl: SlotDecl =
                    {
                        Id = "Nope"
                        Description = " "
                        Default = num "5"
                        Choices = Some [ num "1"; num "2" ]
                        Privileged = false
                    }

                Expect.equal
                    (issues (SlotInfo.ofDecl SlotOwner.Core decl))
                    [
                        "$.id: a slot id has at least two dotted segments, e.g. \"agent.actionSurface\""
                        "$.description: expected a non-empty description"
                        "$.default: expected one of the choices 1, 2"
                    ]
                    "issues"

                Expect.equal
                    (issues (
                        SlotInfo.ofDecl
                            SlotOwner.Core
                            { decl with
                                Id = "a.b"
                                Description = "d"
                                Choices = None
                            }
                    ))
                    [ "$.choices: a declared slot lists the values it may take" ]
                    "open"

                Expect.equal
                    (issues (
                        SlotInfo.ofDecl
                            SlotOwner.Core
                            { decl with
                                Id = "a.b"
                                Description = "d"
                                Choices = Some [ num "1"; num "1.0" ]
                            }
                    ))
                    [ "$.choices: expected distinct values" ]
                    "duplicates"
            }

            test "declarations round-trip through JSON" {
                let text =
                    """{"id":"acme.tools.level","description":"How loud.","default":2,"choices":[1,2,3]}"""

                match Codec.decodeText SlotInfo.declCodec text with
                | Error issues -> failtest (Issue.render issues)
                | Ok decl ->
                    Expect.isFalse decl.Privileged "privileged defaults to false"

                    Expect.equal
                        (Codec.encodeText SlotInfo.declCodec decl)
                        """{"id":"acme.tools.level","description":"How loud.","default":2,"choices":[1,2,3],"privileged":false}"""
                        "written back"
            }

            test "the registry rejects duplicate ids, invalid defaults and invalid choices" {
                let broken =
                    Slot.create "test.depth" "Out of range." (Codec.int |> Codec.between 1 10) 0
                    |> Slot.ownedBy "acme.tools"

                let badChoice = depth |> Slot.withChoices [ 3; 12; 3 ]
                let noChoices = mode |> Slot.withChoices []

                Expect.equal
                    (registryIssues [ Slot.info depth; Slot.info broken ])
                    [
                        "slot test.depth is defined twice, by core and by acme.tools"
                        "slot test.depth (acme.tools): its default is invalid: $: expected an integer from 1 to 10, got number 0"
                    ]
                    "duplicate and default"

                Expect.equal
                    (registryIssues [ Slot.info badChoice ])
                    [
                        "slot test.depth (core): its choice 12 is invalid: $: expected an integer from 1 to 10, got number 12"
                        "slot test.depth (core): its choices are not distinct"
                    ]
                    "choices"

                Expect.equal
                    (registryIssues [ Slot.info noChoices ])
                    [
                        "slot test.mode (core): its default is invalid: $: expected one of , got string \"fast\""
                        "slot test.mode (core): an enumerated slot needs at least one choice"
                    ]
                    "no choices"

                Expect.throwsT<ArgumentException>
                    (fun () -> SlotRegistry.ofSlots [ Slot.info broken ] |> ignore)
                    "defect"
            }

            test "a registry keeps its slots in order" {
                Expect.equal
                    (registry.Slots |> List.map (fun s -> s.Id.Value))
                    [ "test.mode"; "test.depth"; "test.sandbox"; "test.ratio"; "test.width" ]
                    "order"

                Expect.isTrue (registry.Contains depth.Id) "contains"
                Expect.isNone (registry.TryFind(SlotId.create "test.other")) "unknown"
                Expect.equal SlotRegistry.empty.Count 0 "empty"
            }
        ]
