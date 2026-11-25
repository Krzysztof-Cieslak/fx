/// The wire-conformance cases of `openai.responses`, written by hand from OpenAI's documented formats
/// (no API key is needed): for each, the canonical request, the Responses API request it must become
/// (`sources/<case>.request.json`), what the API answers (an event stream in `sources/<case>.sse`, or
/// an error response), and the canonical outcome. They are kept as case files in
/// `cassettes/openai.responses/` (golden: `UPDATE_GOLDEN=1` rewrites them), where recorded cassettes
/// can join them (`LiveTests`), and the conformance suite runs on the files.
module Fx.Ext.ModelOpenAI.Tests.Cases

open System
open System.IO
open Fx.Contracts
open Fx.Effects
open Fx.Ext.ModelOpenAI
open Fx.Ext.ModelOpenAI.Tests.Support
open Fx.Models
open Fx.Models.Tests.Conformance.WireConformance

let endpoint = Provider.Endpoint

let directory = sourceDirectory (Path.Combine("cassettes", Provider.Family))

/// The request a case expects: its body is `sources/<file>`.
let recorded (file: string) : RecordedRequest =
    {
        Method = "POST"
        Url = endpoint + Request.ResponsesPath
        Headers = [ "content-type", "application/json"; "accept", "text/event-stream" ]
        Body = RecordedBody.Json(json (source file))
    }

let private requestId = "x-request-id", "req_7c1e0a5f3b9d2e8a4c6f1b0d"

/// A streamed answer: `sources/<file>`.
let stream (file: string) : RecordedOutcome =
    RecordedOutcome.Response
        {
            Status = 200
            Headers = [ "content-type", "text/event-stream; charset=utf-8"; requestId ]
            Body = source file
            End = BodyEnd.Complete
        }

