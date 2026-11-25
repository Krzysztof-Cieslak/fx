module Fx.Components.Live.Tests.HttpFetchTests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Components
open Fx.Components.Live
open Fx.Components.Live.Tests.LiveEnv
open Fx.Sdk

/// What the test server answers for a path.
type private Reply =
    | Body of status: int * body: byte array
    | Redirect of location: string
    /// Sends headers claiming more than it sends, then closes.
    | Truncated of body: byte array

/// A minimal HTTP/1.1 server on loopback: one request per connection (portable, unlike
/// HttpListener on Windows).
type private Server(routes: Map<string, Reply>) =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    do listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    let stop = new CancellationTokenSource()

    let serve (client: TcpClient) =
        task {
            use client = client
            use stream = client.GetStream()
            let buffer = Array.zeroCreate<byte> 8192
            let request = StringBuilder()

            while not (request.ToString().Contains "\r\n\r\n") do
                let! n = stream.ReadAsync(buffer, 0, buffer.Length)

                if n = 0 then
                    failwith "the client closed the connection"

                request.Append(Encoding.ASCII.GetString(buffer, 0, n)) |> ignore

            let path = request.ToString().Split(' ').[1]

            let head (status: int) (length: int) (extra: string) =
                Encoding.ASCII.GetBytes
                    $"HTTP/1.1 {status} X\r\nContent-Length: {length}\r\nConnection: close\r\n{extra}\r\n"

            let write (bytes: byte array) =
                stream.WriteAsync(bytes, 0, bytes.Length)

            match routes.TryFind path with
            | Some(Body(status, body)) ->
                do! write (head status body.Length "")
                do! write body
            | Some(Redirect location) -> do! write (head 302 0 $"Location: {location}\r\n")
            | Some(Truncated body) ->
                do! write (head 200 (body.Length * 2) "")
                do! write body
            | None -> do! write (head 404 0 "")
        }

    do
        task {
            try
                while true do
                    let! client = listener.AcceptTcpClientAsync stop.Token
                    serve client |> ignore
            with _ ->
                ()
        }
        |> ignore

    member _.Url(path: string) = $"http://127.0.0.1:{port}{path}"

    interface IDisposable with
        member _.Dispose() =
            stop.Cancel()
            listener.Stop()

let private download (client: Http.HttpClient) (url: string) (destination: string) (max: int64) =
    ((HttpFetch.create client).Download
        {
            Url = url
            Destination = destination
            MaxBytes = max
        }
        CancellationToken.None)
        .WaitAsync(TimeSpan.FromMinutes 1.0)
        .GetAwaiter()
        .GetResult()

[<Tests>]
let tests =
    let body = Array.init 200_000 (fun i -> byte (i % 251))

    let routes =
        Map.ofList
            [
                "/archive", Body(200, body)
                "/moved", Redirect "/archive"
                "/error", Body(503, [||])
                "/short", Truncated body
            ]

    testSequenced
    <| testList
        "HttpFetch"
        [
            test "downloads to a file, hashing what it writes, through redirects" {
                use server = new Server(routes)
                use client = HttpFetch.client ()
                let dir = tempDirectory ()

                try
                    let file = Path.Combine(dir, "a")

                    for path in [ "/archive"; "/moved" ] do
                        match download client (server.Url path) file 1_000_000L with
                        | Ok hash ->
                            Expect.equal hash.Size (int64 body.Length) $"{path}: size"

                            Expect.equal
                                hash.Digest
                                (Sha256(Convert.ToHexStringLower(SHA256.HashData body)))
                                $"{path}: digest"

                            Expect.equal (File.ReadAllBytes file) body $"{path}: content"
                        | Error e -> failtest $"{path}: {e}"
                finally
                    removeDirectory dir
            }

            test "statuses, limits and broken connections are typed errors" {
                use server = new Server(routes)
                use client = HttpFetch.client ()
                let dir = tempDirectory ()

                try
                    let file = Path.Combine(dir, "a")

                    match download client (server.Url "/missing") file 1_000_000L with
                    | Error(HttpStatus(_, 404) as e) -> Expect.isFalse e.Transient "404 is final"
                    | other -> failtest $"{other}"

                    match download client (server.Url "/error") file 1_000_000L with
                    | Error(HttpStatus(_, 503) as e) -> Expect.isTrue e.Transient "503 may pass"
                    | other -> failtest $"{other}"

                    match download client (server.Url "/archive") file 1000L with
                    | Error(FetchError.TooLarge(_, 1000L)) -> ()
                    | other -> failtest $"{other}"

                    match download client (server.Url "/short") file 1_000_000L with
                    | Error(Network _ as e) -> Expect.isTrue e.Transient "a broken download may pass"
                    | other -> failtest $"{other}"

                    // Nothing listens there any more.
                    let closed = server.Url "/archive"
                    (server :> IDisposable).Dispose()

                    match download client closed file 1_000_000L with
                    | Error(Network _) -> ()
                    | other -> failtest $"{other}"
                finally
                    removeDirectory dir
            }
        ]
