/// A Kestrel listener for local clients (spec §3, §17): HTTP, WebSocket and server-sent events,
/// every request behind `LocalAccess`. This is an imperative shell: routing and the access decision
/// are pure functions, and the handlers are values.
///
/// Native AOT (docs/spikes/kestrel.md): the server is built with `WebApplication.CreateEmptyBuilder`,
/// which reads no configuration files or environment variables, and requests reach handlers through a
/// single `RequestDelegate`, never through minimal-API delegates (`RequestDelegateFactory`).
module Fx.Transport.Http.LocalServer

open System
open System.Net
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Primitives
open Microsoft.Net.Http.Headers
open Fx.Transport.Http.LocalAccess

/// Where the server listens.
type Listener =
    /// TCP on 127.0.0.1 only; port 0 picks a free port. Clients must connect to `127.0.0.1`, not
    /// `localhost`, which may resolve to `::1` first.
    | Loopback of port: int
    /// A Unix domain socket: Linux, macOS and Windows 10 1803+. Paths are limited to about 100 bytes;
    /// access is also governed by the permissions of the socket's directory.
    | UnixSocket of path: string
    /// A Windows named pipe. Windows only.
    | NamedPipe of name: string

    override this.ToString() =
        match this with
        | Loopback port -> $"loopback port {port}"
        | UnixSocket path -> $"unix socket {path}"
        | NamedPipe name -> $"named pipe {name}"

/// A request that passed the access check.
type Request =
    {
        Context: HttpContext
        Access: Granted
        /// The OS account of the client process, as the system reports it for Unix sockets and named
        /// pipes (`PeerCredentials`); `None` over loopback TCP, or when the system does not say.
        Peer: PeerCredentials.PeerAccount option
        /// Cancelled when the server begins to stop. Long-lived handlers (event streams, sockets)
        /// must end then, or the stop waits for them until the shutdown timeout.
        Stopping: CancellationToken
    }

    override this.ToString() =
        $"{this.Context.Request.Method} {this.Context.Request.Path}"

/// A handler for an exact path. Paths and methods are compared ordinally (case-sensitively).
type Endpoint =
    | Http of method: string * path: string * handle: (Request -> Task)
    /// A WebSocket endpoint (GET with an upgrade). `protocols` are the supported subprotocols; the
    /// first one the client offered (in the client's order) is selected.
    | WebSocket of path: string * protocols: string list * handle: (Request -> Net.WebSockets.WebSocket -> Task)

    override this.ToString() =
        match this with
        | Http(m, path, _) -> $"{m} {path}"
        | WebSocket(path, _, _) -> $"WebSocket {path}"

module Endpoint =
    let get (path: string) (handle: Request -> Task) : Endpoint = Http(HttpMethods.Get, path, handle)

    let post (path: string) (handle: Request -> Task) : Endpoint = Http(HttpMethods.Post, path, handle)

    let webSocket (path: string) (protocols: string list) handle : Endpoint = WebSocket(path, protocols, handle)

    let path (endpoint: Endpoint) =
        match endpoint with
        | Http(_, path, _)
        | WebSocket(path, _, _) -> path

    let method (endpoint: Endpoint) =
        match endpoint with
        | Http(m, _, _) -> m
        | WebSocket _ -> HttpMethods.Get

/// What a request's method and path select.
type Route =
    | Found of Endpoint
    /// The path exists, with other methods.
    | WrongMethod of allowed: string list
    | NotFound

    override this.ToString() =
        match this with
        | Found endpoint -> $"found {endpoint}"
        | WrongMethod allowed -> "wrong method, allowed " + String.Join(", ", allowed)
        | NotFound -> "not found"

/// Selects the endpoint for a method and path. Pure.
let route (endpoints: Endpoint list) (method: string) (path: string) : Route =
    match endpoints |> List.filter (fun e -> Endpoint.path e = path) with
    | [] -> NotFound
    | onPath ->
        match onPath |> List.tryFind (fun e -> Endpoint.method e = method) with
        | Some endpoint -> Found endpoint
        | None -> WrongMethod(onPath |> List.map Endpoint.method |> List.distinct)

