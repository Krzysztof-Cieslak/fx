/// Anthropic's errors as typed `ChatError`s (spec §10.3): error responses before a stream (an HTTP
/// status with `{"type": "error", "error": {"type", "message", "details"}}`) and `error` events in a
/// stream (no status). The error's `type`, and `details.error_code` where it names a cause more
/// precisely, decide; the status decides when the body says nothing (a proxy's page, a plain
/// `Overloaded`).
module Fx.Ext.ModelAnthropic.Errors

open System
open System.Text.RegularExpressions
open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Sdk

/// The most of a body that is not an error object kept as the message.
let private messageLimit = 500

let private field (name: string) (json: Json) : string option =
    match Json.tryField name json with
    | Some(Json.String s) -> Some s
    | _ -> None

let private overflowPatterns =
    [
        // "prompt is too long: 210000 tokens > 200000 maximum"
        Regex(
            "prompt is too long: (\\d+) tokens > (\\d+) maximum",
            RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase
        ),
        false
        // "input length and `max_tokens` exceed context limit: 188240 + 21333 > 200000, ..."
        Regex(
            "exceed context limit: (\\d+) \\+ (\\d+) > (\\d+)",
            RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase
        ),
        true
    ]

let private number (text: string) : int option =
    match Int32.TryParse(text, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
    | true, n -> Some n
    | _ -> None

/// The context overflow a message describes, if it describes one: the request's tokens (input, plus
/// `max_tokens` where the message adds them) and the limit.
let contextOverflow (message: string) : ContextOverflow option =
    overflowPatterns
    |> List.tryPick (fun (pattern, sum) ->
        let m = pattern.Match message

        if not m.Success then
            None
        elif sum then
            Some
                {
                    Tokens =
                        match number m.Groups[1].Value, number m.Groups[2].Value with
                        | Some a, Some b -> Some(a + b)
                        | _ -> None
                    Limit = number m.Groups[3].Value
                    Message = message
                }
        else
            Some
                {
                    Tokens = number m.Groups[1].Value
                    Limit = number m.Groups[2].Value
                    Message = message
                })

let private contains (text: string) (message: string) =
    message.Contains(text, StringComparison.OrdinalIgnoreCase)

/// An `invalid_request_error`: most are malformed requests, but the API also says this when the
/// prompt does not fit, the credit balance or a spend limit you set is used up, or output was
/// filtered.
let private invalidRequest (message: string) : ChatError =
    match contextOverflow message with
    | Some overflow -> ContextOverflow overflow
    | None when contains "credit balance" message || contains "usage limits" message -> QuotaExhausted message
    | None when contains "content filtering policy" message -> Refused message
    | None -> InvalidRequest message

let private tooLarge (message: string) : ChatError =
    ContextOverflow
        {
            Tokens = None
            Limit = None
            Message = message
        }

/// What a status says when the body says nothing more: `ErrorMapping.ofStatus`, except that 413
/// (the request is too large, often answered by a proxy before the API) is a context overflow.
let private ofStatus (status: int) (headers: (string * string) list) (message: string) : ChatError =
    if status = 413 then
        tooLarge message
    else
        ErrorMapping.ofStatus status headers message

/// The error codes (`error.details.error_code`) that name a cause the type does not.
let private ofCode (code: string) (later: RetryLater) : ChatError option =
    match code with
    | "enforced_spend_limit_reached"
    | "contract_spend_limit_reached"
    | "linked_organization_billing_unavailable"
    | "linked_organization_spend_limit_reached" -> Some(QuotaExhausted later.Message)
    | "request_body_too_large_after_decoding" -> Some(tooLarge later.Message)
    | "ramp_limit_exceeded" -> Some(RateLimited later)
    | "compaction_unavailable" -> Some(Overloaded later)
    | _ -> None

/// The error of an Anthropic error object (`{type, message, details?}`), with the HTTP status (none
/// in a stream) and the response's headers (for `retry-after`).
let ofError (status: int option) (headers: (string * string) list) (error: Json) : ChatError =
    let kind = field "type" error |> Option.defaultValue ""
    let message = field "message" error |> Option.defaultValue kind

    let later =
        {
            RetryAfter = ErrorMapping.retryAfter headers
            Message = message
        }

    let code =
        Json.tryField "details" error
        |> Option.bind (field "error_code")
        |> Option.bind (fun code -> ofCode code later)

    match code with
    | Some error -> error
    | None ->
        match kind, status with
        | "invalid_request_error", (None | Some 400) -> invalidRequest message
        | "request_too_large", _ -> tooLarge message
        | "authentication_error", _ -> AuthFailed message
        | "billing_error", _ -> QuotaExhausted message
        | "permission_error", _ -> PermissionDenied message
        | "not_found_error", _ -> NotFound message
        | "rate_limit_error", _ -> RateLimited later
        | "overloaded_error", _ -> Overloaded later
        | "api_error", _ -> ServerError(status |> Option.defaultValue 500, message)
        | "timeout_error", _ -> ServerError(status |> Option.defaultValue 504, message)
        | _, Some status -> ofStatus status headers message
        // An error type this version does not know, in a stream: the provider failed.
        | _, None -> ServerError(500, $"{kind}: {message}")

let private shorten (text: string) =
    let text = text.Trim()

    if text.Length <= messageLimit then
        text
    else
        text.Substring(0, messageLimit) + "…"

/// An error response (any status outside 2xx) before the stream started.
let ofResponse (response: ErrorResponse) : ChatError =
    let error =
        match Json.parse response.Body with
        | Ok body ->
            match Json.tryField "error" body with
            | Some(Json.Object _ as error) -> Some error
            | _ -> None
        | Error _ -> None

    match error with
    | Some error -> ofError (Some response.Status) response.Headers error
    | None -> ofStatus response.Status response.Headers (shorten response.Body)

/// An `error` event in a stream (its data: `{"type": "error", "error": {...}}`).
let ofEvent (data: Json) : ChatError =
    match Json.tryField "error" data with
    | Some(Json.Object _ as error) -> ofError None [] error
    | _ -> InvalidResponse "an error event without an error object"
