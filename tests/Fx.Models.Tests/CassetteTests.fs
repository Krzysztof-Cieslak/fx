module Fx.Models.Tests.CassetteTests

open System
open System.Threading
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Models
open Fx.Models.Tests.Support

let private recorded: RecordedRequest =
    {
        Method = "POST"
        Url = "https://api.example.test/v1/chat"
        Headers = [ "content-type", "application/json"; "x-api-key", Cassette.Redacted ]
        Body = RecordedBody.Json(Json.Object [ "a", Json.Number(JsonNumber.ofInt64 1L); "b", Json.String "ż" ])
    }

let private response (body: string) (ending: BodyEnd) =
    RecordedOutcome.Response
        {
            Status = 200
            Headers = [ "content-type", "text/event-stream" ]
            Body = body
            End = ending
        }

let private cassette (outcome: RecordedOutcome) : Cassette =
    {
        Interactions =
            [
                {
                    Request = recorded
                    Outcome = outcome
                }
            ]
    }

let private matching: HttpRequest =
    HttpRequest.post recorded.Url (bytes "{ \"b\": \"\\u017c\", \"a\": 1.0 }")
    |> HttpRequest.withHeader "Content-Type" "application/json"
    |> HttpRequest.withHeader "X-Api-Key" "sk-secret"

let private send (http: Http) (request: HttpRequest) =
    wait (http.Send request CancellationToken.None)

let private readAll (body: HttpBody) =
    match wait (HttpBody.readAll 1_000_000 body CancellationToken.None) with
    | Ok bytes -> utf8.GetString bytes
    | Error e -> failtest (string e)

