/// The HTTP conformance suite: what every `Http` interpreter must do, against a server that a subject
/// stands up from scripted replies. Also compiled into Fx.Effects.Live.Tests, where the server is a
/// loopback socket.
module Fx.Effects.Tests.Conformance.HttpConformance

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Fakes

/// What a test serves: the reply to each request, which arrives with its path as its URL.
type Routes = HttpRequest -> HttpReply

/// A server a subject stands up for one test, and the capability that reaches it.
type Server =
    {
        Http: Http
        /// The URL of a path on the server.
        Url: string -> string
        /// A URL where nothing listens.
        Unreachable: string
        Stop: unit -> unit
    }

    interface IDisposable with
        member this.Dispose() = this.Stop()

let private utf8 = UTF8Encoding(false)

let private bounded (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

let private send (server: Server) (request: HttpRequest) : Result<HttpResponse, HttpError> =
    bounded (server.Http.Send request CancellationToken.None)

let private ok (result: Result<'T, HttpError>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failtest $"expected a response, got {e}"

let private readText (body: HttpBody) : string =
    match bounded (HttpBody.readAll 1_000_000 body CancellationToken.None) with
    | Ok bytes -> utf8.GetString bytes
    | Error e -> failtest $"reading the body failed: {e}"

let private text (s: string) = BodyStep.Chunk(utf8.GetBytes s)

let private short = TimeSpan.FromMilliseconds 300.0

/// Waits for the task and expects it to be cancelled.
let private expectCancelled (task: Task) =
    let outcome =
        try
            task.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()
            None
        with e ->
            Some e

    match outcome with
    | Some(:? OperationCanceledException) -> ()
    | Some e -> failtest $"expected a cancellation, got {e.GetType().Name}"
    | None -> failtest "expected a cancellation, the call completed"

/// Tests that an interpreter conforms. `serve` stands up a fresh server for each test.
let tests (name: string) (serve: Routes -> Server) : Test =
    let route (handle: HttpRequest -> HttpReply) = handle

    testList
        $"Http conformance ({name})"
        [
            testCase "a response has its status, lower-case headers and body" (fun () ->
                use server =
                    serve (
                        route (fun _ ->
                            HttpReply.create 200 [ text "hello "; text "world" ]
                            |> HttpReply.withHeader "X-Test" "yes")
                    )

                let response = send server (HttpRequest.get (server.Url "/hello")) |> ok
                Expect.equal response.Status 200 "status"
                Expect.equal (HttpHeaders.tryFind "x-test" response.Headers) (Some "yes") "header"
                Expect.isTrue (response.Headers |> List.exists (fun (n, _) -> n = "x-test")) "lower-case name"
                Expect.equal (readText response.Body) "hello world" "body")

            testCase "the request arrives as sent" (fun () ->
                use server =
                    serve (
                        route (fun request ->
                            let thing = HttpRequest.header "x-thing" request |> Option.defaultValue "-"

                            HttpReply.text
                                200
                                $"{request.Method} {request.Url} {thing} {HttpRequest.bodyText request}")
                    )

                let request =
                    HttpRequest.post (server.Url "/echo?q=1") (utf8.GetBytes "payload ü")
                    |> HttpRequest.withHeader "X-Thing" "42"
                    |> HttpRequest.withHeader "Content-Type" "text/plain; charset=utf-8"

                let response = send server request |> ok
                Expect.equal (readText response.Body) "POST /echo?q=1 42 payload ü" "echo")

            testCase "error statuses and redirects are responses" (fun () ->
                use server =
                    serve (
                        route (fun request ->
                            if request.Url = "/busy" then
                                HttpReply.text 503 "busy" |> HttpReply.withHeader "Retry-After" "7"
                            else
                                HttpReply.create 302 [] |> HttpReply.withHeader "Location" "/elsewhere")
                    )

                let busy = send server (HttpRequest.get (server.Url "/busy")) |> ok
                Expect.equal busy.Status 503 "status"
                Expect.equal (HttpHeaders.tryFind "retry-after" busy.Headers) (Some "7") "retry-after"
                Expect.equal (readText busy.Body) "busy" "body"
                let moved = send server (HttpRequest.get (server.Url "/moved")) |> ok
                Expect.equal moved.Status 302 "not followed"
                moved.Body.Close() |> bounded)

            testCase "the body streams before it is complete" (fun () ->
                use server =
                    serve (route (fun _ -> HttpReply.create 200 [ text "first"; BodyStep.Hang ]))

                let response = send server (HttpRequest.get (server.Url "/stream")) |> ok

                match bounded (response.Body.Read CancellationToken.None) with
                | Ok(Some bytes) -> Expect.equal (utf8.GetString bytes) "first" "the first piece"
                | other -> failtest $"expected the first piece, got {other}"

                use cancel = new CancellationTokenSource(short)
                expectCancelled (response.Body.Read cancel.Token)
                response.Body.Close() |> bounded)

            testCase "a body that stalls times out" (fun () ->
                use server =
                    serve (route (fun _ -> HttpReply.create 200 [ text "a"; BodyStep.Hang ]))

                let request =
                    HttpRequest.get (server.Url "/stall")
                    |> HttpRequest.withTimeouts
                        { HttpTimeouts.none with
                            Idle = Some short
                        }

                let response = send server request |> ok
                let mutable outcome = bounded (response.Body.Read CancellationToken.None)

                while (match outcome with
                       | Ok(Some _) -> true
                       | _ -> false) do
                    outcome <- bounded (response.Body.Read CancellationToken.None)

                Expect.equal outcome (Error(HttpError.TimedOut HttpPhase.Body)) "timed out"
                response.Body.Close() |> bounded)

            testCase "a slow response times out" (fun () ->
                use server =
                    serve (route (fun _ -> HttpReply.text 200 "late" |> HttpReply.after (TimeSpan.FromSeconds 10.0)))

                let request =
                    HttpRequest.get (server.Url "/slow")
                    |> HttpRequest.withTimeouts
                        { HttpTimeouts.none with
                            Response = Some short
                        }

                match send server request with
                | Error(HttpError.TimedOut HttpPhase.Response) -> ()
                | other -> failtest $"expected a response timeout, got {other}")

            testCase "cancelling while waiting for the headers" (fun () ->
                use server =
                    serve (route (fun _ -> HttpReply.text 200 "late" |> HttpReply.after (TimeSpan.FromSeconds 10.0)))

                use cancel = new CancellationTokenSource(short)
                expectCancelled (server.Http.Send (HttpRequest.get (server.Url "/slow")) cancel.Token))

            testCase "a connection that breaks off is a reset" (fun () ->
                use server =
                    serve (route (fun _ -> HttpReply.create 200 [ text "partial"; BodyStep.Break ]))

                let response = send server (HttpRequest.get (server.Url "/broken")) |> ok
                let received = StringBuilder()
                let mutable outcome = bounded (response.Body.Read CancellationToken.None)

                while (match outcome with
                       | Ok(Some bytes) ->
                           received.Append(utf8.GetString bytes) |> ignore
                           true
                       | _ -> false) do
                    outcome <- bounded (response.Body.Read CancellationToken.None)

                match outcome with
                | Error(HttpError.Reset _) -> ()
                | other -> failtest $"expected a reset, got {other}"

                Expect.isTrue ("partial".StartsWith(received.ToString(), StringComparison.Ordinal)) "a prefix"
                response.Body.Close() |> bounded)

            testCase "nothing listening is a connection failure" (fun () ->
                use server = serve (route (fun _ -> HttpReply.text 200 "unused"))

                match send server (HttpRequest.get server.Unreachable) with
                | Error(HttpError.ConnectFailed _) -> ()
                | other -> failtest $"expected a connection failure, got {other}")

            testCase "a URL that is not absolute http(s) is an invalid request" (fun () ->
                use server = serve (route (fun _ -> HttpReply.text 200 "unused"))

                for url in [ "/relative"; "ftp://example.com/x"; "not a url" ] do
                    match send server (HttpRequest.get url) with
                    | Error(HttpError.InvalidRequest _) -> ()
                    | other -> failtest $"{url}: expected an invalid request, got {other}")

            testCase "a closed body reads as ended" (fun () ->
                use server =
                    serve (route (fun _ -> HttpReply.create 200 [ text "a"; BodyStep.Hang ]))

                let response = send server (HttpRequest.get (server.Url "/close")) |> ok
                response.Body.Close() |> bounded
                response.Body.Close() |> bounded
                Expect.equal (bounded (response.Body.Read CancellationToken.None)) (Ok None) "ended")

            testCase "a large body arrives whole" (fun () ->
                let piece = String.replicate 1000 "0123456789"

                use server =
                    serve (route (fun _ -> HttpReply.create 200 [ for _ in 1..20 -> text piece ]))

                let response = send server (HttpRequest.get (server.Url "/large")) |> ok
                Expect.equal (readText response.Body) (String.replicate 20 piece) "body")
        ]
