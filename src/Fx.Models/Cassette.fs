namespace Fx.Models

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects

/// The body a cassette expects a request to have.
[<RequireQualifiedAccess>]
type RecordedBody =
    | Empty
    /// Exactly this text.
    | Text of text: string
    /// JSON equal to this value (member order and number notation do not matter).
    | Json of value: Json

    override this.ToString() =
        match this with
        | Empty -> "no body"
        | Text text -> $"text ({text.Length} chars)"
        | Json _ -> "JSON"

/// A request as a cassette expects it. Only the headers listed are checked; a header recorded as
/// `<redacted>` only has to be present.
type RecordedRequest =
    {
        Method: string
        Url: string
        Headers: (string * string) list
        Body: RecordedBody
    }

    override this.ToString() = $"{this.Method} {this.Url}"

/// How a recorded body ended.
[<RequireQualifiedAccess>]
type BodyEnd =
    | Complete
    /// The connection broke off after the body.
    | Reset
    /// Nothing more came: a reader waits until it is cancelled (or its idle timeout, at once).
    | Hang

    override this.ToString() =
        match this with
        | Complete -> "complete"
        | Reset -> "reset"
        | Hang -> "hang"

type RecordedResponse =
    {
        Status: int
        Headers: (string * string) list
        /// The body as UTF-8 text (model APIs answer in text: JSON, event streams).
        Body: string
        End: BodyEnd
    }

    override this.ToString() =
        $"HTTP {this.Status} ({this.Body.Length} chars, {this.End})"

/// What came back for a request: a response, or a failure before one.
[<RequireQualifiedAccess>]
type RecordedOutcome =
    | Response of RecordedResponse
    | Failure of HttpError

    override this.ToString() =
        match this with
        | Response r -> string r
        | Failure e -> string e

type Interaction =
    {
        Request: RecordedRequest
        Outcome: RecordedOutcome
    }

    override this.ToString() = $"{this.Request} -> {this.Outcome}"

/// Recorded HTTP exchanges, replayed in order by `CassetteHttp` (spec §15 record and replay). The
/// basis of the wire-conformance suite: an adapter must send what the cassette expects and turn
/// what it answers into the expected events.
type Cassette =
    {
        Interactions: Interaction list
    }

    override this.ToString() =
        $"cassette({this.Interactions.Length} interactions)"

/// How a replayed body is cut into pieces.
[<RequireQualifiedAccess>]
type Chunking =
    /// One piece.
    | Whole
    /// Pieces of this many bytes.
    | Every of bytes: int
    /// Cut at these byte offsets.
    | At of offsets: int list

    override this.ToString() =
        match this with
        | Whole -> "whole"
        | Every n -> $"every {n} bytes"
        | At offsets -> "at " + (offsets |> List.map string |> String.concat ",")

/// A request differs from what the cassette expects, or the cassette has no more interactions: the
/// code under test did something other than what was recorded (a test failure, not an expected one).
type CassetteMismatchException(message: string) =
    inherit Exception(message)

module Chunking =
    /// Cuts bytes into pieces.
    let split (chunking: Chunking) (bytes: byte array) : byte array list =
        let cut (offsets: int list) =
            let offsets =
                offsets
                |> List.filter (fun o -> o > 0 && o < bytes.Length)
                |> List.distinct
                |> List.sort

            let bounds = 0 :: offsets @ [ bytes.Length ]

            bounds
            |> List.pairwise
            |> List.map (fun (a, b) -> Array.sub bytes a (b - a))
            |> List.filter (fun piece -> piece.Length > 0)

        match chunking with
        | Chunking.Whole -> if bytes.Length = 0 then [] else [ bytes ]
        | Chunking.Every n when n > 0 -> cut [ n..n .. bytes.Length ]
        | Chunking.Every _ -> cut []
        | Chunking.At offsets -> cut offsets