[<Tests>]
let tests =
    testList
        "Cassettes"
        [
            test "a matching request gets the recorded response, cut as asked" {
                let fake =
                    CassetteHttp(cassette (response "data: żółw\n\n" BodyEnd.Complete), Chunking.Every 3)

                match send fake.Http matching with
                | Ok r ->
                    Expect.equal r.Status 200 "status"
                    let mutable pieces = []
                    let mutable reading = true

                    while reading do
                        match wait (r.Body.Read CancellationToken.None) with
                        | Ok(Some piece) -> pieces <- pieces @ [ piece ]
                        | Ok None -> reading <- false
                        | Error e -> failtest (string e)

                    Expect.isTrue (pieces |> List.forall (fun p -> p.Length <= 3)) "pieces of 3 bytes"
                    Expect.equal (utf8.GetString(Array.concat pieces)) "data: żółw\n\n" "the body"
                | Error e -> failtest (string e)

                Expect.equal fake.Remaining [] "used"
                Expect.equal fake.OpenBodies 0 "closed at its end"
            }

            test "JSON bodies compare canonically; redacted headers only need to be present" {
                Expect.equal (Cassette.differences recorded matching) [] "matches"

                let other =
                    { matching with
                        Url = recorded.Url + "?x"
                        Headers = [ "content-type", "text/plain" ]
                        Body = bytes "{\"a\":2,\"b\":\"ż\"}"
                    }

                Expect.equal
                    (Cassette.differences recorded other)
                    [
                        "url: expected https://api.example.test/v1/chat, sent https://api.example.test/v1/chat?x"
                        "header content-type: expected application/json, sent text/plain"
                        "header x-api-key: missing"
                        "body: expected {\"a\":1,\"b\":\"ż\"}, sent {\"a\":2,\"b\":\"ż\"}"
                    ]
                    "differences"
            }

            test "a request that differs, or one too many, is a mismatch" {
                let fake = CassetteHttp(cassette (response "" BodyEnd.Complete))

                Expect.throwsT<CassetteMismatchException>
                    (fun () -> send fake.Http (HttpRequest.get recorded.Url) |> ignore)
                    "differs"

                Expect.throwsT<CassetteMismatchException> (fun () -> send fake.Http matching |> ignore) "one too many"
            }

            test "recorded endings: reset, hang (until cancelled, or at once under an idle timeout), failure" {
                let reset = CassetteHttp(cassette (response "data: a" BodyEnd.Reset))

                match send reset.Http matching with
                | Ok r ->
                    Expect.equal (wait (r.Body.Read CancellationToken.None)) (Ok(Some(bytes "data: a"))) "the body"

                    match wait (r.Body.Read CancellationToken.None) with
                    | Error(HttpError.Reset _) -> ()
                    | other -> failtest $"expected a reset, got {other}"
                | Error e -> failtest (string e)

                let hang = CassetteHttp(cassette (response "" BodyEnd.Hang))

                match send hang.Http matching with
                | Ok r ->
                    use cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)

                    Expect.throwsC (fun () -> wait (r.Body.Read cancel.Token) |> ignore) (fun e ->
                        Expect.isTrue (e :? OperationCanceledException) "cancelled")
                | Error e -> failtest (string e)

                let stalled = CassetteHttp(cassette (response "" BodyEnd.Hang))

                match
                    send
                        stalled.Http
                        (matching
                         |> HttpRequest.withTimeouts
                             { HttpTimeouts.none with
                                 Idle = Some(TimeSpan.FromSeconds 1.0)
                             })
                with
                | Ok r ->
                    Expect.equal
                        (wait (r.Body.Read CancellationToken.None))
                        (Error(HttpError.TimedOut HttpPhase.Body))
                        "timed out"
                | Error e -> failtest (string e)

                let failed =
                    CassetteHttp(cassette (RecordedOutcome.Failure(HttpError.ConnectFailed "refused")))

                Expect.equal
                    (send failed.Http matching |> Result.map ignore)
                    (Error(HttpError.ConnectFailed "refused"))
                    "failure"
            }

            test "chunkings cut anywhere and keep every byte" {
                let data = Array.init 50 byte

                for chunking in
                    [
                        Chunking.Whole
                        Chunking.Every 1
                        Chunking.Every 7
                        Chunking.Every 100
                        Chunking.At [ 49; 3; 3; 0; 60; 10 ]
                    ] do
                    let pieces = Chunking.split chunking data
                    Expect.equal (Array.concat pieces) data (string chunking)
                    Expect.isTrue (pieces |> List.forall (fun p -> p.Length > 0)) "no empty pieces"

                Expect.equal (Chunking.split (Chunking.At [ 3; 10 ]) data |> List.map Array.length) [ 3; 7; 40 ] "at"
            }

            test "the recorder writes what was sent and received, secrets redacted, and it replays" {
                let server =
                    InMemoryHttp(
                        (FakeClock.Create()).Clock,
                        fun _ ->
                            Some(
                                Ok(
                                    HttpReply.create
                                        200
                                        [ BodyStep.Chunk(bytes "data: 1\n\n"); BodyStep.Chunk(bytes "data: 2\n\n") ]
                                    |> HttpReply.withHeader "Set-Cookie" "session=abc"
                                    |> HttpReply.withHeader "Content-Type" "text/event-stream"
                                )
                            )
                    )

                let recorder = CassetteRecorder(server.Http)

                match send recorder.Http matching with
                | Ok r -> Expect.equal (readAll r.Body) "data: 1\n\ndata: 2\n\n" "read through"
                | Error e -> failtest (string e)

                let tape = recorder.Cassette()
                let text = Cassette.render tape
                Expect.isFalse (text.Contains "sk-secret") "no key"
                Expect.isFalse (text.Contains "session=abc") "no cookie"
                Expect.equal (Cassette.parse text) (Ok tape) "the text round-trips"

                match tape.Interactions with
                | [ { Outcome = RecordedOutcome.Response r } ] ->
                    Expect.equal r.Body "data: 1\n\ndata: 2\n\n" "body"
                    Expect.equal r.End BodyEnd.Complete "complete"
                | other -> failtest $"recorded {other}"

                let replay = CassetteHttp(tape)

                match send replay.Http matching with
                | Ok r -> Expect.equal (readAll r.Body) "data: 1\n\ndata: 2\n\n" "replayed"
                | Error e -> failtest (string e)
            }
        ]
