module Fx.Contracts.Tests.PreviewTests

open System
open Expecto
open FsCheck
open FsCheck.FSharp
open Fx.Contracts
open Fx.Contracts.Tests.Generators
open Fx.Contracts.Tests.Domain

let private generous =
    {
        MaxChars = 100_000
        MaxItems = 1000
        MaxDepth = 100
        MaxStringChars = 100_000
    }

let private limits chars items depth strings =
    {
        MaxChars = chars
        MaxItems = items
        MaxDepth = depth
        MaxStringChars = strings
    }

let private sample: Match =
    {
        Path = "src/Billing/Retry.fs"
        Line = 42
        Text = "Http.retry 3 send"
        Note = None
        Tags = [ "deprecated"; "http" ]
        Score = 0.5
        Weight = 1.10M
        Sequence = 7L
        Span = Some { Start = 40; End = 45 }
        Priority = High
        Counts = Map [ "a", 1 ]
    }

/// Checks the invariants of a preview made for `limits`, and returns its text.
let private bounded (limits: Limits) (preview: Preview) : string =
    Expect.isLessThanOrEqual preview.Text.Length (Math.Max(limits.MaxChars, 0)) $"MaxChars: {preview.Text}"
    Expect.isTrue (isWellFormed preview.Text) $"well-formed: {preview.Text}"
    preview.Text

let private checkCodec (codec: Codec<'T>) (limits: Limits) (value: 'T) =
    let preview = codec.Preview limits value
    bounded limits preview |> ignore
    let full = codec.Preview generous value

    if not preview.Truncated then
        Expect.equal preview.Text full.Text "an untruncated preview is complete"

