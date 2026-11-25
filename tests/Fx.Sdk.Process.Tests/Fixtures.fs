/// Cross-language fixtures (spec §4.1): what the F# codecs do, written for the TypeScript SDK's tests to
/// compare its codecs with. `codecs.json` has a domain that uses every combinator, with each codec's
/// schema (the wire form, `Codecs.schema`), its JSON Schema, and inputs with what decoding them gives
/// (the re-encoded value, or the issues); `protocol.json` has the schemas of the contracts the SDK
/// mirrors at run time (manifests, decisions, the extension protocol's messages) and manifests with
/// their checks and hashes. The TypeScript tests (`packages/sdk/test`) define the same codecs and must
/// produce the same JSON.
module Fx.Sdk.Process.Tests.Fixtures

open Expecto
open Fx.Contracts
open Fx.Variants
open Fx.Sdk
open Fx.Sdk.Process
open Fx.Ext.Sample

type Span = { Start: int; End: int }

type Priority =
    | Low
    | Medium
    | High

    override this.ToString() =
        match this with
        | Low -> "low"
        | Medium -> "medium"
        | High -> "high"

type Match =
    {
        Path: string
        Line: int
        Note: string option
        Tags: string list
        Score: float
        Span: Span option
        Priority: Priority
        Counts: Map<string, int>
    }

    override this.ToString() = $"{this.Path}:{this.Line}"

type TurnResult =
    | Done of summary: string * evidence: Match list option * verify: string option
    | Waiting of on: string list * note: string
    | Moved of Span
    | Cancelled

    override this.ToString() =
        match this with
        | Done(summary, _, _) -> "done " + summary
        | Waiting(on, _) -> "waiting on " + String.concat ", " on
        | Moved span -> $"moved {span.Start}-{span.End}"
        | Cancelled -> "cancelled"

type Tree =
    | Leaf of int
    | Node of label: string * children: Tree list

    override this.ToString() =
        match this with
        | Leaf n -> string n
        | Node(label, _) -> label

type Settings =
    {
        Name: string
        Retries: int option
    }

    override this.ToString() = this.Name

type Shape =
    | Circle of radius: float
    | Square of side: float

    override this.ToString() =
        match this with
        | Circle r -> $"circle {r}"
        | Square s -> $"square {s}"

let span: Codec<Span> =
    Codec.record {
        let! start = Codec.field "start" (fun (s: Span) -> s.Start) Codec.int
        and! finish = Codec.field "end" (fun s -> s.End) Codec.int
        return { Start = start; End = finish }
    }
    |> Codec.describe "A range of lines, end exclusive."
    |> Codec.named "Span"

let priority: Codec<Priority> =
    Codec.enum [ Low, "low"; Medium, "medium"; High, "high" ]

let matchCodec: Codec<Match> =
    Codec.record {
        let! path =
            Codec.field
                "path"
                (fun (m: Match) -> m.Path)
                (Codec.string |> Codec.nonEmpty |> Codec.describe "Workspace-relative.")

        and! line = Codec.field "line" (fun m -> m.Line) (Codec.int |> Codec.atLeast 1)
        and! note = Codec.optionalField "note" (fun m -> m.Note) Codec.string

        and! tags =
            Codec.defaultField "tags" (fun m -> m.Tags) [] (Codec.list Codec.string |> Codec.maxItems 3)

        and! score =
            Codec.field "score" (fun m -> m.Score) (Codec.float |> Codec.between 0.0 1.0)

        and! span = Codec.field "span" (fun m -> m.Span) (Codec.option span)
        and! priority = Codec.defaultField "priority" (fun m -> m.Priority) Medium priority
        and! counts = Codec.field "counts" (fun m -> m.Counts) (Codec.stringMap Codec.int)

        return
            {
                Path = path
                Line = line
                Note = note
                Tags = tags
                Score = score
                Span = span
                Priority = priority
                Counts = counts
            }
    }
    |> Codec.named "Match"

