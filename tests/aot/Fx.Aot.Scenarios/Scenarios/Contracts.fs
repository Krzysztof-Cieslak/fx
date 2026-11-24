/// Fx.Contracts: JSON reading and writing, number conversions, codecs, JSON Schema and previews.
module Fx.Aot.Scenarios.Contracts

open System
open Fx.Contracts
open Fx.Aot.Scenarios.Show

type Priority =
    | Low
    | High

    override this.ToString() =
        match this with
        | Low -> "low"
        | High -> "high"

type Item =
    {
        Name: string
        Count: int
        Price: decimal
        Ratio: float
        Tags: string list
        Note: string option
        Priority: Priority
    }

    override this.ToString() = $"Item({this.Name})"

type Shape =
    | Circle of radius: float
    | Rect of width: float * height: float
    | Group of name: string * items: Item list * shapes: Shape list
    | Empty

    override this.ToString() =
        match this with
        | Circle r -> $"Circle({r})"
        | Rect(w, h) -> $"Rect({w}, {h})"
        | Group(name, _, shapes) -> $"Group({name}, {shapes.Length} shapes)"
        | Empty -> "Empty"

let private priority = Codec.enum [ Low, "low"; High, "high" ]

let private item: Codec<Item> =
    Codec.record {
        let! name = Codec.field "name" (fun i -> i.Name) (Codec.string |> Codec.nonEmpty)

        and! count =
            Codec.field "count" (fun i -> i.Count) (Codec.int |> Codec.between 0 1000)

        and! price = Codec.field "price" (fun i -> i.Price) Codec.decimal
        and! ratio = Codec.field "ratio" (fun i -> i.Ratio) Codec.float
        and! tags = Codec.defaultField "tags" (fun i -> i.Tags) [] (Codec.list Codec.string)
        and! note = Codec.optionalField "note" (fun i -> i.Note) Codec.string
        and! priority = Codec.field "priority" (fun i -> i.Priority) priority

        return
            {
                Name = name
                Count = count
                Price = price
                Ratio = ratio
                Tags = tags
                Note = note
                Priority = priority
            }
    }
    |> Codec.describe "An item with a price."
    |> Codec.named "Item"

let private shape: Codec<Shape> =
    Codec.recursive "Shape" (fun shape ->
        Codec.union "kind" (fun circle rect group empty value ->
            match value with
            | Circle r -> circle r
            | Rect(w, h) -> rect w h
            | Group(name, items, shapes) -> group name items shapes
            | Empty -> empty)
        |> Codec.case1 "circle" Circle (Field.required "radius" Codec.float)
        |> Codec.case2
            "rect"
            (fun w h -> Rect(w, h))
            (Field.required "width" Codec.float)
            (Field.required "height" Codec.float)
        |> Codec.case3
            "group"
            (fun name items shapes -> Group(name, items, shapes))
            (Field.required "name" Codec.string)
            (Field.withDefault "items" [] (Codec.list item))
            (Field.required "shapes" (Codec.list shape))
        |> Codec.case0 "empty" Empty
        |> Codec.buildUnion
        |> Codec.strict)

/// More fields than `MergeSources12`: the compiler chains the merges.
type Wide =
    {
        W1: string
        W2: int
        W3: float
        W4: string list
        W5: int64 option
        W6: decimal
        W7: bool
        W8: Priority
        W9: string
        W10: int
        W11: float
        W12: string list
        W13: int64 option
        W14: decimal
        W15: bool
        W16: Priority
    }

    override this.ToString() = "Wide"

let private wide: Codec<Wide> =
    Codec.record {
        let! w1 = Codec.field "w1" (fun w -> w.W1) Codec.string
        and! w2 = Codec.field "w2" (fun w -> w.W2) Codec.int
        and! w3 = Codec.field "w3" (fun w -> w.W3) Codec.float
        and! w4 = Codec.field "w4" (fun w -> w.W4) (Codec.list Codec.string)
        and! w5 = Codec.field "w5" (fun w -> w.W5) (Codec.option Codec.int64)
        and! w6 = Codec.field "w6" (fun w -> w.W6) Codec.decimal
        and! w7 = Codec.field "w7" (fun w -> w.W7) Codec.bool
        and! w8 = Codec.field "w8" (fun w -> w.W8) priority
        and! w9 = Codec.field "w9" (fun w -> w.W9) Codec.string
        and! w10 = Codec.field "w10" (fun w -> w.W10) Codec.int
        and! w11 = Codec.field "w11" (fun w -> w.W11) Codec.float
        and! w12 = Codec.field "w12" (fun w -> w.W12) (Codec.list Codec.string)
        and! w13 = Codec.field "w13" (fun w -> w.W13) (Codec.option Codec.int64)
        and! w14 = Codec.field "w14" (fun w -> w.W14) Codec.decimal
        and! w15 = Codec.field "w15" (fun w -> w.W15) Codec.bool
        and! w16 = Codec.field "w16" (fun w -> w.W16) priority

        return
            {
                W1 = w1
                W2 = w2
                W3 = w3
                W4 = w4
                W5 = w5
                W6 = w6
                W7 = w7
                W8 = w8
                W9 = w9
                W10 = w10
                W11 = w11
                W12 = w12
                W13 = w13
                W14 = w14
                W15 = w15
                W16 = w16
            }
    }

