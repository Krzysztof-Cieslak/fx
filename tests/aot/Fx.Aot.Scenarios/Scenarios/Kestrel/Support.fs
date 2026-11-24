/// Shared pieces of the Kestrel scenarios (T004, docs/spikes/kestrel.md): the token, servers on an
/// ephemeral port or a socket, and HttpClient / ClientWebSocket clients for loopback, Unix domain
/// sockets and named pipes. Output never shows ports, paths or times.
module Fx.Aot.Scenarios.Kestrel.Support

open System
open System.IO
open System.IO.Pipes
open System.Net.Http
open System.Net.Http.Headers
open System.Net.ServerSentEvents
open System.Net.Sockets
open System.Net.WebSockets
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Aot.Scenarios
open Fx.Transport.Http
open Fx.Transport.Http.LocalServer

/// The scenarios' bearer token. A real host draws a fresh one per listener.
let token = "aot-scenario-token-0123456789-abcdefghij"

let policy = LocalAccess.policy token

/// Fails the scenario instead of hanging when something never happens.
let within (t: Task<'T>) : Task<'T> = t.WaitAsync(TimeSpan.FromSeconds 30.0)

let withinTask (t: Task) : Task = t.WaitAsync(TimeSpan.FromSeconds 30.0)

/// A short hex id for socket and pipe names. Never printed.
let uniqueName (prefix: string) =
    prefix
    + Convert.ToHexString(RandomNumberGenerator.GetBytes 6).ToLowerInvariant()

/// Starts a server on 127.0.0.1 with an ephemeral port.
let startLoopback (configure: Options -> Options) (endpoints: Endpoint list) : Task<Server> =
    LocalServer.start (configure (LocalServer.options (Loopback 0) policy endpoints))

/// `http://127.0.0.1:<port>/` of a loopback server.
let baseUri (server: Server) : Uri =
    Uri(
        server.Addresses
        |> List.find (fun a -> a.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
    )

/// How a client reaches the server.
type Connect =
    | Tcp
    | Unix of path: string
    | Pipe of name: string

    override this.ToString() =
        match this with
        | Tcp -> "tcp"
        | Unix _ -> "unix socket"
        | Pipe _ -> "named pipe"

/// A handler that connects over TCP, a Unix domain socket or a named pipe. No proxy: the system
/// proxy settings must not affect local connections. No draining: a response disposed before its
/// end (an abandoned event stream) closes the connection at once.
let handler (connect: Connect) : SocketsHttpHandler =
    let h =
        new SocketsHttpHandler(
            UseProxy = false,
            AllowAutoRedirect = false,
            Expect100ContinueTimeout = TimeSpan.FromSeconds 30.0,
            MaxResponseDrainSize = 0
        )

    match connect with
    | Tcp -> ()
    | Unix path ->
        h.ConnectCallback <-
            fun _ cancellation ->
                task {
                    let socket =
                        new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

                    let mutable connected = false

                    try
                        do! socket.ConnectAsync(UnixDomainSocketEndPoint path, cancellation)
                        connected <- true
                        return new NetworkStream(socket, true) :> Stream
                    finally
                        if not connected then
                            socket.Dispose()
                }
                |> ValueTask<Stream>
    | Pipe name ->
        h.ConnectCallback <-
            fun _ cancellation ->
                task {
                    let pipe =
                        new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)

                    do! pipe.ConnectAsync cancellation
                    return pipe :> Stream
                }
                |> ValueTask<Stream>

    h

/// An HttpClient for a server; over sockets and pipes the host is `localhost`.
let client (connect: Connect) (baseAddress: Uri) : HttpClient =
    new HttpClient(handler connect, true, BaseAddress = baseAddress)

/// An HttpClient that sends the bearer token with every request.
let authorizedClient (connect: Connect) (baseAddress: Uri) : HttpClient =
    let c = client connect baseAddress
    c.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)
    c

/// The status code and body of a response, e.g. `200 hello`.
let describe (response: HttpResponseMessage) : Task<string> =
    task {
        let! body = response.Content.ReadAsStringAsync()
        let text = body.TrimEnd '\n'
        return $"{int response.StatusCode} {Show.escape text}".TrimEnd()
    }

/// Values of a response header (content headers included), or `-`.
let header (response: HttpResponseMessage) (name: string) : string =
    let joined (found: bool, values: string seq | null) =
        match found, values with
        | true, NonNull values -> Some(String.Join(", ", values))
        | _ -> None

    joined (response.Headers.TryGetValues name)
    |> Option.orElseWith (fun () -> joined (response.Content.Headers.TryGetValues name))
    |> Option.defaultValue "-"

/// A ClientWebSocket set up by `configure`, connected through `connect`. `Ok` with the socket, or
/// `Error` with the HTTP status of the rejected handshake.
let connectWebSocket
    (connect: Connect)
    (uri: Uri)
    (configure: ClientWebSocketOptions -> unit)
    : Task<Result<ClientWebSocket, int>> =
    task {
        let socket = new ClientWebSocket()
        socket.Options.CollectHttpResponseDetails <- true
        socket.Options.KeepAliveInterval <- TimeSpan.Zero
        configure socket.Options

        // The invoker is not disposed here: it owns the handler whose connection the socket keeps using.
        let invoker = new HttpMessageInvoker(handler connect)

        try
            do! withinTask (socket.ConnectAsync(uri, invoker, CancellationToken.None))
            return Ok socket
        with :? WebSocketException ->
            let status = int socket.HttpStatusCode
            socket.Dispose()
            return Error status
    }

/// Adds the bearer token header to a WebSocket handshake.
let withToken (options: ClientWebSocketOptions) =
    options.SetRequestHeader("Authorization", "Bearer " + token)

/// The WebSocket URI for a path on a server reached through `connect`.
let webSocketUri (connect: Connect) (server: Server) (path: string) : Uri =
    match connect with
    | Tcp -> Uri((baseUri server).ToString().Replace("http://", "ws://") + path.TrimStart '/')
    | Unix _
    | Pipe _ -> Uri("ws://localhost" + path)

/// The hex SHA-256 prefix of some bytes, to compare large payloads in output.
let digest (bytes: byte array) : string =
    Convert.ToHexString(SHA256.HashData bytes).Substring(0, 16).ToLowerInvariant()

/// Reads server-sent events from a response, one at a time.
type EventReader(response: HttpResponseMessage, stream: Stream) =
    let events = SseParser.Create(stream).EnumerateAsync().GetAsyncEnumerator()

    /// The next event as `id/type/data`, or None at the end of the stream.
    member _.Next() : Task<string option> =
        task {
            let! more = within (events.MoveNextAsync().AsTask())

            if more then
                let item = events.Current

                let id =
                    match item.EventId with
                    | null -> "-"
                    | id -> id

                return Some $"{id}/{item.EventType}/{Show.escape item.Data}"
            else
                return None
        }

    member _.Response = response

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            let close =
                task {
                    do! events.DisposeAsync()
                    do! stream.DisposeAsync()
                    response.Dispose()
                }

            ValueTask(close :> Task)

/// Opens an event stream: sends the request and returns as soon as the headers arrive.
let openEvents (http: HttpClient) (request: HttpRequestMessage) : Task<EventReader> =
    task {
        let! response =
            within (http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))

        let! stream = response.Content.ReadAsStreamAsync()
        return new EventReader(response, stream)
    }

/// UTF-8 text of a string, for request bodies.
let utf8 (text: string) = Encoding.UTF8.GetBytes text
