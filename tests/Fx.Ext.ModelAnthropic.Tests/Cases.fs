/// The wire-conformance cases of `anthropic.messages`, written by hand from Anthropic's documented
/// formats (no API key is needed): for each, the canonical request, the Messages API request it must
/// become (`sources/<case>.request.json`), what the API answers (an event stream in
/// `sources/<case>.sse`, or an error response), and the canonical outcome. They are kept as case
/// files in `cassettes/anthropic.messages/` (golden: `UPDATE_GOLDEN=1` rewrites them), where recorded
/// cassettes can join them (`LiveTests`), and the conformance suite runs on the files.
module Fx.Ext.ModelAnthropic.Tests.Cases

open System
open System.IO
open Fx.Contracts
open Fx.Effects
open Fx.Ext.ModelAnthropic
open Fx.Ext.ModelAnthropic.Tests.Support
open Fx.Models
open Fx.Models.Tests.Conformance.WireConformance

let endpoint = Provider.Endpoint

let directory = sourceDirectory (Path.Combine("cassettes", Provider.Family))

/// The request a case expects: its body is `sources/<file>`.
let recorded (file: string) : RecordedRequest =
    {
        Method = "POST"
        Url = endpoint + Request.MessagesPath
        Headers =
            [
                "content-type", "application/json"
                "accept", "text/event-stream"
                "anthropic-version", Provider.ApiVersion
            ]
        Body = RecordedBody.Json(json (source file))
    }

let private requestId = "request-id", "req_011CSHoEeqs5C35K2UUqR7Fy"

/// A streamed answer: `sources/<file>`.
let stream (file: string) : RecordedOutcome =
    RecordedOutcome.Response
        {
            Status = 200
            Headers = [ "content-type", "text/event-stream; charset=utf-8"; requestId ]
            Body = source file
            End = BodyEnd.Complete
        }

/// An error response with Anthropic's error body.
let error (status: int) (headers: (string * string) list) (kind: string) (message: string) (code: string option) =
    let details =
        match code with
        | Some code -> [ "details", Json.Object [ "error_code", Json.String code ] ]
        | None -> []

    RecordedOutcome.Response
        {
            Status = status
            Headers = [ "content-type", "application/json"; requestId ] @ headers
            Body =
                Json.serialize (
                    Json.Object
                        [
                            "type", Json.String "error"
                            "error", Json.Object([ "type", Json.String kind; "message", Json.String message ] @ details)
                            "request_id", Json.String(snd requestId)
                        ]
                )
            End = BodyEnd.Complete
        }

let private case (name: string) (chat: ChatRequest) (file: string) (outcome: RecordedOutcome) (expect: Expected) =
    {
        Name = name
        Endpoint = endpoint
        Request = chat
        Cassette =
            {
                Interactions =
                    [
                        {
                            Request = recorded file
                            Outcome = outcome
                        }
                    ]
            }
        Expect = expect
        FollowUp = None
    }

/// A case whose response is replayed in a follow-up request: `sources/<name>.followup.*`.
let private followedBy (messages: Message list) (case: WireCase) : WireCase =
    { case with
        Cassette =
            {
                Interactions =
                    case.Cassette.Interactions
                    @ [
                        {
                            Request = recorded $"{case.Name}.followup.request.json"
                            Outcome = stream $"{case.Name}.followup.sse"
                        }
                    ]
            }
        FollowUp =
            Some
                {
                    Messages = messages
                    StatesVerbatim = true
                }
    }

let private streamed (name: string) (chat: ChatRequest) (response: ChatResponse) =
    case name chat $"{name}.request.json" (stream $"{name}.sse") (Expected.Response response)

let private response (id: string) (model: ModelRef) (parts: Part list) (stop: StopReason) (usage: Usage) =
    {
        Id = Some id
        Model = Some model.Model
        Parts = parts
        Stop = stop
        Usage = usage
    }

let private usage (input: int) (output: int) =
    { Usage.zero with
        Input = input
        Output = output
    }

let private tool (name: string) (description: string) (parameters: string) : ToolSpec =
    {
        Name = name
        Description = description
        Parameters = json parameters
        Strict = false
    }

let private weather =
    tool
        "get_weather"
        "The current weather in a city."
        """{"type": "object", "properties": {"location": {"type": "string"}, "unit": {"type": "string", "enum": ["celsius", "fahrenheit"]}}, "required": ["location"]}"""