[<Tests>]
let tests =
    testList
        "Preview"
        [
            testList
                "bounds"
                [
                    property "JSON previews never exceed their limits" (fun (json: Json, limits: Limits) ->
                        let preview = Preview.ofJson limits json
                        bounded limits preview |> ignore

                        if not preview.Truncated then
                            Expect.equal preview.Text (Preview.ofJson generous json).Text "complete")

                    property "record previews never exceed their limits" (fun (m: Match, limits: Limits) ->
                        checkCodec matchCodec limits m)

                    property "union previews never exceed their limits" (fun (r: TurnResult, limits: Limits) ->
                        checkCodec turnResult limits r)

                    property "recursive previews never exceed their limits" (fun (t: Tree, limits: Limits) ->
                        checkCodec tree limits t)

                    property "string previews never exceed their limits" (fun (s: string, limits: Limits) ->
                        let preview = Preview.string limits s
                        bounded limits preview |> ignore

                        if not preview.Truncated then
                            Expect.equal preview.Text (Json.quote s) "complete")

                    property "custom previews are clamped" (fun (s: string, limits: Limits) ->
                        let codec = Codec.string |> Codec.withPreview (fun _ s -> Preview.full (s + s))
                        let preview = codec.Preview limits s
                        Expect.isLessThanOrEqual preview.Text.Length (Math.Max(limits.MaxChars, 0)) "MaxChars")

                    property "unlimited JSON previews show every scalar as JSON" (fun (json: Json) ->
                        match json with
                        | Json.Array _
                        | Json.Object _ -> ()
                        | scalar -> Expect.equal (Preview.ofJson generous scalar).Text (Json.serialize scalar) "scalar")
                ]

            testList
                "rendering"
                [
                    test "records, named records and unions" {
                        Expect.equal
                            (Codec.preview matchCodec Limits.defaults sample).Text
                            "Match(path=\"src/Billing/Retry.fs\", line=42, text=\"Http.retry 3 send\", tags=[\"deprecated\", \"http\"], score=0.5, weight=1.10, sequence=7, span=Span(start=40, end=45), priority=\"high\", counts={\"a\": 1})"
                            "match"

                        Expect.equal
                            (Codec.preview turnResult Limits.defaults (Done("Migrated.", [], Some "dotnet test"))).Text
                            "done(summary=\"Migrated.\", evidence=[], verify=\"dotnet test\")"
                            "union case"

                        Expect.equal
                            (Codec.preview turnResult Limits.defaults (Moved { Start = 1; End = 2 })).Text
                            "moved(start=1, end=2)"
                            "flattened named payload"

                        Expect.equal (Codec.preview turnResult Limits.defaults Cancelled).Text "cancelled" "no fields"

                        Expect.equal
                            (Codec.preview tree Limits.defaults (Node("root", [ Leaf 1; Node("inner", [ Leaf 2 ]) ])))
                                .Text
                            "node(label=\"root\", children=[leaf(value=1), node(label=\"inner\", children=Tree[](len=1))])"
                            "recursive, collapsed at depth 3"
                    }

                    test "lists show the first items and count the rest" {
                        let numbers = Codec.list Codec.int
                        let items = [ 1..100 ]

                        Expect.equal
                            (Codec.preview numbers (limits 1000 3 3 100) items).Text
                            "[1, 2, 3, …+97]"
                            "MaxItems"

                        Expect.equal
                            (Codec.preview numbers (limits 20 100 3 100) items).Text
                            "[1, 2, 3, 4, …+96]"
                            "MaxChars"

                        Expect.equal
                            (Codec.preview numbers (limits 1000 0 3 100) items).Text
                            "number[](len=100)"
                            "no items"

                        Expect.equal
                            (Codec.preview numbers (limits 1000 10 0 100) items).Text
                            "number[](len=100)"
                            "depth 0"

                        Expect.equal (Codec.preview numbers (limits 1000 10 0 100) []).Text "[]" "empty"

                        let matches = Codec.list matchCodec

                        Expect.equal
                            (Codec.preview matches (limits 1000 10 0 100) (List.replicate 37 sample)).Text
                            "Match[](len=37)"
                            "named items"

                        Expect.equal
                            (Codec.preview matches (limits 40 10 3 100) (List.replicate 37 sample)).Text
                            "[Match(path=\"src/Bi…\"(len=20), …), …+36]"
                            "room for part of one item"

                        Expect.equal
                            (Codec.preview matches (limits 15 10 3 100) (List.replicate 37 sample)).Text
                            "Match[](len=37)"
                            "no room for one item"

                        Expect.equal
                            (Codec.preview matches (limits 12 10 3 100) (List.replicate 37 sample)).Text
                            "…"
                            "no room for the summary"
                    }

                    test "strings are cut and keep their length" {
                        let long = String('a', 500)

                        Expect.equal
                            (Preview.string (limits 1000 10 3 5) long).Text
                            "\"aaaaa…\"(len=500)"
                            "MaxStringChars"

                        Expect.equal (Preview.string (limits 12 10 3 100) long).Text "\"…\"(len=500)" "MaxChars"
                        Expect.equal (Preview.string (limits 14 10 3 100) long).Text "\"aa…\"(len=500)" "MaxChars"
                        Expect.equal (Preview.string (limits 4 10 3 100) long).Text "\"a…\"" "no room for the length"
                        Expect.equal (Preview.string (limits 2 10 3 100) long).Text "…" "no room for quotes"

                        Expect.equal
                            (Preview.string (limits 1000 10 3 100) "line\nbreak").Text
                            "\"line\\nbreak\""
                            "escaped"

                        Expect.equal
                            (Preview.string (limits 1000 10 3 3) "a😀b").Text
                            "\"a😀…\"(len=4)"
                            "surrogate pairs stay whole"

                        Expect.equal
                            (Preview.string (limits 1000 10 3 2) "a😀b").Text
                            "\"a…\"(len=4)"
                            "never split a pair"
                    }

                    test "records drop whole fields first" {
                        let point =
                            Codec.record {
                                let! x = Codec.field "x" fst Codec.int
                                and! y = Codec.field "y" snd Codec.int
                                return x, y
                            }

                        Expect.equal (Codec.preview point Limits.defaults (1, 2)).Text "(x=1, y=2)" "unnamed"
                        Expect.equal (Codec.preview point (limits 8 10 3 10) (1, 2)).Text "(x=1, …)" "cut"
                        Expect.equal (Codec.preview point (limits 1000 10 0 10) (1, 2)).Text "(…)" "depth"
                        let named = Codec.named "Point" point
                        Expect.equal (Codec.preview named (limits 1000 10 0 10) (1, 2)).Text "Point(…)" "named depth"
                        Expect.equal (Codec.preview named (limits 7 10 3 10) (1, 2)).Text "…" "no room for the fields"
                        Expect.equal (Codec.preview named (limits 8 10 3 10) (1, 2)).Text "Point(…)" "collapsed"
                    }

                    test "maps and JSON" {
                        let json =
                            Json.Object
                                [
                                    "name", Json.String "fx"
                                    "tags", Json.Array [ Json.String "a"; Json.Null; Json.Bool true ]
                                    "nested",
                                    Json.Object [ "deep", Json.Object [ "deeper", Json.Array [ Json.int 1 ] ] ]
                                    "a key", Json.int 3
                                ]

                        Expect.equal
                            (Preview.ofJson Limits.defaults json).Text
                            "{\"name\": \"fx\", \"tags\": [\"a\", null, true], \"nested\": {\"deep\": {\"deeper\": any[](len=1)}}, \"a key\": 3}"
                            "json"

                        Expect.equal
                            (Preview.ofJson (limits 1000 10 2 100) json).Text
                            "{\"name\": \"fx\", \"tags\": [\"a\", null, true], \"nested\": {\"deep\": {…}(len=1)}, \"a key\": 3}"
                            "MaxDepth"

                        Expect.equal
                            (Preview.ofJson (limits 1000 2 3 100) json).Text
                            "{\"name\": \"fx\", \"tags\": [\"a\", null, …+1], …+2}"
                            "MaxItems"

                        Expect.equal
                            (Preview.ofJson (limits 1000 2 3 100) (Json.Array [ Json.Array [] ])).Text
                            "[[]]"
                            "empty"

                        Expect.equal (Preview.ofJson (limits 1000 2 0 100) json).Text "{…}(len=4)" "collapsed"
                    }

                    test "custom previews" {
                        let matches =
                            Codec.list matchCodec
                            |> Codec.withPreview (fun limits ms ->
                                let files = ms |> List.map (fun m -> m.Path) |> List.distinct |> List.length
                                Preview.text limits $"Match[](len={ms.Length}, files={files}, …)")

                        Expect.equal
                            (Codec.preview matches Limits.defaults [ sample; { sample with Path = "b.fs" }; sample ])
                                .Text
                            "Match[](len=3, files=2, …)"
                            "summary"
                    }

                    test "tiny limits" {
                        Expect.equal (Preview.text (limits 0 10 3 10) "abc") { Text = ""; Truncated = true } "zero"
                        Expect.equal (Preview.text (limits -5 10 3 10) "abc") { Text = ""; Truncated = true } "negative"
                        Expect.equal (Preview.text (limits 1 10 3 10) "abc") { Text = "…"; Truncated = true } "one"
                        Expect.equal (Preview.text (limits 3 10 3 10) "abc") { Text = "abc"; Truncated = false } "fits"
                        Expect.equal (Preview.text (limits 2 10 3 10) "a😀") { Text = "a…"; Truncated = true } "pair"
                    }
                ]
        ]
