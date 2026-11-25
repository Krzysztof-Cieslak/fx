/// Fx.Contracts at sizes where Native AOT used to fail (T101b): a 24-case recursive union whose cases
/// carry records, nested unions and itself, and a 48-field record of unions and records. ILC aborts
/// curried functions over many function types or deeply nested types (IL3054), so these prove that
/// neither the number of cases nor the number of fields reaches such a function.
module Fx.Aot.Scenarios.ContractsLarge

open Fx.Contracts
open Fx.Aot.Scenarios.Show

type Level =
    | Trace
    | Debug
    | Info
    | Warn
    | Fatal

    override this.ToString() =
        match this with
        | Trace -> "trace"
        | Debug -> "debug"
        | Info -> "info"
        | Warn -> "warn"
        | Fatal -> "fatal"

let private level =
    Codec.enum [ Trace, "trace"; Debug, "debug"; Info, "info"; Warn, "warn"; Fatal, "fatal" ]

type Origin =
    | User of name: string
    | Model of id: string * turn: int
    | Tool of name: string * call: string * attempt: int
    | System

    override this.ToString() =
        match this with
        | User name -> $"User {name}"
        | Model(id, turn) -> $"Model {id} {turn}"
        | Tool(name, call, attempt) -> $"Tool {name} {call} {attempt}"
        | System -> "System"

let private origin: Codec<Origin> =
    Codec.union "from" {
        let! user = Codec.case1 "user" User (Field.required "name" Codec.string)

        and! model =
            Codec.case2
                "model"
                (fun id turn -> Model(id, turn))
                (Field.required "id" Codec.string)
                (Field.required "turn" Codec.int)

        and! tool =
            Codec.case3
                "tool"
                (fun name call attempt -> Tool(name, call, attempt))
                (Field.required "name" Codec.string)
                (Field.required "call" Codec.string)
                (Field.withDefault "attempt" 1 Codec.int)

        and! system = Codec.case0 "system" System

        return
            fun value ->
                match value with
                | User name -> user name
                | Model(id, turn) -> model id turn
                | Tool(name, call, attempt) -> tool name call attempt
                | System -> system
    }
    |> Codec.named "Origin"

type Span =
    {
        Start: int
        End: int
    }

    override this.ToString() = $"{this.Start}-{this.End}"

let private span: Codec<Span> =
    Codec.record {
        let! start = Codec.field "start" (fun s -> s.Start) Codec.int
        and! finish = Codec.field "end" (fun s -> s.End) Codec.int
        return { Start = start; End = finish }
    }
    |> Codec.named "Span"

/// A record of a union and a record, carried by a union case.
type Edit =
    {
        Path: string
        Span: Span
        Text: string
        Origin: Origin
        Tags: string list
    }

    override this.ToString() = $"Edit({this.Path})"

let private edit: Codec<Edit> =
    Codec.record {
        let! path = Codec.field "path" (fun e -> e.Path) Codec.string
        and! span = Codec.field "span" (fun e -> e.Span) span
        and! text = Codec.field "text" (fun e -> e.Text) Codec.string
        and! origin = Codec.field "origin" (fun e -> e.Origin) origin
        and! tags = Codec.defaultField "tags" (fun e -> e.Tags) [] (Codec.list Codec.string)

        return
            {
                Path = path
                Span = span
                Text = text
                Origin = origin
                Tags = tags
            }
    }
    |> Codec.describe "A change to a file."
    |> Codec.named "Edit"

type Tree =
    | Leaf of int
    | Node of label: string * children: Tree list

    override this.ToString() =
        match this with
        | Leaf n -> $"Leaf {n}"
        | Node(label, children) -> $"Node {label} ({children.Length})"

let private tree: Codec<Tree> =
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

/// 48 fields: the chained merges nest the tuple of fields 5 deep, beyond ILC's generic depth cutoff.
type Settings =
    {
        F01: string
        F02: int
        F03: int64
        F04: float
        F05: decimal
        F06: bool
        F07: string list
        F08: string option
        F09: Level
        F10: Origin
        F11: Span
        F12: Map<string, int>
        F13: string
        F14: int
        F15: int64
        F16: float
        F17: decimal
        F18: bool
        F19: string list
        F20: string option
        F21: Level
        F22: Origin
        F23: Span
        F24: Map<string, int>
        F25: string
        F26: int
        F27: int64
        F28: float
        F29: decimal
        F30: bool
        F31: string list
        F32: string option
        F33: Level
        F34: Origin
        F35: Span
        F36: Map<string, int>
        F37: string
        F38: int
        F39: int64
        F40: float
        F41: decimal
        F42: bool
        F43: string list
        F44: string option
        F45: Level
        F46: Origin
        F47: Span
        F48: Map<string, int>
    }

    override this.ToString() = "Settings"

