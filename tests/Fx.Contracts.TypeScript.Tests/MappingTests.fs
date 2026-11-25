/// How each schema construct becomes TypeScript, on small inputs.
module Fx.Contracts.TypeScript.Tests.MappingTests

open System
open Expecto
open Fx.Contracts
open Fx.Contracts.TypeScript

let private ofKind (kind: SchemaKind) = Schema.ofKind kind

let private named (name: string) (body: Schema) =
    ofKind (SchemaKind.Named { Name = name; Body = body })

let private field (name: string) (presence: Presence) (schema: Schema) : FieldSchema =
    {
        Name = name
        Schema = schema
        Presence = presence
    }

let private record (fields: FieldSchema list) =
    ofKind (
        SchemaKind.Object
            {
                Fields = fields
                UnknownFields = UnknownFields.Ignore
            }
    )

/// The declarations of a module that declares `types`, without the header.
let private declare (types: Schema list) : string =
    let text =
        DeclarationFile.render
            {
                Layout = Layout.Module
                Types = types
                Members = []
            }

    let prefix = DeclarationFile.Header + "\n\n"
    Expect.stringStarts text prefix "header"
    text.Substring(prefix.Length).TrimEnd '\n'

/// `export type T = <expression>;` for a schema.
let private aliasOf (schema: Schema) : string =
    let text = declare [ named "T" schema ]
    let declaration = text.Split '\n' |> Array.last
    Expect.stringStarts declaration "export type T = " text
    declaration.Substring("export type T = ".Length).TrimEnd ';'

let private lines (text: string list) = String.Join("\n", text)

type private Node = { Children: Node list }