[<RequireQualifiedAccess>]
module Cassette =
    let private utf8 = UTF8Encoding(false)

    /// The value that stands for a secret in a recorded header.
    [<Literal>]
    let Redacted = "<redacted>"

    /// Request headers whose values are never recorded.
    let sensitiveRequestHeaders =
        [
            "authorization"
            "proxy-authorization"
            "x-api-key"
            "api-key"
            "x-goog-api-key"
            "cookie"
        ]

    /// Response headers whose values are never recorded.
    let sensitiveResponseHeaders =
        [
            "set-cookie"
            "openai-organization"
            "openai-project"
            "anthropic-organization-id"
        ]

    let private isOneOf (names: string list) (name: string) =
        names
        |> List.exists (fun n -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase))

    let private request: Codec<RecordedRequest> =
        Codec.record {
            let! method =
                Codec.field "method" (fun (r: RecordedRequest) -> r.Method) Codec.string

            and! url = Codec.field "url" (fun r -> r.Url) Codec.string

            and! headers =
                Codec.defaultField "headers" (fun r -> r.Headers) [] (Codec.keyValues Codec.string)

            and! json =
                Codec.optionalField
                    "json"
                    (fun r ->
                        match r.Body with
                        | RecordedBody.Json j -> Some j
                        | _ -> None)
                    Codec.json

            and! text =
                Codec.optionalField
                    "text"
                    (fun r ->
                        match r.Body with
                        | RecordedBody.Text t -> Some t
                        | _ -> None)
                    Codec.string

            return
                {
                    Method = method
                    Url = url
                    Headers = headers
                    Body =
                        match json, text with
                        | Some j, _ -> RecordedBody.Json j
                        | None, Some t -> RecordedBody.Text t
                        | None, None -> RecordedBody.Empty
                }
        }
        |> Codec.describe "The request a cassette expects: method, URL, the headers to check, the body (json or text)."

    let private bodyEnd: Codec<BodyEnd> =
        Codec.enum [ BodyEnd.Complete, "complete"; BodyEnd.Reset, "reset"; BodyEnd.Hang, "hang" ]

    let private response: Codec<RecordedResponse> =
        Codec.record {
            let! status = Codec.field "status" (fun (r: RecordedResponse) -> r.Status) Codec.int

            and! headers =
                Codec.defaultField "headers" (fun r -> r.Headers) [] (Codec.keyValues Codec.string)

            and! body = Codec.defaultField "body" (fun r -> r.Body) "" Codec.string
            and! ending = Codec.defaultField "end" (fun r -> r.End) BodyEnd.Complete bodyEnd

            return
                {
                    Status = status
                    Headers = headers
                    Body = body
                    End = ending
                }
        }

    let private interaction: Codec<Interaction> =
        Codec.record {
            let! request = Codec.field "request" (fun (i: Interaction) -> i.Request) request

            and! response =
                Codec.optionalField
                    "response"
                    (fun i ->
                        match i.Outcome with
                        | RecordedOutcome.Response r -> Some r
                        | RecordedOutcome.Failure _ -> None)
                    response

            and! failure =
                Codec.optionalField
                    "failure"
                    (fun i ->
                        match i.Outcome with
                        | RecordedOutcome.Failure e -> Some e
                        | RecordedOutcome.Response _ -> None)
                    HttpErrors.codec

            return
                {
                    Request = request
                    Outcome =
                        match response, failure with
                        | _, Some e -> RecordedOutcome.Failure e
                        | Some r, None -> RecordedOutcome.Response r
                        | None, None ->
                            RecordedOutcome.Response
                                {
                                    Status = 200
                                    Headers = []
                                    Body = ""
                                    End = BodyEnd.Complete
                                }
                }
        }

    let codec: Codec<Cassette> =
        Codec.record {
            let! interactions =
                Codec.field "interactions" (fun (c: Cassette) -> c.Interactions) (Codec.list interaction)

            return { Interactions = interactions }
        }
        |> Codec.describe "Recorded HTTP exchanges, replayed in order."
        |> Codec.named "Cassette"

    /// Reads a cassette file's text.
    let parse (text: string) : Result<Cassette, string> =
        Codec.decodeText codec text
        |> Result.mapError (fun issues -> Issue.renderAtMost 10 issues)

    /// A cassette's text: indented JSON.
    let render (cassette: Cassette) : string =
        Json.serializeIndented (codec.Encode cassette)

    /// How a sent request differs from the recorded one (empty if it matches).
    let differences (recorded: RecordedRequest) (sent: HttpRequest) : string list =
        [
            if not (String.Equals(recorded.Method, sent.Method, StringComparison.OrdinalIgnoreCase)) then
                $"method: expected {recorded.Method}, sent {sent.Method}"

            if recorded.Url <> sent.Url then
                $"url: expected {recorded.Url}, sent {sent.Url}"

            for name, value in recorded.Headers do
                match HttpHeaders.tryFind name sent.Headers with
                | None -> $"header {name}: missing"
                | Some _ when value = Redacted -> ()
                | Some actual when actual <> value -> $"header {name}: expected {value}, sent {actual}"
                | Some _ -> ()

            match recorded.Body with
            | RecordedBody.Empty ->
                if sent.Body.Length > 0 then
                    $"body: expected none, sent {sent.Body.Length} bytes"
            | RecordedBody.Text text ->
                let actual = utf8.GetString sent.Body

                if actual <> text then
                    $"body: expected the text {Json.quote text}, sent {Json.quote actual}"
            | RecordedBody.Json expected ->
                match Json.parse (utf8.GetString sent.Body) with
                | Error e -> $"body: expected JSON, sent text that is not ({e})"
                | Ok actual ->
                    let expectedText = Json.serializeCanonical expected
                    let actualText = Json.serializeCanonical actual

                    if expectedText <> actualText then
                        $"body: expected {expectedText}, sent {actualText}"
        ]

    /// What to record of a request: sensitive headers redacted, the body as JSON when it is JSON.
    let recordRequest (request: HttpRequest) : RecordedRequest =
        let text = utf8.GetString request.Body

        {
            Method = request.Method
            Url = request.Url
            Headers =
                request.Headers
                |> List.map (fun (n, v) ->
                    n.ToLowerInvariant(), (if isOneOf sensitiveRequestHeaders n then Redacted else v))
            Body =
                if request.Body.Length = 0 then
                    RecordedBody.Empty
                else
                    match Json.parse text with
                    | Ok json -> RecordedBody.Json json
                    | Error _ -> RecordedBody.Text text
        }

    /// Response headers as recorded: sensitive values redacted, repeated names joined with `, `.
    let recordHeaders (headers: (string * string) list) : (string * string) list =
        headers
        |> List.groupBy (fun (n, _) -> n.ToLowerInvariant())
        |> List.map (fun (name, values) ->
            let value =
                if isOneOf sensitiveResponseHeaders name then
                    Redacted
                else
                    values |> List.map snd |> String.concat ", "

            name, value)