let private settings: Codec<Settings> =
    Codec.record {
        let! f01 = Codec.field "f01" (fun s -> s.F01) Codec.string
        and! f02 = Codec.field "f02" (fun s -> s.F02) (Codec.int |> Codec.between 0 1000)
        and! f03 = Codec.field "f03" (fun s -> s.F03) Codec.int64
        and! f04 = Codec.field "f04" (fun s -> s.F04) Codec.float
        and! f05 = Codec.field "f05" (fun s -> s.F05) Codec.decimal
        and! f06 = Codec.field "f06" (fun s -> s.F06) Codec.bool
        and! f07 = Codec.defaultField "f07" (fun s -> s.F07) [] (Codec.list Codec.string)
        and! f08 = Codec.optionalField "f08" (fun s -> s.F08) Codec.string
        and! f09 = Codec.field "f09" (fun s -> s.F09) level
        and! f10 = Codec.field "f10" (fun s -> s.F10) origin
        and! f11 = Codec.field "f11" (fun s -> s.F11) span
        and! f12 = Codec.field "f12" (fun s -> s.F12) (Codec.stringMap Codec.int)
        and! f13 = Codec.field "f13" (fun s -> s.F13) Codec.string
        and! f14 = Codec.field "f14" (fun s -> s.F14) (Codec.int |> Codec.between 0 1000)
        and! f15 = Codec.field "f15" (fun s -> s.F15) Codec.int64
        and! f16 = Codec.field "f16" (fun s -> s.F16) Codec.float
        and! f17 = Codec.field "f17" (fun s -> s.F17) Codec.decimal
        and! f18 = Codec.field "f18" (fun s -> s.F18) Codec.bool
        and! f19 = Codec.defaultField "f19" (fun s -> s.F19) [] (Codec.list Codec.string)
        and! f20 = Codec.optionalField "f20" (fun s -> s.F20) Codec.string
        and! f21 = Codec.field "f21" (fun s -> s.F21) level
        and! f22 = Codec.field "f22" (fun s -> s.F22) origin
        and! f23 = Codec.field "f23" (fun s -> s.F23) span
        and! f24 = Codec.field "f24" (fun s -> s.F24) (Codec.stringMap Codec.int)
        and! f25 = Codec.field "f25" (fun s -> s.F25) Codec.string
        and! f26 = Codec.field "f26" (fun s -> s.F26) (Codec.int |> Codec.between 0 1000)
        and! f27 = Codec.field "f27" (fun s -> s.F27) Codec.int64
        and! f28 = Codec.field "f28" (fun s -> s.F28) Codec.float
        and! f29 = Codec.field "f29" (fun s -> s.F29) Codec.decimal
        and! f30 = Codec.field "f30" (fun s -> s.F30) Codec.bool
        and! f31 = Codec.defaultField "f31" (fun s -> s.F31) [] (Codec.list Codec.string)
        and! f32 = Codec.optionalField "f32" (fun s -> s.F32) Codec.string
        and! f33 = Codec.field "f33" (fun s -> s.F33) level
        and! f34 = Codec.field "f34" (fun s -> s.F34) origin
        and! f35 = Codec.field "f35" (fun s -> s.F35) span
        and! f36 = Codec.field "f36" (fun s -> s.F36) (Codec.stringMap Codec.int)
        and! f37 = Codec.field "f37" (fun s -> s.F37) Codec.string
        and! f38 = Codec.field "f38" (fun s -> s.F38) (Codec.int |> Codec.between 0 1000)
        and! f39 = Codec.field "f39" (fun s -> s.F39) Codec.int64
        and! f40 = Codec.field "f40" (fun s -> s.F40) Codec.float
        and! f41 = Codec.field "f41" (fun s -> s.F41) Codec.decimal
        and! f42 = Codec.field "f42" (fun s -> s.F42) Codec.bool
        and! f43 = Codec.defaultField "f43" (fun s -> s.F43) [] (Codec.list Codec.string)
        and! f44 = Codec.optionalField "f44" (fun s -> s.F44) Codec.string
        and! f45 = Codec.field "f45" (fun s -> s.F45) level
        and! f46 = Codec.field "f46" (fun s -> s.F46) origin
        and! f47 = Codec.field "f47" (fun s -> s.F47) span
        and! f48 = Codec.field "f48" (fun s -> s.F48) (Codec.stringMap Codec.int)

        return
            {
                F01 = f01
                F02 = f02
                F03 = f03
                F04 = f04
                F05 = f05
                F06 = f06
                F07 = f07
                F08 = f08
                F09 = f09
                F10 = f10
                F11 = f11
                F12 = f12
                F13 = f13
                F14 = f14
                F15 = f15
                F16 = f16
                F17 = f17
                F18 = f18
                F19 = f19
                F20 = f20
                F21 = f21
                F22 = f22
                F23 = f23
                F24 = f24
                F25 = f25
                F26 = f26
                F27 = f27
                F28 = f28
                F29 = f29
                F30 = f30
                F31 = f31
                F32 = f32
                F33 = f33
                F34 = f34
                F35 = f35
                F36 = f36
                F37 = f37
                F38 = f38
                F39 = f39
                F40 = f40
                F41 = f41
                F42 = f42
                F43 = f43
                F44 = f44
                F45 = f45
                F46 = f46
                F47 = f47
                F48 = f48
            }
    }
    |> Codec.named "Settings"

