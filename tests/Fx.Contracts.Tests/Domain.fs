/// A small domain shaped like fx's contracts, with its codecs.
module Fx.Contracts.Tests.Domain

open Fx.Contracts

type Priority =
    | Low
    | Medium
    | High

type Span = { Start: int; End: int }

type Match =
    {
        Path: string
        Line: int
        Text: string
        Note: string option
        Tags: string list
        Score: float
        Weight: decimal
        Sequence: int64
        Span: Span option
        Priority: Priority
        Counts: Map<string, int>
    }

type TurnResult =
    | Done of summary: string * evidence: Match list * verify: string option
    | NeedInput of decisions: string list
    | Waiting of on: string list * note: string
    | Moved of Span
    | Cancelled

type Tree =
    | Leaf of int
    | Node of label: string * children: Tree list

let priority: Codec<Priority> =
    Codec.enum [ Low, "low"; Medium, "medium"; High, "high" ]

let span: Codec<Span> =
    Codec.record {
        let! start = Codec.field "start" (fun s -> s.Start) Codec.int
        and! finish = Codec.field "end" (fun s -> s.End) Codec.int
        return { Start = start; End = finish }
    }
    |> Codec.describe "A range of lines, end exclusive."
    |> Codec.named "Span"

let matchCodec: Codec<Match> =
    Codec.record {
        let! path = Codec.field "path" (fun m -> m.Path) Codec.string
        and! line = Codec.field "line" (fun m -> m.Line) Codec.int
        and! text = Codec.field "text" (fun m -> m.Text) Codec.string
        and! note = Codec.optionalField "note" (fun m -> m.Note) Codec.string
        and! tags = Codec.defaultField "tags" (fun m -> m.Tags) [] (Codec.list Codec.string)
        and! score = Codec.field "score" (fun m -> m.Score) Codec.float
        and! weight = Codec.field "weight" (fun m -> m.Weight) Codec.decimal
        and! sequence = Codec.field "sequence" (fun m -> m.Sequence) Codec.int64
        and! span = Codec.field "span" (fun m -> m.Span) (Codec.option span)
        and! priority = Codec.field "priority" (fun m -> m.Priority) priority
        and! counts = Codec.field "counts" (fun m -> m.Counts) (Codec.stringMap Codec.int)

        return
            {
                Path = path
                Line = line
                Text = text
                Note = note
                Tags = tags
                Score = score
                Weight = weight
                Sequence = sequence
                Span = span
                Priority = priority
                Counts = counts
            }
    }
    |> Codec.named "Match"

let turnResult: Codec<TurnResult> =
    Codec.union "kind" {
        let! done' =
            Codec.case3
                "done"
                (fun summary evidence verify -> Done(summary, evidence, verify))
                (Field.required "summary" Codec.string)
                (Field.withDefault "evidence" [] (Codec.list matchCodec))
                (Field.optional "verify" Codec.string)

        and! needInput =
            Codec.case1 "needInput" NeedInput (Field.required "decisions" (Codec.list Codec.string))

        and! waiting =
            Codec.case2
                "waiting"
                (fun on note -> Waiting(on, note))
                (Field.required "on" (Codec.list Codec.string))
                (Field.required "note" Codec.string)

        and! moved = Codec.caseOf "moved" Moved span
        and! cancelled = Codec.case0 "cancelled" Cancelled

        return
            fun value ->
                match value with
                | Done(summary, evidence, verify) -> done' summary evidence verify
                | NeedInput decisions -> needInput decisions
                | Waiting(on, note) -> waiting on note
                | Moved span -> moved span
                | Cancelled -> cancelled
    }
    |> Codec.describeCase "done" "The work is complete."
    |> Codec.named "TurnResult"

let tree: Codec<Tree> =
    Codec.recursive "Tree" (fun tree ->
        Codec.union "kind" {
            let! leaf = Codec.case1 "leaf" Leaf (Field.required "value" Codec.int)

            and! node =
                Codec.case2
                    "node"
                    (fun label children -> Node(label, children))
                    (Field.required "label" Codec.string)
                    (Field.required "children" (Codec.list tree))

            return
                fun value ->
                    match value with
                    | Leaf n -> leaf n
                    | Node(label, children) -> node label children
        })
