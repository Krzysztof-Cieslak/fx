/// Sample values of every model type, for round trips, golden files and the other tests.
module Fx.Models.Tests.Samples

open System
open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Models.Tests.Support

let state (data: string) : ProviderState =
    {
        Family = "anthropic.messages"
        Model = ModelRef.create "anthropic" "claude-sonnet-4-5"
        Kind = "signature"
        Data = data
    }

let image: MediaPart =
    {
        Kind = MediaKind.Image
        MediaType = "image/png"
        Source = MediaSource.Base64 "iVBORw0KGgo="
        Name = None
    }

/// A request with every part kind and option set.
let request: ChatRequest =
    {
        Model = ModelRef.create "anthropic" "claude-sonnet-4-5"
        System =
            [
                Part.text "You are a careful coding agent."
                Part.CacheBreakpoint CacheTtl.Long
            ]
        Messages =
            [
                Message.user
                    [
                        Part.text "What is in this picture, and what is the weather?"
                        Part.Media image
                        Part.Media
                            {
                                Kind = MediaKind.Pdf
                                MediaType = "application/pdf"
                                Source = MediaSource.Blob "sha256:00ff"
                                Name = Some "spec.pdf"
                            }
                    ]
                Message.assistant
                    [
                        Part.Reasoning
                            {
                                Text = "A cat; then the tool."
                                State = Some(state "EqQBCgIYAhIM\u00e9+/=")
                            }
                        Part.Text { Text = "A cat."; State = None }
                        Part.ToolCall
                            {
                                Id = "toolu_01"
                                Name = "get_weather"
                                Arguments = "{\"location\": \"Krak\u00f3w\"}"
                                State = None
                            }
                    ]
                Message.user
                    [
                        Part.ToolResult
                            {
                                CallId = "toolu_01"
                                Name = "get_weather"
                                Content = [ ToolContent.Text "12 °C"; ToolContent.Media image ]
                                IsError = false
                            }
                        Part.CacheBreakpoint CacheTtl.Short
                    ]
                Message.assistant [ Part.Refusal "I won't guess." ]
            ]
        Tools =
            [
                {
                    Name = "get_weather"
                    Description = "The weather at a place."
                    Parameters =
                        Json.Object
                            [
                                "type", Json.String "object"
                                "properties", Json.Object [ "location", Json.Object [ "type", Json.String "string" ] ]
                            ]
                    Strict = true
                }
            ]
        ToolChoice = ToolChoice.Tool "get_weather"
        ParallelToolCalls = Some false
        Sampling =
            {
                Temperature = Some 0.7
                TopP = Some 0.95
                TopK = Some 40
                Seed = Some 42L
                Stop = [ "\n\nHuman:" ]
            }
        Reasoning =
            Some
                {
                    Effort = Some Effort.High
                    BudgetTokens = Some 8000
                    Visible = true
                }
        MaxOutputTokens = Some 4096
        Output = OutputFormat.Json("answer", Json.Object [ "type", Json.String "object" ], true)
        Cache =
            {
                Key = Some "session-1"
                Tools = Some CacheTtl.Short
            }
        Metadata = [ "user_id", "u-1" ]
    }

let usage: Usage =
    {
        Input = 2520
        Output = 89
        CacheRead = 2048
        CacheWrite = 0
        Reasoning = 40
    }

/// A stream with every event kind.
let events: ChatEvent list =
    [
        ChatEvent.Started
            {
                Id = Some "msg_01"
                Model = Some "claude-sonnet-4-5"
            }
        ChatEvent.UsageUpdated
            { Usage.zero with
                Input = 2520
                CacheRead = 2048
                Output = 2
            }
        ChatEvent.PartStarted(0, PartStart.Reasoning)
        ChatEvent.Delta(0, "The user wants ")
        ChatEvent.Delta(0, "the weather.")
        ChatEvent.PartState(0, state "EqQBsig==")
        ChatEvent.PartEnded 0
        ChatEvent.PartStarted(1, PartStart.Text)
        ChatEvent.Delta(1, "Checking ")
        ChatEvent.Delta(1, "Kraków.")
        ChatEvent.PartEnded 1
        ChatEvent.PartStarted(2, PartStart.ToolCall("toolu_01", "get_weather"))
        ChatEvent.Delta(2, "{\"location\": \"Kra")
        ChatEvent.Delta(2, "ków\"}")
        ChatEvent.PartEnded 2
        ChatEvent.UsageUpdated usage
        ChatEvent.Finished StopReason.ToolUse
    ]