/// An HTTP interpreter that replays a cassette: each request must match the next interaction (else
/// `CassetteMismatchException`), and gets its recorded outcome, the body cut into pieces by
/// `chunking`. Nothing touches the network.
[<Sealed>]
type CassetteHttp(cassette: Cassette, chunking: Chunking) =
    let gate = obj ()
    let mutable remaining = cassette.Interactions
    let sent = ResizeArray<HttpRequest>()
    let mutable openBodies = 0
    let utf8 = UTF8Encoding(false)

    let forever (cancellation: CancellationToken) : Task<unit> =
        let source =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        cancellation.Register(fun () -> source.TrySetCanceled cancellation |> ignore)
        |> ignore

        source.Task

    let body (request: HttpRequest) (response: RecordedResponse) : HttpBody =
        let mutable pieces = Chunking.split chunking (utf8.GetBytes response.Body)
        let mutable closed = false

        let close () =
            lock gate (fun () ->
                if not closed then
                    closed <- true
                    openBodies <- openBodies - 1)

        let take () =
            lock gate (fun () ->
                if closed then
                    Some None
                else
                    match pieces with
                    | piece :: rest ->
                        pieces <- rest
                        Some(Some piece)
                    | [] -> None)

        let read (cancellation: CancellationToken) : Task<Result<byte array option, HttpError>> =
            task {
                cancellation.ThrowIfCancellationRequested()

                match take () with
                | Some piece -> return Ok piece
                | None ->
                    match response.End with
                    | BodyEnd.Complete ->
                        close ()
                        return Ok None
                    | BodyEnd.Reset -> return Error(HttpError.Reset "the recorded connection broke off")
                    | BodyEnd.Hang ->
                        match request.Timeouts.Idle with
                        | Some _ -> return Error(HttpError.TimedOut HttpPhase.Body)
                        | None ->
                            do! forever cancellation
                            return Ok None
            }

        {
            Read = read
            Close =
                fun () ->
                    close ()
                    Task.FromResult()
        }

    let send (request: HttpRequest) (cancellation: CancellationToken) : Task<Result<HttpResponse, HttpError>> =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<Result<HttpResponse, HttpError>> cancellation
        else
            let next =
                lock gate (fun () ->
                    sent.Add request

                    match remaining with
                    | next :: rest ->
                        remaining <- rest
                        Some next
                    | [] -> None)

            match next with
            | None -> raise (CassetteMismatchException $"the cassette has no more interactions, but {request} was sent")
            | Some interaction ->
                match Cassette.differences interaction.Request request with
                | [] ->
                    match interaction.Outcome with
                    | RecordedOutcome.Failure e -> Task.FromResult(Error e)
                    | RecordedOutcome.Response response ->
                        lock gate (fun () -> openBodies <- openBodies + 1)

                        Task.FromResult(
                            Ok
                                {
                                    Status = response.Status
                                    Headers = response.Headers |> List.map (fun (n, v) -> n.ToLowerInvariant(), v)
                                    Body = body request response
                                }
                        )
                | differences ->
                    let detail = String.concat "\n  " differences

                    raise (
                        CassetteMismatchException
                            $"the request does not match the cassette's {interaction.Request}:\n  {detail}"
                    )

    new(cassette: Cassette) = CassetteHttp(cassette, Chunking.Whole)

    member val Http: Http = { Send = send }

    /// The requests sent so far, as sent.
    member _.Sent: HttpRequest list = lock gate (fun () -> List.ofSeq sent)

    /// Interactions not replayed yet.
    member _.Remaining: Interaction list = lock gate (fun () -> remaining)

    /// Bodies neither read to their end nor closed.
    member _.OpenBodies: int = lock gate (fun () -> openBodies)