let private sampleSettings =
    {
        F01 = "s1"
        F02 = 20
        F03 = 9007199254740993L
        F04 = 0.1 + 0.2
        F05 = 5.10M
        F06 = true
        F07 = [ "a"; "b7" ]
        F08 = Some "note 8"
        F09 = Trace
        F10 = User "ada"
        F11 = { Start = 11; End = 14 }
        F12 = Map.empty
        F13 = "s13"
        F14 = 140
        F15 = 9007199254740995L
        F16 = nan
        F17 = 17.10M
        F18 = false
        F19 = []
        F20 = None
        F21 = Debug
        F22 = Model("m1", 22)
        F23 = { Start = 23; End = 26 }
        F24 = Map.ofList [ "b", 24; "a", 1 ]
        F25 = "s25"
        F26 = 260
        F27 = 9007199254740997L
        F28 = 1e21
        F29 = 29.10M
        F30 = true
        F31 = [ "a"; "b31" ]
        F32 = Some "note 32"
        F33 = Info
        F34 = Tool("grep", "c34", 2)
        F35 = { Start = 35; End = 38 }
        F36 = Map.empty
        F37 = "s37"
        F38 = 380
        F39 = 9007199254740999L
        F40 = -2.5e-8
        F41 = 41.10M
        F42 = false
        F43 = []
        F44 = None
        F45 = Warn
        F46 = System
        F47 = { Start = 47; End = 50 }
        F48 = Map.ofList [ "b", 48; "a", 1 ]
    }

/// 24 cases: without and with payloads, records, nested unions, itself and the 48-field record.
type Event =
    | Started
    | Stopped of reason: string
    | Paused
    | Resumed of after: int64
    | Said of origin: Origin * text: string
    | Edited of Edit
    | Moved of Span
    | Selected of spans: Span list
    | Tagged of tags: string list * note: string option * weight: decimal
    | Scored of a: int * b: float * c: bool * d: string
    | Logged of level: Level * message: string
    | Nested of Event
    | Batch of events: Event list * origin: Origin
    | Grew of Tree
    | Attributed of Origin
    | Raw of Json
    | Counted of Map<string, int>
    | Timed of start: int64 * finish: int64 option
    | Renamed of from: string * into: string
    | Deleted of path: string
    | Created of path: string * size: int64
    | Linked of source: string * target: string * level: Level
    | Configured of Settings
    | Cancelled

    override this.ToString() =
        match this with
        | Started -> "Started"
        | Stopped _ -> "Stopped"
        | Paused -> "Paused"
        | Resumed _ -> "Resumed"
        | Said _ -> "Said"
        | Edited _ -> "Edited"
        | Moved _ -> "Moved"
        | Selected _ -> "Selected"
        | Tagged _ -> "Tagged"
        | Scored _ -> "Scored"
        | Logged _ -> "Logged"
        | Nested _ -> "Nested"
        | Batch _ -> "Batch"
        | Grew _ -> "Grew"
        | Attributed _ -> "Attributed"
        | Raw _ -> "Raw"
        | Counted _ -> "Counted"
        | Timed _ -> "Timed"
        | Renamed _ -> "Renamed"
        | Deleted _ -> "Deleted"
        | Created _ -> "Created"
        | Linked _ -> "Linked"
        | Configured _ -> "Configured"
        | Cancelled -> "Cancelled"

