module Fx.Sdk.Tests.SlotTests

open Expecto
open Fx.Contracts
open Fx.Variants
open Fx.Sdk
open Fx.Sdk.Tests.Support

type private Mode =
    | Fast
    | Careful

    override this.ToString() =
        match this with
        | Fast -> "fast"
        | Careful -> "careful"

let private mode =
    Slot.enum "acme.tools.mode" "How the tools work." [ Fast, "fast"; Careful, "careful" ] Careful

let private load (json: Json) =
    (ContributionType.info VariantSlots.contributionType).Load "mode" (Some json) None

let private owners (contributed: Contributed<SlotInfo> list) =
    VariantSlots.slots contributed
    |> List.map (fun s -> string s.Id, string s.Owner)

[<Tests>]
let tests =
    testList
        "Variant slots"
        [
            test "a native extension contributes its typed slot; its JSON form is the declaration" {
                let c = Contribution.create VariantSlots.contributionType "mode" (Slot.info mode)

                Expect.equal
                    (c.Json |> Option.map Json.serialize)
                    (Some
                        """{"id":"acme.tools.mode","description":"How the tools work.","default":"careful","choices":["fast","careful"],"privileged":false}""")
                    "declaration"

                Expect.equal c.Kind ContributionKind.Data "data"
                Expect.isFalse c.Privileged "any trusted extension may define slots"
            }

            test "a declared slot is enumerated, and the host makes the contributor its owner" {
                match
                    load (
                        Json.Object
                            [
                                "id", Json.String "acme.tools.mode"
                                "description", Json.String "How the tools work."
                                "default", Json.String "careful"
                                "choices", Json.Array [ Json.String "fast"; Json.String "careful" ]
                            ]
                    )
                with
                | Error issues -> failtest (Issue.render issues)
                | Ok c ->
                    let info =
                        Contribution.tryValue VariantSlots.contributionType c
                        |> Option.defaultWith (fun () -> failtest "not a slot")

                    Expect.equal (info.Check(Json.String "fast")) (Ok(Json.String "fast")) "a choice"

                    Expect.equal
                        (info.Check(Json.String "slow") |> Result.mapError (List.map string))
                        (Error [ "$: expected one of \"fast\", \"careful\", got string \"slow\"" ])
                        "not a choice"

                    let core = Slot.info mode |> SlotInfo.withOwner SlotOwner.Core

                    Expect.equal
                        (owners [ contributed "acme.tools" "mode" info; contributed "acme.other" "core" core ])
                        [ "acme.tools.mode", "acme.tools"; "acme.tools.mode", "acme.other" ]
                        "owners come from the contributions, never from the value"
            }

            test "a declared slot lists its choices" {
                match
                    load (
                        Json.Object
                            [
                                "id", Json.String "acme.tools.mode"
                                "description", Json.String "How the tools work."
                                "default", Json.Number(JsonNumber.ofInt 1)
                            ]
                    )
                with
                | Ok _ -> failtest "an open slot cannot be declared"
                | Error issues ->
                    Expect.equal
                        (issues |> List.map string)
                        [ "$.choices: a declared slot lists the values it may take" ]
                        "issues"
            }
        ]