type Options =
    {
        Listener: Listener
        Access: AccessPolicy
        Endpoints: Endpoint list
        /// The largest request body. WebSocket messages are not request bodies: limit them in
        /// `WebSocketMessages.receive`.
        MaxRequestBodySize: int64
        /// How often the server pings idle WebSockets; `TimeSpan.Zero` turns pings off.
        KeepAliveInterval: TimeSpan
        /// How long a stop waits for requests to finish before aborting them.
        ShutdownTimeout: TimeSpan
    }

    override this.ToString() =
        $"{this.Listener}, {this.Endpoints.Length} endpoints"

/// Options with the defaults: 4 MiB request bodies, 30 s pings, 5 s shutdown timeout.
let options (listener: Listener) (access: AccessPolicy) (endpoints: Endpoint list) : Options =
    {
        Listener = listener
        Access = access
        Endpoints = endpoints
        MaxRequestBodySize = 4L * 1024L * 1024L
        KeepAliveInterval = TimeSpan.FromSeconds 30.0
        ShutdownTimeout = TimeSpan.FromSeconds 5.0
    }

let private values (headers: IHeaderDictionary) (name: string) : string list =
    [
        for value in headers[name] do
            match value with
            | null -> ()
            | v -> v
    ]

let private facts (headers: IHeaderDictionary) : RequestFacts =
    {
        Host = values headers HeaderNames.Host |> List.tryHead
        Origin = values headers HeaderNames.Origin |> List.tryHead
        Authorization = values headers HeaderNames.Authorization
        WebSocketProtocols =
            values headers HeaderNames.SecWebSocketProtocol
            |> List.collect (fun v ->
                v.Split(',', StringSplitOptions.TrimEntries ||| StringSplitOptions.RemoveEmptyEntries)
                |> List.ofArray)
    }

/// Ends a request with a status code and a one-line plain-text body.
let respond (context: HttpContext) (status: int) (text: string) : Task =
    context.Response.StatusCode <- status
    context.Response.ContentType <- "text/plain; charset=utf-8"
    context.Response.Headers.XContentTypeOptions <- StringValues "nosniff"
    context.Response.WriteAsync(text + "\n", context.RequestAborted)

let private reject (context: HttpContext) (denied: Denied) : Task =
    if statusCode denied = 401 then
        context.Response.Headers.WWWAuthenticate <- StringValues "Bearer"

    respond context (statusCode denied) (reason denied)

let private acceptWebSocket
    (request: Request)
    (protocols: string list)
    (handle: Request -> Net.WebSockets.WebSocket -> Task)
    : Task =
    task {
        let selected: string | null =
            match request.Access.Protocols |> List.tryFind (fun p -> List.contains p protocols) with
            | Some p -> p
            | None -> null

        // No permessage-deflate: compressing secrets next to attacker-chosen text leaks them (CRIME).
        let accept = WebSocketAcceptContext(SubProtocol = selected)
        use! socket = request.Context.WebSockets.AcceptWebSocketAsync accept
        do! handle request socket
    }

let private dispatch (options: Options) (stopping: CancellationToken) (context: HttpContext) : Task =
    match LocalAccess.decide options.Access (facts context.Request.Headers) with
    | Error denied -> reject context denied
    | Ok granted ->
        let request =
            {
                Context = context
                Access = granted
                Peer =
                    match options.Listener with
                    | Loopback _ -> None
                    | UnixSocket _
                    | NamedPipe _ -> PeerCredentials.ofContext context
                Stopping = stopping
            }

        let path =
            match context.Request.Path.Value with
            | null -> ""
            | p -> p

        match route options.Endpoints context.Request.Method path with
        | NotFound -> respond context 404 "not found"
        | WrongMethod allowed ->
            context.Response.Headers.Allow <- StringValues(String.Join(", ", allowed))
            respond context 405 "method not allowed"
        | Found(Http(_, _, handle)) -> handle request
        | Found(WebSocket(_, protocols, handle)) ->
            if context.WebSockets.IsWebSocketRequest then
                acceptWebSocket request protocols handle
            else
                context.Response.Headers.Upgrade <- StringValues "websocket"
                respond context 426 "websocket upgrade required"

/// Leaves process signals alone. The host's default `ConsoleLifetime` (even with the empty builder)
/// swallows Ctrl+C and SIGTERM and stops only the server, so the process would keep running: signals
/// belong to the program's entry point, not to a library.
type private LibraryLifetime() =
    interface IHostLifetime with
        member _.WaitForStartAsync _ = Task.CompletedTask
        member _.StopAsync _ = Task.CompletedTask