/// The response those events make.
let response: ChatResponse =
    {
        Id = Some "msg_01"
        Model = Some "claude-sonnet-4-5"
        Parts =
            [
                Part.Reasoning
                    {
                        Text = "The user wants the weather."
                        State = Some(state "EqQBsig==")
                    }
                Part.Text
                    {
                        Text = "Checking Kraków."
                        State = None
                    }
                Part.ToolCall
                    {
                        Id = "toolu_01"
                        Name = "get_weather"
                        Arguments = "{\"location\": \"Kraków\"}"
                        State = None
                    }
            ]
        Stop = StopReason.ToolUse
        Usage = usage
    }

/// One of every error.
let errors: ChatError list =
    [
        RateLimited
            {
                RetryAfter = Some(TimeSpan.FromSeconds 7.5)
                Message = "slow down"
            }
        QuotaExhausted "credits"
        ContextOverflow
            {
                Tokens = Some 210_000
                Limit = Some 200_000
                Message = "prompt is too long"
            }
        Refused "policy"
        AuthFailed "invalid key"
        PermissionDenied "no access"
        NotFound "no such model"
        ChatError.InvalidRequest "bad"
        Overloaded { RetryAfter = None; Message = "busy" }
        ServerError(502, "bad gateway")
        Transport(HttpError.Reset "the connection broke off")
        Transport(HttpError.TimedOut HttpPhase.Body)
        Cancelled "cancelled by the provider"
        InvalidResponse "garbage"
        EgressBlocked
            {
                Ask = true
                Reasons = [ "acme.egress/paths: secrets/ is excluded" ]
            }
        Unavailable "no wire"
    ]

let stops: StopReason list =
    [
        StopReason.EndTurn
        StopReason.ToolUse
        StopReason.MaxTokens
        StopReason.StopSequence(Some "END")
        StopReason.StopSequence None
        StopReason.Refusal
        StopReason.ContentFilter
        StopReason.ContextWindow
        StopReason.Pause
        StopReason.Other "new_reason"
    ]

let info: ModelInfo =
    {
        Ref = ModelRef.create "anthropic" "claude-sonnet-4-5"
        Kind = ModelKind.Chat
        Family = Some "anthropic.messages"
        Name = Some "Claude Sonnet 4.5"
        Limits = { Context = 200_000; Output = 64_000 }
        Caps =
            {
                Reasoning =
                    Some
                        {
                            Efforts = [ Effort.Low; Effort.Medium; Effort.High ]
                            Budget = Some { Min = 1024; Max = 32_000 }
                            Visible = true
                            Replay = true
                        }
                Tools =
                    {
                        Calls = true
                        Parallel = true
                        Forced = true
                        Strict = false
                    }
                StructuredOutput = true
                Logprobs = false
                Caching =
                    {
                        Mode = CacheMode.Breakpoints
                        MaxBreakpoints = 4
                        Key = false
                        LongTtl = true
                    }
                Media = [ MediaKind.Image; MediaKind.Pdf ]
                Sampling =
                    {
                        Temperature = true
                        TopP = true
                        TopK = true
                        Seed = false
                        Stop = true
                    }
                Transports = [ ModelTransport.Sse ]
            }
        Cost =
            Some
                {
                    Input = 3m
                    Output = 15m
                    CacheRead = Some 0.3m
                    CacheWrite = Some 3.75m
                }
        PlanBacked = false
    }

let provider: ProviderDef =
    {
        Id = "anthropic"
        Name = "Anthropic"
        Endpoints = [ "anthropic.messages", "https://api.anthropic.com/v1" ]
        Catalog =
            {
                Models = [ info ]
                Discover = Some "/models"
            }
        Auth =
            [
                {
                    Kind = "api_key"
                    Settings = Json.Object [ "header", Json.String "x-api-key" ]
                }
            ]
        Quirks = [ "maxCacheBreakpoints", Json.Number(JsonNumber.ofInt64 4L) ]
        Transform =
            Some
                {
                    SetHeaders = [ "anthropic-version", "2023-06-01" ]
                    RemoveHeaders = [ "x-stainless-os" ]
                    SetQuery = [ "beta", "true" ]
                    Body =
                        [
                            BodyEdit.Set([ "metadata"; "source" ], Json.String "agent")
                            BodyEdit.Remove [ "top_k" ]
                        ]
                }
    }