let private event: Codec<Event> =
    Codec.recursive "Event" (fun event ->
        Codec.union "kind" {
            let! started = Codec.case0 "started" Started

            and! stopped =
                Codec.case1 "stopped" Stopped (Field.required "reason" (Codec.string |> Codec.nonEmpty))

            and! paused = Codec.case0 "paused" Paused
            and! resumed = Codec.case1 "resumed" Resumed (Field.required "after" Codec.int64)

            and! said =
                Codec.case2
                    "said"
                    (fun origin text -> Said(origin, text))
                    (Field.required "origin" origin)
                    (Field.required "text" Codec.string)

            and! edited = Codec.caseOf "edited" Edited edit
            and! moved = Codec.caseOf "moved" Moved span

            and! selected =
                Codec.case1 "selected" Selected (Field.required "spans" (Codec.list span |> Codec.minItems 1))

            and! tagged =
                Codec.case3
                    "tagged"
                    (fun tags note weight -> Tagged(tags, note, weight))
                    (Field.withDefault "tags" [] (Codec.list Codec.string))
                    (Field.optional "note" Codec.string)
                    (Field.required "weight" Codec.decimal)

            and! scored =
                Codec.case4
                    "scored"
                    (fun a b c d -> Scored(a, b, c, d))
                    (Field.required "a" Codec.int)
                    (Field.required "b" Codec.float)
                    (Field.required "c" Codec.bool)
                    (Field.required "d" Codec.string)

            and! logged =
                Codec.case2
                    "logged"
                    (fun level message -> Logged(level, message))
                    (Field.required "level" level)
                    (Field.required "message" Codec.string)

            and! nested = Codec.case1 "nested" Nested (Field.required "event" event)

            and! batch =
                Codec.case2
                    "batch"
                    (fun events origin -> Batch(events, origin))
                    (Field.required "events" (Codec.list event))
                    (Field.required "origin" origin)

            and! grew = Codec.case1 "grew" Grew (Field.required "tree" tree)

            and! attributed =
                Codec.case1 "attributed" Attributed (Field.required "origin" origin)

            and! raw = Codec.case1 "raw" Raw (Field.required "json" Codec.json)

            and! counted =
                Codec.case1 "counted" Counted (Field.required "counts" (Codec.stringMap Codec.int))

            and! timed =
                Codec.case2
                    "timed"
                    (fun start finish -> Timed(start, finish))
                    (Field.required "start" Codec.int64)
                    (Field.optional "finish" Codec.int64)

            and! renamed =
                Codec.case2
                    "renamed"
                    (fun from into -> Renamed(from, into))
                    (Field.required "from" Codec.string)
                    (Field.required "into" Codec.string)

            and! deleted = Codec.case1 "deleted" Deleted (Field.required "path" Codec.string)

            and! created =
                Codec.case2
                    "created"
                    (fun path size -> Created(path, size))
                    (Field.required "path" Codec.string)
                    (Field.required "size" (Codec.int64 |> Codec.atLeast 0L))

            and! linked =
                Codec.case3
                    "linked"
                    (fun source target level -> Linked(source, target, level))
                    (Field.required "source" Codec.string)
                    (Field.required "target" Codec.string)
                    (Field.required "level" level)

            and! configured = Codec.caseOf "configured" Configured settings
            and! cancelled = Codec.case0 "cancelled" Cancelled

            return
                fun value ->
                    match value with
                    | Started -> started
                    | Stopped reason -> stopped reason
                    | Paused -> paused
                    | Resumed after -> resumed after
                    | Said(origin, text) -> said origin text
                    | Edited edit -> edited edit
                    | Moved span -> moved span
                    | Selected spans -> selected spans
                    | Tagged(tags, note, weight) -> tagged tags note weight
                    | Scored(a, b, c, d) -> scored a b c d
                    | Logged(level, message) -> logged level message
                    | Nested event -> nested event
                    | Batch(events, origin) -> batch events origin
                    | Grew tree -> grew tree
                    | Attributed origin -> attributed origin
                    | Raw json -> raw json
                    | Counted counts -> counted counts
                    | Timed(start, finish) -> timed start finish
                    | Renamed(from, into) -> renamed from into
                    | Deleted path -> deleted path
                    | Created(path, size) -> created path size
                    | Linked(source, target, level) -> linked source target level
                    | Configured settings -> configured settings
                    | Cancelled -> cancelled
        }
        |> Codec.describeCase "batch" "Several events from one origin."
        |> Codec.strict)

