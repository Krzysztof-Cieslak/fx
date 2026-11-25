/// The stream decoder on Anthropic's documented event streams: the example stream T118 keeps
/// (tests/Fx.Models.Tests/streams), events and blocks it does not know, usage, stop reasons, and
/// streams that break the format.
module Fx.Ext.ModelAnthropic.Tests.DecoderTests

open System
open System.IO
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Ext.ModelAnthropic
open Fx.Ext.ModelAnthropic.Tests.Support
open Fx.Models

let private sonnet45 = ModelRef.create Provider.Id "claude-sonnet-4-5"

let private context = ctx sonnet45 Provider.def

/// An event-stream text from `event: data` pairs.
let private sse (events: (string * string) list) : string =
    events
    |> List.map (fun (name, data) -> Sse.render (Sse.event name data))
    |> String.concat ""

let private start =
    "message_start",
    """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude-sonnet-4-5","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}"""

let private stop (reason: string) =
    [
        "message_delta",
        """{"type":"message_delta","delta":{"stop_reason":"REASON","stop_sequence":null},"usage":{"output_tokens":5}}"""
            .Replace("REASON", reason)
        "message_stop", """{"type":"message_stop"}"""
    ]

let private text (index: int) (text: string) =
    [
        "content_block_start",
        """{"type":"content_block_start","index":INDEX,"content_block":{"type":"text","text":""}}"""
            .Replace("INDEX", string index)
        "content_block_delta",
        """{"type":"content_block_delta","index":INDEX,"delta":{"type":"text_delta","text":TEXT}}"""
            .Replace("INDEX", string index)
            .Replace("TEXT", Json.quote text)
        "content_block_stop", """{"type":"content_block_stop","index":INDEX}""".Replace("INDEX", string index)
    ]

let private outcome (events: ChatEvent list) =
    match fold events with
    | Ok response -> response
    | Error failure -> failtest $"failed: {failure}"

let private failure (events: ChatEvent list) =
    match fold events with
    | Error failure -> failure.Error
    | Ok response -> failtest $"expected a failure, got {response}"

