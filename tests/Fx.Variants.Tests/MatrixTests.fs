module Fx.Variants.Tests.MatrixTests

open Expecto
open Fx.Variants
open Fx.Variants.Tests.Support

[<Tests>]
let tests =
    testList
        "Matrices"
        [
            test "a matrix expands to one explicit layer per combination, the first axis slowest" {
                let axes =
                    [ Matrix.axis mode [ Mode.Fast; Mode.Careful ]; Matrix.axis depth [ 1; 5; 9 ] ]

                let layers = Matrix.expand "smoke" axes

                Expect.equal
                    (layers |> List.map (fun l -> l.Source.Origin))
                    [
                        "smoke: test.mode=\"fast\", test.depth=1"
                        "smoke: test.mode=\"fast\", test.depth=5"
                        "smoke: test.mode=\"fast\", test.depth=9"
                        "smoke: test.mode=\"careful\", test.depth=1"
                        "smoke: test.mode=\"careful\", test.depth=5"
                        "smoke: test.mode=\"careful\", test.depth=9"
                    ]
                    "combinations"

                Expect.allEqual (layers |> List.map (fun l -> l.Source.Layer)) Layer.Explicit "explicit"
            }

            test "each combination resolves to its own profile" {
                let baseLayer = layer Layer.Host "h" [ "test.width", num "120" ]

                let ids =
                    Matrix.expand "m" [ (Matrix.choices (Slot.info mode)).Value; Matrix.axis depth [ 2; 4 ] ]
                    |> List.map (fun combination ->
                        resolveOk [] [ baseLayer; combination ]
                        |> Profile.ofResolution "0.1.0"
                        |> Profile.id)

                Expect.equal ids.Length 6 "six combinations"
                Expect.equal (List.distinct ids).Length 6 "six profiles"
            }

            test "a combination a policy forbids does not resolve" {
                let policy = Policy.empty "org" |> Policy.forbid mode [ Mode.Careful ]

                let results =
                    Matrix.expand "m" [ (Matrix.choices (Slot.info mode)).Value ]
                    |> List.map (fun combination ->
                        Resolution.resolve registry [ policy ] [ combination ] |> Result.isOk)

                Expect.equal results [ true; true; false ] "careful is forbidden"
            }

            test "edge cases" {
                Expect.equal (Matrix.expand "m" [] |> List.map (fun l -> l.Values)) [ [] ] "no axes"
                Expect.isEmpty (Matrix.expand "m" [ Matrix.axis depth [] ]) "an empty axis"
                Expect.isNone (Matrix.choices (Slot.info depth)) "an open slot has no choices"
            }
        ]