[<Tests>]
let tests =
    testList
        "Mapping"
        [
            test "scalars" {
                Expect.equal (aliasOf Codec.json.Schema) "unknown" "any JSON value"
                Expect.equal (aliasOf Codec.unit.Schema) "null" "null"
                Expect.equal (aliasOf Codec.bool.Schema) "boolean" "boolean"
                Expect.equal (aliasOf Codec.string.Schema) "string" "string"
                Expect.equal (aliasOf Codec.float.Schema) "number" "float"
                Expect.equal (aliasOf Codec.decimal.Schema) "number" "decimal"
                Expect.equal (aliasOf Codec.number.Schema) "number" "exact number"
            }

            test "integers are numbers documented as integers" {
                Expect.equal
                    (declare [ named "T" Codec.int64.Schema ])
                    "/** Integer. */\nexport type T = number;"
                    "int64"

                Expect.equal
                    (declare [ named "T" (Codec.int |> Codec.between 0 100).Schema ])
                    "/** Integer, from 0 to 100. */\nexport type T = number;"
                    "bounded"
            }

            test "literals" {
                let literal json = aliasOf (Codec.literal json).Schema
                Expect.equal (literal (Json.String "2.0")) "\"2.0\"" "string"
                Expect.equal (literal (Json.String "a\"b\n")) "\"a\\\"b\\n\"" "escaped string"
                Expect.equal (literal (Json.Number(JsonNumber.ofDecimal 1.50m))) "1.5" "normalized number"
                Expect.equal (literal (Json.int -3)) "-3" "negative"
                Expect.equal (literal (Json.Bool false)) "false" "boolean"
                Expect.equal (literal Json.Null) "null" "null"
                Expect.equal (literal (Json.Array [ Json.int 1; Json.Array [] ])) "[1, []]" "array"

                Expect.equal
                    (literal (Json.Object [ "a-b", Json.int 1; "c", Json.Bool true ]))
                    "{ \"a-b\": 1; c: true }"
                    "object"

                Expect.equal (literal (Json.Object [])) "{ [key: string]: never }" "empty object"
            }

            test "string enums" {
                Expect.equal (aliasOf (Codec.enum [ 1, "a"; 2, "b" ]).Schema) "\"a\" | \"b\"" "union of literals"
                Expect.equal (aliasOf (Codec.enum [ 1, "only" ]).Schema) "\"only\"" "one value"
                Expect.equal (aliasOf (ofKind (SchemaKind.Enum []))) "never" "no values"
            }

            test "arrays, tuples and maps" {
                Expect.equal (aliasOf (Codec.list Codec.string).Schema) "string[]" "list"

                Expect.equal
                    (aliasOf (Codec.array (Codec.option Codec.int)).Schema)
                    "(number | null)[]"
                    "parenthesized union"

                Expect.equal
                    (aliasOf (Codec.tuple3 Codec.int Codec.string Codec.bool).Schema)
                    "[number, string, boolean]"
                    "tuple"

                Expect.equal
                    (aliasOf (Codec.stringMap (Codec.list Codec.int)).Schema)
                    "{ [key: string]: number[] }"
                    "map"

                Expect.equal
                    (aliasOf (Codec.keyValues Codec.json).Schema)
                    "{ [key: string]: unknown }"
                    "key-value pairs"
            }

            test "nullable values and unions are flattened" {
                let twice = ofKind (SchemaKind.Nullable((Codec.option Codec.string).Schema))
                Expect.equal (aliasOf twice) "string | null" "null once"

                let anyOf =
                    ofKind (
                        SchemaKind.AnyOf
                            [
                                (Codec.option Codec.int).Schema
                                ofKind (SchemaKind.AnyOf [ Codec.string.Schema; Codec.unit.Schema ])
                            ]
                    )

                Expect.equal (aliasOf anyOf) "number | null | string" "nested anyOf"
                Expect.equal (aliasOf (ofKind (SchemaKind.AnyOf []))) "never" "empty anyOf"
            }

            test "optional, nullable and defaulted fields" {
                let shape =
                    record
                        [
                            field "required" Presence.Required Codec.string.Schema
                            field "optional" Presence.Optional Codec.string.Schema
                            field "nullable" Presence.Required (Codec.option Codec.string).Schema
                            field "defaulted" (Presence.Defaulted(Json.String "x")) Codec.string.Schema
                        ]

                Expect.equal
                    (declare [ named "T" shape ])
                    (lines
                        [
                            "export interface T {"
                            "  required: string;"
                            "  optional?: string;"
                            "  nullable: string | null;"
                            "  /** @default \"x\" */"
                            "  defaulted?: string;"
                            "}"
                        ])
                    "interface"
            }

            test "records are interfaces; inline records fit on a line when they can" {
                let point =
                    record
                        [
                            field "x" Presence.Required Codec.float.Schema
                            field "y" Presence.Required Codec.float.Schema
                        ]

                Expect.equal (declare [ named "Empty" (record []) ]) "export interface Empty {}" "empty interface"

                Expect.equal
                    (aliasOf (
                        ofKind (
                            SchemaKind.Array
                                {
                                    Items = ofKind (SchemaKind.Nullable(named "P" point))
                                    MinItems = None
                                    MaxItems = None
                                }
                        )
                    ))
                    "(P | null)[]"
                    "named"

                Expect.equal
                    (aliasOf (
                        ofKind (
                            SchemaKind.Array
                                {
                                    Items = point
                                    MinItems = None
                                    MaxItems = None
                                }
                        )
                    ))
                    "{ x: number; y: number }[]"
                    "inline"

                let wide =
                    record
                        [
                            for name in [ "alpha"; "bravo"; "charlie"; "delta"; "echo"; "foxtrot"; "golf" ] ->
                                field name Presence.Required Codec.string.Schema
                        ]

                Expect.equal
                    (declare [ named "T" (ofKind (SchemaKind.Map wide)) ])
                    (lines
                        [
                            "export type T = {"
                            "  [key: string]: {"
                            "    alpha: string;"
                            "    bravo: string;"
                            "    charlie: string;"
                            "    delta: string;"
                            "    echo: string;"
                            "    foxtrot: string;"
                            "    golf: string;"
                            "  };"
                            "};"
                        ])
                    "wider than a line"
            }

            test "tagged unions: one case per line, the case description on the tag" {
                let union =
                    Codec.union "type" {
                        let! a = Codec.case1 "a" Choice1Of2 (Field.required "x" Codec.string)
                        and! b = Codec.case0 "b" (Choice2Of2())

                        return
                            fun value ->
                                match value with
                                | Choice1Of2 x -> a x
                                | Choice2Of2() -> b
                    }
                    |> Codec.describeCase "b" "The other one."

                Expect.equal
                    (declare [ named "U" union.Schema ])
                    (lines
                        [
                            "export type U ="
                            "  | { type: \"a\"; x: string }"
                            "  | {"
                            "      /** The other one. */"
                            "      type: \"b\";"
                            "    };"
                        ])
                    "union"

                Expect.equal
                    (aliasOf (ofKind (SchemaKind.Union { Discriminator = "kind"; Cases = [] })))
                    "never"
                    "no cases"
            }

            test "multi-line unions inside arrays, maps and fields" {
                let union =
                    Codec.union "kind" {
                        let! a = Codec.case1 "a" Choice1Of2 (Field.required "x" Codec.string)
                        and! b = Codec.case1 "b" Choice2Of2 (Field.required "y" Codec.bool)

                        return
                            fun value ->
                                match value with
                                | Choice1Of2 x -> a x
                                | Choice2Of2 x -> b x
                    }

                let shape =
                    record
                        [
                            field "one" Presence.Required (Codec.option union).Schema
                            field "many" Presence.Required (Codec.list union).Schema
                        ]

                Expect.equal
                    (declare [ named "T" shape ])
                    (lines
                        [
                            "export interface T {"
                            "  one:"
                            "    | { kind: \"a\"; x: string }"
                            "    | { kind: \"b\"; y: boolean }"
                            "    | null;"
                            "  many: ("
                            "    | { kind: \"a\"; x: string }"
                            "    | { kind: \"b\"; y: boolean }"
                            "  )[];"
                            "}"
                        ])
                    "layout"
            }

            test "long unions get a line per member" {
                let values = [ for i in 1..12 -> i, $"value{i}" ]

                let expected =
                    [
                        yield "export type E ="
                        for _, v in values do
                            yield $"  | \"{v}\""
                    ]

                Expect.equal (declare [ named "E" (Codec.enum values).Schema ]) (lines expected + ";") "split"
            }

            test "recursive types refer to themselves by name" {
                let tree =
                    Codec.recursive "Node" (fun node ->
                        Codec.record {
                            let! children = Codec.field "children" (fun n -> n.Children) (Codec.list node)
                            return { Children = children }
                        })

                Expect.equal
                    (declare [ tree.Schema ])
                    (lines [ "export interface Node {"; "  children: Node[];"; "}" ])
                    "interface"
            }

            test "property names are quoted unless they are identifiers" {
                let shape =
                    record
                        [
                            for name in
                                [
                                    "plain"
                                    "_x$"
                                    "default"
                                    "class"
                                    "a-b"
                                    "a.b"
                                    "2fa"
                                    ""
                                    "é"
                                    "__proto__"
                                ] -> field name Presence.Required Codec.bool.Schema
                        ]

                Expect.equal
                    (declare [ named "T" shape ])
                    (lines
                        [
                            "export interface T {"
                            "  plain: boolean;"
                            "  _x$: boolean;"
                            "  default: boolean;"
                            "  class: boolean;"
                            "  \"a-b\": boolean;"
                            "  \"a.b\": boolean;"
                            "  \"2fa\": boolean;"
                            "  \"\": boolean;"
                            "  \"é\": boolean;"
                            "  __proto__: boolean;"
                            "}"
                        ])
                    "properties"
            }

            test "type names that TypeScript cannot use are renamed, without collisions" {
                let text =
                    declare
                        [
                            named "Promise" Codec.string.Schema
                            named "Promise_" Codec.string.Schema
                            named "unknown" Codec.string.Schema
                            named "a-b" Codec.string.Schema
                            named "a_b" Codec.string.Schema
                            named "a b" Codec.string.Schema
                            named "1x" Codec.string.Schema
                            named "Refs" (Codec.tuple2 (Codec.list Codec.json) Codec.json).Schema
                        ]

                let declared =
                    text.Split '\n'
                    |> Array.filter (fun l -> l.StartsWith("export type ", StringComparison.Ordinal))
                    |> Array.map (fun l -> l.Split(' ')[2])
                    |> List.ofArray

                Expect.equal
                    declared
                    [ "_1x"; "Promise_2"; "Promise_"; "Refs"; "a_b2"; "a_b3"; "a_b"; "unknown_" ]
                    "names"
            }

            test "descriptions and constraints become JSDoc" {
                let shape =
                    record
                        [
                            field
                                "a"
                                Presence.Required
                                (Codec.string
                                 |> Codec.nonEmpty
                                 |> Codec.describe "First line.\r\nSecond */ line.")
                                    .Schema
                            field
                                "b"
                                Presence.Required
                                (Codec.string |> Codec.pattern "^a/b$" |> Codec.format "uri").Schema
                            field
                                "c"
                                Presence.Required
                                (Codec.list (Codec.int |> Codec.atLeast 1) |> Codec.maxItems 1).Schema
                            field "d" Presence.Required (Codec.string |> Codec.maxLength 1).Schema
                            field "e" Presence.Required (Codec.string |> Codec.minLength 2 |> Codec.maxLength 9).Schema
                            field "f" Presence.Required (Codec.int64 |> Codec.atMost 0L).Schema
                        ]
                    |> Schema.describe "A record."

                Expect.equal
                    (declare [ named "T" shape ])
                    (lines
                        [
                            "/** A record. */"
                            "export interface T {"
                            "  /**"
                            "   * First line."
                            "   * Second *\\/ line."
                            "   * Non-empty."
                            "   */"
                            "  a: string;"
                            "  /** Matches /^a\\/b$/. Format: uri. */"
                            "  b: string;"
                            "  /** At most 1 item. Items: Integer, at least 1. */"
                            "  c: number[];"
                            "  /** At most 1 character. */"
                            "  d: string;"
                            "  /** From 2 to 9 characters. */"
                            "  e: string;"
                            "  /** Integer, at most 0. */"
                            "  f: number;"
                            "}"
                        ])
                    "docs"
            }

            test "a named type is documented by its body, or by the root that declares it" {
                let body = Codec.string |> Codec.describe "The body." |> Codec.named "A"
                let root = Codec.string |> Codec.named "B" |> Codec.describe "The root."

                let used =
                    record [ field "b" Presence.Required (root.Schema |> Schema.describe "This use.") ]
                    |> named "C"

                Expect.equal
                    (declare [ body.Schema; root.Schema ])
                    (lines
                        [
                            "/** The body. */"
                            "export type A = string;"
                            ""
                            "/** The root. */"
                            "export type B = string;"
                        ])
                    "docs"

                Expect.equal
                    (declare [ used ])
                    (lines
                        [
                            "export type B = string;"
                            ""
                            "export interface C {"
                            "  /** This use. */"
                            "  b: B;"
                            "}"
                        ])
                    "a use is not the type's documentation"
            }
        ]
