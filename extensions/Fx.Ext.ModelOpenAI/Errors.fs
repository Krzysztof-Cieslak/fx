/// OpenAI's errors as typed `ChatError`s (spec §10.3): error responses before a stream (an HTTP
/// status with `{"error": {"message", "type", "param", "code"}}`), `error` events in a stream, and the
/// `error` of a `response.failed` event (no status). The error's `code` decides first, as OpenAI's
/// documentation tells clients to (`insufficient_quota` is a 429 like a rate limit, a context overflow
/// a 400 like any malformed request); then its `type`; then the status, when the body says nothing (a
/// proxy's page).
module Fx.Ext.ModelOpenAI.Errors

open System
open System.Globalization
open System.Text.RegularExpressions
open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Sdk

/// The most of a body that is not an error object kept as the message.
let private messageLimit = 500

let private field (name: string) (json: Json) : string option =
    match Json.tryField name json with
    | Some(Json.String s) when s <> "" -> Some s
    | _ -> None

/// Codes of credits, billing, spend limits and plan quotas: retrying does not help.
let quotaCodes =
    set
        [
            "insufficient_quota"
            "credit_balance_exhausted"
            "organization_spend_limit_exceeded"
            "project_spend_limit_exceeded"
            "organization_usage_limit_exceeded"
            "billing_hard_limit_reached"
            "billing_not_active"
        ]

/// Codes of requests refused on safety or policy grounds.
let refusalCodes =
    set
        [
            "invalid_prompt"
            "content_policy_violation"
            "bio_policy"
            "cyber_policy"
            "misalignment_policy_violation"
            "image_content_policy_violation"
        ]

/// Codes of requests the API cannot take as they are (a failed response's images, a region): sending
/// them again does not help.
let invalidCodes =
    set
        [
            "invalid_image"
            "invalid_image_format"
            "invalid_base64_image"
            "invalid_image_url"
            "image_too_large"
            "image_too_small"
            "image_parse_error"
            "invalid_image_mode"
            "image_file_too_large"
            "unsupported_image_media_type"
            "empty_image_file"
            "failed_to_download_image"
            "image_file_not_found"
            "data_residency_mismatch"
        ]

/// Codes of a model that is too busy: retry later.
let overloadCodes =
    set [ "server_is_overloaded"; "overloaded"; "engine_overloaded" ]

let private options = RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase

let private overflowPatterns =
    [
        // "This model's maximum context length is 128000 tokens. However, your messages resulted in
        // 130567 tokens."
        Regex(
            "maximum context length is (\\d+) tokens.*?resulted in (\\d+) tokens",
            options ||| RegexOptions.Singleline
        )
        // "Input tokens exceed the configured limit of 922000 tokens. Your messages resulted in
        // 1048576 tokens."
        Regex("limit of (\\d+) tokens.*?resulted in (\\d+) tokens", options ||| RegexOptions.Singleline)
    ]

let private number (text: string) : int option =
    match Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture) with
    | true, n -> Some n
    | _ -> None

/// The context overflow of a message: the limit and the request's tokens where it names them.
let contextOverflow (message: string) : ContextOverflow =
    let counts =
        overflowPatterns
        |> List.tryPick (fun pattern ->
            let m = pattern.Match message

            if m.Success then
                Some(number m.Groups[2].Value, number m.Groups[1].Value)
            else
                None)

    {
        Tokens = counts |> Option.bind fst
        Limit = counts |> Option.bind snd
        Message = message
    }

/// Whether a message says the request does not fit the context window.
let private overflows (message: string) : bool =
    message.Contains("context window", StringComparison.OrdinalIgnoreCase)
    || message.Contains("context length", StringComparison.OrdinalIgnoreCase)
    || overflowPatterns |> List.exists (fun p -> p.IsMatch message)

let private wait =
    Regex("try again in ((?:\\d+(?:\\.\\d+)?(?:ms|h|m|s))+)", options)

let private part = Regex("(\\d+(?:\\.\\d+)?)(ms|h|m|s)", options)