/// Records what an HTTP interpreter sends and receives into a cassette (e.g. a live session with a
/// provider, to make conformance cassettes). Secrets in headers are redacted; review cassettes before
/// committing them.
[<Sealed>]
type CassetteRecorder(inner: Http) =
    let gate = obj ()
    let entries = List<RecordedRequest * (unit -> RecordedOutcome)>()

    let send (request: HttpRequest) (cancellation: CancellationToken) : Task<Result<HttpResponse, HttpError>> =
        task {
            let recorded = Cassette.recordRequest request
            let! outcome = inner.Send request cancellation

            match outcome with
            | Error e ->
                lock gate (fun () -> entries.Add(recorded, (fun () -> RecordedOutcome.Failure e)))
                return Error e
            | Ok response ->
                let received = new MemoryStream()
                let mutable ending = BodyEnd.Hang

                let snapshot () =
                    lock gate (fun () ->
                        RecordedOutcome.Response
                            {
                                Status = response.Status
                                Headers = Cassette.recordHeaders response.Headers
                                Body = UTF8Encoding(false).GetString(received.ToArray())
                                End = ending
                            })

                lock gate (fun () -> entries.Add(recorded, snapshot))

                let read (token: CancellationToken) =
                    task {
                        let! piece = response.Body.Read token

                        lock gate (fun () ->
                            match piece with
                            | Ok(Some bytes) -> received.Write(bytes, 0, bytes.Length)
                            | Ok None -> ending <- BodyEnd.Complete
                            | Error(HttpError.Reset _) -> ending <- BodyEnd.Reset
                            | Error _ -> ())

                        return piece
                    }

                return
                    Ok
                        { response with
                            Body = { response.Body with Read = read }
                        }
        }

    member val Http: Http = { Send = send }

    /// What was recorded so far. A body not read to its end is recorded as far as it was read, and
    /// hangs there when replayed.
    member _.Cassette() : Cassette =
        let snapshot = lock gate (fun () -> List.ofSeq entries)

        {
            Interactions =
                snapshot
                |> List.map (fun (request, outcome) ->
                    {
                        Request = request
                        Outcome = outcome ()
                    })
        }
