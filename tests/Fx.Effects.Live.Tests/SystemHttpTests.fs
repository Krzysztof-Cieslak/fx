module Fx.Effects.Live.Tests.SystemHttpTests

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Effects.Tests.Conformance

let private latin1 = Encoding.Latin1

/// A minimal HTTP/1.1 server on loopback that plays scripted replies: one request per connection,
/// chunked bodies flushed piece by piece (portable, unlike HttpListener on Windows).
type private Loopback(routes: HttpConformance.Routes) =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    do listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    let stop = new CancellationTokenSource()

    let readRequest (stream: NetworkStream) : Task<HttpRequest> =
        task {
            let buffer = Array.zeroCreate<byte> 8192
            use received = new IO.MemoryStream()
            let mutable headEnd = -1

            while headEnd < 0 do
                let! n = stream.ReadAsync(buffer, 0, buffer.Length, stop.Token)

                if n = 0 then
                    failwith "the client closed the connection"

                received.Write(buffer, 0, n)
                let text = latin1.GetString(received.ToArray())
                headEnd <- text.IndexOf("\r\n\r\n", StringComparison.Ordinal)

            let all = received.ToArray()
            let head = latin1.GetString(all, 0, headEnd)
            let lines = head.Split("\r\n")
            let parts = lines[0].Split(' ')

            let headers =
                [
                    for line in Array.skip 1 lines do
                        let colon = line.IndexOf ':'
                        line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim()
                ]

            let length =
                HttpHeaders.tryFind "content-length" headers
                |> Option.map int
                |> Option.defaultValue 0

            use body = new IO.MemoryStream()
            body.Write(all, headEnd + 4, all.Length - headEnd - 4)

            while int body.Length < length do
                let! n = stream.ReadAsync(buffer, 0, buffer.Length, stop.Token)

                if n = 0 then
                    failwith "the client closed the connection"

                body.Write(buffer, 0, n)

            return
                {
                    Method = parts[0]
                    Url = parts[1]
                    Headers = headers
                    Body = body.ToArray()
                    Timeouts = HttpTimeouts.none
                }
        }

    let serve (client: TcpClient) =
        task {
            use client = client
            use stream = client.GetStream()
            let! request = readRequest stream
            let reply = routes request

            if reply.Delay > TimeSpan.Zero then
                do! Task.Delay(reply.Delay, stop.Token)

            let write (bytes: byte array) =
                task {
                    do! stream.WriteAsync(bytes, 0, bytes.Length, stop.Token)
                    do! stream.FlushAsync stop.Token
                }

            let head = StringBuilder()
            head.Append($"HTTP/1.1 {reply.Status} Scripted\r\n") |> ignore

            for name, value in reply.Headers do
                head.Append($"{name}: {value}\r\n") |> ignore

            head.Append("Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n") |> ignore
            do! write (latin1.GetBytes(head.ToString()))
            let mutable broken = false

            for step in reply.Body do
                if not broken then
                    match step with
                    | BodyStep.Chunk bytes ->
                        do! write (latin1.GetBytes $"{bytes.Length:x}\r\n")
                        do! write bytes
                        do! write (latin1.GetBytes "\r\n")
                    | BodyStep.Pause duration -> do! Task.Delay(duration, stop.Token)
                    | BodyStep.Hang ->
                        // Until the client goes away (or the server stops).
                        let! _ = stream.ReadAsync(Array.zeroCreate<byte> 1, 0, 1, stop.Token)
                        broken <- true
                    | BodyStep.Break -> broken <- true

            if not broken then
                do! write (latin1.GetBytes "0\r\n\r\n")
        }

    do
        task {
            try
                while true do
                    let! client = listener.AcceptTcpClientAsync stop.Token

                    task {
                        try
                            do! serve client
                        with _ ->
                            ()
                    }
                    |> ignore
            with _ ->
                ()
        }
        |> ignore

    member _.Url(path: string) = $"http://127.0.0.1:{port}{path}"

    member _.Stop() =
        stop.Cancel()
        listener.Stop()

/// A loopback URL where nothing listens: a port that was free a moment ago.
let private unreachable () =
    let probe = new TcpListener(IPAddress.Loopback, 0)
    probe.Start()
    let port = (probe.LocalEndpoint :?> IPEndPoint).Port
    probe.Stop()
    $"http://127.0.0.1:{port}/"

let private client = lazy (SystemHttp.client ())

let private live (routes: HttpConformance.Routes) : HttpConformance.Server =
    let server = new Loopback(routes)

    {
        Http = SystemHttp.create client.Value
        Url = server.Url
        Unreachable = unreachable ()
        Stop = server.Stop
    }

[<Tests>]
let tests =
    testList
        "SystemHttp"
        [
            HttpConformance.tests "SystemHttp" live

            test "an invalid method is an invalid request" {
                let http = SystemHttp.create client.Value

                let outcome =
                    (http.Send (HttpRequest.create "NOT A METHOD" "http://127.0.0.1:1/") CancellationToken.None)
                        .GetAwaiter()
                        .GetResult()

                match outcome with
                | Error(HttpError.InvalidRequest _) -> ()
                | other -> failtest $"expected an invalid request, got {other}"
            }

            test "the user agent names the product" {
                let mutable agent = ""

                let server =
                    new Loopback(fun request ->
                        agent <- HttpRequest.header "user-agent" request |> Option.defaultValue ""
                        HttpReply.text 200 "ok")

                try
                    let http = SystemHttp.create client.Value

                    match (http.Send (HttpRequest.get (server.Url "/")) CancellationToken.None).Result with
                    | Ok response -> (HttpBody.readAll 100 response.Body CancellationToken.None).Result |> ignore
                    | Error e -> failtest (string e)

                    Expect.stringStarts agent (Fx.Product.Name + "/") "user agent"
                finally
                    server.Stop()
            }
        ]
