/// OpenAI's errors as typed errors: error responses by code, type and status; error events and
/// failed responses in a stream; waits from headers and messages; context overflows.
module Fx.Ext.ModelOpenAI.Tests.ErrorTests

open System
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Ext.ModelOpenAI
open Fx.Ext.ModelOpenAI.Tests.Support
open Fx.Models
open Fx.Sdk

let private response (status: int) (headers: (string * string) list) (body: string) : ErrorResponse =
    {
        Status = status
        Headers = headers
        Body = body
    }

let private openai (kind: string) (code: string option) (message: string) : string =
    Json.serialize (
        Json.Object
            [
                "error",
                Json.Object
                    [
                        "message", Json.String message
                        "type", Json.String kind
                        "param", Json.Null
                        "code", (code |> Option.map Json.String |> Option.defaultValue Json.Null)
                    ]
            ]
    )

let private later (seconds: float option) (message: string) : RetryLater =
    {
        RetryAfter = seconds |> Option.map TimeSpan.FromSeconds
        Message = message
    }

[<Tests>]
let tests =
    testList
        "errors"
        [
            test "error responses: the code first, then the type, then the status" {
                let m = "message"

                for status, kind, code, expected in
                    [
                        // Quota: a 429 that waiting does not fix.
                        429, "insufficient_quota", Some "insufficient_quota", QuotaExhausted m
                        429, "insufficient_quota", None, QuotaExhausted m
                        429, "requests", Some "credit_balance_exhausted", QuotaExhausted m
                        429, "requests", Some "organization_spend_limit_exceeded", QuotaExhausted m
                        429, "requests", Some "organization_usage_limit_exceeded", QuotaExhausted m
                        400, "invalid_request_error", Some "billing_not_active", QuotaExhausted m
                        // Rate limits.
                        429, "tokens", Some "rate_limit_exceeded", RateLimited(later None m)
                        429, "rate_limit_error", Some "slow_down", RateLimited(later None m)
                        429, "rate_limit_error", None, RateLimited(later None m)
                        429, "something_new", None, RateLimited(later None m)
                        // Context overflow: a 400 like a malformed request.
                        400,
                        "invalid_request_error",
                        Some "context_length_exceeded",
                        ContextOverflow
                            {
                                Tokens = None
                                Limit = None
                                Message = m
                            }
                        // Refusals.
                        400, "invalid_request_error", Some "invalid_prompt", Refused m
                        400, "invalid_request_error", Some "bio_policy", Refused m
                        400, "invalid_request_error", None, InvalidRequest m
                        400, "invalid_request_error", Some "unsupported_value", InvalidRequest m
                        401, "invalid_request_error", Some "invalid_api_key", AuthFailed m
                        401, "authentication_error", None, AuthFailed m
                        401, "invalid_request_error", None, AuthFailed m
                        403, "request_forbidden", Some "unsupported_country_region_territory", PermissionDenied m
                        403, "permission_error", None, PermissionDenied m
                        404, "invalid_request_error", Some "model_not_found", NotFound m
                        404, "not_found_error", None, NotFound m
                        409, "invalid_request_error", None, InvalidRequest m
                        413,
                        "invalid_request_error",
                        None,
                        ContextOverflow
                            {
                                Tokens = None
                                Limit = None
                                Message = m
                            }
                        500, "server_error", None, ServerError(500, m)
                        500, "api_error", Some "server_error", ServerError(500, m)
                        502, "server_error", None, ServerError(502, m)
                        503, "service_unavailable_error", Some "server_is_overloaded", Overloaded(later None m)
                        503, "server_error", None, ServerError(503, m)
                        529, "unknown", None, Overloaded(later None m)
                    ] do
                    Expect.equal
                        (Errors.ofResponse (response status [] (openai kind code m)))
                        expected
                        $"{status} {kind} {code}"
            }

            test "waits: retry-after-ms, retry-after, else what the message asks for" {
                let rate (headers: (string * string) list) (message: string) =
                    Errors.ofResponse (response 429 headers (openai "requests" (Some "rate_limit_exceeded") message))
                    |> ChatError.retryAfter

                Expect.equal
                    (rate [ "retry-after-ms", "250" ] "Please try again in 7s.")
                    (Some(TimeSpan.FromMilliseconds 250.0))
                    "ms header"

                Expect.equal
                    (rate [ "retry-after", "2" ] "Please try again in 7s.")
                    (Some(TimeSpan.FromSeconds 2.0))
                    "header"

                Expect.equal (rate [] "Please try again in 7s.") (Some(TimeSpan.FromSeconds 7.0)) "message"

                Expect.equal
                    (rate [] "Please try again in 1m6.24s.")
                    (Some(TimeSpan.FromMilliseconds 66_240.0))
                    "minutes"

                Expect.equal
                    (rate [] "Please try again in 820ms.")
                    (Some(TimeSpan.FromMilliseconds 820.0))
                    "milliseconds"

                Expect.equal (rate [] "Please try again in 1h2m.") (Some(TimeSpan.FromMinutes 62.0)) "hours"
                Expect.equal (rate [] "Please try again later.") None "no wait"
                Expect.equal (Errors.retryHint "try again in 0.5s") (Some(TimeSpan.FromMilliseconds 500.0)) "a fraction"
            }

            test "context overflows: the tokens and the limit where the message names them" {
                let message =
                    "This model's maximum context length is 128000 tokens. However, your messages resulted in 130567 tokens. Please reduce the length of the messages."

                Expect.equal
                    (Errors.contextOverflow message)
                    {
                        Tokens = Some 130_567
                        Limit = Some 128_000
                        Message = message
                    }
                    "the classic message"

                let configured =
                    "Input tokens exceed the configured limit of 922000 tokens. Your messages resulted in 1048576 tokens. Please reduce the length of the messages."

                Expect.equal
                    (Errors.ofResponse (response 400 [] (openai "invalid_request_error" None configured)))
                    (ContextOverflow
                        {
                            Tokens = Some 1_048_576
                            Limit = Some 922_000
                            Message = configured
                        })
                    "an invalid request whose message says it does not fit"

                let window = "Your input exceeds the context window of this model."

                Expect.equal
                    (Errors.ofResponse (response 400 [] (openai "invalid_request_error" None window)))
                    (ContextOverflow
                        {
                            Tokens = None
                            Limit = None
                            Message = window
                        })
                    "the context window"
            }

            test "bodies without an error object: the status decides, the body (cut) is the message" {
                Expect.equal (Errors.ofResponse (response 401 [] "nope")) (AuthFailed "nope") "401"

                Expect.equal
                    (Errors.ofResponse (response 429 [ "retry-after", "5" ] ""))
                    (RateLimited(later (Some 5.0) ""))
                    "429"

                Expect.equal
                    (Errors.ofResponse (response 503 [] "Slow Down"))
                    (Overloaded(later None "Slow Down"))
                    "503"

                Expect.equal
                    (Errors.ofResponse (response 413 [] "too big"))
                    (ContextOverflow
                        {
                            Tokens = None
                            Limit = None
                            Message = "too big"
                        })
                    "413"

                Expect.equal
                    (Errors.ofResponse (response 400 [] """{"message": "x"}"""))
                    (InvalidRequest """{"message": "x"}""")
                    "JSON without an error"

                match Errors.ofResponse (response 502 [] (String('x', 2000))) with
                | ServerError(502, message) -> Expect.equal message.Length 501 "cut to 500 characters and an ellipsis"
                | other -> failtest $"{other}"
            }

            test "in a stream: error events and failed responses" {
                let event (members: string) =
                    json ("{\"type\":\"error\"," + members + "}")

                Expect.equal
                    (Errors.ofEvent (event "\"code\":\"insufficient_quota\",\"message\":\"m\",\"param\":null"))
                    (QuotaExhausted "m")
                    "quota"

                Expect.equal
                    (Errors.ofEvent (event "\"code\":\"server_error\",\"message\":\"m\""))
                    (ServerError(500, "m"))
                    "server error"

                Expect.equal
                    (Errors.ofEvent (
                        event "\"code\":\"ERR_SOMETHING\",\"message\":\"Something went wrong\",\"param\":null"
                    ))
                    (ServerError(500, "ERR_SOMETHING: Something went wrong"))
                    "an unknown code"

                Expect.equal (Errors.ofEvent (event "\"message\":\"m\"")) (ServerError(500, "error: m")) "no code"

                Expect.equal
                    (Errors.ofEvent (Json.String "x"))
                    (InvalidResponse "an error event that is not an object")
                    "not an object"

                for code, expected in
                    [
                        "server_error", ServerError(500, "m")
                        "rate_limit_exceeded", RateLimited(later None "m")
                        "invalid_prompt", Refused "m"
                        "misalignment_policy_violation", Refused "m"
                        "vector_store_timeout", ServerError(500, "vector_store_timeout: m")
                        "invalid_image", InvalidRequest "m"
                        "data_residency_mismatch", InvalidRequest "m"
                    ] do
                    Expect.equal
                        (Errors.ofFailed (
                            json (
                                "{\"status\":\"failed\",\"error\":{\"code\":\""
                                + code
                                + "\",\"message\":\"m\"}}"
                            )
                        ))
                        expected
                        code
            }
        ]
