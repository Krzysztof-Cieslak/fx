/// Strict mode's dialect (`Strict.tryRewrite`): schemas from codecs, closed objects, optional
/// properties made nullable (and read back as absent by the codecs), and the schemas it gives up on.
module Fx.Ext.ModelOpenAI.Tests.StrictTests

open Expecto
open Fx.Contracts
open Fx.Ext.ModelOpenAI
open Fx.Ext.ModelOpenAI.Tests.Support
open Fx.Models

let private rewrite (schema: string) : Result<Json, string> = Strict.tryRewrite (json schema)

let private rewritten (schema: string) : Json =
    match rewrite schema with
    | Ok json -> json
    | Error e -> failtest $"not rewritten: {e}"

type private Inner =
    { Depth: int option; Tags: string list }

type private Outer =
    {
        Name: string
        Inner: Inner
        Choice: string option
    }

let private inner: Codec<Inner> =
    Codec.record {
        let! depth = Codec.optionalField "depth" (fun (i: Inner) -> i.Depth) Codec.int
        and! tags = Codec.defaultField "tags" (fun i -> i.Tags) [] (Codec.list Codec.string)
        return { Depth = depth; Tags = tags }
    }

let private outer: Codec<Outer> =
    Codec.record {
        let! name = Codec.field "name" (fun (o: Outer) -> o.Name) Codec.string
        and! inner = Codec.field "inner" (fun o -> o.Inner) inner

        and! choice =
            Codec.optionalField "choice" (fun o -> o.Choice) (Codec.enum [ "a", "a"; "b", "b" ])

        return
            {
                Name = name
                Inner = inner
                Choice = choice
            }
    }