/// A started server. Disposing it stops it.
type Server(app: WebApplication) =
    /// Where the server listens: `http://127.0.0.1:<port>`, `http://unix:<path>` or `http://pipe:/<name>`.
    member _.Addresses: string list = List.ofSeq app.Urls

    /// Stops accepting connections and waits for requests to finish (see `Options.ShutdownTimeout`).
    member _.StopAsync() : Task = app.StopAsync()

    interface IAsyncDisposable with
        member _.DisposeAsync() : ValueTask =
            let stopThenDispose =
                task {
                    do! app.StopAsync()
                    do! app.DisposeAsync()
                }

            ValueTask(stopThenDispose :> Task)

/// Starts a server. Fails if the listener cannot bind (e.g. the port or socket path is in use).
let start (options: Options) : Task<Server> =
    task {
        // The empty builder has no configuration sources: CreateSlimBuilder would read appsettings.json
        // from the working directory, whose Kestrel:Endpoints add listeners, e.g. on 0.0.0.0.
        let builder = WebApplication.CreateEmptyBuilder(WebApplicationOptions())
        builder.WebHost.UseKestrelCore() |> ignore

        match options.Listener with
        | NamedPipe _ -> builder.WebHost.UseNamedPipes() |> ignore
        | Loopback _
        | UnixSocket _ -> ()

        builder.WebHost.ConfigureKestrel(fun (kestrel: KestrelServerOptions) ->
            kestrel.AddServerHeader <- false
            kestrel.Limits.MaxRequestBodySize <- options.MaxRequestBodySize

            let http1 (listen: ListenOptions) = listen.Protocols <- HttpProtocols.Http1

            match options.Listener with
            | Loopback port -> kestrel.Listen(IPAddress.Loopback, port, http1)
            | UnixSocket path -> kestrel.ListenUnixSocket(path, http1)
            | NamedPipe name -> kestrel.ListenNamedPipe(name, http1))
        |> ignore

        builder.Services.Configure<HostOptions>(fun (host: HostOptions) ->
            host.ShutdownTimeout <- options.ShutdownTimeout)
        |> ignore

        builder.Services.AddSingleton<IHostLifetime>(LibraryLifetime()) |> ignore

        let app = builder.Build()

        app.UseWebSockets(WebSocketOptions(KeepAliveInterval = options.KeepAliveInterval))
        |> ignore

        app.Run(RequestDelegate(dispatch options app.Lifetime.ApplicationStopping))
        do! app.StartAsync()
        return new Server(app)
    }

/// Server-sent event streams on a response (`Sse` formats the frames).
module Events =
    /// Starts an event stream: sets the headers, turns off response buffering and sends the headers,
    /// so the client sees the stream before the first event.
    let start (context: HttpContext) : Task =
        context.Response.ContentType <- Sse.contentType
        context.Response.Headers.CacheControl <- StringValues "no-cache"
        // Reverse proxies such as nginx buffer responses unless told otherwise.
        context.Response.Headers["X-Accel-Buffering"] <- StringValues "no"

        match context.Features.Get<IHttpResponseBodyFeature>() with
        | null -> ()
        | body -> body.DisableBuffering()

        context.Response.StartAsync context.RequestAborted

    let private writeFrame (context: HttpContext) (frame: string) (cancellation: CancellationToken) : Task =
        task {
            do! context.Response.Body.WriteAsync(ReadOnlyMemory(Encoding.UTF8.GetBytes frame), cancellation)
            do! context.Response.Body.FlushAsync cancellation
        }

    /// Writes one event and flushes it to the client.
    let write (context: HttpContext) (event: Sse.Event) (cancellation: CancellationToken) : Task =
        writeFrame context (Sse.format event) cancellation

    /// Writes a comment (a keep-alive) and flushes it.
    let comment (context: HttpContext) (text: string) (cancellation: CancellationToken) : Task =
        writeFrame context (Sse.comment text) cancellation

    /// The id of the last event a reconnecting client saw (`Last-Event-ID`).
    let lastEventId (context: HttpContext) : string option =
        values context.Request.Headers "Last-Event-ID" |> List.tryHead