let private sampleWide =
    {
        W1 = "a"
        W2 = 1
        W3 = 1.5
        W4 = [ "x"; "y" ]
        W5 = Some 9007199254740993L
        W6 = 0.10M
        W7 = true
        W8 = Low
        W9 = "b"
        W10 = -2
        W11 = 2.5e-8
        W12 = []
        W13 = None
        W14 = -1M
        W15 = false
        W16 = High
    }

let private sampleItem =
    {
        Name = "Zażółć 😀"
        Count = 3
        Price = 19.90M
        Ratio = 0.1 + 0.2
        Tags = [ "a"; "b\n" ]
        Note = None
        Priority = High
    }

let private sampleShape =
    Group("root", [ sampleItem ], [ Circle 1.5; Rect(2.0, 1e21); Group("inner", [], [ Empty ]); Circle nan ])

let private result (validation: Validation<'T, Issue>) (show: 'T -> string) : string =
    match validation with
    | Ok value -> "ok " + show value
    | Error issues -> "issues " + escape (Issue.render issues)

let jsonText =
    Scenario.lines "contracts-json-text" (fun () ->
        let text =
            "\ufeff{\"b\": [1, 2.50, -0, 1E+400, 123456789012345678901234567890], \"a\": \"\\u00e9\\ud83d\\ude00\\\"\\\\\\/\\b\\f\\n\\r\\t\\u0000\", \"c\": {\"z\": null, \"y\": true, \"x\": false}, \"d\": \"\\udc00\"}"

        let invalid =
            [
                ""
                "[1,]"
                "{\"a\" 1}"
                "{\"a\":1,\"a\":2}"
                "01"
                "1e1234567890"
                "\"\\x\""
                "\"a\nb\""
                "[\r\n  tru\r\n]"
                String('[', 129) + String(']', 129)
                "{} x"
            ]

        [
            match Json.parse text with
            | Ok json ->
                yield kv "compact" (Json.serialize json)
                yield kv "indented" (Json.serializeIndented json)
                yield kv "canonical" (Json.serializeCanonical json)
                yield kv "describe" (Json.describe json)
                yield kv "round trip" (string (Json.parse (Json.serialize json) = Ok json))
            | Error e -> yield kv "error" (string e)
            for input in invalid do
                match Json.parse input with
                | Ok json -> yield kv "unexpected success" (string json)
                | Error e -> yield $"{escape input} -> {e.Offset} {escape (string e)}"
            let deep = String('[', 100_000) + String(']', 100_000)

            match Json.parseWith { MaxDepth = Int32.MaxValue } deep with
            | Ok _ -> yield "deep input = ok"
            | Error e -> yield kv "deep input" (string e)
        ])