[<Tests>]
let tests =
    testList
        "decoder"
        [
            test "the documented stream: thinking with its signature, text, a tool call" {
                let text =
                    File.ReadAllText(
                        Path.Combine(root, "tests", "Fx.Models.Tests", "streams", "anthropic-messages.sse")
                    )

                let events = decode context text
                Expect.isEmpty (ChatEvents.check events) "the stream rules"
                let response = outcome events

                Expect.equal response.Id (Some "msg_01XFDUDYJgAACzvnptvVoYEL") "id"
                Expect.equal response.Model (Some "claude-sonnet-4-5") "model"
                Expect.equal response.Stop StopReason.ToolUse "stop"

                Expect.equal
                    response.Parts
                    [
                        Part.Reasoning
                            {
                                Text = "The user wants the weather in Kraków. I should call get_weather."
                                State =
                                    Some(
                                        state
                                            sonnet45
                                            Request.SignatureKind
                                            "EqQBCgIYAhIM1gbcDa9GJwZA2b3hGgxBdjrkzLoky3dl1pkiMOYds1pBEXAMPLEonly+/="
                                    )
                            }
                        Part.text "Let me check the weather in Kraków for you."
                        Part.ToolCall
                            {
                                Id = "toolu_01T1x1fJ34qAmk2tNTrN7Up6"
                                Name = "get_weather"
                                Arguments = "{\"location\": \"Kraków\", \"unit\": \"celsius\"}"
                                State = None
                            }
                    ]
                    "parts"

                Expect.equal
                    response.Usage
                    {
                        Input = 2520
                        Output = 89
                        CacheRead = 2048
                        CacheWrite = 0
                        Reasoning = 0
                    }
                    "usage: cache reads are input"

                for size in [ 1; 2; 3; 5; 17; 64 ] do
                    Expect.equal
                        (ChatEvents.coalesce (decodeIn size context text))
                        (ChatEvents.coalesce events)
                        $"cut every {size} bytes"
            }

            test "state is stamped with the call's family and model" {
                let events =
                    decode
                        (ctx opus Provider.def)
                        (sse
                            [
                                start
                                "content_block_start",
                                """{"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"RRR"}}"""
                                "content_block_stop", """{"type":"content_block_stop","index":0}"""
                                yield! stop "end_turn"
                            ])

                Expect.contains events (ChatEvent.PartState(0, state opus Request.RedactedKind "RRR")) "state"
            }

            test "blocks, deltas and events it does not know are skipped, and parts stay numbered from 0" {
                let events =
                    decode
                        context
                        (sse
                            [
                                start
                                "content_block_start",
                                """{"type":"content_block_start","index":0,"content_block":{"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{}}}"""
                                "content_block_delta",
                                """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"query\": \"x\"}"}}"""
                                "content_block_stop", """{"type":"content_block_stop","index":0}"""
                                "content_block_start",
                                """{"type":"content_block_start","index":1,"content_block":{"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[]}}"""
                                "content_block_stop", """{"type":"content_block_stop","index":1}"""
                                "future_event", """{"type":"future_event","x":1}"""
                                let found = text 2 "Found it."
                                found[0]

                                "content_block_delta",
                                """{"type":"content_block_delta","index":2,"delta":{"type":"citations_delta","citation":{}}}"""

                                yield! List.tail found
                                "ping", """{"type": "ping"}"""
                                yield! stop "end_turn"
                            ])

                Expect.isEmpty (ChatEvents.check events) "rules"
                Expect.equal (outcome events).Parts [ Part.text "Found it." ] "parts"
                Expect.contains events (ChatEvent.PartStarted(0, PartStart.Text)) "part 0"
            }

            test "a call whose input arrives whole, and one whose input never streams" {
                let events =
                    decode
                        context
                        (sse
                            [
                                start
                                "content_block_start",
                                """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"a","input":{"n":1}}}"""
                                "content_block_stop", """{"type":"content_block_stop","index":0}"""
                                "content_block_start",
                                """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_2","name":"b","input":{}}}"""
                                "content_block_delta",
                                """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":""}}"""
                                "content_block_stop", """{"type":"content_block_stop","index":1}"""
                                yield! stop "tool_use"
                            ])

                Expect.equal
                    ((outcome events).Parts
                     |> List.choose (fun p ->
                         match p with
                         | Part.ToolCall c -> Some c.Arguments
                         | _ -> None))
                    [ "{\"n\":1}"; "{}" ]
                    "arguments"
            }

            test "usage: cumulative, cache reads and writes are input, thinking is reasoning, never lower" {
                let events =
                    decode
                        context
                        (sse
                            [
                                "message_start",
                                """{"type":"message_start","message":{"id":"msg_1","model":"m","content":[]}}"""
                                yield! text 0 "x"
                                "message_delta",
                                """{"type":"message_delta","delta":{},"usage":{"input_tokens":100,"cache_creation_input_tokens":20,"cache_read_input_tokens":30,"output_tokens":40,"output_tokens_details":{"thinking_tokens":25}}}"""
                                "message_delta",
                                """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":90,"output_tokens":45}}"""
                                "message_stop", """{"type":"message_stop"}"""
                            ])

                Expect.isEmpty (ChatEvents.check events) "rules"

                let reports =
                    events
                    |> List.choose (fun e ->
                        match e with
                        | ChatEvent.UsageUpdated u -> Some u
                        | _ -> None)

                Expect.equal
                    reports
                    [
                        {
                            Input = 150
                            Output = 40
                            CacheRead = 30
                            CacheWrite = 20
                            Reasoning = 25
                        }
                        {
                            Input = 150
                            Output = 45
                            CacheRead = 30
                            CacheWrite = 20
                            Reasoning = 25
                        }
                    ]
                    "reports"
            }

            test "stop reasons" {
                for reason, expected in
                    [
                        "end_turn", StopReason.EndTurn
                        "max_tokens", StopReason.MaxTokens
                        "refusal", StopReason.Refusal
                        "pause_turn", StopReason.Pause
                        "model_context_window_exceeded", StopReason.ContextWindow
                        "a_new_reason", StopReason.Other "a_new_reason"
                    ] do
                    let events = decode context (sse [ start; yield! text 0 "x"; yield! stop reason ])
                    Expect.equal (outcome events).Stop expected reason

                Expect.equal
                    (Decoder.stopReason "stop_sequence" (Some "##"))
                    (StopReason.StopSequence(Some "##"))
                    "sequence"
            }

            test "errors in the stream" {
                let error (body: string) =
                    failure (decode context (sse [ start; yield! text 0 "x"; "error", body ]))

                Expect.equal
                    (error """{"type":"error","error":{"type":"api_error","message":"Internal server error"}}""")
                    (ServerError(500, "Internal server error"))
                    "api error"

                Expect.equal
                    (error """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""")
                    (RateLimited
                        {
                            RetryAfter = None
                            Message = "slow down"
                        })
                    "rate limit"

                Expect.equal
                    (error """{"type":"error","error":{"type":"strange_error","message":"what"}}""")
                    (ServerError(500, "strange_error: what"))
                    "unknown"

                Expect.equal
                    (error
                        """{"type":"error","error":{"type":"rate_limit_error","message":"cap","details":{"error_code":"enforced_spend_limit_reached"}}}""")
                    (QuotaExhausted "cap")
                    "spend cap"

                match error """{"type":"error"}""" with
                | InvalidResponse _ -> ()
                | other -> failtest $"no error object: {other}"
            }

            test "streams that break the format are invalid responses" {
                let invalid (events: (string * string) list) =
                    match failure (decode context (sse events)) with
                    | InvalidResponse message -> message
                    | other -> failtest $"expected an invalid response, got {other}"

                invalid [ start; "content_block_delta", "not json" ] |> ignore

                invalid
                    [
                        start
                        "content_block_delta",
                        """{"type":"content_block_delta","index":4,"delta":{"type":"text_delta","text":"x"}}"""
                    ]
                |> ignore

                let open0 =
                    "content_block_start",
                    """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""

                let stop0 = "content_block_stop", """{"type":"content_block_stop","index":0}"""

                let delta0 =
                    "content_block_delta",
                    """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"x"}}"""

                Expect.stringContains (invalid [ start; open0; open0 ]) "started twice" "started twice"
                Expect.stringContains (invalid [ start; yield! text 0 "x"; open0 ]) "started twice" "index reused"
                Expect.stringContains (invalid [ start; stop0 ]) "never started" "stopped first"
                Expect.stringContains (invalid [ start; open0; stop0; stop0 ]) "stopped twice" "stopped twice"
                Expect.stringContains (invalid [ start; open0; stop0; delta0 ]) "after it stopped" "late delta"

                invalid [ start; yield! text 0 "x"; "message_stop", """{"type":"message_stop"}""" ]
                |> ignore

                invalid
                    [
                        start
                        "content_block_start",
                        """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","input":{}}}"""
                    ]
                |> ignore

                // Ended without message_stop: the driver says so.
                Expect.stringContains (invalid [ start; yield! text 0 "x" ]) "ended" "ended early"
            }
        ]
