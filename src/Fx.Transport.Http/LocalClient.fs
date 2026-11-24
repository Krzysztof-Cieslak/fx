/// The client side of a local listener (spec §17, §18): a WebSocket to a `LocalServer`, over loopback
/// TCP, a Unix domain socket or a Windows named pipe, presenting the bearer token. This file is the
/// imperative shell that dials; it has its own entry in analyzers.json.
module Fx.Transport.Http.LocalClient

open System
open System.IO
open System.IO.Pipes
open System.Net.Http
open System.Net.Sockets
open System.Net.WebSockets
open System.Threading
open System.Threading.Tasks
open Fx.Transport.Http.LocalServer

/// Why a connection was not made.
type ConnectError =
    /// The server answered the handshake with this HTTP status (401 token, 403 host or origin,
    /// 404 path, …).
    | Rejected of status: int
    /// No connection: nothing listens there, or the connection failed (the exception's type).
    | Unreachable of reason: string

    override this.ToString() =
        match this with
        | Rejected status -> $"rejected with HTTP {status}"
        | Unreachable reason -> "unreachable: " + reason

/// A handler that reaches the listener and nothing else: no proxy (system proxy settings must not
/// affect local connections), no redirects.
let private handler (listener: Listener) : SocketsHttpHandler =
    let h = new SocketsHttpHandler(UseProxy = false, AllowAutoRedirect = false)

    match listener with
    | Loopback _ -> ()
    | UnixSocket path ->
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
    | NamedPipe name ->
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

/// The `ws://` URI of a path on a listener. Over sockets and pipes the host is `localhost`, which the
/// server's `Host` check accepts.
let uri (listener: Listener) (path: string) : Uri =
    match listener with
    | Loopback port -> Uri($"ws://127.0.0.1:{port}{path}")
    | UnixSocket _
    | NamedPipe _ -> Uri("ws://localhost" + path)

/// Connects a WebSocket to `path` on a local listener (for `Loopback`, the port the server got),
/// with `Authorization: Bearer <token>`, offering `protocols` in order. The server's choice is the
/// socket's `SubProtocol`. The caller owns and disposes the socket.
let connectWebSocket
    (listener: Listener)
    (path: string)
    (token: string)
    (protocols: string list)
    (cancellation: CancellationToken)
    : Task<Result<WebSocket, ConnectError>> =
    task {
        let socket = new ClientWebSocket()
        socket.Options.CollectHttpResponseDetails <- true
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token)

        for protocol in protocols do
            socket.Options.AddSubProtocol protocol

        // Not disposed: the socket keeps using the connection the handler made.
        let invoker = new HttpMessageInvoker(handler listener)

        try
            do! socket.ConnectAsync(uri listener path, invoker, cancellation)
            return Ok(socket :> WebSocket)
        with
        | :? OperationCanceledException ->
            socket.Dispose()
            return raise (OperationCanceledException cancellation)
        | e ->
            let status = int socket.HttpStatusCode
            socket.Dispose()

            if status > 0 then
                return Error(Rejected status)
            else
                return Error(Unreachable(e.GetType().Name))
    }
