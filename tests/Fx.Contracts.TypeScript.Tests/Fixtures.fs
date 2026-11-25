/// JSON shared by the F# codecs and the generated TypeScript types: values the codecs write, which
/// must type-check, and values the codecs reject for their shape, which must not. `golden/fixtures.ts`
/// holds them; `bun scripts/declarations.ts` type-checks it against the golden declarations.
module Fx.Contracts.TypeScript.Tests.Fixtures

open System
open Fx
open Fx.Contracts
open Fx.Variants
open Fx.Contracts.TypeScript.Tests.Samples

/// A JSON value, the TypeScript type it is checked against, and whether the F# side accepts it.
type Fixture =
    {
        Name: string
        /// A type expression in `fixtures.ts`, e.g. `domain.Match`.
        Type: string
        Json: Json
        /// Whether the TypeScript type must accept the value (and the codec decode it).
        Expected: bool
        /// Whether the codec decoded it.
        Decoded: bool
        /// Why a rejected value is wrong.
        Reason: string
    }

    override this.ToString() = $"{this.Name}: {this.Type}"

let private parse (text: string) : Json =
    match Json.parse text with
    | Ok json -> json
    | Error error -> failwith $"invalid fixture JSON: {error}"

/// What `codec` writes for `value`.
let accepted (name: string) (tsType: string) (codec: Codec<'T>) (value: 'T) : Fixture =
    let json = codec.Encode value

    {
        Name = name
        Type = tsType
        Json = json
        Expected = true
        Decoded = Result.isOk (codec.Decode json)
        Reason = ""
    }

