module Fx.Variants.Tests.ResolutionTests

open System
open FsCheck
open FsCheck.FSharp
open Expecto
open Fx.Contracts
open Fx.Variants
open Fx.Variants.Tests.Support

let private valueOf (slot: Slot<'T>) (resolution: Resolution) = Resolution.value slot resolution

let private sourceOf (slot: Slot<'T>) (resolution: Resolution) =
    (Resolution.tryFind slot.Id resolution).Value.Source

/// The selecting layers, in precedence order.
let private selecting = [ Layer.Host; Layer.Project; Layer.Session; Layer.Explicit ]

/// A stack of layers that may select `test.mode`: for each selecting layer, maybe a mode.
let private stacks: Gen<(Layer * Mode option) list> =
    selecting
    |> List.map (fun l ->
        Gen.optionOf (Gen.elements [ Mode.Fast; Mode.Thorough; Mode.Careful ])
        |> Gen.map (fun choice -> l, choice))
    |> Gen.sequenceToList

let private toLayers (stack: (Layer * Mode option) list) : Selections list =
    stack
    |> List.map (fun (l, choice) ->
        let values =
            match choice with
            | Some m -> [ Selections.select mode m ]
            | None -> [ "test.depth", num "4" ]

        layer l (string l) values)

[<Tests>]
let tests =
    testList
        "Resolution"
        [
            test "without selections every slot has its default" {
                let resolution = resolveOk [] []

                Expect.equal
                    (Resolution.explain resolution)
                    [
                        "test.mode = \"fast\" (default)"
                        "test.depth = 3 (default)"
                        "test.sandbox = true (default)"
                        "test.ratio = 0.5 (default)"
                        "test.width = 80 (default)"
                    ]
                    "defaults"

                Expect.equal (Resolution.defaults registry).Values resolution.Values "Resolution.defaults agrees"
            }

            test "higher layers override lower ones" {
                let resolution =
                    resolveOk
                        []
                        [
                            layer Layer.Explicit "--variant" [ "test.depth", num "9" ]
                            layer Layer.Host "~/host.json" [ "test.mode", str "thorough"; "test.depth", num "5" ]
                            layer Layer.Session "/mode" [ "test.mode", str "careful" ]
                            layer
                                Layer.Project
                                "project/config.json"
                                [ "test.mode", str "fast"; "test.width", num "120" ]
                        ]

                Expect.equal
                    (Resolution.explain resolution)
                    [
                        "test.mode = \"careful\" (session (/mode))"
                        "test.depth = 9 (explicit (--variant))"
                        "test.sandbox = true (default)"
                        "test.ratio = 0.5 (default)"
                        "test.width = 120 (project (project/config.json))"
                    ]
                    "resolution"
            }

            testPropertyWithConfig config "the highest selecting layer wins, whatever the order layers are given in"
            <| Prop.forAll (Arb.fromGen (Gen.zip stacks (Gen.shuffle selecting))) (fun (stack, order) ->
                let layers = toLayers stack

                let shuffled =
                    order
                    |> Seq.map (fun l -> layers |> List.find (fun s -> s.Source.Layer = l))
                    |> List.ofSeq

                let resolution = resolveOk [] layers

                let expected =
                    stack
                    |> List.choose (fun (l, choice) -> choice |> Option.map (fun m -> l, m))
                    |> List.tryLast

                match expected with
                | Some(l, m) ->
                    Expect.equal (valueOf mode resolution) m "value"
                    Expect.equal (sourceOf mode resolution).Layer l "source"
                | None ->
                    Expect.equal (valueOf mode resolution) mode.Default "default"
                    Expect.equal (sourceOf mode resolution) Source.defaultValue "source"

                Expect.equal (resolveOk [] shuffled).Values resolution.Values "order of layers")

            test "within one layer the later selections win" {
                let resolution =
                    resolveOk
                        []
                        [
                            layer
                                Layer.Explicit
                                "eval profile smoke"
                                [ "test.mode", str "thorough"; "test.depth", num "2" ]
                            layer Layer.Explicit "--variant" [ "test.mode", str "careful" ]
                        ]

                Expect.equal (valueOf mode resolution) Mode.Careful "the command line after the eval profile"
                Expect.equal (valueOf depth resolution) 2 "the eval profile's other selection"
                Expect.equal (sourceOf depth resolution).Origin "eval profile smoke" "its source"
            }

            test "values are canonical" {
                let resolution =
                    resolveOk [] [ layer Layer.Host "h" [ "test.ratio", num "0.750"; "test.depth", num "4.0" ] ]

                Expect.equal (Resolution.tryFind ratio.Id resolution).Value.Value (num "0.75") "ratio"
                Expect.equal (Resolution.tryFind depth.Id resolution).Value.Value (num "4") "depth"
            }

            test "unknown slots are reported with suggestions" {
                Expect.equal
                    (resolveIssues
                        []
                        [
                            layer
                                Layer.Host
                                "~/host.json"
                                [
                                    "test.mdoe", str "fast"
                                    "mode", str "fast"
                                    "Test.Depth", num "1"
                                    "other.thing", num "1"
                                ]
                        ])
                    [
                        "host (~/host.json): test.mdoe: unknown variant slot; did you mean \"test.mode\"?"
                        "host (~/host.json): mode: unknown variant slot; did you mean \"test.mode\"?"
                        "host (~/host.json): Test.Depth: unknown variant slot; did you mean \"test.depth\"?"
                        "host (~/host.json): other.thing: unknown variant slot"
                    ]
                    "issues"
            }

            test "every selection is checked, also overridden ones, and every issue is reported" {
                Expect.equal
                    (resolveIssues
                        []
                        [
                            layer Layer.Session "/depth" [ "test.depth", num "4"; "test.width", num "90" ]
                            layer Layer.Host "~/host.json" [ "test.depth", num "11"; "test.mode", str "slow" ]
                        ])
                    [
                        "host (~/host.json): test.depth: expected an integer from 1 to 10, got number 11"
                        "host (~/host.json): test.mode: expected one of \"fast\", \"thorough\", \"careful\", got string \"slow\""
                        "session (/depth): test.width: expected one of 80, 100, 120, got number 90"
                    ]
                    "issues in precedence order"
            }

            test "a layer selects a slot once" {
                Expect.equal
                    (resolveIssues [] [ layer Layer.Host "h" [ "test.depth", num "4"; "test.depth", num "5" ] ])
                    [ "host (h): test.depth: selected more than once" ]
                    "duplicate"
            }

            test "only host config, explicit selections and policy select privileged slots" {
                for l in [ Layer.Host; Layer.Explicit ] do
                    let resolution = resolveOk [] [ layer l "x" [ "test.sandbox", Json.Bool false ] ]
                    Expect.isFalse (valueOf sandbox resolution) (string l)

                Expect.equal
                    (resolveIssues
                        []
                        [
                            layer Layer.Project "project/config.json" [ "test.sandbox", Json.Bool false ]
                            layer Layer.Session "/sandbox" [ "test.sandbox", Json.Bool false ]
                        ])
                    [
                        "project (project/config.json): test.sandbox: privileged: only host config, explicit selections and policy may select it"
                        "session (/sandbox): test.sandbox: privileged: only host config, explicit selections and policy may select it"
                    ]
                    "project and session"

                let pinned = resolveOk [ Policy.empty "org" |> Policy.pin sandbox false ] []
                Expect.isFalse (valueOf sandbox pinned) "policy"
            }

            test "a pin replaces the default and conflicts with other selections" {
                let policy = Policy.empty "org.json" |> Policy.pin mode Mode.Careful

                let resolution =
                    resolveOk [ policy ] [ layer Layer.Host "h" [ "test.mode", str "careful" ] ]

                Expect.equal (valueOf mode resolution) Mode.Careful "pinned"
                Expect.equal (sourceOf mode resolution) (Source.create Layer.Policy "org.json") "source"

                Expect.equal (sourceOf mode (resolveOk [ policy ] [])).Layer Layer.Policy "over the default"

                Expect.equal
                    (resolveIssues [ policy ] [ layer Layer.Session "/mode" [ "test.mode", str "fast" ] ])
                    [
                        "session (/mode): test.mode: \"fast\" conflicts with policy org.json, which pins \"careful\""
                    ]
                    "conflict"
            }

            test "a forbidden value in effect is an issue, an overridden one is not" {
                let policy = Policy.empty "org.json" |> Policy.forbid mode [ Mode.Thorough ]

                let resolution =
                    resolveOk
                        [ policy ]
                        [
                            layer Layer.Host "h" [ "test.mode", str "thorough" ]
                            layer Layer.Session "/mode" [ "test.mode", str "careful" ]
                        ]

                Expect.equal (valueOf mode resolution) Mode.Careful "allowed"

                Expect.equal
                    (resolveIssues [ policy ] [ layer Layer.Host "h" [ "test.mode", str "thorough" ] ])
                    [ "host (h): test.mode: \"thorough\" is forbidden by policy org.json" ]
                    "forbidden selection"

                Expect.equal
                    (resolveIssues [ Policy.empty "org.json" |> Policy.forbid depth [ 3 ] ] [])
                    [
                        "test.depth: the default 3 is forbidden by policy org.json; select another value"
                    ]
                    "forbidden default"
            }

            test "policies that disagree, or name unknown slots or invalid values, are issues" {
                let first = Policy.empty "org.json" |> Policy.pin mode Mode.Careful
                let second = Policy.empty "team.json" |> Policy.pin mode Mode.Fast
                let third = Policy.empty "lab.json" |> Policy.forbid mode [ Mode.Careful ]

                let invalid =
                    {
                        Origin = "bad.json"
                        Pins = [ "test.depth", num "0"; "test.nope", num "1" ]
                        Forbids = [ "test.width", [ num "7" ] ]
                    }

                Expect.equal
                    (resolveIssues [ first; second; third; invalid ] [])
                    [
                        "test.mode: pinned to \"careful\" by policy org.json and to \"fast\" by policy team.json"
                        "policy (bad.json): test.depth: expected an integer from 1 to 10, got number 0"
                        "policy (bad.json): test.nope: unknown variant slot; did you mean \"test.mode\"?"
                        "policy (bad.json): test.width: expected one of 80, 100, 120, got number 7"
                        "policy (org.json): test.mode: \"careful\" is forbidden by policy lab.json"
                    ]
                    "issues"
            }

            test "issues are model-readable too" {
                match
                    Resolution.resolve
                        registry
                        []
                        [
                            layer Layer.Host "~/host.json" [ "test.ratio", Json.Object [ "x", num "1" ] ]
                        ]
                with
                | Ok _ -> failtest "resolved"
                | Error issues ->
                    Expect.equal
                        (issues |> List.collect VariantIssue.toIssues |> List.map string)
                        [ "$[\"test.ratio\"]: expected number, got object (from host (~/host.json))" ]
                        "issues"
            }

            test "selections come only from the selecting layers" {
                Expect.throwsT<ArgumentException>
                    (fun () -> Resolution.resolve registry [] [ layer Layer.Policy "p" [] ] |> ignore)
                    "policy"

                Expect.throwsT<ArgumentException>
                    (fun () -> Resolution.resolve registry [] [ layer Layer.Default "d" [] ] |> ignore)
                    "default"
            }

            test "a typed value of an unregistered slot is a defect" {
                let other = Slot.create "test.other" "Other." Codec.int 1

                Expect.throwsT<UnregisteredSlotException>
                    (fun () -> Resolution.value other (resolveOk [] []) |> ignore)
                    "unregistered"

                let mistyped = Slot.create "test.mode" "Mode as a number." Codec.int 1

                Expect.throwsT<SlotTypeMismatchException>
                    (fun () -> Resolution.value mistyped (resolveOk [] []) |> ignore)
                    "mismatch"
            }

            test "resolution is deterministic" {
                let layers =
                    [
                        layer Layer.Host "h" [ "test.mode", str "thorough"; "test.nope", num "1" ]
                        layer Layer.Session "s" [ "test.depth", num "12" ]
                    ]

                let run () =
                    match Resolution.resolve registry [ Policy.empty "p" |> Policy.pin width 120 ] layers with
                    | Ok resolution -> Resolution.explain resolution
                    | Error issues -> issues |> List.map string

                Expect.equal (run ()) (run ()) "same output"
            }
        ]

[<Tests>]
let configTests =
    testList
        "Config"
        [
            test "a config file's variants member is a layer" {
                let config =
                    """{"theme": "dark", "variants": {"test.mode": "thorough", "test.depth": 4}}"""

                match Json.parse config with
                | Error error -> failtest (string error)
                | Ok json ->
                    match Selections.ofConfig (source Layer.Host "~/host.json") json with
                    | Error issues -> failtest (Issue.render issues)
                    | Ok selections ->
                        Expect.equal selections.Values [ "test.mode", str "thorough"; "test.depth", num "4" ] "values"
                        Expect.equal selections.Source.Origin "~/host.json" "source"

                Expect.equal
                    (Selections.ofConfig (source Layer.Host "h") (Json.Object [])
                     |> Result.map (fun s -> s.Values))
                    (Ok [])
                    "no variants"
            }

            test "a config whose variants member is not an object is an issue" {
                match Selections.ofConfig (source Layer.Host "h") (Json.Object [ "variants", Json.Array [] ]) with
                | Ok selections -> failtest $"read {selections}"
                | Error issues ->
                    Expect.equal (issues |> List.map string) [ "$.variants: expected object, got empty array" ] "issues"
            }

            test "command-line selections are slot=value with JSON or plain text" {
                Expect.equal (Selections.parseArgument "edit.guard=warn") (Ok("edit.guard", str "warn")) "text"
                Expect.equal (Selections.parseArgument "test.depth=4") (Ok("test.depth", num "4")) "number"
                Expect.equal (Selections.parseArgument "a.b=\"true\"") (Ok("a.b", str "true")) "quoted"
                Expect.equal (Selections.parseArgument "a.b=true") (Ok("a.b", Json.Bool true)) "bool"
                Expect.equal (Selections.parseArgument "a.b=x=y") (Ok("a.b", str "x=y")) "first ="
                Expect.equal (Selections.parseArgument "a.b=") (Ok("a.b", str "")) "empty"
                Expect.isError (Selections.parseArgument "a.b") "no ="
                Expect.isError (Selections.parseArgument "=x") "no slot"
            }

            test "policies read from JSON" {
                let text =
                    """{"pin": {"test.mode": "careful"}, "forbid": {"test.width": [100, 120]}}"""

                match
                    Json.parse text
                    |> Result.mapError string
                    |> Result.bind (Policy.ofJson "org.json" >> Result.mapError Issue.render)
                with
                | Error error -> failtest error
                | Ok policy ->
                    Expect.equal policy.Origin "org.json" "origin"
                    Expect.equal policy.Pins [ "test.mode", str "careful" ] "pins"
                    Expect.equal policy.Forbids [ "test.width", [ num "100"; num "120" ] ] "forbids"

                    Expect.equal
                        (Codec.decode (Policy.codec "org.json") (Codec.encode (Policy.codec "org.json") policy))
                        (Ok policy)
                        "round trip"

                    Expect.equal
                        (resolveIssues [ policy ] [ layer Layer.Host "h" [ "test.width", num "100" ] ])
                        [ "host (h): test.width: 100 is forbidden by policy org.json" ]
                        "applied"
            }
        ]
