module Fx.Contracts.Tests.SchemaTests

open System
open Expecto
open Fx.Contracts
open Fx.Contracts.Tests.Domain

let private jsonSchema (codec: Codec<'T>) : string =
    Json.serializeIndented (Schema.toJsonSchema codec.Schema)

let private body (schema: Schema) : Schema =
    match schema.Kind with
    | SchemaKind.Named named -> named.Body
    | _ -> failtest $"not a named type: {schema}"

[<Tests>]
let tests =
    testList
        "Schema"
        [
            test "type expressions" {
                Expect.equal (Schema.render matchCodec.Schema) "Match" "named"
                Expect.equal (Schema.typeName (Codec.list matchCodec).Schema) "Match[]" "list"
                Expect.equal (Schema.typeName (Codec.option Codec.int).Schema) "number | null" "nullable"
                Expect.equal (Schema.typeName (Codec.stringMap Codec.int).Schema) "object" "map"

                Expect.equal
                    (Schema.render (body matchCodec.Schema))
                    "{ path: string; line: integer; text: string; note?: string; tags?: string[]; score: number; weight: number; sequence: integer; span: Span | null; priority: \"low\" | \"medium\" | \"high\"; counts: { [key: string]: integer } }"
                    "record"

                Expect.equal
                    (Schema.render (body turnResult.Schema))
                    "{ kind: \"done\"; summary: string; evidence?: Match[]; verify?: string } | { kind: \"needInput\"; decisions: string[] } | { kind: \"waiting\"; on: string[]; note: string } | { kind: \"moved\"; start: integer; end: integer } | { kind: \"cancelled\" }"
                    "union"

                Expect.equal
                    (Schema.render (Codec.tuple2 Codec.int (Codec.literal (Json.String "x"))).Schema)
                    "[integer, \"x\"]"
                    "tuple"

                Expect.equal (string turnResult.Schema) "TurnResult" "ToString"
            }

            test "definitions are collected once and sorted" {
                let names = Schema.definitions turnResult.Schema |> List.map (fun d -> d.Name)
                Expect.equal names [ "Match"; "Span"; "TurnResult" ] "names"
            }

            test "conflicting definitions are rejected" {
                let other = Codec.int |> Codec.named "Span"
                let both = Codec.tuple2 span other

                Expect.throwsT<ArgumentException> (fun () -> Schema.definitions both.Schema |> ignore) "definitions"
                Expect.throwsT<ArgumentException> (fun () -> Schema.toJsonSchema both.Schema |> ignore) "JSON Schema"
            }

            test "dangling references are rejected" {
                let dangling =
                    Schema.ofKind (
                        SchemaKind.Array
                            {
                                Items = Schema.ofKind (SchemaKind.Ref "Nowhere")
                                MinItems = None
                                MaxItems = None
                            }
                    )

                Expect.throwsT<ArgumentException> (fun () -> Schema.toJsonSchema dangling |> ignore) "dangling"
            }

            test "JSON Schema for a record with constraints and descriptions" {
                let codec =
                    Codec.record {
                        let! path =
                            Codec.field
                                "path"
                                fst
                                (Codec.string |> Codec.nonEmpty |> Codec.describe "Workspace-relative path.")

                        and! lines =
                            Codec.optionalField
                                "lines"
                                snd
                                (Codec.tuple2 (Codec.int |> Codec.atLeast 1) (Codec.int |> Codec.atLeast 1))

                        return path, lines
                    }
                    |> Codec.strict
                    |> Codec.describe "Reads a file."
                    |> Codec.named "ReadFile"

                Golden.expect "read-file.schema.json" (jsonSchema codec)
            }

            test "JSON Schema for a union with named types" {
                Golden.expect "turn-result.schema.json" (jsonSchema turnResult)
            }

            test "JSON Schema for a recursive type" { Golden.expect "tree.schema.json" (jsonSchema tree) }

            test "JSON Schema for scalars and collections" {
                let codec =
                    Codec.tuple3
                        (Codec.option (Codec.decimal |> Codec.between 0M 1.5M))
                        (Codec.stringMap (Codec.list Codec.bool |> Codec.maxItems 3))
                        (Codec.tuple3
                            (Codec.literal (Json.int 2))
                            (Codec.string
                             |> Codec.pattern "^[a-z]+$"
                             |> Codec.maxLength 8
                             |> Codec.format "hostname")
                            Codec.json)

                Golden.expect "collections.schema.json" (jsonSchema codec)
            }

            test "contract types" {
                Golden.expect "issue.schema.json" (jsonSchema Codecs.issue)
                Golden.expect "limits.schema.json" (jsonSchema Codecs.limits)
                Golden.expect "preview.schema.json" (jsonSchema Codecs.preview)
            }

            testList
                "Schema JSON (Codecs.schema)"
                [
                    let everyKind =
                        Codec.tuple3
                            (Codec.tuple3
                                (Codec.option (Codec.decimal |> Codec.between 0M 1.5M))
                                (Codec.stringMap (Codec.list Codec.bool |> Codec.minItems 1 |> Codec.maxItems 3))
                                (Codec.tuple3
                                    (Codec.literal Json.Null)
                                    (Codec.string
                                     |> Codec.pattern "^[a-z]+$"
                                     |> Codec.minLength 2
                                     |> Codec.maxLength 8
                                     |> Codec.format "hostname"
                                     |> Codec.describe "A host.")
                                    Codec.json))
                            (Codec.tuple3
                                (Codec.int |> Codec.atLeast 0 |> Codec.atMost 9)
                                Codec.unit
                                (Codec.float |> Codec.atLeast 0.5))
                            (Codec.tuple2 turnResult tree)

                    let roundTrip (name: string) (schema: Schema) =
                        test name {
                            let json = Codecs.schema.Encode schema
                            Expect.equal (Codecs.schema.Decode json) (Ok schema) "reads back"

                            Expect.equal
                                (Codec.decodeText Codecs.schema (Json.serialize json))
                                (Ok schema)
                                "reads back from text"
                        }

                    roundTrip "every kind of schema reads back" everyKind.Schema
                    roundTrip "a strict record" (Codec.strict Codecs.issue).Schema
                    roundTrip "a recursive type" tree.Schema
                    roundTrip "the schema of schemas" Codecs.schema.Schema
                    roundTrip "contract types" (Codec.tuple3 Codecs.issue Codecs.limits Codecs.preview).Schema

                    test "the wire form" {
                        Golden.expect
                            "turn-result.schema-json.json"
                            (Json.serializeIndented (Codecs.schema.Encode turnResult.Schema))
                    }

                    test "invalid schemas are issues with paths" {
                        let decoded =
                            Codec.decodeText
                                Codecs.schema
                                """{"kind":"array","items":{"kind":"named","name":"not a name","body":{"kind":"strin"}},"minItems":-1,"extra":true}"""

                        match decoded with
                        | Ok schema -> failtest $"expected issues, got {schema}"
                        | Error issues ->
                            Expect.equal
                                (issues |> List.map string)
                                [
                                    "$.items.name: expected a string matching /^[A-Za-z_][A-Za-z0-9_]*$/, got string \"not a name\""
                                    "$.items.body.kind: expected one of \"any\", \"null\", \"boolean\", \"integer\", \"number\", \"string\", \"const\", \"enum\", \"array\", \"tuple\", \"map\", \"object\", \"union\", \"anyOf\", \"nullable\", \"ref\", \"named\", got string \"strin\""
                                    "$.minItems: expected an integer of at least 0, got number -1"
                                    "$.extra: unknown field; known fields are \"kind\", \"items\", \"minItems\", \"maxItems\", \"description\""
                                ]
                                "issues"
                    }

                    test "JSON Schema of the schema of schemas" {
                        Golden.expect "schema.schema.json" (jsonSchema Codecs.schema)
                    }
                ]
        ]
