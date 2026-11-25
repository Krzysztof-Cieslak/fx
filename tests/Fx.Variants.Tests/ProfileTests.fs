module Fx.Variants.Tests.ProfileTests

open System
open System.Security.Cryptography
open System.Text
open FsCheck
open FsCheck.FSharp
open Expecto
open Fx.Contracts
open Fx.Variants
open Fx.Variants.Tests.Support

let private hashA = "sha256:" + String('a', 64)
let private hashB = "sha256:" + String('b', 64)

/// A fixed profile: its canonical text and id are pinned by a golden file.
let private fixedProfile: Profile =
    resolveOk
        [ Policy.empty "org" |> Policy.pin width 100 ]
        [
            layer Layer.Host "h" [ "test.mode", str "thorough"; "test.ratio", num "0.250" ]
        ]
    |> Profile.ofResolution "0.1.0"
    |> Profile.withExtension "acme.tools" { Version = "1.2.0"; Hash = Some hashA }
    |> Profile.withExtension "acme.runtime" { Version = "0.1.0"; Hash = None }
    |> Profile.withConformance "acme.interpreter" "kernel" { Passed = 41; Failed = 0; Skipped = 2 }
    |> Profile.withLockfile hashB

let private roundTrip (profile: Profile) =
    match Codec.decode Profile.codec (Codec.encode Profile.codec profile) with
    | Ok back -> back
    | Error issues -> failtest (Issue.render issues)