let numbers =
    Scenario.lines "contracts-json-numbers" (fun () ->
        let floats =
            [
                0.1 + 0.2
                1e21
                1e20
                1e-7
                0.000001
                -0.0
                5e-324
                Double.MaxValue
                Double.Epsilon * 3.0
                123.456
                Math.PI
                -1.5e300
                100.0
                2.0 ** 53.0 + 2.0
            ]

        let texts =
            [
                "0"
                "-0.000e5"
                "1.50"
                "12345678901234567890.1234567890"
                "9223372036854775807"
                "-9223372036854775808"
                "9223372036854775808"
                "1e999999999"
                "0.000000000000000000000000001"
                "79228162514264337593543950335"
                "79228162514264337593543950336"
                "1e-400"
                "2147483648"
                "-12.5e-8"
            ]

        [
            for f in floats do
                let n = JsonNumber.ofFloat f
                let roundTrip = f.ToString("R", Globalization.CultureInfo.InvariantCulture)
                $"float {roundTrip} -> {n} -> {JsonNumber.toFloat n = f}"
            for text in texts do
                match JsonNumber.tryParse text with
                | Some n ->
                    let int64 = JsonNumber.tryToInt64 n |> Option.map string |> Option.defaultValue "-"
                    let int32 = JsonNumber.tryToInt n |> Option.map string |> Option.defaultValue "-"

                    let decimal =
                        JsonNumber.tryToDecimal n
                        |> Option.map (fun d -> d.ToString(Globalization.CultureInfo.InvariantCulture))
                        |> Option.defaultValue "-"

                    let float =
                        (JsonNumber.toFloat n).ToString("R", Globalization.CultureInfo.InvariantCulture)

                    $"{text}: normalized {JsonNumber.normalize n}, int64 {int64}, int {int32}, decimal {decimal}, float {float}"
                | None -> $"{text}: invalid"
            for value in [ 1.10M; -0.5M; Decimal.MaxValue; 0.0000000000000000000000000001M ] do
                $"decimal {Json.decimal value}"
        ])

let codecs =
    Scenario.lines "contracts-codecs" (fun () ->
        let encoded = Codec.encodeText shape sampleShape
        let decoded = Codec.decodeText shape encoded

        let invalid =
            [
                """{"kind": "group", "name": 1, "shapes": [{"kind": "circle"}, {"kind": "rect", "width": "2", "height": 1, "depth": 3}, {"kind": "hexagon"}], "items": [{"name": "", "count": 5000, "price": 1e30, "ratio": "NaNs", "priority": "urgent"}]}"""
                """{"kind": "circle", "radius": "Infinity"}"""
                """[1, 2]"""
                """{"name": "x"}"""
            ]

        [
            kv "encoded" encoded
            kv "decoded" (result decoded (Codec.encodeText shape))
            for input in invalid do
                kv "invalid" (result (Codec.decodeText shape input) (Codec.encodeText shape))
            kv "item" (Codec.encodeText item sampleItem)
            kv "issue" (Codec.encodeText Codecs.issue (Issue.at [ PathSegment.Key "a b"; PathSegment.Index 2 ] "bad"))
            kv "limits" (result (Codec.decodeText Codecs.limits """{"maxChars": -1}""") string)
        ])

let schemas =
    Scenario.lines "contracts-json-schema" (fun () ->
        [
            kv "shape" (Json.serialize (Schema.toJsonSchema shape.Schema))
            kv "item" (Json.serialize (Schema.toJsonSchema item.Schema))
            kv "type" (Schema.render (Codec.list (Codec.option (Codec.tuple2 item Codec.json))).Schema)
            kv "definitions" (String.Join(", ", Schema.definitions shape.Schema |> List.map string))
        ])

let previews =
    Scenario.lines "contracts-previews" (fun () ->
        let limits chars items depth strings =
            {
                MaxChars = chars
                MaxItems = items
                MaxDepth = depth
                MaxStringChars = strings
            }

        let all =
            [
                Limits.defaults
                limits 60 3 2 10
                limits 30 2 1 5
                limits 12 10 3 3
                limits 1 1 1 1
                limits 0 0 0 0
            ]

        let long = String.replicate 50 "abc😀"

        [
            for l in all do
                let p = Codec.preview shape l sampleShape
                $"{l} -> {p.Truncated} {escape p.Text}"

                let j =
                    Preview.ofJson l (Json.Array [ Json.String long; Json.Object [ "k", Json.Array [] ] ])

                $"  json -> {j.Truncated} {escape j.Text}"
                $"  list -> {escape (Codec.preview (Codec.list Codec.int) l [ 1..100 ]).Text}"
        ])

let wideRecords =
    Scenario.lines "contracts-wide-records" (fun () ->
        let encoded = Codec.encodeText wide sampleWide

        [
            kv "encoded" encoded
            kv "decoded" (result (Codec.decodeText wide encoded) (Codec.encodeText wide))
            kv "issues" (result (Codec.decodeText wide """{"w2": "x", "w16": "medium"}""") (Codec.encodeText wide))
            kv "preview" (Codec.preview wide Limits.defaults sampleWide).Text
            kv "schema" (Schema.render wide.Schema)
        ])

let scenarios = [ jsonText; numbers; codecs; schemas; previews; wideRecords ]
