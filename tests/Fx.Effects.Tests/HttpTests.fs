module Fx.Effects.Tests.HttpTests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Tests.Conformance

/// Real time for the conformance suite (its timeouts are a few hundred milliseconds). Tests are an
/// imperative shell; production code gets its clock from the environment.
let private realClock: Clock =
    let origin = Diagnostics.Stopwatch.GetTimestamp()

    {
        Now = fun () -> Instant.ofDateTimeOffset DateTimeOffset.UtcNow
        Monotonic = fun () -> MonoTime(Diagnostics.Stopwatch.GetElapsedTime origin)
        Delay = fun duration cancellation -> task { do! Task.Delay(duration, cancellation) }
    }

let private unreachable = "http://unreachable.test/"

/// The in-memory server: `InMemoryHttp` answering from the routes, with paths as URLs.
let private inMemory (routes: HttpConformance.Routes) : HttpConformance.Server =
    let http =
        InMemoryHttp(
            realClock,
            fun request ->
                if request.Url = unreachable then
                    Some(Error(ConnectFailed "nothing listens"))
                else
                    let uri = Uri request.Url
                    Some(Ok(routes { request with Url = uri.PathAndQuery }))
        )

    {
        Http = http.Http
        Url = fun path -> "http://in-memory.test" + path
        Unreachable = unreachable
        Stop = ignore
    }

let private utf8 = UTF8Encoding(false)

[<Tests>]
let tests =
    testList
        "Http"
        [
            HttpConformance.tests "InMemoryHttp" inMemory

            test "headers compare names without case and set replaces" {
                let headers = [ "Content-Type", "a"; "x-a", "1"; "X-A", "2" ]
                Expect.equal (HttpHeaders.tryFind "content-type" headers) (Some "a") "found"
                Expect.equal (HttpHeaders.findAll "x-a" headers) [ "1"; "2" ] "all"
                Expect.equal (HttpHeaders.set "x-A" "3" headers) [ "Content-Type", "a"; "x-A", "3" ] "set"
            }

            test "readAll keeps at most the limit, reads to the end and closes" {
                let fake =
                    InMemoryHttp(
                        realClock,
                        fun _ ->
                            Some(Ok(HttpReply.create 200 [ Chunk(utf8.GetBytes "abc"); Chunk(utf8.GetBytes "def") ]))
                    )

                let response =
                    match (fake.Http.Send (HttpRequest.get "http://x.test/") CancellationToken.None).Result with
                    | Ok response -> response
                    | Error e -> failtest (string e)

                Expect.equal fake.OpenBodies 1 "open"

                let kept =
                    (HttpBody.readAll 4 response.Body CancellationToken.None).Result
                    |> Result.map utf8.GetString

                Expect.equal kept (Ok "abcd") "kept"
                Expect.equal fake.OpenBodies 0 "closed"
                Expect.equal fake.Requests.Length 1 "recorded"
            }

            test "an unexpected request is a defect of the test" {
                let fake = InMemoryHttp(realClock, fun _ -> None)

                Expect.throwsT<InvalidOperationException>
                    (fun () ->
                        (fake.Http.Send (HttpRequest.get "http://x.test/") CancellationToken.None)
                            .GetAwaiter()
                            .GetResult()
                        |> ignore)
                    "throws"
            }
        ]