/// The wait a message asks for ("Please try again in 1m6.24s", "in 820ms"), when headers say none.
let retryHint (message: string) : TimeSpan option =
    let m = wait.Match message

    if not m.Success then
        None
    else
        let total =
            part.Matches m.Groups[1].Value
            |> Seq.sumBy (fun p ->
                let value =
                    Double.Parse(p.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture)

                match p.Groups[2].Value.ToLowerInvariant() with
                | "ms" -> value
                | "s" -> value * 1000.0
                | "m" -> value * 60_000.0
                | _ -> value * 3_600_000.0)

        Some(TimeSpan.FromMilliseconds total)

/// How long to wait: `retry-after-ms` or `retry-after`, else what the message asks for.
let retryAfter (headers: (string * string) list) (message: string) : TimeSpan option =
    ErrorMapping.retryAfter headers |> Option.orElse (retryHint message)

/// What a status says when the body says nothing more: `ErrorMapping.ofStatus`, except that 413 (the
/// request is too large, often answered by a proxy before the API) is a context overflow.
let private ofStatus (status: int) (headers: (string * string) list) (message: string) : ChatError =
    if status = 413 then
        ContextOverflow(contextOverflow message)
    else
        ErrorMapping.ofStatus status headers message

/// The error of an OpenAI error object (`{message, type, param, code}`), with the HTTP status (none in
/// a stream) and the response's headers (for `retry-after`).
let ofError (status: int option) (headers: (string * string) list) (error: Json) : ChatError =
    let code = field "code" error
    let kind = field "type" error

    let message =
        field "message" error
        |> Option.orElse code
        |> Option.orElse kind
        |> Option.defaultValue "the request failed"

    let later =
        {
            RetryAfter = retryAfter headers message
            Message = message
        }

    let server () =
        ServerError(status |> Option.defaultValue 500, message)

    match code with
    | Some c when quotaCodes.Contains c -> QuotaExhausted message
    | Some "context_length_exceeded" -> ContextOverflow(contextOverflow message)
    | Some("rate_limit_exceeded" | "slow_down") -> RateLimited later
    | Some c when overloadCodes.Contains c -> Overloaded later
    | Some c when refusalCodes.Contains c -> Refused message
    | Some c when invalidCodes.Contains c -> InvalidRequest message
    | Some("invalid_api_key" | "invalid_authentication") -> AuthFailed message
    | Some "unsupported_country_region_territory" -> PermissionDenied message
    | Some "model_not_found" -> NotFound message
    | Some "server_error" -> server ()
    | _ ->
        match kind, status with
        | Some "insufficient_quota", _ -> QuotaExhausted message
        | Some("rate_limit_error" | "tokens" | "requests"), _ -> RateLimited later
        | Some "service_unavailable_error", _ -> Overloaded later
        | Some "authentication_error", _ -> AuthFailed message
        | Some "permission_error", _ -> PermissionDenied message
        | Some "not_found_error", _ -> NotFound message
        | Some("server_error" | "api_error"), _ -> server ()
        | Some "invalid_request_error", (None | Some 400) when overflows message ->
            ContextOverflow(contextOverflow message)
        | Some "invalid_request_error", (None | Some 400) -> InvalidRequest message
        | _, Some status -> ofStatus status headers message
        // A code or type this version does not know, in a stream: the provider failed.
        | _, None ->
            let name = code |> Option.orElse kind |> Option.defaultValue "error"
            ServerError(500, $"{name}: {message}")

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

/// An `error` event in a stream: its data has the error's fields (`code`, `message`, `param`) beside
/// `type: "error"`, or (older servers) an `error` object.
let ofEvent (data: Json) : ChatError =
    match Json.tryField "error" data with
    | Some(Json.Object _ as error) -> ofError None [] error
    | _ ->
        match data with
        | Json.Object members -> ofError None [] (Json.Object(members |> List.filter (fun (name, _) -> name <> "type")))
        | _ -> InvalidResponse "an error event that is not an object"

/// The `error` of a failed response (`response.failed`): `{code, message}`.
let ofFailed (response: Json) : ChatError =
    match Json.tryField "error" response with
    | Some(Json.Object _ as error) -> ofError None [] error
    | _ -> ServerError(500, "the response failed without an error")
