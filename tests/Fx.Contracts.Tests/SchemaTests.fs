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
        ]