/// An error response with OpenAI's error body.
let error
    (status: int)
    (headers: (string * string) list)
    (kind: string)
    (code: string option)
    (param: string option)
    (message: string)
    : RecordedOutcome =
    let nullable (value: string option) =
        value |> Option.map Json.String |> Option.defaultValue Json.Null

    RecordedOutcome.Response
        {
            Status = status
            Headers = [ "content-type", "application/json"; requestId ] @ headers
            Body =
                Json.serializeIndented (
                    Json.Object
                        [
                            "error",
                            Json.Object
                                [
                                    "message", Json.String message
                                    "type", Json.String kind
                                    "param", nullable param
                                    "code", nullable code
                                ]
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

/// A case whose response is replayed in a follow-up request: `sources/<name>.followup.*`. Reasoning
/// state is the whole reasoning item as JSON text, unpacked into the request: the cassette checks the
/// replay (`statesVerbatim` off).
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
                    StatesVerbatim = false
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

let readFile =
    tool
        "read_file"
        "Reads a file."
        """{"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}"""

let private editFile =
    tool
        "edit_file"
        "Replaces a file's text."
        """{"type": "object", "properties": {"path": {"type": "string"}, "text": {"type": "string"}}, "required": ["path", "text"]}"""

/// The input of a tool defined from a codec: `create` is optional (false by default).
type WriteFile =
    {
        Path: string
        Text: string
        Create: bool
    }

let writeFileCodec: Codec<WriteFile> =
    Codec.record {
        let! path =
            Codec.field
                "path"
                (fun (w: WriteFile) -> w.Path)
                (Codec.string |> Codec.describe "Relative to the workspace.")

        and! text = Codec.field "text" (fun w -> w.Text) Codec.string
        and! create = Codec.defaultField "create" (fun w -> w.Create) false Codec.bool

        return
            {
                Path = path
                Text = text
                Create = create
            }
    }
    |> Codec.strict

/// A strict tool from a codec: the wire rewrites its schema into strict mode's dialect.
let writeFile: ToolSpec =
    { ToolSpec.ofCodec "write_file" "Writes a file." writeFileCodec with
        Strict = true
    }

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

/// A reasoning item as the API sends it in `response.output_item.done`, as JSON text.
let reasoningItem (id: string) (summary: string list) (encrypted: string) : string =
    Json.serialize (
        Json.Object
            [
                "id", Json.String id
                "type", Json.String "reasoning"
                "summary",
                Json.Array
                    [
                        for text in summary ->
                            Json.Object [ "type", Json.String "summary_text"; "text", Json.String text ]
                    ]
                "encrypted_content", Json.String encrypted
            ]
    )

let private reasoning (model: ModelRef) (text: string) (item: string) : Part =
    Part.Reasoning
        {
            Text = text
            State = Some(state model Request.ReasoningKind item)
        }

/// Text of an assistant message with its phase.
let private phased (model: ModelRef) (phase: string) (text: string) : Part =
    Part.Text
        {
            Text = text
            State = Some(state model Request.PhaseKind phase)
        }

let private reason (effort: Effort) (visible: bool) =
    Some
        {
            Effort = Some effort
            BudgetTokens = None
            Visible = visible
        }

let encrypted1 =
    "gAAAAABpfmDsLqFtcoCkv1I3oplBkJKVb2E72WpmDzAC5cTK38TCLAIPybeUbUfE16b1hLLaj4/CjbQYpXB5q6mXTikDrHkyfkTUHEnuK+4PN2VNuqhx92pKsVzKC9Ve8lKl5ZkH6uvKd60JAZwpw5ruSZVANFUaV34DRASvcj63xy0pTo/CguH9jZLOmt+qfG06xqirjFiOcJ8w3ESIAWEER3wShyvYy3z+2sqF4y3LCxYkHE2QE1nWb9MWL0Emk8oyxUAFavZIa+xUuwEZRyOgxY/+tnCvWPVk8+BsMu8MHmscVPbIQ91L44xK376wRFrklrn5PcJyFNOucylXhYQ6yyBTffjF+6w2V+r2kZ3EK+N6qnMpN6yUmwpQnQw2542/jLZCnN2vZq35NizWNfeP"

let encrypted3 =
    "gAAAAABpeASklqZ1hfHHL7VOaFhP98VPKEqb+bvVpQWnGdyNSdXn+4cKrmDoidMFCJN8hS1RDdURPN2c4Z6gk/4RGqMk+8RoU7cW0yp+NUdDweHhk7rbvlByabqe7ZIARiW41zYpDG6QbB7wsEGtZ3CvKgHxt74m2pIM6HApcdktPFy8lN3JqahYUix5oUgS/GONINkG+T4IPD9bLJUdaoFBGee9Z4PO0xqnFpq67LhhJ+sT8GsVQfMDIkQv9QmaLOXUONJcnPc8KayklLj+on4JDVG4zO8aXmwD1EthEhpIYw=="

let encrypted4 =
    "gAAAAABp1U2bz1kniJZSB1+hBh5q2w3exiVNqypFK61vUL6BTbdDTmHRJ1eVtu4oJpRyCWC5wjBNC01ihnECVlGiyr2WnUv4cWjX9g5ydsVJm5m0Qv2qyKnFp/f5g3JBNORslcbYerD8MSCMYUp9p+ZjViSd1gSxSPARIKGDOXTjwyq6JSYQ5jYk/hssEz5B+vxLjguYcHUFvZI93TBw9sjtgdkV0kdqTdeudg8K2mTXrKFWu+fUXra+"

let encrypted5 =
    "gAAAAABpY168RMblFdMSOpneY46CSZBVqJ7E6g+FqRF1arJ7bIPFYti57NozEBvr8m2v57+fnVvjol5sIxrUQnGPESKLZuHyHDhSNrpJFcdXs2HuA/bEY1g4ECpDxrfJl6h7A3iNoXhtfch8Z4UIXI2+hj34pWCk/zlTDvMy"

/// The summaries of the first reasoning item of `reasoning-tool-call`.
let summaries =
    [
        "**Checking the weather**\n\nThe user wants the current weather in Kraków."
        "**Calling get_weather**\n\nI'll ask for Kraków in celsius."
    ]

/// A request every error case shares.
let simple: ChatRequest =
    { ChatRequest.create sol [ Message.userText "Hi" ] with
        MaxOutputTokens = Some 256
    }

/// The conversation of `prompt-cache` (and of the catalog's cache test): breakpoints after the system
/// prompt, the repository map and the question; a phase on the assistant's reply.
let cachedConversation (model: ModelRef) : ChatRequest =
    { ChatRequest.create
          model
          [
              Message.user
                  [
                      Part.text "Repository map: src/Program.fs, src/Library.fs, tests/Tests.fs."
                      Part.CacheBreakpoint CacheTtl.Short
                  ]
              Message.assistant [ phased model "final_answer" "Noted." ]
              Message.user [ Part.text "Which file defines main?"; Part.CacheBreakpoint CacheTtl.Long ]
          ] with
        System = [ Part.text "You are a coding agent."; Part.CacheBreakpoint CacheTtl.Long ]
        Tools = [ readFile ]
        // The Responses API has no breakpoint after the tools: it is left out.
        Cache =
            {
                Key = Some "repo-fx"
                Tools = Some CacheTtl.Long
            }
        Reasoning = reason Effort.Medium false
        MaxOutputTokens = Some 4096
    }

let private failing (name: string) (outcome: RecordedOutcome) (expected: ChatError) =
    case name simple "simple.request.json" outcome (Expected.Failure expected)

let private simpleStream (name: string) (expected: Expected) =
    case name simple "simple.request.json" (stream $"{name}.sse") expected

/// Every case.
let all: WireCase list =
    [
        // No reasoning; the cache key, the user's safety identifier and the other metadata.
        streamed
            "text"
            { ChatRequest.create luna [ Message.userText "Say hello in Polish." ] with
                System = [ Part.text "Be brief." ]
                Reasoning = reason Effort.Off false
                MaxOutputTokens = Some 1024
                Cache =
                    { CacheHints.none with
                        Key = Some "conversation-1"
                    }
                Metadata = [ "user_id", "user-7f3a"; "session", "s-1" ]
            }
            (response
                "resp_5d94463082aaf93660540ce70a92846cc19771a38b679bde"
                luna
                [ phased luna "final_answer" "Cześć! 👋 W czym mogę pomóc?" ]
                StopReason.EndTurn
                { usage 1290 12 with CacheRead = 1152 })

        // Encrypted reasoning with a two-part summary, a commentary and a tool call; the follow-up
        // replays the reasoning item, the phase and the call.
        (let chat =
            { ChatRequest.create astra [ Message.userText "What's the weather in Kraków?" ] with
                System = [ Part.text "You are a coding agent." ]
                Tools = [ weather ]
                Reasoning = reason Effort.High true
                MaxOutputTokens = Some 16000
                Cache =
                    { CacheHints.none with
                        Key = Some "weather-1"
                    }
            }

         streamed
             "reasoning-tool-call"
             chat
             (response
                 "resp_bdd07f2a98ccdd6e21d29d5925254556578f3b22ce33caf0"
                 astra
                 [
                     reasoning
                         astra
                         (String.concat "\n\n" summaries)
                         (reasoningItem "rs_14b1e79e03fc85fb8891142f033f02d5e527df79abc206f2" summaries encrypted1)
                     phased astra "commentary" "Let me check the weather in Kraków."
                     call "call_TKcqkXObC6C30KgIHKmel5kU" "get_weather" "{\"location\":\"Kraków\",\"unit\":\"celsius\"}"
                 ]
                 StopReason.ToolUse
                 { usage 412 187 with Reasoning = 128 })
         |> followedBy
             [
                 Message.user
                     [
                         result "call_TKcqkXObC6C30KgIHKmel5kU" "get_weather" "4 °C, light snow" false
                     ]
             ])

        // Parallel calls after reasoning without a summary; the follow-up's results (one an error)
        // go before the user's text.
        (let chat =
            { ChatRequest.create luna [ Message.userText "Rename x to count in src/main.fs." ] with
                Tools = [ readFile; editFile ]
                ParallelToolCalls = Some true
                Reasoning = reason Effort.Low false
                MaxOutputTokens = Some 8000
            }

         streamed
             "parallel-calls"
             chat
             (response
                 "resp_ffb78eed685d67b4836ccb75e31c59f8a8b07bc0c7b6cfb5"
                 luna
                 [
                     reasoning
                         luna
                         ""
                         (reasoningItem "rs_8fed9bf0cf9e7a1340101356a0cb0cdba6c99cca247215d5" [] encrypted3)
                     call "call_rSQNDNOmvkTEMGxxxgd29GaV" "read_file" "{\"path\":\"src/main.fs\"}"
                     call "call_Nu1gxPLa6PVel9D51NRaewWY" "read_file" "{\"path\":\"tests/main.fs\"}"
                 ]
                 StopReason.ToolUse
                 { usage 1210 97 with Reasoning = 38 })
         |> followedBy
             [
                 Message.user
                     [
                         Part.text "There are no tests yet."
                         result "call_rSQNDNOmvkTEMGxxxgd29GaV" "read_file" "let x = 1" false
                         result "call_Nu1gxPLa6PVel9D51NRaewWY" "read_file" "no such file: tests/main.fs" true
                     ]
             ])

        // A history with a phase and cache breakpoints, for a model the case's provider has no
        // catalog entry for: the breakpoints are left out, the cache key goes. Usage counts cached
        // tokens as input.
        streamed
            "prompt-cache"
            (cachedConversation gpt55)
            (response
                "resp_632683edd7c2b5fa8f40e1d3278847304800ed7dc92358db"
                gpt55
                [ phased gpt55 "final_answer" "`src/Program.fs` defines `main`." ]
                StopReason.EndTurn
                { usage 4212 15 with CacheRead = 3968 })

        // A strict tool from a codec (rewritten into strict mode's dialect: the defaulted field is
        // required and nullable) and one strict mode cannot take (sent with strict: false).
        streamed
            "strict-tools"
            { ChatRequest.create sol [ Message.userText "Create README.md with a title." ] with
                Tools =
                    [
                        writeFile
                        { tool
                              "search"
                              "Searches the workspace."
                              """{"type": "object", "properties": {"query": {"type": "string", "minLength": 1}}, "required": ["query"]}""" with
                            Strict = true
                        }
                    ]
                ToolChoice = ToolChoice.Required
                Reasoning = reason Effort.Medium false
                MaxOutputTokens = Some 4096
            }
            (response
                "resp_aeb50f63eb6b601e40202f7ce69cd2fb6919d86b6b824f23"
                sol
                [
                    reasoning sol "" (reasoningItem "rs_be3605cd5d9a6d23ecbbae34039dc5cfd65ee093660917a2" [] encrypted5)
                    call
                        "call_KBkYnvXufuKKP06JN6td9Wde"
                        "write_file"
                        "{\"path\":\"README.md\",\"text\":\"# fx\\n\",\"create\":null}"
                ]
                StopReason.ToolUse
                { usage 302 75 with Reasoning = 48 })

        streamed
            "structured-output"
            { ChatRequest.create
                  terra
                  [
                      Message.userText "Extract the contact: John Smith (john@example.com) asked for a demo."
                  ] with
                Output =
                    OutputFormat.Json(
                        "contact info",
                        json
                            """{"type": "object", "properties": {"name": {"type": "string"}, "email": {"type": "string", "format": "email"}, "phone": {"type": "string"}}, "required": ["name", "email"]}""",
                        true
                    )
                MaxOutputTokens = Some 1024
            }
            (response
                "resp_2e207e5f511743995a297c88e636e600293a4ab5bd6879e9"
                terra
                [
                    phased
                        terra
                        "final_answer"
                        "{\"name\":\"John Smith\",\"email\":\"john@example.com\",\"phone\":null}"
                ]
                StopReason.EndTurn
                { usage 88 43 with Reasoning = 24 })

        streamed
            "media"
            { ChatRequest.create
                  luna
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
                Reasoning = reason Effort.Off false
                MaxOutputTokens = Some 2048
            }
            (response
                "resp_f28cf999ea78c6c9ebe0c980d7f48d8d12cd92ab01b4f342"
                luna
                [ phased luna "final_answer" "A red square; the PDF is a one-page invoice." ]
                StopReason.EndTurn
                (usage 1650 14))

        // A forced call of a tool without parameters: its arguments come only with the done event.
        streamed
            "forced-tool"
            { ChatRequest.create terra [ Message.userText "What is in this directory?" ] with
                Tools =
                    [
                        tool
                            "list_files"
                            "Lists the files of the working directory."
                            """{"type": "object", "properties": {}}"""
                    ]
                ToolChoice = ToolChoice.Tool "list_files"
                ParallelToolCalls = Some false
                Reasoning = reason Effort.Off false
                MaxOutputTokens = Some 512
            }
            (response
                "resp_0f0fc697041b683455933104c9db95af8758989fecb86ea5"
                terra
                [ call "call_vGaQvH0EoTJpSxTUva4asyzW" "list_files" "{}" ]
                StopReason.ToolUse
                (usage 402 11))

        simpleStream
            "max-tokens"
            (Expected.Response(
                response
                    "resp_543fe516abeb26d7d8004d6e1833e7b351e264ce3a37b3f0"
                    sol
                    [
                        reasoning
                            sol
                            ""
                            (reasoningItem "rs_3c9e1a7f5b2d8e0c4a6f1b9d3e5a7c0f2b4d6e8a1c3f5b7d" [] encrypted4)
                        phased sol "final_answer" "Hello! It is a pleasure to meet"
                    ]
                    StopReason.MaxTokens
                    { usage 9 256 with Reasoning = 248 }
            ))

        // Cut off by the content filter before the message was done: the open part ends.
        simpleStream
            "content-filter"
            (Expected.Response(
                response
                    "resp_17cecdb0cbd0d952b1d6931d0133f0820e4878af1af4ff31"
                    sol
                    [ Part.text "Here is how" ]
                    StopReason.ContentFilter
                    (usage 9 3)
            ))

        simpleStream
            "refusal"
            (Expected.Response(
                response
                    "resp_a2937267c458cbd1a35319358a532e62318ced4605d716ad"
                    sol
                    [ Part.Refusal "I can't help with that." ]
                    StopReason.Refusal
                    (usage 30 7)
            ))

        simpleStream
            "failed-mid-stream"
            (Expected.Failure(ServerError(500, "The model failed to generate a response.")))

        simpleStream
            "rate-limited-mid-stream"
            (Expected.Failure(
                RateLimited
                    {
                        RetryAfter = Some(TimeSpan.FromMilliseconds 96.0)
                        Message =
                            "Rate limit reached for gpt-6.2-sol in organization org-a1b2c3d4e5 on tokens per min (TPM): Limit 2000000, Used 1999100, Requested 4200. Please try again in 96ms. Visit https://platform.openai.com/account/rate-limits to learn more."
                    }
            ))

        simpleStream
            "quota-mid-stream"
            (Expected.Failure(
                QuotaExhausted
                    "You exceeded your current quota, please check your plan and billing details. For more information on this error, read the docs: https://platform.openai.com/docs/guides/error-codes/api-errors."
            ))

        (let message =
            "Invalid type for 'input[0].content': expected one of an array of input items or a string, but got an object instead."

         failing
             "invalid-request"
             (error 400 [] "invalid_request_error" None (Some "input[0].content") message)
             (InvalidRequest message))

        (let message =
            "Your input exceeds the context window of this model. Please adjust your input and try again."

         failing
             "context-overflow"
             (error 400 [] "invalid_request_error" (Some "context_length_exceeded") (Some "input") message)
             (ContextOverflow
                 {
                     Tokens = None
                     Limit = None
                     Message = message
                 }))

        (let message =
            "This model's maximum context length is 1050000 tokens. However, your messages resulted in 1088016 tokens. Please reduce the length of the messages."

         failing
             "context-length"
             (error 400 [] "invalid_request_error" (Some "context_length_exceeded") (Some "input") message)
             (ContextOverflow
                 {
                     Tokens = Some 1_088_016
                     Limit = Some 1_050_000
                     Message = message
                 }))

        (let message =
            "Invalid prompt: your prompt was flagged as potentially violating our usage policy. Please try again with a different prompt."

         failing
             "invalid-prompt"
             (error 400 [] "invalid_request_error" (Some "invalid_prompt") None message)
             (Refused message))

        (let message =
            "Incorrect API key provided: sk-proj-********************abcd. You can find your API key at https://platform.openai.com/account/api-keys."

         failing
             "unauthorized"
             (error 401 [] "invalid_request_error" (Some "invalid_api_key") None message)
             (AuthFailed message))

        failing
            "region"
            (error
                403
                []
                "request_forbidden"
                (Some "unsupported_country_region_territory")
                None
                "Country, region, or territory not supported")
            (PermissionDenied "Country, region, or territory not supported")

        (let message = "The model `gpt-9` does not exist or you do not have access to it."

         failing
             "not-found"
             (error 404 [] "invalid_request_error" (Some "model_not_found") None message)
             (NotFound message))

        (let message =
            "Rate limit reached for gpt-6.2-sol in organization org-a1b2c3d4e5 on requests per min (RPM): Limit 5000, Used 5000, Requested 1. Please try again in 12ms."

         failing
             "rate-limited"
             (error
                 429
                 [ "retry-after", "3"; "x-ratelimit-remaining-requests", "0" ]
                 "requests"
                 (Some "rate_limit_exceeded")
                 None
                 message)
             (RateLimited
                 {
                     RetryAfter = Some(TimeSpan.FromSeconds 3.0)
                     Message = message
                 }))

        (let message =
            "Your request rate increased too quickly. Reduce your request rate, then increase it gradually."

         failing
             "slow-down"
             (error 429 [ "retry-after-ms", "1500" ] "rate_limit_error" (Some "slow_down") None message)
             (RateLimited
                 {
                     RetryAfter = Some(TimeSpan.FromMilliseconds 1500.0)
                     Message = message
                 }))

        (let message =
            "You exceeded your current quota, please check your plan and billing details."

         failing
             "insufficient-quota"
             (error 429 [] "insufficient_quota" (Some "insufficient_quota") None message)
             (QuotaExhausted message))

        (let message = "Your organization has no prepaid credits remaining."

         failing
             "credit-balance"
             (error 429 [] "insufficient_quota" (Some "credit_balance_exhausted") None message)
             (QuotaExhausted message))

        (let message = "Your project reached its enforced spend limit."

         failing
             "spend-limit"
             (error 429 [] "insufficient_quota" (Some "project_spend_limit_exceeded") None message)
             (QuotaExhausted message))

        (let message =
            "The server had an error while processing your request. Sorry about that!"

         failing "server-error" (error 500 [] "server_error" None None message) (ServerError(500, message)))

        (let message =
            "The model is currently overloaded with other requests. You can retry your request."

         failing
             "overloaded"
             (error 503 [ "retry-after", "2" ] "service_unavailable_error" (Some "server_is_overloaded") None message)
             (Overloaded
                 {
                     RetryAfter = Some(TimeSpan.FromSeconds 2.0)
                     Message = message
                 }))

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
