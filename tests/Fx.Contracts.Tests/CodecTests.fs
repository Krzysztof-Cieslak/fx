module Fx.Contracts.Tests.CodecTests

open System
open Expecto
open FsCheck
open FsCheck.FSharp
open Fx.Contracts
open Fx.Contracts.Tests.Generators
open Fx.Contracts.Tests.Domain

let private parse (text: string) : Json =
    match Json.parse text with
    | Ok json -> json
    | Error e -> failtest $"parse failed: {e}"

/// Decoding the encoding gives a value that encodes the same (floats make `=` unusable: NaN).
let private roundTrips (codec: Codec<'T>) (value: 'T) : 'T =
    let json = codec.Encode value

    let decoded =
        match codec.Decode json with
        | Ok decoded -> decoded
        | Error issues -> failtest $"{Issue.render issues}\nin {json}"

    Expect.equal (codec.Encode decoded) json "re-encoded"

    match Codec.decodeText codec (Codec.encodeText codec value) with
    | Ok throughText -> Expect.equal (codec.Encode throughText) json "through text"
    | Error issues -> failtest (Issue.render issues)

    decoded

let private issues (codec: Codec<'T>) (json: string) : string list =
    match codec.Decode(parse json) with
    | Ok value -> failtest $"expected issues, decoded {value}"
    | Error issues -> issues |> List.map string

let private decodes (codec: Codec<'T>) (json: string) : 'T =
    match codec.Decode(parse json) with
    | Ok value -> value
    | Error issues -> failtest (Issue.render issues)

let private sample: Match =
    {
        Path = "src/Billing/Retry.fs"
        Line = 42
        Text = "Http.retry 3 send"
        Note = None
        Tags = [ "deprecated" ]
        Score = 0.5
        Weight = 1.10M
        Sequence = 9007199254740993L
        Span = Some { Start = 40; End = 45 }
        Priority = High
        Counts = Map [ "b", 2; "a", 1 ]
    }

type private Five =
    {
        A: int
        B: int
        C: int
        D: int
        E: int
    }

let private five: Codec<Five> =
    Codec.record {
        let! a = Codec.field "a" (fun f -> f.A) Codec.int
        and! b = Codec.field "b" (fun f -> f.B) Codec.int
        and! c = Codec.field "c" (fun f -> f.C) Codec.int
        and! d = Codec.field "d" (fun f -> f.D) Codec.int
        and! e = Codec.field "e" (fun f -> f.E) Codec.int

        return { A = a; B = b; C = c; D = d; E = e }
    }

type private Wide =
    {
        F1: string
        F2: int
        F3: int
        F4: string
        F5: int
        F6: int
        F7: string
        F8: int
        F9: int
        F10: string
        F11: int
        F12: int
        F13: string
        F14: int
        F15: int
    }

/// More fields than the largest MergeSources: the compiler chains them.
let private wide: Codec<Wide> =
    Codec.record {
        let! f1 = Codec.field "f1" (fun w -> w.F1) Codec.string
        and! f2 = Codec.field "f2" (fun w -> w.F2) Codec.int
        and! f3 = Codec.field "f3" (fun w -> w.F3) Codec.int
        and! f4 = Codec.field "f4" (fun w -> w.F4) Codec.string
        and! f5 = Codec.field "f5" (fun w -> w.F5) Codec.int
        and! f6 = Codec.field "f6" (fun w -> w.F6) Codec.int
        and! f7 = Codec.field "f7" (fun w -> w.F7) Codec.string
        and! f8 = Codec.field "f8" (fun w -> w.F8) Codec.int
        and! f9 = Codec.field "f9" (fun w -> w.F9) Codec.int
        and! f10 = Codec.field "f10" (fun w -> w.F10) Codec.string
        and! f11 = Codec.field "f11" (fun w -> w.F11) Codec.int
        and! f12 = Codec.field "f12" (fun w -> w.F12) Codec.int
        and! f13 = Codec.field "f13" (fun w -> w.F13) Codec.string
        and! f14 = Codec.field "f14" (fun w -> w.F14) Codec.int
        and! f15 = Codec.field "f15" (fun w -> w.F15) Codec.int

        return
            {
                F1 = f1
                F2 = f2
                F3 = f3
                F4 = f4
                F5 = f5
                F6 = f6
                F7 = f7
                F8 = f8
                F9 = f9
                F10 = f10
                F11 = f11
                F12 = f12
                F13 = f13
                F14 = f14
                F15 = f15
            }
    }

[<Tests>]
let tests =
    testList
        "Codec"
        [
            testList
                "round trips"
                [
                    property "records" (fun (m: Match) -> roundTrips matchCodec m |> ignore)
                    property "unions" (fun (r: TurnResult) -> roundTrips turnResult r |> ignore)
                    property "recursive types" (fun (t: Tree) -> Expect.equal (roundTrips tree t) t "tree")
                    property "enums" (fun (p: Priority) -> Expect.equal (roundTrips priority p) p "enum")

                    property "records with more fields than the largest MergeSources" (fun (w: Wide) ->
                        Expect.equal (roundTrips wide w) w "wide")

                    property "collections and options" (fun (value: (string * int option) list * Map<string, bool>) ->
                        let codec =
                            Codec.tuple2
                                (Codec.list (Codec.tuple2 Codec.string (Codec.option Codec.int)))
                                (Codec.stringMap Codec.bool)

                        Expect.equal (roundTrips codec value) value "value")

                    property
                        "arrays, key-value lists and triples"
                        (fun (value: int64 array * (string * decimal) list * bool) ->
                            let codec =
                                Codec.tuple3 (Codec.array Codec.int64) (Codec.keyValues Codec.decimal) Codec.bool

                            let pairs = value |> fun (_, pairs, _) -> List.distinctBy fst pairs
                            let value = value |> fun (a, _, c) -> a, pairs, c
                            Expect.equal (roundTrips codec value) value "value")

                    property "floats, including NaN and infinities" (fun (f: float) ->
                        let decoded = roundTrips Codec.float f
                        Expect.isTrue (decoded.Equals f) $"{f} became {decoded}")

                    property "any JSON" (fun (json: Json) -> Expect.equal (roundTrips Codec.json json) json "json")

                    property "contract types" (fun (issue: string * int list * Limits * bool) ->
                        let message, indexes, limits, truncated = issue

                        let path =
                            indexes
                            |> List.map (fun i ->
                                if i % 2 = 0 then
                                    PathSegment.Index(abs i)
                                else
                                    PathSegment.Key(string i))

                        let value = { Path = path; Message = message }
                        Expect.equal (roundTrips Codecs.issue value) value "issue"

                        let limits =
                            { limits with
                                MaxChars = abs limits.MaxChars
                                MaxItems = abs limits.MaxItems
                                MaxDepth = abs limits.MaxDepth
                                MaxStringChars = abs limits.MaxStringChars
                            }

                        Expect.equal (roundTrips Codecs.limits limits) limits "limits"

                        let preview =
                            {
                                Text = message
                                Truncated = truncated
                            }

                        Expect.equal (roundTrips Codecs.preview preview) preview "preview")
                ]

            testList
                "encoding"
                [
                    test "records write fields in order and leave out absent optional fields" {
                        Expect.equal
                            (Codec.encodeText matchCodec sample)
                            """{"path":"src/Billing/Retry.fs","line":42,"text":"Http.retry 3 send","tags":["deprecated"],"score":0.5,"weight":1.10,"sequence":9007199254740993,"span":{"start":40,"end":45},"priority":"high","counts":{"a":1,"b":2}}"""
                            "match"
                    }

                    test "unions write the tag first" {
                        Expect.equal
                            (Codec.encodeText turnResult (Done("Migrated.", [], Some "dotnet test")))
                            """{"kind":"done","summary":"Migrated.","evidence":[],"verify":"dotnet test"}"""
                            "done"

                        Expect.equal
                            (Codec.encodeText turnResult (Moved { Start = 1; End = 2 }))
                            """{"kind":"moved","start":1,"end":2}"""
                            "flattened payload"

                        Expect.equal (Codec.encodeText turnResult Cancelled) """{"kind":"cancelled"}""" "no fields"
                    }

                    test "floats that JSON cannot represent are strings" {
                        Expect.equal (Codec.encode Codec.float nan) (Json.String "NaN") "NaN"
                        Expect.equal (Codec.encode Codec.float infinity) (Json.String "Infinity") "infinity"
                        Expect.equal (Codec.encode Codec.float -infinity) (Json.String "-Infinity") "-infinity"
                    }

                    test "enum values must be listed" {
                        let partial = Codec.enum [ Low, "low" ]

                        Expect.throwsT<ArgumentException> (fun () -> Codec.encode partial High |> ignore) "unlisted"
                    }
                ]

            testList
                "decoding"
                [
                    test "every invalid field is reported with its path" {
                        let json =
                            """{"path": 42, "line": "x", "text": "t", "note": null, "score": "high", "weight": 1,
                                "sequence": 1.5, "span": {"start": 1}, "priority": "urgent", "counts": {"a": 1, "b": "2"}}"""

                        Expect.equal
                            (issues matchCodec json)
                            [
                                "$.path: expected string, got number 42"
                                "$.line: expected integer, got string \"x\""
                                "$.score: expected number, got string \"high\""
                                "$.sequence: expected integer, got number 1.5"
                                "$.span.end: missing required field"
                                "$.priority: expected one of \"low\", \"medium\", \"high\", got string \"urgent\""
                                "$.counts.b: expected integer, got string \"2\""
                            ]
                            "issues"
                    }

                    test "paths go through lists and union payloads" {
                        let json =
                            """{"kind": "done", "summary": 7, "evidence": [{}, {"path": "a", "line": 1, "text": "", "score": 1,
                                "weight": 1, "sequence": 1, "span": null, "priority": "low", "counts": {}, "tags": [1]}]}"""

                        Expect.equal
                            (issues turnResult json)
                            [
                                "$.summary: expected string, got number 7"
                                "$.evidence[0].path: missing required field"
                                "$.evidence[0].line: missing required field"
                                "$.evidence[0].text: missing required field"
                                "$.evidence[0].score: missing required field"
                                "$.evidence[0].weight: missing required field"
                                "$.evidence[0].sequence: missing required field"
                                "$.evidence[0].span: missing required field"
                                "$.evidence[0].priority: missing required field"
                                "$.evidence[0].counts: missing required field"
                                "$.evidence[1].tags[0]: expected string, got number 1"
                            ]
                            "issues"
                    }

                    test "union tags" {
                        let tags = "\"done\", \"needInput\", \"waiting\", \"moved\", \"cancelled\""

                        Expect.equal
                            (issues turnResult "{}")
                            [ $"$.kind: missing required field; expected one of {tags}" ]
                            "missing"

                        Expect.equal
                            (issues turnResult """{"kind": "don"}""")
                            [ $"$.kind: expected one of {tags}, got string \"don\"" ]
                            "unknown"

                        Expect.equal
                            (issues turnResult """{"kind": 1}""")
                            [ $"$.kind: expected one of {tags}, got number 1" ]
                            "not a string"

                        Expect.equal (issues turnResult "[]") [ "$: expected object, got empty array" ] "not an object"

                        Expect.equal
                            (issues turnResult """{"kind": "waiting"}""")
                            [ "$.on: missing required field"; "$.note: missing required field" ]
                            "case fields"

                        Expect.equal
                            (issues turnResult """{"kind": "moved", "start": 1}""")
                            [ "$.end: missing required field" ]
                            "flattened payload"
                    }

                    property "validation accumulates every error" (fun (invalid: bool * bool * bool * bool * bool) ->
                        let a, b, c, d, e = invalid
                        let fields = [ "a", a; "b", b; "c", c; "d", d; "e", e ]

                        let json =
                            fields
                            |> List.map (fun (name, bad) -> name, (if bad then Json.String "x" else Json.int 1))
                            |> Json.Object

                        let expected =
                            fields
                            |> List.filter snd
                            |> List.map (fun (name, _) -> $"$.{name}: expected integer, got string \"x\"")

                        match five.Decode json with
                        | Ok _ -> Expect.isEmpty expected "no errors expected"
                        | Error found -> Expect.equal (found |> List.map string) expected "all errors")

                    test "wide records keep field order and every issue" {
                        let names =
                            match wide.Schema.Kind with
                            | SchemaKind.Object o -> o.Fields |> List.map (fun f -> f.Name)
                            | _ -> []

                        Expect.equal names [ for i in 1..15 -> $"f{i}" ] "order"
                        let found = issues wide "{}"
                        Expect.equal found [ for i in 1..15 -> $"$.f{i}: missing required field" ] "issues"
                    }

                    test "integers accept integral numbers in range" {
                        Expect.equal (decodes Codec.int "1.0") 1 "1.0"
                        Expect.equal (decodes Codec.int "1e3") 1000 "1e3"
                        Expect.equal (decodes Codec.int "-2147483648") Int32.MinValue "min"
                        Expect.equal (issues Codec.int "1.5") [ "$: expected integer, got number 1.5" ] "fraction"

                        Expect.equal
                            (issues Codec.int "3000000000")
                            [ "$: expected integer from -2147483648 to 2147483647, got number 3000000000" ]
                            "range"

                        Expect.equal (decodes Codec.int64 "9007199254740993") 9007199254740993L "exact int64"

                        Expect.equal
                            (issues Codec.int64 "1e19")
                            [
                                "$: expected integer from -9223372036854775808 to 9223372036854775807, got number 1e19"
                            ]
                            "int64 range"
                    }

                    test "numbers" {
                        Expect.equal
                            (issues Codec.float "1e400")
                            [ "$: expected number within the float range, got number 1e400" ]
                            "float range"

                        Expect.equal
                            (issues Codec.float "\"NaNa\"")
                            [ "$: expected number, got string \"NaNa\"" ]
                            "float string"

                        Expect.equal (decodes Codec.decimal "1.10") 1.10M "decimal"

                        Expect.equal
                            (issues Codec.decimal "1e29")
                            [ "$: expected number within the decimal range, got number 1e29" ]
                            "decimal range"

                        Expect.equal ((decodes Codec.number "1.50").Text) "1.50" "exact number"
                        Expect.equal (issues Codec.bool "1") [ "$: expected boolean, got number 1" ] "bool"
                        Expect.equal (issues Codec.unit "0") [ "$: expected null, got number 0" ] "unit"
                    }

                    test "optional, defaulted and nullable values" {
                        let withNote =
                            decodes matchCodec (Codec.encodeText matchCodec { sample with Note = Some "n" })

                        Expect.equal withNote.Note (Some "n") "present"

                        let json = Codec.encode matchCodec sample

                        let without (name: string) =
                            match json with
                            | Json.Object members -> Json.Object(members |> List.filter (fun (k, _) -> k <> name))
                            | other -> other

                        let decoded (json: Json) =
                            match matchCodec.Decode json with
                            | Ok m -> m
                            | Error issues -> failtest (Issue.render issues)

                        Expect.equal (decoded (without "tags")).Tags [] "default when absent"
                        Expect.equal (decodes (Codec.option Codec.int) "null") None "nullable"
                        Expect.equal (decodes (Codec.option Codec.int) "3") (Some 3) "nullable value"

                        Expect.equal
                            (issues matchCodec (Json.serialize (without "span")))
                            [ "$.span: missing required field" ]
                            "nullable but required"
                    }

                    test "tuples and literals" {
                        let pair = Codec.tuple2 Codec.int Codec.string
                        Expect.equal (decodes pair "[1, \"a\"]") (1, "a") "pair"

                        Expect.equal
                            (issues pair "[1]")
                            [ "$: expected array of 2 items, got array of 1 item" ]
                            "length"

                        Expect.equal
                            (issues pair "[\"x\", 2]")
                            [
                                "$[0]: expected integer, got string \"x\""
                                "$[1]: expected string, got number 2"
                            ]
                            "items"

                        let version = Codec.literal (Json.String "2.0")
                        Expect.equal (decodes version "\"2.0\"") () "literal"
                        Expect.equal (issues version "\"1.0\"") [ "$: expected \"2.0\", got string \"1.0\"" ] "other"
                        Expect.equal (decodes (Codec.literal (Json.int 1)) "1.0") () "numbers compare by value"
                    }

                    test "invalid JSON text is one issue" {
                        Expect.equal
                            (Codec.decodeText Codec.int "[1," |> Result.mapError (List.map string))
                            (Error
                                [
                                    "$: invalid JSON: unexpected end of input; expected a value at line 1, column 4"
                                ])
                            "syntax"
                    }

                    test "maps keep keys and report per key" {
                        Expect.equal
                            (decodes (Codec.keyValues Codec.int) """{"z": 1, "a": 2}""")
                            [ "z", 1; "a", 2 ]
                            "order"

                        Expect.equal
                            (issues (Codec.stringMap Codec.int) """{"ok": 1, "a key": true}""")
                            [ "$[\"a key\"]: expected integer, got boolean true" ]
                            "issue"
                    }
                ]

            testList
                "refinements"
                [
                    test "strings" {
                        let name = Codec.string |> Codec.nonEmpty |> Codec.maxLength 5
                        Expect.equal (decodes name "\"abc\"") "abc" "valid"
                        Expect.equal (issues name "\"\"") [ "$: expected a non-empty string" ] "empty"
                        Expect.equal (issues name "\"abcdef\"") [ "$: expected at most 5 characters, got 6" ] "long"
                        Expect.equal (decodes name "\"😀😀😀😀😀\"") "😀😀😀😀😀" "code points"
                        let min = Codec.string |> Codec.minLength 3
                        Expect.equal (issues min "\"ab\"") [ "$: expected at least 3 characters, got 2" ] "short"
                        let id = Codec.string |> Codec.pattern "^D-[0-9]+$"
                        Expect.equal (decodes id "\"D-12\"") "D-12" "match"

                        Expect.equal
                            (issues id "\"D-x\"")
                            [ "$: expected a string matching /^D-[0-9]+$/, got string \"D-x\"" ]
                            "no match"

                        Expect.isTrue
                            ((Codec.string |> Codec.pattern "\\d").Decode(Json.String "a1b") |> Result.isOk)
                            "unanchored"

                        Expect.isTrue
                            ((Codec.string |> Codec.pattern "^\\w+$").Decode(Json.String "zażółć")
                             |> Result.isError)
                            "ECMAScript \\w is ASCII"
                    }

                    test "numbers" {
                        let percent = Codec.int |> Codec.between 0 100
                        Expect.equal (decodes percent "100") 100 "max"

                        Expect.equal
                            (issues percent "101")
                            [ "$: expected an integer from 0 to 100, got number 101" ]
                            "above"

                        let positive = Codec.float |> Codec.atLeast 0.0

                        Expect.equal
                            (issues positive "-0.5")
                            [ "$: expected a number of at least 0, got number -0.5" ]
                            "below"

                        let small = Codec.decimal |> Codec.atMost 1M

                        Expect.equal
                            (issues small "1.5")
                            [ "$: expected a number of at most 1, got number 1.5" ]
                            "above"
                    }

                    test "lists" {
                        let some = Codec.list Codec.int |> Codec.minItems 1 |> Codec.maxItems 2
                        Expect.equal (issues some "[]") [ "$: expected a non-empty array" ] "empty"
                        Expect.equal (issues some "[1,2,3]") [ "$: expected at most 2 items, got 3" ] "long"
                        let three = Codec.list Codec.int |> Codec.minItems 3
                        Expect.equal (issues three "[1]") [ "$: expected at least 3 items, got 1" ] "short"
                    }

                    test "item refinements report their own paths" {
                        let names = Codec.list (Codec.string |> Codec.nonEmpty)

                        Expect.equal
                            (issues names "[\"a\", \"\", 3]")
                            [ "$[1]: expected a non-empty string"; "$[2]: expected string, got number 3" ]
                            "issues"
                    }

                    test "custom checks and conversions" {
                        let even =
                            Codec.int
                            |> Codec.refine (fun n ->
                                if n % 2 = 0 then
                                    Ok()
                                else
                                    Error $"expected an even number, got {n}")

                        Expect.equal (issues even "3") [ "$: expected an even number, got 3" ] "refine"

                        let date =
                            Codec.string
                            |> Codec.format "date"
                            |> Codec.tryMap
                                (fun s ->
                                    match
                                        DateOnly.TryParseExact(
                                            s,
                                            "yyyy-MM-dd",
                                            Globalization.CultureInfo.InvariantCulture,
                                            Globalization.DateTimeStyles.None
                                        )
                                    with
                                    | true, d -> Ok d
                                    | _ -> Error $"expected a date like 2024-02-29, got {Json.quote s}")
                                (fun d -> d.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture))

                        Expect.equal (decodes date "\"2024-02-29\"") (DateOnly(2024, 2, 29)) "parsed"

                        Expect.equal
                            (issues date "\"tomorrow\"")
                            [ "$: expected a date like 2024-02-29, got \"tomorrow\"" ]
                            "invalid"

                        Expect.equal (Codec.encode date (DateOnly(2024, 1, 2))) (Json.String "2024-01-02") "encode"
                    }
                ]

            testList
                "strictness"
                [
                    test "strict records report unknown fields" {
                        let strict = Codec.strict span

                        Expect.equal
                            (issues strict """{"start": 1, "end": "x", "ned": 2, "extra": null}""")
                            [
                                "$.end: expected integer, got string \"x\""
                                "$.ned: unknown field; known fields are \"start\", \"end\""
                                "$.extra: unknown field; known fields are \"start\", \"end\""
                            ]
                            "issues"

                        Expect.equal
                            (decodes span """{"start": 1, "end": 2, "extra": null}""")
                            { Start = 1; End = 2 }
                            "lenient"
                    }

                    test "strict unions check the fields of the decoded case" {
                        let strict = Codec.strict turnResult

                        Expect.equal
                            (issues strict """{"kind": "waiting", "on": [], "note": "", "summary": ""}""")
                            [ "$.summary: unknown field; known fields are \"kind\", \"on\", \"note\"" ]
                            "case fields"

                        Expect.equal (decodes strict """{"kind": "cancelled"}""") Cancelled "tag only"
                    }

                    test "strict applies to records and unions only" {
                        Expect.throwsT<ArgumentException> (fun () -> Codec.strict Codec.int |> ignore) "int"
                    }
                ]

            testList
                "construction"
                [
                    test "defects are exceptions" {
                        let throws name (f: unit -> 'T) =
                            Expect.throwsT<ArgumentException> (fun () -> f () |> ignore) name

                        throws "duplicate field" (fun () ->
                            Codec.record {
                                let! a = Codec.field "a" fst Codec.int
                                and! b = Codec.field "a" snd Codec.int
                                return a, b
                            })

                        throws "duplicate enum value" (fun () -> Codec.enum [ Low, "x"; High, "x" ])
                        throws "invalid type name" (fun () -> Codec.named "Not a name" Codec.int)
                        throws "invalid recursive name" (fun () -> Codec.recursive "a-b" id)

                        throws "duplicate tag" (fun () ->
                            Codec.union "kind" {
                                let! a = Codec.case0 "x" true
                                and! b = Codec.case0 "x" false
                                return fun value -> if value then a else b
                            })

                        throws "field named like the tag" (fun () ->
                            Codec.union "kind" {
                                let! a = Codec.case1 "x" id (Field.required "kind" Codec.int)
                                return fun (value: int) -> a value
                            })

                        throws "duplicate field in a case" (fun () ->
                            Codec.union "kind" {
                                let! a =
                                    Codec.case2
                                        "x"
                                        (fun (a: int) (b: int) -> a + b)
                                        (Field.required "a" Codec.int)
                                        (Field.required "a" Codec.int)

                                return fun value -> a value 0
                            })

                        throws "case payload that is not an object" (fun () ->
                            Codec.caseOf "x" (fun (value: int) -> value) Codec.int)

                        throws "describing a missing case" (fun () -> Codec.describeCase "nope" "" turnResult)
                    }

                    test "a recursive codec cannot be used while it is built" {
                        Expect.throwsT<InvalidOperationException>
                            (fun () ->
                                Codec.recursive "Loop" (fun (self: Codec<int>) ->
                                    self.Encode 1 |> ignore
                                    self)
                                |> ignore)
                            "used too early"
                    }

                    test "codecs print their type" {
                        Expect.equal (string span) "Codec<Span>" "named"

                        Expect.equal (string (Codec.list (Codec.option Codec.int))) "Codec<(integer | null)[]>" "list"
                    }
                ]
        ]