/// What `codec` writes after reading `text` (for values that are easier to write as JSON).
let rewritten (name: string) (tsType: string) (codec: Codec<'T>) (text: string) : Fixture =
    match codec.Decode(parse text) with
    | Ok value -> accepted name tsType codec value
    | Error issues -> failwith $"fixture {name} does not decode: {Issue.render issues}"

/// JSON that `decodes` rejects, for `reason`.
let rejectedBy (name: string) (tsType: string) (decodes: Json -> bool) (text: string) (reason: string) : Fixture =
    let json = parse text

    {
        Name = name
        Type = tsType
        Json = json
        Expected = false
        Decoded = decodes json
        Reason = reason
    }

let rejected (name: string) (tsType: string) (codec: Codec<'T>) (text: string) (reason: string) : Fixture =
    rejectedBy name tsType (codec.Decode >> Result.isOk) text reason

/// Whether a config file's `variants` resolve against the core slots.
let private resolvesConfig (json: Json) : bool =
    let config = Json.Object [ "variants", json ]

    match Selections.ofConfig (Source.create Layer.Host "fixture") config with
    | Ok selections -> Result.isOk (Resolution.resolve coreSlots [] [ selections ])
    | Error _ -> false

let private kernelType (name: string) = Product.kernelGlobal + "." + name

let private sampleMatch =
    {
        Path = "src/Retry.fs"
        Line = 14
        Text = "let retry n f ="
        Note = Some "deprecated"
        Tags = [ "http"; "retry" ]
        Score = 0.75
        Weight = 0.5m
        Sequence = 9007199254740993L
        Span = Some { Start = 14; End = 31 }
        Priority = High
        Counts = Map.ofList [ "calls", 37; "files", 14 ]
    }

let all: Fixture list =
    [
        accepted "priority" "domain.Priority" priority Medium
        accepted "span" "domain.Span" span { Start = 1; End = 3 }
        accepted "fullMatch" "domain.Match" matchCodec sampleMatch
        accepted
            "sparseMatch"
            "domain.Match"
            matchCodec
            { sampleMatch with
                Note = None
                Tags = []
                Span = None
                Counts = Map.empty
            }
        accepted "done" "domain.TurnResult" turnResult (Done("Migrated.", [ sampleMatch ], Some "dotnet test"))
        accepted "doneWithout" "domain.TurnResult" turnResult (Done("Migrated.", [], None))
        accepted "needInput" "domain.TurnResult" turnResult (NeedInput [ "Which retry policy?" ])
        accepted "waiting" "domain.TurnResult" turnResult (Waiting([ "job-1" ], "CI"))
        accepted "moved" "domain.TurnResult" turnResult (Moved { Start = 2; End = 4 })
        accepted "cancelled" "domain.TurnResult" turnResult Cancelled
        accepted "tree" "domain.Tree" tree (Node("root", [ Leaf 1; Node("inner", [ Leaf 2 ]); Node("empty", []) ]))
        accepted
            "issue"
            "contracts.Issue"
            Codecs.issue
            {
                Path = [ PathSegment.Key "items"; PathSegment.Index 2 ]
                Message = "expected string, got number 42"
            }
        accepted "limits" "contracts.Limits" Codecs.limits Limits.defaults
        accepted
            "preview"
            "contracts.Preview"
            Codecs.preview
            {
                Text = "[1, 2, …+8]"
                Truncated = true
            }
        rewritten
            "blob"
            "store.BlobRef"
            Fx.Store.Codecs.blobRef
            $"""{{"hash": "sha256:{String('a', 64)}", "size": 3}}"""
        rewritten
            "recorded"
            "store.RecordedEvent"
            (Fx.Store.Codecs.recorded Codec.json)
            """{"stream": "chat/1", "seq": 4, "event": {"kind": "said", "text": "hi"}, "at": 1700000000000000,
                "by": {"principal": "user:alice", "via": "tui"}, "key": "k-4", "cause": {"stream": "chat/1", "seq": 3}}"""
        rewritten
            "recordedFirst"
            "store.RecordedEvent"
            (Fx.Store.Codecs.recorded Codec.json)
            """{"stream": "chat/1", "seq": 1, "event": null, "at": 0, "by": {"principal": "host", "via": "cli"}, "key": "k-1"}"""
        rewritten
            "slot"
            "variants.SlotDecl"
            SlotInfo.declCodec
            """{"id": "acme.mode", "description": "A mode.", "default": "fast", "choices": ["fast", "thorough"]}"""
        accepted "resolved" "variants.ResolvedVariant" Resolved.codec (Resolution.defaults coreSlots).Values.Head
        accepted "reads" "variants.VariantReads" Variants.readsCodec (Resolution.defaults coreSlots).Values
        accepted
            "profile"
            "variants.Profile"
            Profile.codec
            (Profile.ofResolution "0.1.0" (Resolution.defaults coreSlots)
             |> Profile.withExtension
                 "acme.tools"
                 {
                     Version = "1.2.0"
                     Hash = Some("sha256:" + String('a', 64))
                 }
             |> Profile.withConformance "acme.interpreter" "kernel" { Passed = 41; Failed = 0; Skipped = 2 }
             |> Profile.withLockfile ("sha256:" + String('b', 64)))
        rewritten
            "policy"
            "variants.VariantPolicy"
            (Policy.codec "fixture")
            """{"pin": {"edit.guard": "require"}, "forbid": {"agent.actionSurface": ["hybrid"]}}"""
        {
            Name = "config"
            Type = "variants.VariantsConfig"
            Json =
                parse
                    """{"edit.guard": "require", "values.previewLimits": {"maxChars": 400, "maxItems": 5, "maxDepth": 2, "maxStringChars": 60},
                        "model.role.main": "anthropic/claude-opus", "model.role.fast": null}"""
            Expected = true
            Decoded = false
            Reason = ""
        }
        |> fun f ->
            { f with
                Decoded = resolvesConfig f.Json
            }
        accepted "kernelResult" (kernelType "TurnResult") turnResult (Done("Done.", [], Some "bun test"))
        accepted
            "finishResult"
            (kernelType "FinishResult")
            finishResult
            (false,
             [
                 {
                     Path = [ PathSegment.Key "summary" ]
                     Message = "missing required field"
                 }
             ])
        accepted
            "shellResult"
            (kernelType "ShellResult")
            shellResult
            {
                ExitCode = 0
                Stdout = "ok\n"
                Lines = [ "ok" ]
            }

        rejected "badPriority" "domain.Priority" priority "\"urgent\"" "not one of the values"
        rejected "badSpan" "domain.Span" span """{"start": 1}""" "a required field is missing"
        rejected
            "badLine"
            "domain.Match"
            matchCodec
            (Json.serialize (matchCodec.Encode sampleMatch)
             |> fun t -> t.Replace("\"line\":14", "\"line\":\"14\""))
            "a number as a string"
        rejected "badTag" "domain.TurnResult" turnResult """{"kind": "don", "summary": "x"}""" "an unknown tag"
        rejected "noTag" "domain.TurnResult" turnResult """{"summary": "x"}""" "no tag"
        rejected "badDone" "domain.TurnResult" turnResult """{"kind": "done"}""" "a case without its required field"
        rejected
            "badCaseField"
            "domain.TurnResult"
            turnResult
            """{"kind": "needInput", "decisions": "which?"}"""
            "a string where the case has a list"
        rejected
            "badTree"
            "domain.Tree"
            tree
            """{"kind": "node", "label": "x", "children": [{"kind": "leaf"}]}"""
            "a nested case without its field"
        rejected
            "badIssue"
            "contracts.Issue"
            Codecs.issue
            """{"path": [true], "message": "m"}"""
            "a path segment that is neither a key nor an index"
        rejected
            "badLimits"
            "contracts.Limits"
            Codecs.limits
            """{"maxChars": 1, "maxItems": 1, "maxStringChars": 1}"""
            "a required field is missing"
        rejected
            "badBlob"
            "store.BlobRef"
            Fx.Store.Codecs.blobRef
            """{"hash": 1, "size": 3}"""
            "a number where a string goes"
        rejected
            "badProfile"
            "variants.Profile"
            Profile.codec
            """{"format": "1", "core": "0.1.0", "selections": {}, "extensions": {}, "conformance": {}}"""
            "a string where a number goes"
        rejectedBy
            "badConfig"
            "variants.VariantsConfig"
            resolvesConfig
            """{"edit.guard": "sometimes"}"""
            "not one of the slot's values"
        rejected
            "badKernelResult"
            (kernelType "TurnResult")
            turnResult
            """{"kind": "finished", "summary": "x"}"""
            "an unknown tag"
    ]

/// `fixtures.ts`: a module that imports the golden module declarations by namespace, uses the global
/// declarations, and declares one constant per fixture. Rejected values are on one line after
/// `@ts-expect-error`, so the type checker fails if it accepts them.
let render (fixtures: Fixture list) : string =
    let lines =
        [
            "// Generated by Fx.Contracts.TypeScript.Tests from the F# codecs: JSON they write, which must type-check,"
            "// and JSON they reject, which must not. Do not edit: run the tests with UPDATE_GOLDEN=1."
            ""
            "import type * as contracts from \"./contracts\";"
            "import type * as domain from \"./domain\";"
            "import type * as store from \"./store\";"
            "import type * as variants from \"./variants\";"
            for fixture in fixtures do
                ""

                if fixture.Expected then
                    $"export const {fixture.Name}: {fixture.Type} = {Json.serializeIndented fixture.Json};"
                else
                    $"// Rejected: {fixture.Reason}."
                    "// @ts-expect-error"
                    $"export const {fixture.Name}: {fixture.Type} = {Json.serialize fixture.Json};"
        ]

    String.Join("\n", lines) + "\n"