let private sampleEvents =
    [
        Started
        Stopped "done"
        Paused
        Resumed 9007199254740993L
        Said(Model("m1", 3), "Zażółć 😀")
        Edited
            {
                Path = "src/a.fs"
                Span = { Start = 1; End = 4 }
                Text = "let x = 1\n"
                Origin = Tool("edit", "c1", 1)
                Tags = [ "fix" ]
            }
        Moved { Start = 3; End = 7 }
        Selected [ { Start = 0; End = 1 }; { Start = 5; End = 9 } ]
        Tagged([ "a"; "b" ], Some "note", 1.10M)
        Scored(-1, nan, true, "d")
        Logged(Warn, "careful")
        Nested(Nested Paused)
        Batch([ Started; Said(User "ada", "hi"); Batch([], System) ], User "ada")
        Grew(Node("root", [ Leaf 1; Node("inner", [ Leaf 2 ]) ]))
        Attributed System
        Raw(Json.Array [ Json.Null; Json.Bool true; Json.Object [ "k", Json.String "v" ] ])
        Counted(Map.ofList [ "b", 2; "a", 1 ])
        Timed(1L, None)
        Renamed("a", "b")
        Deleted "x"
        Created("y", 0L)
        Linked("s", "t", Fatal)
        Configured sampleSettings
        Cancelled
    ]

let private result (validation: Validation<'T, Issue>) (show: 'T -> string) : string =
    match validation with
    | Ok value -> "ok " + show value
    | Error issues -> "issues " + Issue.render issues

let private tight =
    {
        MaxChars = 60
        MaxItems = 3
        MaxDepth = 2
        MaxStringChars = 8
    }

let largeUnion =
    Scenario.lines "contracts-large-union" (fun () ->
        let invalid =
            [
                """{"kind": "batch", "origin": {"from": "robot"}, "events": [{"kind": "stopped", "reason": ""}, {"kind": "scored", "a": "1", "b": "Infinity", "c": null}, {"kind": "nested", "event": {"kind": "nope"}}, {"kind": "edited", "path": "p", "span": {"start": 1}, "text": "t", "origin": {"from": "tool", "name": "x"}, "extra": 1}, {"kind": "configured", "f01": 1, "f10": {"from": "model", "id": "m"}}]}"""
                """{"kind": "selected", "spans": []}"""
                """{"kind": "created", "path": "p", "size": -1, "kind2": 0}"""
                """{"kind": "timed", "start": 1.5, "finish": null}"""
                """{"kind": 3}"""
                """{}"""
                """[]"""
            ]

        [
            for value in sampleEvents do
                let encoded = Codec.encodeText event value
                kv (string value) encoded

                kv
                    "  decoded"
                    (result (Codec.decodeText event encoded) (fun e -> string (Codec.encodeText event e = encoded)))

                kv "  preview" (Codec.preview event Limits.defaults value).Text
                kv "  tight" (Codec.preview event tight value).Text
            for input in invalid do
                kv "invalid" (result (Codec.decodeText event input) (Codec.encodeText event))
            kv "schema" (Json.serialize (Schema.toJsonSchema event.Schema))
            kv "definitions" (System.String.Join(", ", Schema.definitions event.Schema |> List.map string))
        ])

let largeRecord =
    Scenario.lines "contracts-large-record" (fun () ->
        let encoded = Codec.encodeText settings sampleSettings

        [
            kv "encoded" encoded
            kv
                "decoded"
                (result (Codec.decodeText settings encoded) (fun s -> string (Codec.encodeText settings s = encoded)))
            kv "missing" (result (Codec.decodeText settings "{}") (Codec.encodeText settings))
            kv
                "wrong"
                (result
                    (Codec.decodeText
                        settings
                        (encoded.Replace("\"f02\":20", "\"f02\":5000").Replace("\"f09\":\"trace\"", "\"f09\":\"loud\"")))
                    (Codec.encodeText settings))
            kv "preview" (Codec.preview settings Limits.defaults sampleSettings).Text
            kv "tight" (Codec.preview settings tight sampleSettings).Text
            kv
                "type"
                (Schema.render (Schema.definitions settings.Schema |> List.find (fun d -> d.Name = "Settings")).Body)
            kv "schema" (Json.serialize (Schema.toJsonSchema settings.Schema))
        ])

let scenarios = [ largeUnion; largeRecord ]