let private readFile =
    tool
        "read_file"
        "Reads a file."
        """{"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}"""

let private editFile =
    tool
        "edit_file"
        "Replaces a file's text."
        """{"type": "object", "properties": {"path": {"type": "string"}, "text": {"type": "string"}}, "required": ["path", "text"]}"""

let private runCommand =
    tool
        "run_command"
        "Runs a shell command in the repository."
        """{"type": "object", "properties": {"command": {"type": "string"}}, "required": ["command"]}"""

let private call (id: string) (name: string) (arguments: string) : Part =
    Part.ToolCall
        {
            Id = id
            Name = name
            Arguments = arguments
            State = None
        }

let private result (id: string) (name: string) (text: string) (isError: bool) : Part =
    Part.ToolResult
        {
            CallId = id
            Name = name
            Content = [ ToolContent.Text text ]
            IsError = isError
        }

let private reasoning (model: ModelRef) (text: string) (kind: string) (data: string) : Part =
    Part.Reasoning
        {
            Text = text
            State = Some(state model kind data)
        }

let private reason (effort: Effort) (visible: bool) =
    Some
        {
            Effort = Some effort
            BudgetTokens = None
            Visible = visible
        }

let signature1 =
    "YkRYCjRqz12BSkjc9lvNq1UwCIyiaMMdxYZpRCB+iTgJ3vXDv1exumjdcDce0kSacj2P/kScquAOHqW1CcQXTdejVczwxl5AlGplyMKTV3sS3F7CLdj3N0OMU8BgEYL8/bI166scaJn6GpzcPYeNFmsd8s5/CcdLoHG+9vDmQVCcX2o/EpHRiFqPvOWxlubGt8uENrAiFaNU8Zn4cbVWk+oVaM2lPUR5yetY9ZdatLHB4RIy"

let signature2 =
    "DpgvvyuAvk4ZcHZ1SiBPtwDhl9R1HyWQOM8S/blWr9Lsvh5s5jIzIFWcbjrrOI4ffR8mcbDHpszHXG2QYMpJIWKr34krBs2IobCzrco2EITSRIKkUDbCTDZNuVSatAExRZEYFA5SZFHmUocTH4nXi5YVFoaeZq4cCTj+QNnAVuf1p9ygq4KAg/R6gpdM15oSTtxIaj6MThK0nYcLx8fnuQAA5I7ZbTYWUgQ/pPmzGwFJbyjz"

let signature3 =
    "EBRQ15ruemhJMWup63/5BJNRDr4CqDK+mba4jV6q8r4TYIK9ra7H3h0XG2yc3J5tFXPMO840o7tNLw+bhDrJhV2PhT/WasTepXwLDKP0cTjMkpHorsuFRVdpq/bgs2eKUWooOS1OFbrvo1UdDGJxc4R8hXX/VN3ZGSwGYOw9ptK0rnXbi2MNrhnBgNekDr5TmfOVX8c0Znvz94HeAZWZhU4JOX1+vC8vver1rWMccWSW0BXP"

let redacted1 =
    "xZrPybeJCMNzkigJ5QZhOMy1jPsPcdOw6ycLntEIOkUQXPadvgfyt0s4Q46WNK+rEwhe8DcYmqZEPS67NJ5QDMqhmmnLNDSfY+g35lIRtd7S4alxiGnkZSXrp18BUnTE6dFLfDem4WbMPfSTihJJlrpWQ3LAO4fInm4cDbWQQYOckPk+Q3Dx07N2UCHuCqbO4H3lNktOvT9+f1CVIvAaNH1MeG7sctqJfrFoW3W7fGcn7ZQ8"

/// A request every error case shares.
let simple: ChatRequest =
    { ChatRequest.create opus [ Message.userText "Hi" ] with
        MaxOutputTokens = Some 256
    }

let private failing (name: string) (outcome: RecordedOutcome) (expected: ChatError) =
    case name simple "simple.request.json" outcome (Expected.Failure expected)

let private textCase (name: string) (chat: ChatRequest) (id: string) (text: string) (stop: StopReason) (used: Usage) =
    streamed name chat (response id chat.Model [ Part.text text ] stop used)

