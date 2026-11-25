/// Schemas the golden files declare: a domain shaped like fx's contracts that uses every schema
/// construct, the real contracts that exist today, and a kernel host bridge like the one T117 builds.
module Fx.Contracts.TypeScript.Tests.Samples

open Fx
open Fx.Contracts
open Fx.Contracts.TypeScript

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
    |> Codec.describe "How urgent a match is."
    |> Codec.named "Priority"

let span: Codec<Span> =
    Codec.record {
        let! start = Codec.field "start" (fun s -> s.Start) (Codec.int |> Codec.atLeast 1)
        and! finish = Codec.field "end" (fun s -> s.End) (Codec.int |> Codec.atLeast 1)
        return { Start = start; End = finish }
    }
    |> Codec.describe "A range of lines, end exclusive."
    |> Codec.named "Span"

let matchCodec: Codec<Match> =
    Codec.record {
        let! path =
            Codec.field
                "path"
                (fun m -> m.Path)
                (Codec.string |> Codec.nonEmpty |> Codec.describe "Workspace-relative.")

        and! line = Codec.field "line" (fun m -> m.Line) (Codec.int |> Codec.atLeast 1)
        and! text = Codec.field "text" (fun m -> m.Text) Codec.string
        and! note = Codec.optionalField "note" (fun m -> m.Note) Codec.string
        and! tags = Codec.defaultField "tags" (fun m -> m.Tags) [] (Codec.list Codec.string)
        and! score = Codec.field "score" (fun m -> m.Score) Codec.float

        and! weight =
            Codec.field "weight" (fun m -> m.Weight) (Codec.decimal |> Codec.between 0m 1m)

        and! sequence = Codec.field "sequence" (fun m -> m.Sequence) Codec.int64
        and! span = Codec.field "span" (fun m -> m.Span) (Codec.option span)
        and! priority = Codec.field "priority" (fun m -> m.Priority) priority

        and! counts =
            Codec.field "counts" (fun m -> m.Counts) (Codec.stringMap (Codec.int |> Codec.atLeast 0))

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
    |> Codec.describe "A line that matched a search."
    |> Codec.named "Match"

let turnResult: Codec<TurnResult> =
    Codec.union "kind" {
        let! done' =
            Codec.case3
                "done"
                (fun summary evidence verify -> Done(summary, evidence, verify))
                (Field.required "summary" Codec.string)
                (Field.withDefault "evidence" [] (Codec.list matchCodec))
                (Field.optional "verify" (Codec.string |> Codec.describe "A command that checks the work."))

        and! needInput =
            Codec.case1 "needInput" NeedInput (Field.required "decisions" (Codec.list Codec.string |> Codec.minItems 1))

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
    |> Codec.describeCase "cancelled" "The user cancelled the turn."
    |> Codec.describe "How a turn ends."
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

let private ofKind (kind: SchemaKind) = Schema.ofKind kind

let private named (name: string) (body: Schema) =
    ofKind (SchemaKind.Named { Name = name; Body = body })

let private field (name: string) (presence: Presence) (schema: Schema) : FieldSchema =
    {
        Name = name
        Schema = schema
        Presence = presence
    }

let private str = Codec.string.Schema
let private integer = Codec.int.Schema

/// Every schema construct that the domain's codecs do not use, in one record built as a schema.
let shapes: Schema =
    let objectOf (fields: FieldSchema list) =
        ofKind (
            SchemaKind.Object
                {
                    Fields = fields
                    UnknownFields = UnknownFields.Ignore
                }
        )

    let nullableUnion =
        (Codec.option (
            Codec.union "type" {
                let! text = Codec.case1 "text" Choice1Of2 (Field.required "text" Codec.string)
                and! image = Codec.case1 "image" Choice2Of2 (Field.required "bytes" Codec.int)

                return
                    fun value ->
                        match value with
                        | Choice1Of2 s -> text s
                        | Choice2Of2 n -> image n
            }
        ))
            .Schema

    objectOf
        [
            field "any" Presence.Required Codec.json.Schema
            field "unit" Presence.Required Codec.unit.Schema
            field "flag" Presence.Optional Codec.bool.Schema
            field "pair" Presence.Required (Codec.tuple2 Codec.string Codec.int).Schema
            field
                "triple"
                Presence.Required
                (Codec.tuple3 Codec.bool (Codec.option Codec.string) (Codec.list Codec.int)).Schema
            field "version" Presence.Required (Codec.literal (Json.String "2.0")).Schema
            field "answer" Presence.Required (Codec.literal (Json.Number(JsonNumber.ofDecimal 42.50m))).Schema
            field "negative" Presence.Required (Codec.literal (Json.int -1)).Schema
            field "nothing" Presence.Required (Codec.literal Json.Null).Schema
            field
                "fixed"
                Presence.Required
                (Codec.literal (
                    Json.Object
                        [
                            "on", Json.Bool true
                            "content-type", Json.String "json"
                            "list", Json.Array [ Json.int 1; Json.String "x" ]
                        ]
                ))
                    .Schema
            field "empty" Presence.Required (Codec.literal (Json.Object [])).Schema
            field "either" Presence.Required (ofKind (SchemaKind.AnyOf [ str; integer; Codec.bool.Schema ]))
            field "neither" Presence.Optional (ofKind (SchemaKind.AnyOf []))
            field "noChoice" Presence.Optional (ofKind (SchemaKind.Enum []))
            field "noCase" Presence.Optional (ofKind (SchemaKind.Union { Discriminator = "kind"; Cases = [] }))
            field "attachment" Presence.Required nullableUnion
            field
                "attachments"
                Presence.Required
                (ofKind (
                    SchemaKind.Array
                        {
                            Items = nullableUnion
                            MinItems = Some 1
                            MaxItems = Some 3
                        }
                ))
            field "grid" Presence.Required (Codec.list (Codec.list (Codec.option Codec.int))).Schema
            field "byName" Presence.Required (Codec.stringMap (Codec.list (Codec.option Codec.string))).Schema
            field "byKey" Presence.Required (Codec.stringMap span).Schema
            field "points" Presence.Required (Codec.list (Codec.tuple2 Codec.int Codec.int)).Schema
            field
                "inline"
                Presence.Required
                (objectOf [ field "x" Presence.Required integer; field "label" Presence.Optional str ])
            field "nested" Presence.Required (Codec.stringMap (Codec.record { return () })).Schema
            field
                "host"
                Presence.Required
                (Codec.string
                 |> Codec.pattern "^[a-z]+(\\.[a-z]+)*/x*/$"
                 |> Codec.minLength 3
                 |> Codec.maxLength 63
                 |> Codec.format "hostname")
                    .Schema
            field "ratio" Presence.Required (Codec.float |> Codec.between 0.0 1.0).Schema
            field
                "exclusive"
                Presence.Required
                (ofKind (
                    SchemaKind.Number
                        {
                            Minimum = Some(Bound.Exclusive(JsonNumber.ofInt 0))
                            Maximum = Some(Bound.Exclusive(JsonNumber.ofInt 1))
                        }
                ))
            field "few" Presence.Required (Codec.list Codec.string |> Codec.minItems 2 |> Codec.maxItems 2).Schema
            field
                "described"
                Presence.Required
                (Codec.list (Codec.string |> Codec.nonEmpty |> Codec.describe "A label.")).Schema
            field "codes" Presence.Optional (Codec.stringMap (Codec.int |> Codec.between 100 599)).Schema
            field "content-type" Presence.Required str
            field "2fa" Presence.Required Codec.bool.Schema
            field "default" Presence.Required str
            field "$ref" Presence.Required str
            field "__proto__" Presence.Optional str
            field "" Presence.Optional str
            field "é" Presence.Optional str
            field
                "mode"
                (Presence.Defaulted(Json.String "*/fast"))
                (Codec.enum [ "fast", "fast"; "*/fast", "*/fast" ]).Schema
            field
                "limits"
                (Presence.Defaulted(Codecs.limits.Encode Limits.defaults))
                (Codecs.limits.Schema |> Schema.describe "Bounds for previews of this value.")
            field
                "comment"
                Presence.Required
                (str
                 |> Schema.describe
                     "Text that ends a comment: */ here.\r\nA second line.\n\n  Indented after a blank line.")
            field
                "tree"
                Presence.Optional
                (ofKind (SchemaKind.Nullable(ofKind (SchemaKind.Nullable tree.Schema)))
                 |> Schema.describe "Nullable twice.")
        ]
    |> Schema.describe "Every schema construct."
    |> named "Shapes"

/// Type names TypeScript cannot use as they are, and the names they would collide with.
let naming: Schema list =
    let alias (name: string) (body: Schema) = named name body

    [
        alias "Promise" (str |> Schema.describe "Would shadow the global Promise.")
        alias "Promise_" integer
        alias "string" str
        alias "default" Codec.bool.Schema
        alias
            "my-type"
            (ofKind (
                SchemaKind.Array
                    {
                        Items = ofKind (SchemaKind.Ref "my-type")
                        MinItems = None
                        MaxItems = None
                    }
            ))
        alias "my_type" Codec.unit.Schema
        alias "9lives" (ofKind (SchemaKind.Map(ofKind (SchemaKind.Ref "Promise"))))
        alias
            "Uses"
            (ofKind (
                SchemaKind.Tuple
                    [
                        for name in [ "Promise"; "Promise_"; "string"; "default"; "my-type"; "my_type"; "9lives" ] ->
                            ofKind (SchemaKind.Ref name)
                    ]
            ))
    ]

/// The domain's named types.
let domain: Schema list =
    [
        priority.Schema
        span.Schema
        matchCodec.Schema
        turnResult.Schema
        tree.Schema
        shapes
    ]

// ---- A kernel host bridge (T117 builds the real one from binding codecs) -------------------------

type ShellResult =
    {
        ExitCode: int
        Stdout: string
        Lines: string list
    }

let shellOptions =
    Codec.record {
        let! cwd = Codec.optionalField "cwd" fst Codec.string
        and! timeout = Codec.optionalField "timeoutMs" snd (Codec.int |> Codec.atLeast 1)
        return cwd, timeout
    }
    |> Codec.describe "How to run a command."
    |> Codec.named "ShellOptions"

let shellResult =
    Codec.record {
        let! exitCode = Codec.field "exitCode" (fun r -> r.ExitCode) Codec.int
        and! stdout = Codec.field "stdout" (fun r -> r.Stdout) Codec.string
        and! lines = Codec.field "lines" (fun r -> r.Lines) (Codec.list Codec.string)

        return
            {
                ExitCode = exitCode
                Stdout = stdout
                Lines = lines
            }
    }
    |> Codec.describe "What a command did."
    |> Codec.named "ShellResult"

let finishResult =
    Codec.record {
        let! accepted = Codec.field "accepted" fst Codec.bool
        and! issues = Codec.defaultField "issues" snd [] (Codec.list Codecs.issue)
        return accepted, issues
    }
    |> Codec.named "FinishResult"

let review =
    Codec.record {
        let! verdict =
            Codec.field "verdict" fst (Codec.enum [ "approve", "approve"; "changes", "changes" ])

        and! comments = Codec.field "comments" snd (Codec.list Codec.string)
        return verdict, comments
    }
    |> Codec.named "Review"

let private method (name: string) (description: string) (returns: Schema) (parameters: Parameter list) =
    {
        Name = name
        Description = Some description
        Parameters = parameters
        Returns = returns
    }

let private required name (schema: Schema) = Parameter.required name schema

/// Bindings as a kernel receives them: dotted paths.
let bindings: Method list =
    [
        method
            "shell.run"
            "Runs a command line in the workspace."
            shellResult.Schema
            [
                required "command" str |> Parameter.describe "The command line."
                Parameter.optional "options" shellOptions.Schema
            ]
        method "fs.read" "Reads a file." str [ required "path" str ]
        method
            "fs.delete"
            "Deletes a file; the name is a reserved word, which is fine for a property."
            Codec.unit.Schema
            [ required "path" str ]
        method
            "repo.refs"
            "Finds the references to a symbol."
            (Codec.list matchCodec).Schema
            [
                required "symbol" (str |> Schema.describe "A qualified name, e.g. `Http.retry`.")
            ]
        method
            "agents.reviewer"
            "Asks the reviewer agent for a review."
            review.Schema
            [
                required
                    "input"
                    (Codec.record {
                        let! diff = Codec.field "diff" fst Codec.string
                        and! intent = Codec.field "intent" snd Codec.string
                        return diff, intent
                    })
                        .Schema
            ]
        method
            "ledger.search"
            "Searches decisions; an optional parameter before a required one."
            (Codec.list Codec.string).Schema
            [
                Parameter.optional "scope" str
                required "text" str
                Parameter.optional "limit" (Codec.int |> Codec.between 1 100).Schema
            ]
        method
            "tools.my-tool"
            "A tool whose name needs quotes."
            Codec.json.Schema
            [ required "class" str; required "class" integer ]
        method
            "context.pin"
            "Pins a value into the prompt."
            Codec.unit.Schema
            [ required "name" str; required "value" Codec.json.Schema ]
        method
            "finish"
            "Ends the turn with a result the host validates."
            finishResult.Schema
            [ required "result" turnResult.Schema ]
        method "now" "The host's clock, in microseconds since the Unix epoch." integer []
    ]

let groups =
    [
        "shell", "Commands in the workspace's shell."
        "fs", "The workspace's files."
        "agents", "Specialized agents."
    ]

/// The kernel's declaration file: `<global>.d.ts`.
let kernel: DeclarationFile =
    {
        Layout =
            Layout.Global(Product.kernelGlobal, Some "The host bridge: what cells can call, and the types it uses.")
        Types = [ turnResult.Schema ]
        Members = Member.ofPaths groups bindings
    }

// ---- Real contracts --------------------------------------------------------------------------------

/// Fx.Contracts: issues, preview limits and previews.
let contracts: Schema list =
    [ Codecs.issue.Schema; Codecs.limits.Schema; Codecs.preview.Schema ]

/// Fx.Store: event envelopes and blob references.
let store: Schema list =
    [
        Fx.Store.Codecs.attribution.Schema
        Fx.Store.Codecs.eventRef.Schema
        Fx.Store.Codecs.payload.Schema
        Fx.Store.Codecs.blobRef.Schema
        named "RecordedEvent" (Fx.Store.Codecs.recorded Codec.json).Schema
    ]

let coreSlots = Fx.Variants.SlotRegistry.ofSlots Fx.Variants.Slots.all

/// Fx.Variants: slot declarations, resolved values and their sources, recorded reads, profiles,
/// policies and the `variants` member of a config file with the core slots.
let variants: Schema list =
    [
        Fx.Variants.SlotInfo.declCodec.Schema
        Fx.Variants.Resolved.codec.Schema
        Fx.Variants.Profile.codec.Schema
        named "VariantReads" Fx.Variants.Variants.readsCodec.Schema
        named "VariantPolicy" (Fx.Variants.Policy.codec "").Schema
        named "VariantsConfig" (Fx.Variants.SlotRegistry.configSchema coreSlots)
    ]
