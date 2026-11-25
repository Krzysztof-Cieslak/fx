/// Fx.Contracts.TypeScript (T102, docs/modules/typescript.md): generating TypeScript declarations
/// from schemas, as the host does at run time for a kernel's `<global>.d.ts`.
module Fx.Aot.Scenarios.TypeScript

open System
open Fx
open Fx.Contracts
open Fx.Contracts.TypeScript
open Fx.Aot.Scenarios.Show

type Mode =
    | Fast
    | Careful

    override this.ToString() =
        match this with
        | Fast -> "fast"
        | Careful -> "careful"

type Hit =
    {
        Path: string
        Line: int
        Note: string option
        Tags: string list
        Weight: decimal
        Counts: Map<string, int64>
        Span: (int * int) option
        Mode: Mode
    }

    override this.ToString() = $"Hit({this.Path}:{this.Line})"

type Outcome =
    | Done of summary: string * hits: Hit list
    | Asked of questions: string list
    | Cancelled

    override this.ToString() =
        match this with
        | Done(summary, _) -> $"Done({summary})"
        | Asked questions -> $"Asked({questions.Length})"
        | Cancelled -> "Cancelled"

type Tree =
    | Leaf of int
    | Node of string * Tree list

    override this.ToString() =
        match this with
        | Leaf n -> $"Leaf({n})"
        | Node(label, children) -> $"Node({label}, {children.Length})"

let private mode =
    Codec.enum [ Fast, "fast"; Careful, "careful" ] |> Codec.named "Mode"

let private hit: Codec<Hit> =
    Codec.record {
        let! path =
            Codec.field "path" (fun h -> h.Path) (Codec.string |> Codec.nonEmpty |> Codec.describe "Relative.")

        and! line =
            Codec.field "line" (fun h -> h.Line) (Codec.int |> Codec.between 1 1000000)

        and! note =
            Codec.optionalField "note" (fun h -> h.Note) (Codec.string |> Codec.pattern "^[^*]*/x$")

        and! tags =
            Codec.defaultField "tags" (fun h -> h.Tags) [] (Codec.list Codec.string |> Codec.maxItems 4)

        and! weight = Codec.field "weight" (fun h -> h.Weight) Codec.decimal
        and! counts = Codec.field "counts" (fun h -> h.Counts) (Codec.stringMap Codec.int64)

        and! span =
            Codec.field "span" (fun h -> h.Span) (Codec.option (Codec.tuple2 Codec.int Codec.int))

        and! mode = Codec.field "mode" (fun h -> h.Mode) mode

        return
            {
                Path = path
                Line = line
                Note = note
                Tags = tags
                Weight = weight
                Counts = counts
                Span = span
                Mode = mode
            }
    }
    |> Codec.describe "A search hit; */ ends no comment."
    |> Codec.named "Hit"

let private outcome: Codec<Outcome> =
    Codec.union "kind" (fun finished asked cancelled value ->
        match value with
        | Done(summary, hits) -> finished summary hits
        | Asked questions -> asked questions
        | Cancelled -> cancelled)
    |> Codec.case2
        "done"
        (fun summary hits -> Done(summary, hits))
        (Field.required "summary" Codec.string)
        (Field.withDefault "hits" [] (Codec.list hit))
    |> Codec.case1 "asked" Asked (Field.required "questions" (Codec.list Codec.string |> Codec.minItems 1))
    |> Codec.case0 "cancelled" Cancelled
    |> Codec.buildUnion
    |> Codec.describeCase "done" "Finished."
    |> Codec.named "Outcome"

let private tree: Codec<Tree> =
    Codec.recursive "Tree" (fun tree ->
        Codec.union "kind" (fun leaf node value ->
            match value with
            | Leaf n -> leaf n
            | Node(label, children) -> node label children)
        |> Codec.case1 "leaf" Leaf (Field.required "value" Codec.int)
        |> Codec.case2
            "node"
            (fun label children -> Node(label, children))
            (Field.required "label" Codec.string)
            (Field.required "children" (Codec.list tree))
        |> Codec.buildUnion)

let private named (name: string) (body: Schema) =
    Schema.ofKind (SchemaKind.Named { Name = name; Body = body })

let private method (path: string) (returns: Schema) (parameters: Parameter list) : Method =
    {
        Name = path
        Description = Some $"Calls {path}."
        Parameters = parameters
        Returns = returns
    }

let private bridge: Member list =
    Member.ofPaths
        [ "repo", "The repository." ]
        [
            method
                "repo.search"
                (Codec.list hit).Schema
                [
                    Parameter.required "pattern" Codec.string.Schema
                    Parameter.optional "mode" mode.Schema
                ]
            method "repo.delete" Codec.unit.Schema [ Parameter.required "class" Codec.string.Schema ]
            method
                "tools.my-tool"
                Codec.json.Schema
                [
                    Parameter.optional "first" Codec.int.Schema
                    Parameter.required "second" (Codec.literal (Json.Object [ "a-b", Json.int 1 ])).Schema
                ]
            method
                "finish"
                (Codec.option (Codec.list Codecs.issue)).Schema
                [ Parameter.required "outcome" outcome.Schema ]
        ]

let private print (text: string) : string list =
    text.Split '\n' |> List.ofArray |> List.map escape

let private failure (render: unit -> string) : string =
    try
        render () |> ignore
        "rendered"
    with
    | :? ArgumentException as e -> e.GetType().Name
    | e -> "unexpected " + e.GetType().Name

let scenarios: Scenario list =
    [
        Scenario.lines "typescript-module" (fun () ->
            DeclarationFile.render
                {
                    Layout = Layout.Module
                    Types =
                        [
                            hit.Schema
                            outcome.Schema
                            tree.Schema
                            Codecs.limits.Schema
                            named "Promise" (Codec.stringMap (Codec.option Codec.string)).Schema
                            named "my-type" (Codec.tuple3 Codec.bool Codec.unit Codec.number).Schema
                            named "Promise_" (Codec.literal (Json.Array [ Json.Null; Json.String "x" ])).Schema
                        ]
                    Members = []
                }
            |> print)

        Scenario.lines "typescript-global" (fun () ->
            DeclarationFile.render
                {
                    Layout = Layout.Global(Product.kernelGlobal, Some "The host bridge.")
                    Types = [ outcome.Schema ]
                    Members = bridge
                }
            |> print)

        Scenario.lines "typescript-invalid-input" (fun () ->
            let file types members =
                {
                    Layout = Layout.Module
                    Types = types
                    Members = members
                }

            [
                kv "unnamed" (failure (fun () -> DeclarationFile.render (file [ Codec.string.Schema ] [])))
                kv
                    "dangling"
                    (failure (fun () ->
                        DeclarationFile.render (file [ named "D" (Schema.ofKind (SchemaKind.Ref "Nowhere")) ] [])))
                kv
                    "conflict"
                    (failure (fun () ->
                        DeclarationFile.render (file [ named "Mode" Codec.int.Schema; mode.Schema ] [])))
                kv
                    "reserved member"
                    (failure (fun () ->
                        DeclarationFile.render (file [] [ Member.Method(Method.create "delete" Codec.unit.Schema) ])))
                kv
                    "paths"
                    (failure (fun () ->
                        Member.ofPaths
                            []
                            [ Method.create "a" Codec.unit.Schema; Method.create "a.b" Codec.unit.Schema ]
                        |> List.length
                        |> string))
                kv "valid" (failure (fun () -> DeclarationFile.render (file [ mode.Schema ] bridge)))
            ])
    ]
