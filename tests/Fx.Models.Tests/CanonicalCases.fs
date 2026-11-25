/// The conformance cases of the canonical wire, built from canonical events and kept as case files
/// in `cassettes/canonical/` (the example of the case format for other adapters). The files are
/// golden: the tests fail if they differ from what this module builds (`UPDATE_GOLDEN=1` rewrites
/// them), and the conformance suite runs on the files.
module Fx.Models.Tests.CanonicalCases

open System
open System.IO
open Fx.Contracts
open Fx.Effects
open Fx.Ext.ModelCanonical
open Fx.Models
open Fx.Models.Tests.Conformance.WireConformance
open Fx.Models.Tests.Support

let endpoint = "https://models.example.test/v1"

let directory = sourceDirectory (Path.Combine("cassettes", "canonical"))

let private recorded (request: ChatRequest) : RecordedRequest =
    {
        Method = "POST"
        Url = endpoint + Canonical.ChatPath
        Headers = [ "content-type", "application/json"; "accept", "text/event-stream" ]
        Body = RecordedBody.Json(ChatRequest.codec.Encode request)
    }

let private stream (events: ChatEvent list) : RecordedOutcome =
    RecordedOutcome.Response
        {
            Status = 200
            Headers = [ "content-type", "text/event-stream" ]
            Body = Canonical.render events
            End = BodyEnd.Complete
        }

let private status (code: int) (headers: (string * string) list) (body: string) : RecordedOutcome =
    RecordedOutcome.Response
        {
            Status = code
            Headers = headers
            Body = body
            End = BodyEnd.Complete
        }

let private expected (events: ChatEvent list) : Expected =
    match ChatAggregate.result (ChatAggregate.ofEvents events) with
    | Ok response -> Expected.Response response
    | Error failure -> Expected.Failure failure.Error

let private case (name: string) (chat: ChatRequest) (outcome: RecordedOutcome) (expect: Expected) : WireCase =
    {
        Name = name
        Endpoint = endpoint
        Request = chat
        Cassette =
            {
                Interactions =
                    [
                        {
                            Request = recorded chat
                            Outcome = outcome
                        }
                    ]
            }
        Expect = expect
        FollowUp = None
    }

let private echoCase (name: string) (chat: ChatRequest) : WireCase =
    let events = Canonical.echo chat
    case name chat (stream events) (expected events)

let private tool =
    {
        Name = "echo"
        Description = "Echoes its text."
        Parameters =
            Json.Object
                [
                    "type", Json.String "object"
                    "properties", Json.Object [ "text", Json.Object [ "type", Json.String "string" ] ]
                    "required", Json.Array [ Json.String "text" ]
                ]
        Strict = false
    }

/// A tool call, its result in a follow-up that replays the reasoning state.
let private toolCall: WireCase =
    let first =
        { request [ Message.userText "call ping" ] with
            Tools = [ tool ]
            Cache =
                { CacheHints.none with
                    Key = Some "conversation-1"
                }
        }

    let response =
        match ChatAggregate.result (ChatAggregate.ofEvents (Canonical.echo first)) with
        | Ok response -> response
        | Error failure -> failwith (string failure)

    let followUp =
        [
            Message.user
                [
                    Part.ToolResult
                        {
                            CallId = "call-1"
                            Name = "echo"
                            Content = [ ToolContent.Text "pong" ]
                            IsError = false
                        }
                ]
        ]

    let second =
        { first with
            Messages = first.Messages @ [ ChatResponse.message response ] @ followUp
        }

    { echoCase "tool-call" first with
        Cassette =
            {
                Interactions =
                    [
                        {
                            Request = recorded first
                            Outcome = stream (Canonical.echo first)
                        }
                        {
                            Request = recorded second
                            Outcome = stream (Canonical.echo second)
                        }
                    ]
            }
        FollowUp =
            Some
                {
                    Messages = followUp
                    StatesVerbatim = true
                }
    }

