module Fx.Contracts.Tests.ValidationTests

open Expecto
open Fx.Contracts
open Fx.Contracts.Tests.Generators

let private check (n: int) : Validation<int, string> =
    if n >= 0 then Ok n else Error [ $"negative {n}" ]

[<Tests>]
let tests =
    testList
        "Validation"
        [
            test "map2 keeps the errors of both sides" {
                Expect.equal (Validation.map2 (+) (check 1) (check 2)) (Ok 3) "both ok"
                Expect.equal (Validation.map2 (+) (check -1) (check 2)) (Error [ "negative -1" ]) "left"
                Expect.equal (Validation.map2 (+) (check -1) (check -2)) (Error [ "negative -1"; "negative -2" ]) "both"
            }

            test "and! accumulates, let! stops" {
                let applicative =
                    validation {
                        let! a = check -1
                        and! b = check -2
                        and! c = check 3
                        return a + b + c
                    }

                Expect.equal applicative (Error [ "negative -1"; "negative -2" ]) "and!"

                let monadic =
                    validation {
                        let! a = check -1
                        let! b = check -2
                        return a + b
                    }

                Expect.equal monadic (Error [ "negative -1" ]) "let!"
            }

            property "traverse keeps every error in order" (fun (items: int list) ->
                let expected =
                    items |> List.filter (fun n -> n < 0) |> List.map (fun n -> $"negative {n}")

                match Validation.traverse check items with
                | Ok values ->
                    Expect.isEmpty expected "no errors"
                    Expect.equal values items "values"
                | Error errors -> Expect.equal errors expected "errors")

            test "helpers" {
                Expect.equal (Validation.ofOption "none" (Some 1)) (Ok 1) "ofOption some"
                Expect.equal (Validation.ofOption "none" None) (Error [ "none" ]) "ofOption none"
                Expect.equal (Validation.ofResult (Error "e")) (Error [ "e" ]: Validation<int, string>) "ofResult"

                Expect.equal
                    (Validation.mapErrors String.length (Error [ "ab"; "c" ]: Validation<int, string>))
                    (Error [ 2; 1 ])
                    "mapErrors"

                Expect.equal
                    (Validation.sequence [ check 1; check -2; check -3 ])
                    (Error [ "negative -2"; "negative -3" ])
                    "sequence"

                Expect.equal (Validation.apply (Ok((+) 1)) (check 2)) (Ok 3) "apply"
            }

            test "issues render with JSON paths" {
                let cases =
                    [
                        [], "$"
                        [ PathSegment.Key "items"; PathSegment.Index 2; PathSegment.Key "name" ], "$.items[2].name"
                        [ PathSegment.Key "a key" ], "$[\"a key\"]"
                        [ PathSegment.Key "" ], "$[\"\"]"
                        [ PathSegment.Key "1st" ], "$[\"1st\"]"
                        [ PathSegment.Key "$ok_1" ], "$.$ok_1"
                        [ PathSegment.Key "quote\"" ], "$[\"quote\\\"\"]"
                    ]

                for path, expected in cases do
                    Expect.equal (JsonPath.render path) expected expected

                let issue =
                    Issue.at [ PathSegment.Key "items"; PathSegment.Index 2 ] "expected string, got number 42"

                Expect.equal (string issue) "$.items[2]: expected string, got number 42" "issue"

                Expect.equal
                    (string (Issue.under (PathSegment.Key "outer") issue))
                    "$.outer.items[2]: expected string, got number 42"
                    "under"
            }

            test "issue lists render boundedly" {
                let issues = [ for i in 1..5 -> Issue.create $"problem {i}" ]
                Expect.equal (Issue.render issues.[..1]) "$: problem 1\n$: problem 2" "render"
                Expect.equal (Issue.renderAtMost 5 issues) (Issue.render issues) "all fit"

                Expect.equal (Issue.renderAtMost 2 issues) "$: problem 1\n$: problem 2\n… and 3 more issues" "bounded"

                Expect.equal
                    (Issue.renderAtMost 4 issues)
                    (Issue.render issues.[..3] + "\n… and 1 more issue")
                    "one more"
            }
        ]