[<Tests>]
let tests =
    testList
        "strict mode"
        [
            test "a codec's schema: optional and defaulted fields become required and nullable" {
                let tool = Request.tool Cases.writeFile

                expectJson
                    tool
                    (json
                        """{"type": "function", "name": "write_file", "description": "Writes a file.",
                            "parameters": {"type": "object", "properties": {
                              "path": {"type": "string", "description": "Relative to the workspace."},
                              "text": {"type": "string"},
                              "create": {"anyOf": [{"type": "boolean"}, {"type": "null"}]}},
                              "required": ["path", "text", "create"], "additionalProperties": false},
                            "strict": true}""")
                    "the tool"

                // What the model sends for a left-out property reads as the default.
                Expect.equal
                    (Codec.decodeText Cases.writeFileCodec """{"path": "a", "text": "b", "create": null}""")
                    (Ok
                        {
                            Path = "a"
                            Text = "b"
                            Create = false
                        })
                    "null is the default"
            }

            test "nested records close too, and their nulls read as absent" {
                let schema = Schema.toJsonSchema outer.Schema
                let strict = Strict.schema schema
                Expect.isSome strict "rewritten"

                expectJson
                    strict.Value
                    (json
                        """{"type": "object", "properties": {
                              "name": {"type": "string"},
                              "inner": {"type": "object", "properties": {
                                "depth": {"anyOf": [{"type": "integer"}, {"type": "null"}]},
                                "tags": {"anyOf": [{"type": "array", "items": {"type": "string"}}, {"type": "null"}]}},
                                "required": ["depth", "tags"], "additionalProperties": false},
                              "choice": {"anyOf": [{"type": "string", "enum": ["a", "b"]}, {"type": "null"}]}},
                            "required": ["name", "inner", "choice"], "additionalProperties": false}""")
                    "the schema"

                Expect.equal
                    (Codec.decodeText outer """{"name": "n", "inner": {"depth": null, "tags": null}, "choice": null}""")
                    (Ok
                        {
                            Name = "n"
                            Inner = { Depth = None; Tags = [] }
                            Choice = None
                        })
                    "decoded"
            }

            test "nullable schemas stay as they are; definitions, anyOf options and items are rewritten" {
                let schema =
                    rewritten
                        """{"type": "object", "properties": {
                              "a": {"type": ["string", "null"]},
                              "b": {"anyOf": [{"type": "integer"}, {"type": "null"}]},
                              "c": {"$ref": "#/$defs/Node"},
                              "d": {"type": "array", "items": {"type": "object", "properties": {"x": {"type": "number", "minimum": 0}}}, "maxItems": 3},
                              "e": {"enum": ["x", null]}},
                            "$defs": {"Node": {"type": "object", "properties": {"next": {"$ref": "#/$defs/Node"}}}}}"""

                expectJson
                    schema
                    (json
                        """{"type": "object",
                            "$defs": {"Node": {"type": "object", "properties": {
                              "next": {"anyOf": [{"$ref": "#/$defs/Node"}, {"type": "null"}]}},
                              "required": ["next"], "additionalProperties": false}},
                            "properties": {
                              "a": {"type": ["string", "null"]},
                              "b": {"anyOf": [{"type": "integer"}, {"type": "null"}]},
                              "c": {"anyOf": [{"$ref": "#/$defs/Node"}, {"type": "null"}]},
                              "d": {"anyOf": [{"type": "array", "maxItems": 3, "items": {"type": "object", "properties": {
                                "x": {"anyOf": [{"type": "number", "minimum": 0}, {"type": "null"}]}},
                                "required": ["x"], "additionalProperties": false}}, {"type": "null"}]},
                              "e": {"enum": ["x", null]}},
                            "required": ["a", "b", "c", "d", "e"], "additionalProperties": false}""")
                    "the schema"
            }

            test "what strict mode cannot take is not rewritten" {
                for schema in
                    [
                        """{"type": "object", "properties": {"a": {"type": "string", "minLength": 1}}}"""
                        """{"type": "object", "properties": {"a": {"type": "string", "format": "uri"}}}"""
                        """{"type": "object", "properties": {}, "additionalProperties": true}"""
                        """{"type": "object", "properties": {}, "additionalProperties": {"type": "string"}}"""
                        """{"type": "object", "properties": {"a": {"type": "object"}}}"""
                        """{"type": "object", "properties": {"a": {}}}"""
                        """{"type": "object", "properties": {"a": {"oneOf": [{"type": "string"}]}}}"""
                        """{"type": "object", "properties": {"a": {"type": "array", "prefixItems": [{"type": "string"}], "items": false}}}"""
                        """{"type": "object", "properties": {"a": {"allOf": [{"type": "string"}]}}}"""
                        """{"anyOf": [{"type": "object", "properties": {}}]}"""
                        """{"type": "string"}"""
                        """{"type": "object"}"""
                        "true"
                    ] do
                    Expect.isError (rewrite schema) schema
            }

            test "tools: strict when asked and possible; as given and not strict otherwise" {
                let spec: ToolSpec =
                    {
                        Name = "t"
                        Description = "d"
                        Parameters =
                            json """{"type": "object", "properties": {"a": {"type": "string"}}, "required": ["a"]}"""
                        Strict = true
                    }

                let strict (tool: Json) = Json.tryField "strict" tool

                Expect.equal (strict (Request.tool spec)) (Some(Json.Bool true)) "strict"

                Expect.equal
                    (Json.tryField "parameters" (Request.tool spec))
                    (Some(
                        json
                            """{"type": "object", "properties": {"a": {"type": "string"}}, "required": ["a"], "additionalProperties": false}"""
                    ))
                    "closed"

                let loose = { spec with Strict = false }
                Expect.equal (strict (Request.tool loose)) (Some(Json.Bool false)) "not asked"
                Expect.equal (Json.tryField "parameters" (Request.tool loose)) (Some spec.Parameters) "as given"

                let impossible =
                    { spec with
                        Parameters =
                            json """{"type": "object", "properties": {"a": {"type": "string", "maxLength": 3}}}"""
                    }

                Expect.equal (strict (Request.tool impossible)) (Some(Json.Bool false)) "not possible"

                Expect.equal
                    (Json.tryField "parameters" (Request.tool impossible))
                    (Some impossible.Parameters)
                    "as given"
            }
        ]