let private started =
    ChatEvent.Started
        {
            Id = Some "resp-1"
            Model = Some "echo-1"
        }

/// Every case.
let all: WireCase list =
    [
        echoCase
            "text"
            { request [ Message.userText "Hello there, canonical world" ] with
                System = [ Part.text "Be brief."; Part.CacheBreakpoint CacheTtl.Short ]
                Sampling =
                    { Sampling.none with
                        Temperature = Some 0.2
                    }
                MaxOutputTokens = Some 1024
            }

        echoCase "unicode" (request [ Message.userText "Zażółć gęślą jaźń 🦀 — naïve “quotes” ✓" ])

        toolCall

        (let events =
            [
                started
                ChatEvent.PartStarted(0, PartStart.Refusal)
                ChatEvent.Delta(0, "I can't help with that.")
                ChatEvent.PartEnded 0
                ChatEvent.UsageUpdated
                    { Usage.zero with
                        Input = 12
                        Output = 6
                    }
                ChatEvent.Finished StopReason.Refusal
            ]

         let chat = request [ Message.userText "Something forbidden" ]
         case "refusal" chat (stream events) (expected events))

        (let events =
            [
                started
                ChatEvent.PartStarted(0, PartStart.Text)
                ChatEvent.Delta(0, "The first part of a long")
                ChatEvent.Delta(0, " answer that")
                ChatEvent.PartEnded 0
                ChatEvent.UsageUpdated
                    { Usage.zero with
                        Input = 9
                        Output = 8
                    }
                ChatEvent.Finished StopReason.MaxTokens
            ]

         let chat =
             { request [ Message.userText "Write a long answer" ] with
                 MaxOutputTokens = Some 8
             }

         case "max-tokens" chat (stream events) (expected events))

        (let events =
            [
                started
                ChatEvent.UsageUpdated
                    { Usage.zero with
                        Input = 40
                        CacheRead = 30
                    }
                ChatEvent.PartStarted(0, PartStart.Text)
                ChatEvent.Delta(0, "Half an answer, then the")
                ChatEvent.UsageUpdated
                    { Usage.zero with
                        Input = 40
                        CacheRead = 30
                        Output = 5
                    }
                ChatEvent.Failed(QuotaExhausted "the credit balance is too low")
            ]

         let chat = request [ Message.userText "Tell me everything" ]
         case "quota-mid-stream" chat (stream events) (expected events))

        (let chat = request [ Message.userText "Hi" ]

         let error =
             RateLimited
                 {
                     RetryAfter = None
                     Message = "too many requests"
                 }

         case
             "rate-limited"
             chat
             (status 429 [ "retry-after", "7"; "content-type", "application/json" ] (Canonical.errorBody error))
             (Expected.Failure(
                 RateLimited
                     {
                         RetryAfter = Some(TimeSpan.FromSeconds 7.0)
                         Message = "too many requests"
                     }
             )))

        (let chat = request [ Message.userText (String.replicate 50 "very long history ") ]

         let error =
             ContextOverflow
                 {
                     Tokens = Some 210_000
                     Limit = Some 200_000
                     Message = "prompt is too long"
                 }

         case
             "context-overflow"
             chat
             (status 400 [ "content-type", "application/json" ] (Canonical.errorBody error))
             (Expected.Failure error))

        (let chat = request [ Message.userText "Hi" ]

         case
             "overloaded"
             chat
             (status 529 [ "content-type", "text/plain" ] "Overloaded")
             (Expected.Failure(
                 Overloaded
                     {
                         RetryAfter = None
                         Message = "Overloaded"
                     }
             )))

        (let chat = request [ Message.userText "Hi" ]

         case
             "unauthorized"
             chat
             (status 401 [ "content-type", "text/plain" ] "invalid key")
             (Expected.Failure(AuthFailed "invalid key")))
    ]

/// The file of a case.
let path (case: WireCase) =
    Path.Combine(directory, case.Name + ".json")
