module Fx.Contracts.Tests.JsonTests

open System
open System.Text
open Expecto
open FsCheck
open FsCheck.FSharp
open Fx.Contracts
open Fx.Contracts.Tests.Generators

/// Lone surrogates, built from code units: F# string literals replace them with U+FFFD.
let private high = string (char 0xD800)
let private low = string (char 0xDC00)

let private parseOk (text: string) : Json =
    match Json.parse text with
    | Ok json -> json
    | Error e -> failtest $"parse failed: {e}"

let private parseError (text: string) : string =
    match Json.parse text with
    | Ok json -> failtest $"expected an error, got {json}"
    | Error e -> string e

let rec private shuffleMembers (seed: int) (json: Json) : Json =
    match json with
    | Json.Array items -> Json.Array(items |> List.map (shuffleMembers seed))
    | Json.Object members ->
        let random = Random(seed + members.Length)

        members
        |> List.map (fun (k, v) -> k, shuffleMembers seed v)
        |> List.sortBy (fun _ -> random.Next())
        |> Json.Object
    | other -> other

/// Nesting depth, computed without recursion.
let private depth (json: Json) : int =
    let mutable max = 0
    let stack = Collections.Generic.Stack<Json * int>()
    stack.Push(json, 0)

    while stack.Count > 0 do
        let value, level = stack.Pop()

        match value with
        | Json.Array items ->
            max <- Math.Max(max, level + 1)

            for item in items do
                stack.Push(item, level + 1)
        | Json.Object members ->
            max <- Math.Max(max, level + 1)

            for _, item in members do
                stack.Push(item, level + 1)
        | _ -> ()

    max

