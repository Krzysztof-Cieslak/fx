/// The error mapping: Anthropic's error types and codes, statuses, and bodies that say nothing.
module Fx.Ext.ModelAnthropic.Tests.ErrorTests

open System
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Ext.ModelAnthropic
open Fx.Ext.ModelAnthropic.Tests.Support
open Fx.Models
open Fx.Sdk

let private response (status: int) (headers: (string * string) list) (body: string) : ChatError =
    Errors.ofResponse
        {
            Status = status
            Headers = headers
            Body = body
        }

let private anthropic (status: int) (kind: string) (message: string) : ChatError =
    response
        status
        []
        (Json.serialize (
            Json.Object
                [
                    "type", Json.String "error"
                    "error", Json.Object [ "type", Json.String kind; "message", Json.String message ]
                ]
        ))

let private later (message: string) =
    { RetryAfter = None; Message = message }

[<Tests>]
let tests =
    testList
        "errors"
        [
            test "each error type" {
                for status, kind, expected in
                    [
                        400, "invalid_request_error", InvalidRequest "m"
                        401, "authentication_error", AuthFailed "m"
                        402, "billing_error", QuotaExhausted "m"
                        403, "permission_error", PermissionDenied "m"
                        404, "not_found_error", NotFound "m"
                        409, "conflict_error", InvalidRequest "m"
                        413,
                        "request_too_large",
                        ContextOverflow
                            {
                                Tokens = None
                                Limit = None
                                Message = "m"
                            }
                        429, "rate_limit_error", RateLimited(later "m")
                        500, "api_error", ServerError(500, "m")
                        500, "timeout_error", ServerError(500, "m")
                        504, "timeout_error", ServerError(504, "m")
                        529, "overloaded_error", Overloaded(later "m")
                        503, "a_new_error", Overloaded(later "m")
                        418, "a_new_error", InvalidRequest "m"
                    ] do
                    Expect.equal (anthropic status kind "m") expected $"{status} {kind}"
            }

            test "invalid requests that are something else: overflow, quota, filtered output, other statuses" {
                Expect.equal
                    (anthropic 400 "invalid_request_error" "prompt is too long: 205000 tokens > 200000 maximum")
                    (ContextOverflow
                        {
                            Tokens = Some 205_000
                            Limit = Some 200_000
                            Message = "prompt is too long: 205000 tokens > 200000 maximum"
                        })
                    "prompt too long"

                let older =
                    "input length and `max_tokens` exceed context limit: 188240 + 21333 > 200000, decrease input length or `max_tokens` and try again"

                Expect.equal
                    (anthropic 400 "invalid_request_error" older)
                    (ContextOverflow
                        {
                            Tokens = Some 209_573
                            Limit = Some 200_000
                            Message = older
                        })
                    "input and max_tokens"

                Expect.equal
                    (anthropic 400 "invalid_request_error" "You have reached your specified workspace API usage limits.")
                    (QuotaExhausted "You have reached your specified workspace API usage limits.")
                    "a spend limit you set"

                Expect.equal
                    (anthropic 400 "invalid_request_error" "Output blocked by content filtering policy")
                    (Refused "Output blocked by content filtering policy")
                    "filtered"

                Expect.equal (anthropic 404 "invalid_request_error" "m") (NotFound "m") "the status, for other 4xx"

                Expect.equal
                    (anthropic 413 "invalid_request_error" "m")
                    (ContextOverflow
                        {
                            Tokens = None
                            Limit = None
                            Message = "m"
                        })
                    "413"
            }

            test "error codes name the cause" {
                let coded (status: int) (kind: string) (code: string) =
                    response
                        status
                        [ "retry-after", "12" ]
                        ("""{"type":"error","error":{"type":"KIND","message":"m","details":{"error_code":"CODE"}}}"""
                            .Replace("KIND", kind)
                            .Replace("CODE", code))

                Expect.equal
                    (coded 429 "rate_limit_error" "enforced_spend_limit_reached")
                    (QuotaExhausted "m")
                    "spend cap"

                Expect.equal
                    (coded 429 "rate_limit_error" "contract_spend_limit_reached")
                    (QuotaExhausted "m")
                    "contract"

                Expect.equal
                    (coded 402 "billing_error" "linked_organization_spend_limit_reached")
                    (QuotaExhausted "m")
                    "linked"

                Expect.equal
                    (coded 429 "rate_limit_error" "ramp_limit_exceeded")
                    (RateLimited
                        {
                            RetryAfter = Some(TimeSpan.FromSeconds 12.0)
                            Message = "m"
                        })
                    "ramp"

                Expect.equal
                    (coded 413 "invalid_request_error" "request_body_too_large_after_decoding")
                    (ContextOverflow
                        {
                            Tokens = None
                            Limit = None
                            Message = "m"
                        })
                    "decoded body"

                Expect.equal
                    (coded 429 "rate_limit_error" "a_new_code")
                    (RateLimited
                        {
                            RetryAfter = Some(TimeSpan.FromSeconds 12.0)
                            Message = "m"
                        })
                    "unknown codes: the type"
            }

            test "waits: retry-after-ms, then retry-after" {
                let body =
                    """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""

                Expect.equal
                    (response 529 [ "retry-after-ms", "1500"; "retry-after", "9" ] body)
                    (Overloaded
                        {
                            RetryAfter = Some(TimeSpan.FromMilliseconds 1500.0)
                            Message = "Overloaded"
                        })
                    "milliseconds"
            }

            test "bodies without an error object: the status decides, with a short message" {
                Expect.equal (response 529 [] "Overloaded") (Overloaded(later "Overloaded")) "plain"

                Expect.equal
                    (response 413 [ "content-type", "text/html" ] "<html>413 Request Entity Too Large</html>")
                    (ContextOverflow
                        {
                            Tokens = None
                            Limit = None
                            Message = "<html>413 Request Entity Too Large</html>"
                        })
                    "a proxy's 413"

                Expect.equal
                    (response 401 [] """{"message": "no"}""")
                    (AuthFailed """{"message": "no"}""")
                    "no error object"

                match response 502 [] (String.replicate 1000 "x") with
                | ServerError(502, message) -> Expect.equal message (String.replicate 500 "x" + "…") "shortened"
                | other -> failtest $"{other}"
            }

            test "an error event without an error object" {
                match Errors.ofEvent (json """{"type": "error"}""") with
                | InvalidResponse _ -> ()
                | other -> failtest $"{other}"
            }

            test "context overflow messages" {
                Expect.isNone (Errors.contextOverflow "prompt is fine") "none"

                Expect.equal
                    (Errors.contextOverflow "Prompt is too long: 7 tokens > 5 maximum")
                    (Some
                        {
                            Tokens = Some 7
                            Limit = Some 5
                            Message = "Prompt is too long: 7 tokens > 5 maximum"
                        })
                    "case-insensitive"
            }
        ]
