module Fx.Models.Tests.CodecTests

open System
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Models.Tests.Support
open Fx.Models.Tests.Samples

/// Encodes to text, reads it back, and expects the same value and the same text again.
let private roundTrip (codec: Codec<'T>) (value: 'T) =
    let text = Codec.encodeText codec value

    match Codec.decodeText codec text with
    | Ok decoded ->
        Expect.equal decoded value $"decoded {text}"
        Expect.equal (Codec.encodeText codec decoded) text "the same text again"
    | Error issues -> failtest $"{text} does not decode: {Issue.renderAtMost 10 issues}"

[<Tests>]
let tests =
    testList
        "Codecs"
        [
            test "a request with every part and option round-trips" { roundTrip ChatRequest.codec request }

            test "a minimal request is small and reads with defaults" {
                let minimal = Support.request [ Message.userText "hi" ]

                Expect.equal
                    (Codec.encodeText ChatRequest.codec minimal)
                    """{"model":"example/echo-1","system":[],"messages":[{"role":"user","parts":[{"kind":"text","text":"hi"}]}],"tools":[],"toolChoice":{"kind":"auto"},"sampling":{"stop":[]},"output":{"kind":"text"},"cache":{},"metadata":{}}"""
                    "encoded"

                Expect.equal
                    (Codec.decodeText ChatRequest.codec """{"model":"example/echo-1","messages":[]}""")
                    (Ok(Support.request []))
                    "defaults"
            }

            test "every event round-trips" {
                for event in events do
                    roundTrip ChatEvent.codec event
            }

            test "every error, stop reason and the response round-trip" {
                for error in errors do
                    roundTrip ChatError.codec error

                for stop in stops do
                    roundTrip StopReason.codec stop

                roundTrip ChatResponse.codec response

                roundTrip
                    ChatFailure.codec
                    {
                        Error = QuotaExhausted "credits"
                        Id = Some "x"
                        Model = None
                        Parts = response.Parts
                        Usage = usage
                    }
            }

            test "model info and provider definitions round-trip" {
                roundTrip ModelInfo.codec info
                roundTrip ProviderDef.codec provider
                roundTrip Replay.codec Replay.defaults
            }

            test "provider state survives byte for byte, odd characters included" {
                for data in
                    [
                        ""
                        "plain"
                        "with \"quotes\" and \\ and \n"
                        "\u0000\u001f"
                        "🦀ż"
                        "\ud800 lone"
                        String('x', 100_000)
                    ] do
                    let part = Part.Reasoning { Text = ""; State = Some(state data) }
                    let text = Codec.encodeText Part.codec part

                    match Codec.decodeText Part.codec text with
                    | Ok(Part.Reasoning { State = Some decoded }) ->
                        Expect.isTrue (String.Equals(decoded.Data, data, StringComparison.Ordinal)) "the same data"
                    | other -> failtest $"decoded {other}"
            }

            test "model refs are provider/model" {
                Expect.equal (ModelRef.tryParse "openai/gpt-5.1") (Ok(ModelRef.create "openai" "gpt-5.1")) "plain"

                Expect.equal
                    (ModelRef.tryParse "bedrock/anthropic.claude/v1:0")
                    (Ok(ModelRef.create "bedrock" "anthropic.claude/v1:0"))
                    "the model may have slashes"

                for bad in [ "nomodel"; "/x"; "x/"; "Open AI/x" ] do
                    Expect.isError (ModelRef.tryParse bad) bad
            }

            test "the wrong shapes are model-readable issues" {
                match Codec.decodeText ChatEvent.codec """{"kind":"delta","index":-1}""" with
                | Error issues ->
                    let text = Issue.renderAtMost 10 issues
                    Expect.stringContains text "$.index" "the index"
                    Expect.stringContains text "$.text" "the missing text"
                | Ok e -> failtest $"decoded {e}"
            }

            test "ToolSpec.ofCodec takes the JSON Schema of a codec" {
                let input =
                    Codec.record {
                        let! path = Codec.field "path" fst Codec.string
                        and! line = Codec.optionalField "line" snd Codec.int
                        return path, line
                    }

                let spec = ToolSpec.ofCodec "read" "Reads a file." input
                Expect.equal spec.Parameters (Schema.toJsonSchema input.Schema) "the schema"
            }

            test "errors say what to do about them" {
                let retryable = errors |> List.filter ChatError.isRetryable |> List.map string

                Expect.equal
                    retryable
                    [
                        "rate limited: slow down (retry after 7.5 s)"
                        "overloaded: busy"
                        "server error 502: bad gateway"
                        "transport: connection reset: the connection broke off"
                        "transport: timed out waiting for the body"
                        "invalid response: garbage"
                    ]
                    "retryable"

                Expect.equal (errors |> List.filter ChatError.needsCompaction |> List.length) 1 "compaction"
                Expect.equal (ChatError.retryAfter errors.Head) (Some(TimeSpan.FromSeconds 7.5)) "retry after"
            }

            test "usage costs what its parts cost" {
                let cost = Usage.cost info.Cost.Value usage
                // 472 uncached × $3, 2048 cache reads × $0.30, 89 output × $15, per million tokens.
                Expect.equal cost (472m * 3m / 1_000_000m + 2048m * 0.3m / 1_000_000m + 89m * 15m / 1_000_000m) "cost"
            }

            test "the canonical JSON of the samples (golden)" {
                let document =
                    Json.Object
                        [
                            "request", ChatRequest.codec.Encode request
                            "events", Json.Array(events |> List.map ChatEvent.codec.Encode)
                            "response", ChatResponse.codec.Encode response
                            "errors", Json.Array(errors |> List.map ChatError.codec.Encode)
                            "model", ModelInfo.codec.Encode info
                            "provider", ProviderDef.codec.Encode provider
                        ]

                golden "samples.json" (Json.serializeIndented document)
            }

            test "the JSON Schemas of requests and events (golden)" {
                let document =
                    Json.Object
                        [
                            "ChatRequest", Schema.toJsonSchema ChatRequest.codec.Schema
                            "ChatEvent", Schema.toJsonSchema ChatEvent.codec.Schema
                        ]

                golden "schemas.json" (Json.serializeIndented document)
            }
        ]