/// Every case.
let all: WireCase list =
    [
        textCase
            "text"
            { ChatRequest.create opus [ Message.userText "Say hello in Polish." ] with
                System = [ Part.text "Be brief."; Part.CacheBreakpoint CacheTtl.Short ]
                MaxOutputTokens = Some 1024
                // Only the user id travels (the Messages API has no other metadata).
                Metadata = [ "user_id", "user-7f3a"; "session", "s-1" ]
            }
            "msg_01HCDu5LRGeP2o7s2xGmxyx8"
            "Cześć! 👋 W czym mogę pomóc?"
            StopReason.EndTurn
            (usage 21 12)

        // Thinking, text and a tool call; the follow-up replays the signature and the call.
        (let chat =
            { ChatRequest.create opus [ Message.userText "What's the weather in Kraków?" ] with
                Tools = [ weather ]
                Reasoning = reason Effort.High true
                MaxOutputTokens = Some 16000
            }

         streamed
             "thinking-tool-use"
             chat
             (response
                 "msg_01XFDUDYJgAACzvnptvVoYEL"
                 opus
                 [
                     reasoning
                         opus
                         "The user wants the weather in Kraków.\n\nI should call get_weather with \"Kraków\" in celsius."
                         Request.SignatureKind
                         signature1
                     Part.text "Let me check the weather in Kraków for you."
                     call
                         "toolu_01T1x1fJ34qAmk2tNTrN7Up6"
                         "get_weather"
                         "{\"location\": \"Kraków\", \"unit\": \"celsius\"}"
                 ]
                 StopReason.ToolUse
                 { usage 472 89 with Reasoning = 41 })
         |> followedBy
             [
                 Message.user
                     [
                         result "toolu_01T1x1fJ34qAmk2tNTrN7Up6" "get_weather" "4 °C, light snow" false
                     ]
             ])

        // Redacted thinking and omitted thinking (empty text), both replayed byte for byte.
        (let chat =
            { ChatRequest.create sonnet [ Message.userText "What changed in the working tree?" ] with
                Tools = [ runCommand ]
                Reasoning = reason Effort.Medium false
                MaxOutputTokens = Some 32000
            }

         streamed
             "redacted-thinking"
             chat
             (response
                 "msg_01KbsTMqD8KTUpWmQzJZnvGW"
                 sonnet
                 [
                     reasoning sonnet "" Request.RedactedKind redacted1
                     reasoning sonnet "" Request.SignatureKind signature2
                     call "toolu_01ABQmGsdx5aXXDpgxp8ewjv" "run_command" "{\"command\": \"git status --short\"}"
                 ]
                 StopReason.ToolUse
                 { usage 318 152 with Reasoning = 118 })
         |> followedBy
             [
                 Message.user
                     [
                         result "toolu_01ABQmGsdx5aXXDpgxp8ewjv" "run_command" " M src/main.fs\n?? notes.md" false
                     ]
             ])

        // Parallel calls; the follow-up's results (one an error) go before the user's text, and the
        // model thinks again between tool calls (interleaved thinking, then a progress update).
        (let chat =
            { ChatRequest.create fable [ Message.userText "Rename x to count in src/main.fs." ] with
                Tools = [ readFile; editFile ]
                Reasoning = reason Effort.Max true
                MaxOutputTokens = Some 64000
            }

         streamed
             "interleaved-thinking"
             chat
             (response
                 "msg_01Vq7mJ2wFh1hCxT8R4ZbN6s"
                 fable
                 [
                     reasoning fable "Read both files before changing anything." Request.SignatureKind signature3
                     call "toolu_01AaReadMainFs00000000001" "read_file" "{\"path\": \"src/main.fs\"}"
                     call "toolu_01BbReadTestsFs0000000002" "read_file" "{\"path\": \"tests/main.fs\"}"
                 ]
                 StopReason.ToolUse
                 { usage 1210 97 with Reasoning = 38 })
         |> followedBy
             [
                 Message.user
                     [
                         Part.text "There are no tests yet."
                         result "toolu_01AaReadMainFs00000000001" "read_file" "let x = 1" false
                         result "toolu_01BbReadTestsFs0000000002" "read_file" "no such file: tests/main.fs" true
                     ]
             ])

        // Five breakpoints: the earliest (after the tools) is dropped, two on one block are one, and
        // 1-hour ones after a 5-minute one become 5-minute ones. Usage counts cache reads and writes
        // as input.
        textCase
            "cache-usage"
            { ChatRequest.create
                  sonnet
                  [
                      Message.user
                          [
                              Part.text "Repository map: src/Program.fs, src/Library.fs, tests/Tests.fs."
                              Part.CacheBreakpoint CacheTtl.Short
                          ]
                      Message.assistant [ Part.text "Noted."; Part.CacheBreakpoint CacheTtl.Short ]
                      Message.user
                          [
                              Part.CacheBreakpoint CacheTtl.Long
                              Part.text "Which file defines main?"
                              Part.CacheBreakpoint CacheTtl.Long
                          ]
                  ] with
                System = [ Part.text "You are a coding agent."; Part.CacheBreakpoint CacheTtl.Long ]
                Tools = [ readFile ]
                // The Messages API has no cache key: it is left out.
                Cache =
                    {
                        Key = Some "conversation-1"
                        Tools = Some CacheTtl.Long
                    }
                MaxOutputTokens = Some 4096
            }
            "msg_01Ce5Jd4u5BmPvFMhAzSgWkX"
            "`src/Program.fs` defines `main`."
            StopReason.EndTurn
            {
                Input = 4212
                Output = 15
                CacheRead = 2400
                CacheWrite = 1800
                Reasoning = 0
            }

        textCase
            "media"
            { ChatRequest.create
                  haiku
                  [
                      Message.user
                          [
                              Part.text "Describe the image and the document."
                              Part.Media
                                  {
                                      Kind = MediaKind.Image
                                      MediaType = "image/png"
                                      Source =
                                          MediaSource.Base64
                                              "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="
                                      Name = None
                                  }
                              Part.Media
                                  {
                                      Kind = MediaKind.Pdf
                                      MediaType = "application/pdf"
                                      Source = MediaSource.Url "https://example.com/invoice.pdf"
                                      Name = Some "invoice.pdf"
                                  }
                          ]
                  ] with
                MaxOutputTokens = Some 2048
            }
            "msg_01MdX2nB7uY3pQ9sVwK4cR1e"
            "A red square; the PDF is a one-page invoice."
            StopReason.EndTurn
            (usage 1650 14)

        textCase
            "structured-output"
            { ChatRequest.create
                  opus
                  [
                      Message.userText "Extract the contact: John Smith (john@example.com) asked for a demo."
                  ] with
                Output =
                    OutputFormat.Json(
                        "contact",
                        json
                            """{"type": "object", "properties": {"name": {"type": "string"}, "email": {"type": "string"}}, "required": ["name", "email"], "additionalProperties": false}""",
                        true
                    )
                MaxOutputTokens = Some 1024
            }
            "msg_01StRu7cTuReDoUtPuT00001"
            "{\"name\": \"John Smith\", \"email\": \"john@example.com\"}"
            StopReason.EndTurn
            (usage 88 17)

        // A forced call of a tool without parameters: its input never streams, so it is `{}`.
        (let chat =
            { ChatRequest.create haiku [ Message.userText "What is in this directory?" ] with
                Tools =
                    [
                        tool
                            "list_files"
                            "Lists the files of the working directory."
                            """{"type": "object", "properties": {}}"""
                    ]
                ToolChoice = ToolChoice.Tool "list_files"
                ParallelToolCalls = Some false
                MaxOutputTokens = Some 512
            }

         streamed
             "forced-tool"
             chat
             (response
                 "msg_01FoRcEdToOlUsE000000001"
                 haiku
                 [ call "toolu_01DdListFiles000000000004" "list_files" "{}" ]
                 StopReason.ToolUse
                 (usage 402 11)))

        // API version 2026-10-21 takes no temperature: it is left out.
        textCase
            "stop-sequence"
            { ChatRequest.create opus [ Message.userText "List the steps." ] with
                Sampling =
                    { Sampling.none with
                        Stop = [ "3."; "###" ]
                        Temperature = Some 0.2
                    }
                MaxOutputTokens = Some 256
            }
            "msg_01StOpSeQuEnCe0000000001"
            "1. Plan\n2. Build\n"
            (StopReason.StopSequence(Some "3."))
            (usage 14 10)

        case
            "max-tokens"
            simple
            "simple.request.json"
            (stream "max-tokens.sse")
            (Expected.Response(
                response
                    "msg_01MaXtOkEnS0000000000001"
                    opus
                    [ Part.text "Hello! It is a pleasure to meet you. There is a great deal" ]
                    StopReason.MaxTokens
                    (usage 9 256)
            ))

        case
            "context-window"
            simple
            "simple.request.json"
            (stream "context-window.sse")
            (Expected.Response(
                response
                    "msg_01CoNtExTwInDoW000000001"
                    opus
                    [ Part.text "In summary, the" ]
                    StopReason.ContextWindow
                    (usage 999_990 10)
            ))

        // A streaming classifier's refusal: a normal stop, with what came before it.
        case
            "refusal"
            simple
            "simple.request.json"
            (stream "refusal.sse")
            (Expected.Response(
                response "msg_01ReFuSaL00000000000001" opus [ Part.text "Here is how" ] StopReason.Refusal (usage 30 3)
            ))

        case
            "overloaded-mid-stream"
            simple
            "simple.request.json"
            (stream "overloaded-mid-stream.sse")
            (Expected.Failure(
                Overloaded
                    {
                        RetryAfter = None
                        Message = "Overloaded"
                    }
            ))

        failing
            "invalid-request"
            (error
                400
                []
                "invalid_request_error"
                "messages.0.content.0.text: text content blocks must be non-empty"
                None)
            (InvalidRequest "messages.0.content.0.text: text content blocks must be non-empty")

        (let message = "prompt is too long: 1048577 tokens > 1000000 maximum"

         failing
             "context-overflow"
             (error 400 [] "invalid_request_error" message None)
             (ContextOverflow
                 {
                     Tokens = Some 1_048_577
                     Limit = Some 1_000_000
                     Message = message
                 }))

        (let message =
            "Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits."

         failing "credit-balance" (error 400 [] "invalid_request_error" message None) (QuotaExhausted message))

        // The monthly spend cap: a 429 that waiting does not fix.
        (let message =
            "You have reached your API usage limits. You will regain access on 2026-12-01 at 00:00 UTC."

         failing
             "spend-cap"
             (error 429 [] "rate_limit_error" message (Some "enforced_spend_limit_reached"))
             (QuotaExhausted message))

        failing
            "unauthorized"
            (error 401 [] "authentication_error" "invalid x-api-key" None)
            (AuthFailed "invalid x-api-key")

        failing
            "billing"
            (error 402 [] "billing_error" "Your payment method was declined." None)
            (QuotaExhausted "Your payment method was declined.")

        failing
            "permission"
            (error 403 [] "permission_error" "Your API key does not have permission to use the specified resource." None)
            (PermissionDenied "Your API key does not have permission to use the specified resource.")

        failing
            "not-found"
            (error 404 [] "not_found_error" "model: claude-opus-9" None)
            (NotFound "model: claude-opus-9")

        failing
            "request-too-large"
            (error 413 [] "request_too_large" "Request exceeds the maximum allowed number of bytes." None)
            (ContextOverflow
                {
                    Tokens = None
                    Limit = None
                    Message = "Request exceeds the maximum allowed number of bytes."
                })

        (let message =
            "This request would exceed your organization's rate limit of 400,000 input tokens per minute."

         failing
             "rate-limited"
             (error
                 429
                 [ "retry-after", "30"; "anthropic-ratelimit-input-tokens-remaining", "0" ]
                 "rate_limit_error"
                 message
                 None)
             (RateLimited
                 {
                     RetryAfter = Some(TimeSpan.FromSeconds 30.0)
                     Message = message
                 }))

        failing
            "server-error"
            (error 500 [ "x-should-retry", "true" ] "api_error" "Internal server error" None)
            (ServerError(500, "Internal server error"))

        failing
            "timeout"
            (error 504 [] "timeout_error" "Request timed out while processing." None)
            (ServerError(504, "Request timed out while processing."))

        failing
            "overloaded"
            (error 529 [] "overloaded_error" "Overloaded" None)
            (Overloaded
                {
                    RetryAfter = None
                    Message = "Overloaded"
                })

        // A proxy's page: the status decides.
        failing
            "bad-gateway"
            (RecordedOutcome.Response
                {
                    Status = 502
                    Headers = [ "content-type", "text/html" ]
                    Body = "<html><body><h1>502 Bad Gateway</h1></body></html>\n"
                    End = BodyEnd.Complete
                })
            (ServerError(502, "<html><body><h1>502 Bad Gateway</h1></body></html>"))
    ]

/// The file of a case.
let path (case: WireCase) =
    Path.Combine(directory, case.Name + ".json")