let turnResult: Codec<TurnResult> =
    Codec.union "kind" {
        let! finished =
            Codec.case3
                "done"
                (fun summary evidence verify -> Done(summary, evidence, verify))
                (Field.required "summary" Codec.string)
                (Field.optional "evidence" (Codec.list matchCodec))
                (Field.optional "verify" (Codec.string |> Codec.pattern "^[a-z]"))

        and! waiting =
            Codec.case2
                "waiting"
                (fun on note -> Waiting(on, note))
                (Field.required "on" (Codec.list Codec.string |> Codec.minItems 1))
                (Field.required "note" (Codec.string |> Codec.maxLength 20))

        and! moved = Codec.caseOf "moved" Moved span
        and! cancelled = Codec.case0 "cancelled" Cancelled

        return
            fun value ->
                match value with
                | Done(summary, evidence, verify) -> finished summary evidence verify
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

let settings: Codec<Settings> =
    Codec.record {
        let! name = Codec.field "name" (fun (s: Settings) -> s.Name) Codec.string

        and! retries =
            Codec.optionalField "retries" (fun s -> s.Retries) (Codec.int |> Codec.between 0 5)

        return { Name = name; Retries = retries }
    }
    |> Codec.strict
    |> Codec.describe "Strict settings: unknown members are issues."
    |> Codec.named "Settings"

let shape: Codec<Shape> =
    Codec.union "type" {
        let! circle = Codec.case1 "circle" Circle (Field.required "radius" Codec.float)
        and! square = Codec.case1 "square" Square (Field.required "side" Codec.float)

        return
            fun value ->
                match value with
                | Circle r -> circle r
                | Square s -> square s
    }
    |> Codec.strict

let header: Codec<unit * int * (string * bool) list> =
    Codec.tuple3 (Codec.literal (Json.String "v1")) Codec.int (Codec.keyValues Codec.bool)

let misc: Codec<Json * unit * string * float> =
    Codec.record {
        let! any = Codec.field "any" (fun (a, _, _, _) -> a) Codec.json
        and! nothing = Codec.field "nothing" (fun (_, n, _, _) -> n) Codec.unit

        and! host =
            Codec.field "host" (fun (_, _, h, _) -> h) (Codec.string |> Codec.format "hostname" |> Codec.minLength 3)

        and! ratio =
            Codec.field "ratio" (fun (_, _, _, r) -> r) (Codec.float |> Codec.atMost 2.5)

        return any, nothing, host, ratio
    }

/// One codec of the domain, with its inputs.
type Entry =
    {
        Name: string
        Schema: Schema
        Encode: Json -> Validation<Json, Issue>
        Inputs: string list
    }

    override this.ToString() = this.Name

let private entry (name: string) (codec: Codec<'T>) (inputs: string list) : Entry =
    {
        Name = name
        Schema = codec.Schema
        Encode = fun json -> codec.Decode json |> Result.map codec.Encode
        Inputs = inputs
    }

let entries: Entry list =
    [
        entry
            "span"
            span
            [
                """{"start":1,"end":3}"""
                """{"end":3,"start":1,"extra":true}"""
                """{"start":"1"}"""
                """[1,3]"""
                """null"""
                """{"start":1.5,"end":3000000000}"""
            ]
        entry
            "match"
            matchCodec
            [
                """{"path":"src/a.fs","line":3,"note":"n","tags":["x"],"score":0.5,"span":{"start":1,"end":2},"priority":"high","counts":{"b":2,"a":1}}"""
                """{"path":"a","line":1,"score":1,"span":null,"counts":{}}"""
                """{"path":"a","line":1,"note":null,"tags":null,"score":0,"span":null,"priority":null,"counts":{}}"""
                """{"path":"","line":0,"tags":["a","b","c","d"],"score":1.5,"span":{"start":"1"},"priority":"urgent","counts":{"a":"x","b":2}}"""
                """{"line":"3","note":4,"tags":[1]}"""
            ]
        entry
            "turnResult"
            turnResult
            [
                """{"kind":"done","summary":"ok"}"""
                """{"summary":"ok","kind":"done","verify":"dotnet test","evidence":[{"path":"a","line":2,"score":0.25,"span":null,"counts":{"x":1}}]}"""
                """{"kind":"waiting","on":["ci"],"note":"soon"}"""
                """{"kind":"moved","start":1,"end":9}"""
                """{"kind":"cancelled","why":"ignored"}"""
                """{"summary":"ok"}"""
                """{"kind":"don"}"""
                """{"kind":3}"""
                """{"kind":null}"""
                """{"kind":"waiting","on":[],"note":"this note is far too long to pass"}"""
                """{"kind":"moved","start":1}"""
                """{"kind":"done","summary":1,"verify":"Dotnet","evidence":[{"path":"","line":1}]}"""
                "\"done\""
            ]
        entry
            "tree"
            tree
            [
                """{"kind":"node","label":"root","children":[{"kind":"leaf","value":1},{"kind":"node","label":"inner","children":[]}]}"""
                """{"kind":"node","label":"root","children":[{"kind":"node","label":"x","children":[{"kind":"leaf","value":"1"}]},{"kind":"twig"}]}"""
            ]
        entry
            "settings"
            settings
            [
                """{"name":"a"}"""
                """{"name":"a","retries":5}"""
                """{"name":"a","retries":6,"retry":1,"other":null}"""
                """{"retries":1,"nam":"a"}"""
            ]
        entry
            "shape"
            shape
            [
                """{"type":"circle","radius":1.5}"""
                """{"type":"square","side":2,"radius":1}"""
                """{"type":"circle","radius":"NaN"}"""
                """{"type":"circle","radius":"big","color":"red"}"""
            ]
        entry
            "header"
            header
            [
                """["v1",2,{"b":true,"a":false}]"""
                """["v2",2.5,{"a":1}]"""
                """["v1",2]"""
                """{"0":"v1"}"""
            ]
        entry
            "misc"
            misc
            [
                """{"any":{"x":[1,null,"y"]},"nothing":null,"host":"example","ratio":2.5}"""
                """{"any":null,"nothing":0,"host":"ab","ratio":3}"""
                """{"nothing":null,"host":7,"ratio":"Infinity"}"""
            ]
    ]

let private issues (list: Issue list) : Json =
    Json.Array(list |> List.map Codecs.issue.Encode)

let private parse (text: string) : Json =
    match Json.parse text with
    | Ok json -> json
    | Error e -> failtest $"fixture input {text}: {e}"

/// `codecs.json`.
let renderCodecs () : string =
    let codec (e: Entry) =
        Json.Object
            [
                "name", Json.String e.Name
                "schema", Codecs.schema.Encode e.Schema
                "jsonSchema", Schema.toJsonSchema e.Schema
                "cases",
                Json.Array
                    [
                        for text in e.Inputs do
                            let input = parse text

                            match e.Encode input with
                            | Ok output -> Json.Object [ "input", input; "output", output ]
                            | Error found -> Json.Object [ "input", input; "issues", issues found ]
                    ]
            ]

    Json.serializeIndented (Json.Object [ "codecs", Json.Array(entries |> List.map codec) ])
    + "\n"

/// The contracts the TypeScript SDK mirrors with runtime codecs, by the name it gives them.
let contracts: (string * Schema) list =
    [
        "issue", Codecs.issue.Schema
        "manifest", Manifest.codec.Schema
        "decision", Decision.codec.Schema
        "transformSettings", Transform.settings.Schema
        "slotDecl", SlotInfo.declCodec.Schema
        "activateParams", ExtensionProtocol.Codecs.activateParams.Schema
        "activated", ExtensionProtocol.Codecs.activated.Schema
        "invokeParams", ExtensionProtocol.Codecs.invokeParams.Schema
        "callParams", ExtensionProtocol.Codecs.callParams.Schema
        "extensionRef", ExtensionProtocol.Codecs.extensionRef.Schema
        "output", ExtensionProtocol.Codecs.output.Schema
        "protocolError", ExtensionProtocol.Codecs.protocolError.Schema
    ]

/// Manifests, and what `Manifest.decode` makes of them: the manifest and its hash, or the issues.
let manifests: string list =
    [
        """{"id":"acme.tools","version":"1.2.0","sdk":"0.1","protocol":"1","runtime":"bun","capabilities":["clock"],"contributions":[{"type":"gate:acme.check","id":"strict"}],"rpc":{"methods":[{"name":"tools/list","description":"Lists tools."}]}}"""
        """{"id":"acme.data","version":"0.1.0","description":"Data.","sdk":"0.1","protocol":"1","placement":"environment","runtime":"declarative","contributions":[{"type":"sample.word","id":"w","value":"word"}],"replaces":["acme.old"]}"""
        """{"id":"acme.proc","version":"1.0.0","sdk":"0.1","protocol":"1","runtime":"process","launch":{"exe":"${root}/bin/tools${exeSuffix}","args":["--stdio"],"envAllowlist":["PATH"],"envSet":{"MODE":"fast"}}}"""
        """{"id":"Acme","version":"1.0","sdk":"one","protocol":"","runtime":"process","extra":1,"contributions":[{"type":"x","id":"a"},{"type":"x","id":"a"}],"replaces":["Acme"]}"""
        """{"id":"acme.decl","version":"1.0.0","sdk":"0.1","protocol":"1","runtime":"declarative","contributions":[{"type":"x","id":"a"}],"rpc":{"methods":[{"name":"m"}]},"launch":{"exe":"x","envSet":{"NODE_OPTIONS":"--require x"}}}"""
        """{"id":"acme.tools","version":"1.2.0","sdk":"0.1","protocol":"1","runtime":"bun","hash":"sha256:0000000000000000000000000000000000000000000000000000000000000000"}"""
        """{"id":"acme.proc","version":"1.0.0","sdk":"0.1","protocol":"1","runtime":"process","launch":{"exe":"${root}/run","untrustedOverrides":["--tsconfig-override=${root}/tsconfig.json"],"envSet":{"HOME":"${root}/home"}}}"""
        """{"id":"acme.proc","version":"1.0.0","sdk":"0.1","protocol":"1","runtime":"process","launch":{"exe":"${root}/run","untrustedOverrides":["tsconfig-override"],"envSet":{"NODE_PATH":"x","ok":"1","BUN_JSC_useJIT":"0"}}}"""
        """{"id":"acme.proc","version":"1.0.0","sdk":"0.1","protocol":"1","runtime":"process","launch":{"exe":"${root}/run","untrustedOverrides":[]}}"""
    ]

/// `protocol.json`.
let renderProtocol () : string =
    Json.serializeIndented (
        Json.Object
            [
                "schemas", Json.Object(contracts |> List.map (fun (name, schema) -> name, Codecs.schema.Encode schema))
                "manifests",
                Json.Array
                    [
                        for text in manifests do
                            let input = parse text

                            match Manifest.decode input with
                            | Ok manifest ->
                                Json.Object
                                    [
                                        "input", input
                                        "output", Manifest.codec.Encode manifest
                                        "hash", Json.String(Manifest.hash manifest)
                                    ]
                            | Error found -> Json.Object [ "input", input; "issues", issues found ]
                    ]
            ]
    )
    + "\n"

/// `packages/sample-extension/test/fixtures/manifest.json`: the F# sample's manifest as the TypeScript
/// sample declares it (on the `bun` runtime, T115), and its hash.
let renderSampleManifest () : string =
    let manifest = { Sample.manifest with Runtime = "bun" }

    Json.serializeIndented (
        Json.Object
            [
                "manifest", Manifest.codec.Encode manifest
                "hash", Json.String(Manifest.hash manifest)
            ]
    )
    + "\n"

[<Tests>]
let tests =
    testList
        "Cross-language fixtures"
        [
            test "codecs.json is what the F# codecs do" {
                Repo.expectGenerated [ "packages"; "sdk"; "test"; "fixtures"; "codecs.json" ] (renderCodecs ())
            }

            test "protocol.json is what the F# contracts do" {
                Repo.expectGenerated [ "packages"; "sdk"; "test"; "fixtures"; "protocol.json" ] (renderProtocol ())
            }

            test "the TypeScript sample declares the F# sample's manifest" {
                Repo.expectGenerated
                    [ "packages"; "sample-extension"; "test"; "fixtures"; "manifest.json" ]
                    (renderSampleManifest ())
            }
        ]