[<Tests>]
let tests =
    testList
        "Json"
        [
            testList
                "round trips"
                [
                    property "compact text reads back unchanged" (fun (json: Json) ->
                        Expect.equal (Json.parse (Json.serialize json)) (Ok json) "round trip")

                    property "indented text reads back unchanged" (fun (json: Json) ->
                        Expect.equal (Json.parse (Json.serializeIndented json)) (Ok json) "round trip")

                    property "ToString is the compact text" (fun (json: Json) ->
                        Expect.equal (string json) (Json.serialize json) "ToString")

                    property "output is well-formed UTF-16 and has no raw control characters" (fun (json: Json) ->
                        let text = Json.serialize json
                        Expect.isTrue (isWellFormed text) "well-formed"
                        Expect.isFalse (text |> Seq.exists (fun c -> c < ' ')) "no control characters")
                ]

            testList
                "canonical form"
                [
                    property "is idempotent" (fun (json: Json) ->
                        let once = Json.canonicalize json
                        Expect.equal (Json.canonicalize once) once "canonicalize"

                        Expect.equal
                            (Json.parse (Json.serializeCanonical json) |> Result.map Json.serializeCanonical)
                            (Ok(Json.serializeCanonical json))
                            "through text")

                    property "ignores member order" (fun (json: Json, seed: int) ->
                        Expect.equal
                            (Json.serializeCanonical (shuffleMembers seed json))
                            (Json.serializeCanonical json)
                            "same text")

                    property "keeps the value" (fun (json: Json) ->
                        let canonical = Json.canonicalize json
                        Expect.equal (depth canonical) (depth json) "depth"
                        Expect.equal (Json.kind canonical) (Json.kind json) "kind")

                    test "RFC 8785 example, with exact numbers" {
                        let input =
                            """{
                              "numbers": [333333333.33333329, 1E30, 4.50, 2e-3, 0.000000000000000000000000001],
                              "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
                              "literals": [null, true, false]
                            }"""

                        // JCS would write 333333333.3333333: it rounds numbers to doubles first.
                        Expect.equal
                            (Json.serializeCanonical (parseOk input))
                            """{"literals":[null,true,false],"numbers":[333333333.33333329,1e+30,4.5,0.002,1e-27],"string":"€$\u000f\nA'B\"\\\\\"/"}"""
                            "canonical"
                    }

                    test "sorts keys by UTF-16 code units" {
                        let json =
                            parseOk """{"b":1,"a":2,"\u00e9":3,"A":4,"\ud83d\ude00":5,"\uffff":6,"10":7,"1":8}"""

                        Expect.equal
                            (Json.serializeCanonical json)
                            "{\"1\":8,\"10\":7,\"A\":4,\"a\":2,\"b\":1,\"é\":3,\"😀\":5,\"\uffff\":6}"
                            "order"
                    }

                    test "rejects duplicate keys" {
                        let json = Json.Object [ "a", Json.Null; "a", Json.Bool true ]

                        Expect.throwsT<ArgumentException> (fun () -> Json.canonicalize json |> ignore) "duplicates"
                    }
                ]

            testList
                "writer"
                [
                    test "escapes minimally" {
                        let s =
                            "\"\\/\b\f\n\r\t\u0000\u001f\u007f é😀 " + high + " " + low + " " + low + high

                        Expect.equal
                            (Json.serialize (Json.String s))
                            "\"\\\"\\\\/\\b\\f\\n\\r\\t\\u0000\\u001f\u007f é😀 \\ud800 \\udc00 \\udc00\\ud800\""
                            "escapes"
                    }

                    test "indents like JSON.stringify(value, null, 2)" {
                        let json = parseOk """{"a":[1,{"b":null},[]],"c":{},"d":"x"}"""

                        let expected =
                            String.Join(
                                "\n",
                                [
                                    "{"
                                    "  \"a\": ["
                                    "    1,"
                                    "    {"
                                    "      \"b\": null"
                                    "    },"
                                    "    []"
                                    "  ],"
                                    "  \"c\": {},"
                                    "  \"d\": \"x\""
                                    "}"
                                ]
                            )

                        Expect.equal (Json.serializeIndented json) expected "indented"
                        Expect.equal (Json.serialize json) """{"a":[1,{"b":null},[]],"c":{},"d":"x"}""" "compact"
                    }

                    test "keeps number text and member order" {
                        let text = """{"z":1.50,"a":-0,"m":1E+400,"n":12345678901234567890123}"""
                        Expect.equal (Json.serialize (parseOk text)) text "unchanged"
                    }
                ]

            testList
                "reader"
                [
                    test "reads every JSON type" {
                        let json =
                            parseOk " \t\r\n{\"a\" : [ true , false , null , -1.5e3 , \"s\" ] , \"b\":{}} \n"

                        let expected =
                            Json.Object
                                [
                                    "a",
                                    Json.Array
                                        [
                                            Json.Bool true
                                            Json.Bool false
                                            Json.Null
                                            Json.Number(JsonNumber.tryParse "-1.5e3" |> Option.get)
                                            Json.String "s"
                                        ]
                                    "b", Json.Object []
                                ]

                        Expect.equal json expected "value"
                    }

                    test "decodes escapes" {
                        Expect.equal
                            (parseOk "\"\\\"\\\\\\/\\b\\f\\n\\r\\t\\u0041\\u00e9\\uD83D\\uDE00\"")
                            (Json.String "\"\\/\b\f\n\r\t\u0041é😀")
                            "escapes"
                    }

                    test "keeps lone surrogates" {
                        Expect.equal (parseOk "\"\\ud800x\\udc00\"") (Json.String(high + "x" + low)) "escaped"
                        Expect.equal (parseOk ("\"" + high + "\"")) (Json.String high) "raw"
                    }

                    test "ignores one leading byte order mark" {
                        Expect.equal (parseOk "\ufeff[1]") (Json.Array [ Json.int 1 ]) "bom"
                        Expect.isError (Json.parse "\ufeff\ufeff[1]") "only one"
                        Expect.isError (Json.parse "[1]\ufeff") "only leading"
                    }

                    test "reports errors with positions" {
                        let cases =
                            [
                                "", "unexpected end of input; expected a value at line 1, column 1"
                                "   ", "unexpected end of input; expected a value at line 1, column 4"
                                "[1,]", "unexpected character ']'; expected a value at line 1, column 4"
                                "[1 2]",
                                "unexpected character '2'; expected ',' or ']' after an array element at line 1, column 4"
                                "[1",
                                "unexpected end of input; expected ',' or ']' after an array element at line 1, column 3"
                                "{\"a\":1,}", "unexpected character '}'; expected a string key at line 1, column 8"
                                "{'a':1}", "unexpected character \"'\"; expected a string key at line 1, column 2"
                                "{a:1}", "unexpected character 'a'; expected a string key at line 1, column 2"
                                "{\"a\" 1}",
                                "unexpected character '1'; expected ':' after an object key at line 1, column 6"
                                "{\"a\":1 \"b\":2}",
                                "unexpected character '\"'; expected ',' or '}' after an object member at line 1, column 8"
                                "{\"a\":1,\"a\":2}", "duplicate key \"a\" at line 1, column 8"
                                "01", "leading zeros are not allowed in numbers at line 1, column 2"
                                "-", "expected a digit at line 1, column 2"
                                "-a", "expected a digit at line 1, column 2"
                                "1.", "expected a digit after the decimal point at line 1, column 3"
                                "1.e5", "expected a digit after the decimal point at line 1, column 3"
                                "1e", "expected a digit in the exponent at line 1, column 3"
                                "1e+", "expected a digit in the exponent at line 1, column 4"
                                "1e1234567890", "number exponent is too large at line 1, column 3"
                                ".5", "unexpected character '.'; expected a value at line 1, column 1"
                                "+1", "unexpected character '+'; expected a value at line 1, column 1"
                                "NaN", "unexpected character 'N'; expected a value at line 1, column 1"
                                "tru", "invalid literal; expected true at line 1, column 1"
                                "nul", "invalid literal; expected null at line 1, column 1"
                                "True", "unexpected character 'T'; expected a value at line 1, column 1"
                                "\"abc", "unterminated string at line 1, column 1"
                                "\"a\\qb\"", "invalid escape sequence \\q at line 1, column 3"
                                "\"\\u12G4\"", "invalid \\u escape; expected 4 hex digits at line 1, column 2"
                                "\"\\u12", "invalid \\u escape; expected 4 hex digits at line 1, column 2"
                                "\"a\nb\"", "control character U+000A must be escaped in a string at line 1, column 3"
                                "\"\u0000\"", "control character U+0000 must be escaped in a string at line 1, column 2"
                                "1 2", "unexpected character '2' after the JSON value at line 1, column 3"
                                "[]]", "unexpected character ']' after the JSON value at line 1, column 3"
                                "// comment", "unexpected character '/'; expected a value at line 1, column 1"
                                "\u00a0 1", "unexpected character U+00A0; expected a value at line 1, column 1"
                                "[\n  1,\n  x\n]", "unexpected character 'x'; expected a value at line 3, column 3"
                                "[\r\n1,\r\nx]", "unexpected character 'x'; expected a value at line 3, column 1"
                                "[\r1,\rx]", "unexpected character 'x'; expected a value at line 3, column 1"
                            ]

                        for input, expected in cases do
                            Expect.equal (parseError input) expected input
                    }

                    test "error offsets point into the text" {
                        match Json.parse "{\n  \"key\": tru }" with
                        | Error e ->
                            Expect.equal e.Offset 11 "offset"
                            Expect.equal (e.Line, e.Column) (2, 10) "line and column"
                        | Ok _ -> failtest "expected an error"
                    }

                    test "limits nesting depth" {
                        let nested n = String('[', n) + String(']', n)

                        Expect.isOk (Json.parse (nested 128)) "128 levels"

                        Expect.equal
                            (parseError (nested 129))
                            "maximum nesting depth 128 exceeded at line 1, column 129"
                            "129 levels"

                        let objects n =
                            String.replicate n "{\"a\":" + "1" + String('}', n)

                        Expect.isOk (Json.parseWith { MaxDepth = 3 } (objects 3)) "3 objects"
                        Expect.isError (Json.parseWith { MaxDepth = 3 } (objects 4)) "4 objects"
                        Expect.isError (Json.parseWith { MaxDepth = 0 } "[]") "depth 0"
                        Expect.isOk (Json.parseWith { MaxDepth = 0 } "\"scalar\"") "scalars have no depth"
                    }

                    test "deep input never overflows the stack" {
                        let n = 200_000
                        let text = String('[', n) + String(']', n)

                        match Json.parseWith { MaxDepth = Int32.MaxValue } text with
                        | Ok json -> Expect.equal (depth json) n "depth"
                        | Error e -> failtest (string e)

                        Expect.isError (Json.parse text) "default limit"
                    }

                    test "reads large input" {
                        let sb = StringBuilder "["

                        for i in 0..99_999 do
                            if i > 0 then
                                sb.Append ',' |> ignore

                            sb.Append($"{{\"id\":{i},\"name\":\"item {i}\",\"tags\":[\"a\",\"b\"],\"score\":{i}.5}}")
                            |> ignore

                        sb.Append ']' |> ignore
                        let text = sb.ToString()
                        let json = parseOk text

                        match json with
                        | Json.Array items -> Expect.equal items.Length 100_000 "items"
                        | _ -> failtest "not an array"

                        Expect.equal (Json.serialize json) text "round trip"
                    }

                    test "detects duplicates in large objects" {
                        let members = [ for i in 0..99 -> $"\"k{i}\":{i}" ]
                        let text = "{" + String.Join(",", members) + ",\"k57\":0}"
                        Expect.stringStarts (parseError text) "duplicate key \"k57\"" "duplicate"
                    }
                ]

            testList
                "helpers"
                [
                    test "describe" {
                        let cases =
                            [
                                Json.Null, "null"
                                Json.Bool true, "boolean true"
                                Json.int 42, "number 42"
                                Json.String "abc", "string \"abc\""
                                Json.String(String('x', 50)), "string \"" + String('x', 40) + "…\""
                                Json.Array [], "empty array"
                                Json.Array [ Json.Null ], "array of 1 item"
                                Json.Array [ Json.Null; Json.Null ], "array of 2 items"
                                Json.Object [], "empty object"
                                Json.Object [ "a", Json.Null ], "object"
                            ]

                        for json, expected in cases do
                            Expect.equal (Json.describe json) expected expected
                    }

                    test "constructors" {
                        Expect.equal (Json.float 0.5) (Json.Number(JsonNumber.ofFloat 0.5)) "float"
                        Expect.equal (string (Json.decimal 1.10M)) "1.10" "decimal"
                        Expect.equal (string (Json.int64 Int64.MinValue)) "-9223372036854775808" "int64"
                        Expect.equal (Json.tryNumber "1e5" |> Option.map string) (Some "1e5") "number"
                        Expect.isNone (Json.tryNumber "1e") "invalid number"
                    }

                    test "tryField finds the first member" {
                        let json = Json.Object [ "a", Json.int 1; "b", Json.int 2 ]
                        Expect.equal (Json.tryField "b" json) (Some(Json.int 2)) "found"
                        Expect.isNone (Json.tryField "c" json) "missing"
                        Expect.isNone (Json.tryField "a" (Json.Array [])) "not an object"
                    }
                ]
        ]
