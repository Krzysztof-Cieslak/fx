module Fx.Models.Tests.ProviderTests

open System
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Models.Tests.Support
open Fx.Models.Tests.Samples

let private json (text: string) =
    match Json.parse text with
    | Ok value -> value
    | Error e -> failtest (string e)

[<Tests>]
let tests =
    testList
        "Providers"
        [
            test "a request transform sets and removes headers, adds query parameters and edits JSON bodies" {
                let request =
                    HttpRequest.post "https://api.anthropic.com/v1/messages" (bytes """{"model":"m","top_k":5}""")
                    |> HttpRequest.withHeader "X-Stainless-OS" "Linux"
                    |> HttpRequest.withHeader "Anthropic-Version" "old"

                let transformed = RequestTransform.apply provider.Transform.Value request
                Expect.equal transformed.Url "https://api.anthropic.com/v1/messages?beta=true" "query"
                Expect.equal transformed.Headers [ "anthropic-version", "2023-06-01" ] "headers"

                Expect.equal
                    (json (HttpRequest.bodyText transformed))
                    (json """{"model":"m","metadata":{"source":"agent"}}""")
                    "body"
            }

            test "body edits reach into nested objects and leave other bodies alone" {
                let edits =
                    [ BodyEdit.Set([ "a"; "b" ], Json.Bool true); BodyEdit.Remove [ "a"; "c" ] ]

                Expect.equal
                    (RequestTransform.editBody edits (json """{"a":{"c":1,"d":2}}"""))
                    (json """{"a":{"d":2,"b":true}}""")
                    "nested"

                let text = HttpRequest.post "https://x.test/" (bytes "not json")

                Expect.equal
                    (RequestTransform.apply
                        { RequestTransform.none with
                            Body = edits
                        }
                        text)
                        .Body
                    text.Body
                    "untouched"
            }

            test "requests may go only below a provider's endpoints" {
                for url, allowed in
                    [
                        "https://api.anthropic.com/v1/messages", true
                        "https://api.anthropic.com/v1", true
                        "https://API.anthropic.com/v1/messages?x=1", true
                        "https://api.anthropic.com/v10/messages", false
                        "https://api.anthropic.com/v2/messages", false
                        "http://api.anthropic.com/v1/messages", false
                        "https://api.anthropic.com:8443/v1/messages", false
                        "https://evil.example/v1/messages", false
                        "not a url", false
                    ] do
                    Expect.equal (ProviderDef.allows url provider) allowed url
            }

            test "lookups: endpoints, models and quirks" {
                Expect.equal
                    (ProviderDef.endpoint "anthropic.messages" provider)
                    (Some "https://api.anthropic.com/v1")
                    "endpoint"

                Expect.equal (ProviderDef.model info.Ref provider) (Some info) "model"

                Expect.equal
                    (ProviderDef.quirk "maxCacheBreakpoints" provider)
                    (Some(Json.Number(JsonNumber.ofInt64 4L)))
                    "quirk"

                Expect.equal (ProviderDef.quirk "none" provider) None "no quirk"
            }

            test "a provider definition's issues are model-readable" {
                match
                    Codec.decodeText ProviderDef.codec """{"id":"Bad Id","name":"x","endpoints":{"a":"ftp://x"}}"""
                with
                | Error issues ->
                    let text = Issue.renderAtMost 10 issues
                    Expect.stringContains text "$.id" "the id"
                    Expect.stringContains text "$.endpoints.a: expected an absolute http or https URL" "the endpoint"
                | Ok p -> failtest $"decoded {p}"
            }

            test "resolved auth sets headers and query, then signs; its text hides values" {
                let auth =
                    {
                        Headers = [ "x-api-key", "sk-secret" ]
                        Query = [ "key", "q-secret" ]
                        Sign =
                            Some(fun request _ ->
                                Threading.Tasks.Task.FromResult(HttpRequest.withHeader "x-signature" "signed" request))
                    }

                let request =
                    wait (
                        ResolvedAuth.apply
                            auth
                            (HttpRequest.get "https://x.test/a?b=1"
                             |> HttpRequest.withHeader "X-Api-Key" "old")
                            Threading.CancellationToken.None
                    )

                Expect.equal request.Url "https://x.test/a?b=1&key=q-secret" "query"
                Expect.equal request.Headers [ "x-api-key", "sk-secret"; "x-signature", "signed" ] "headers"
                Expect.isFalse ((string auth).Contains "secret") "redacted"
            }

            test "error statuses map to typed errors, with Retry-After" {
                let cases =
                    [
                        400, [], ChatError.InvalidRequest "m"
                        401, [], AuthFailed "m"
                        402, [], QuotaExhausted "m"
                        403, [], PermissionDenied "m"
                        404, [], NotFound "m"
                        408, [], Transport(HttpError.TimedOut HttpPhase.Response)
                        413, [], ChatError.InvalidRequest "m"
                        429,
                        [ "retry-after", "2" ],
                        RateLimited
                            {
                                RetryAfter = Some(TimeSpan.FromSeconds 2.0)
                                Message = "m"
                            }
                        429,
                        [ "retry-after-ms", "1500"; "retry-after", "9" ],
                        RateLimited
                            {
                                RetryAfter = Some(TimeSpan.FromMilliseconds 1500.0)
                                Message = "m"
                            }
                        500, [], ServerError(500, "m")
                        503, [ "retry-after", "nonsense" ], Overloaded { RetryAfter = None; Message = "m" }
                        529, [], Overloaded { RetryAfter = None; Message = "m" }
                    ]

                for status, headers, expected in cases do
                    Expect.equal (ErrorMapping.ofStatus status headers "m") expected (string status)
            }
        ]