[<Tests>]
let tests =
    testList
        "Profiles"
        [
            test "a profile's id is the SHA-256 of its canonical JSON" {
                let text = Profile.canonicalText fixedProfile

                let expected =
                    "sha256:"
                    + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes text))

                Expect.equal (string (Profile.id fixedProfile)) expected "id"
                Expect.equal (Profile.id fixedProfile).Short (expected.Substring(7, 12)) "short form"
                Golden.expect "profile.json" (text + "\n" + string (Profile.id fixedProfile))
            }

            test "every registered slot is in the profile, defaults included" {
                Expect.equal
                    (fixedProfile.Selections
                     |> Map.toList
                     |> List.map (fun (id, v) -> id.Value, Json.serialize v))
                    [
                        "test.depth", "3"
                        "test.mode", "\"thorough\""
                        "test.ratio", "0.25"
                        "test.sandbox", "true"
                        "test.width", "100"
                    ]
                    "selections"
            }

            property "profiles round-trip through their codec"
            <| fun (profile: Profile) -> Expect.equal (roundTrip profile) profile "round trip"

            property "profiles round-trip through their stored bytes"
            <| fun (profile: Profile) ->
                Expect.equal (Profile.ofBytes (Profile.id profile) (Profile.bytes profile)) (Ok profile) "bytes"

            testPropertyWithConfig config "the id does not depend on the order things were added in"
            <| Prop.forAll (Arb.fromGen (Gen.zip profile (Gen.choose (0, Int32.MaxValue)))) (fun (p, seed) ->
                let random = Random seed

                let shuffle (items: 'T list) =
                    items
                    |> List.map (fun item -> random.Next(), item)
                    |> List.sortBy fst
                    |> List.map snd

                let conformance =
                    p.Conformance
                    |> Map.toList
                    |> List.collect (fun (subject, suites) ->
                        suites |> Map.toList |> List.map (fun (suite, result) -> subject, suite, result))

                let rebuilt =
                    let empty =
                        { p with
                            Selections = p.Selections |> Map.toList |> shuffle |> Map.ofList
                            Extensions = Map.empty
                            Conformance = Map.empty
                        }

                    let withExtensions =
                        p.Extensions
                        |> Map.toList
                        |> shuffle
                        |> List.fold (fun acc (id, version) -> Profile.withExtension id version acc) empty

                    conformance
                    |> shuffle
                    |> List.fold
                        (fun acc (subject, suite, result) -> Profile.withConformance subject suite result acc)
                        withExtensions

                Expect.equal (Profile.id rebuilt) (Profile.id p) "id"

                let rec reorder (json: Json) =
                    match json with
                    | Json.Object members -> Json.Object(members |> List.map (fun (k, v) -> k, reorder v) |> shuffle)
                    | Json.Array items -> Json.Array(List.map reorder items)
                    | other -> other

                let reread =
                    Codec.decodeText Profile.codec (Json.serializeIndented (reorder (Codec.encode Profile.codec p)))

                Expect.equal (reread |> Result.map Profile.id) (Ok(Profile.id p)) "id of reordered JSON")

            test "equal values in other notations give the same id" {
                let a =
                    resolveOk [] [ layer Layer.Host "h" [ "test.ratio", num "0.250"; "test.depth", num "4" ] ]
                    |> Profile.ofResolution "0.1.0"

                let b =
                    resolveOk
                        []
                        [
                            layer Layer.Session "other" [ "test.depth", num "4.0" ]
                            layer Layer.Explicit "x" [ "test.ratio", num "2.5e-1" ]
                        ]
                    |> Profile.ofResolution "0.1.0"

                Expect.equal (Profile.id a) (Profile.id b) "sources do not matter, values do"
            }

            test "any change gives another id" {
                let id = Profile.id fixedProfile

                let changes =
                    [
                        { fixedProfile with Core = "0.1.1" }
                        { fixedProfile with
                            Selections = fixedProfile.Selections.Add(depth.Id, num "4")
                        }
                        Profile.withExtension "acme.tools" { Version = "1.2.1"; Hash = Some hashA } fixedProfile
                        Profile.withConformance
                            "acme.interpreter"
                            "kernel"
                            { Passed = 40; Failed = 1; Skipped = 2 }
                            fixedProfile
                        { fixedProfile with Lockfile = None }
                    ]

                for changed in changes do
                    Expect.notEqual (Profile.id changed) id (string changed)

                Expect.equal (changes |> List.map Profile.id |> List.distinct).Length changes.Length "distinct"
            }

            test "stored bytes must match the id" {
                let bytes = Profile.bytes fixedProfile
                let other = Profile.id { fixedProfile with Core = "9.9.9" }

                Expect.isError (Profile.ofBytes other bytes) "another profile's id"

                let tampered =
                    Encoding.UTF8.GetBytes((Profile.canonicalText fixedProfile).Replace("41", "42"))

                Expect.equal
                    (Profile.ofBytes (Profile.id fixedProfile) tampered)
                    (Error $"the content is profile {ProfileId.ofBytes tampered}, not {Profile.id fixedProfile}")
                    "tampered"
            }

            test "profiles of a newer format or with invalid ids do not decode" {
                let json =
                    """{"format":2,"core":"0.1.0","selections":{"Bad":1},"extensions":{},"conformance":{},"lockfile":"sha256:XYZ"}"""

                match Codec.decodeText Profile.codec json with
                | Ok profile -> failtest $"decoded {profile}"
                | Error issues ->
                    Expect.equal
                        (issues |> List.map string)
                        [
                            "$.format: expected an integer from 1 to 1, got number 2"
                            "$.selections: \"Bad\": a slot id has at least two dotted segments, e.g. \"agent.actionSurface\""
                            "$.lockfile: expected a string matching /^sha256:[0-9a-f]{64}$/, got string \"sha256:XYZ\""
                        ]
                        "issues"
            }

            test "profile ids have one spelling" {
                let id = Profile.id fixedProfile
                Expect.equal (ProfileId.tryParse (string id)) (Some id) "parses"
                Expect.equal (ProfileId.tryParse ((string id).ToUpperInvariant())) None "uppercase"
                Expect.equal (ProfileId.tryParse ("sha256:" + id.Hex.Substring 1)) None "short"
                Expect.equal (ProfileId.tryParse id.Hex) None "no prefix"
                Expect.equal (Codec.decode ProfileId.codec (Codec.encode ProfileId.codec id)) (Ok id) "codec"
            }

            test "a profile as a layer reproduces its values" {
                let again =
                    resolveOk [] [ Profile.layer fixedProfile ]
                    |> Profile.ofResolution fixedProfile.Core

                Expect.equal again.Selections fixedProfile.Selections "selections"

                Expect.equal
                    (Profile.layer fixedProfile).Source
                    (Source.create Layer.Explicit $"profile {Profile.id fixedProfile}")
                    "source"
            }

            test "diff lists what changed, in a fixed order" {
                let after =
                    { fixedProfile with
                        Core = "0.2.0"
                        Selections = fixedProfile.Selections.Add(mode.Id, str "careful").Remove(depth.Id)
                        Lockfile = None
                    }
                    |> Profile.withExtension "acme.tools" { Version = "1.3.0"; Hash = None }
                    |> Profile.withConformance "acme.interpreter" "kernel" { Passed = 43; Failed = 0; Skipped = 0 }

                Expect.equal
                    (Profile.diff fixedProfile after |> List.map string)
                    [
                        "core: 0.1.0 -> 0.2.0"
                        "test.depth: 3 -> (none)"
                        "test.mode: \"thorough\" -> \"careful\""
                        $"extension acme.tools: 1.2.0 ({hashA}) -> 1.3.0"
                        "conformance acme.interpreter kernel: 41 passed, 0 failed, 2 skipped -> 43 passed, 0 failed, 0 skipped"
                        $"lockfile: {hashB} -> (none)"
                    ]
                    "differences"

                Expect.isEmpty (Profile.diff fixedProfile fixedProfile) "none"
            }
        ]
