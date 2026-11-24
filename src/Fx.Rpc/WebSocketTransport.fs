namespace Fx.Rpc

open System
open System.Buffers
open System.Net.WebSockets
open System.Text
open System.Text.Unicode
open System.Threading
open System.Threading.Tasks

/// A transport over a WebSocket (spec §17): JSON-RPC in text messages, channel frames in binary
/// messages, one frame per message. Works for both ends: the server's socket from
/// `Fx.Transport.Http.LocalServer` and a client's `ClientWebSocket`. Offer and select
/// `Protocol.webSocketSubprotocol`.
///
/// Kestrel's request-body limit does not apply to WebSocket messages; `FrameLimits` does, here.
module WebSocketTransport =

    /// Bytes requested from the socket per read.
    let private chunkSize = 16 * 1024

    let private failure (e: exn) = TransportError.Broken(e.GetType().Name)

    let private status (shutdown: Shutdown) =
        match shutdown with
        | Shutdown.Normal -> WebSocketCloseStatus.NormalClosure
        | Shutdown.ProtocolError -> WebSocketCloseStatus.ProtocolError
        | Shutdown.TooLarge -> WebSocketCloseStatus.MessageTooBig

    let private contents (buffer: ArrayBufferWriter<byte>) (kind: WebSocketMessageType) : Received =
        if kind = WebSocketMessageType.Binary then
            Received.Frame(Frame.Binary(buffer.WrittenSpan.ToArray()))
        elif Utf8.IsValid buffer.WrittenSpan then
            Received.Frame(Frame.Text(Encoding.UTF8.GetString buffer.WrittenSpan))
        else
            Received.Failed(TransportError.Malformed "a text message that is not UTF-8")

    /// A transport over an open WebSocket. Aborting it aborts and disposes the socket.
    let create (limits: FrameLimits) (socket: WebSocket) : Transport =
        let buffer = ArrayBufferWriter<byte>()

        let receive (cancellation: CancellationToken) : Task<Received> =
            task {
                try
                    buffer.ResetWrittenCount()
                    let mutable result = None
                    let mutable limit = max limits.MaxText limits.MaxBinary

                    while result.IsNone do
                        // At most one byte past the limit: enough to see that a message is too large.
                        let room = min (chunkSize - 1) (limit - buffer.WrittenCount) + 1
                        let! read = socket.ReceiveAsync(buffer.GetMemory(room).Slice(0, room), cancellation)

                        match read.MessageType with
                        | WebSocketMessageType.Close -> result <- Some Received.End
                        | kind ->
                            buffer.Advance read.Count

                            limit <-
                                if kind = WebSocketMessageType.Text then
                                    limits.MaxText
                                else
                                    limits.MaxBinary

                            if buffer.WrittenCount > limit then
                                result <- Some(Received.Failed(TransportError.TooLarge limit))
                            elif read.EndOfMessage then
                                result <- Some(contents buffer kind)

                    return result.Value
                with e ->
                    return Received.Failed(failure e)
            }

        let send (frame: Frame) (cancellation: CancellationToken) : Task<Result<unit, TransportError>> =
            task {
                try
                    match frame with
                    | Frame.Text text ->
                        do!
                            socket.SendAsync(
                                ReadOnlyMemory(Encoding.UTF8.GetBytes text),
                                WebSocketMessageType.Text,
                                true,
                                cancellation
                            )
                    | Frame.Binary bytes ->
                        do! socket.SendAsync(ReadOnlyMemory bytes, WebSocketMessageType.Binary, true, cancellation)

                    return Ok()
                with e ->
                    return Error(failure e)
            }

        let closeOutput (shutdown: Shutdown) (cancellation: CancellationToken) : Task<unit> =
            task {
                try
                    match socket.State with
                    | WebSocketState.Open
                    | WebSocketState.CloseReceived ->
                        // Does not wait for the peer's close frame: the reader sees it as the end.
                        do! socket.CloseOutputAsync(status shutdown, "", cancellation)
                    | _ -> ()
                with _ ->
                    ()
            }

        let abort () =
            try
                socket.Abort()
                socket.Dispose()
            with _ ->
                ()

        {
            Name = "websocket"
            Send = send
            Receive = receive
            CloseOutput = closeOutput
            Abort = abort
        }
