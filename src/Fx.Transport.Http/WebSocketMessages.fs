/// Whole WebSocket messages over `System.Net.WebSockets.WebSocket`, which reads and writes frames.
///
/// A WebSocket allows one receive and one send at a time: a protocol on top (JSON-RPC, T107) must
/// queue its sends. Kestrel's request-body limit does not apply to WebSocket messages, so `receive`
/// takes its own limit.
module Fx.Transport.Http.WebSocketMessages

open System
open System.Buffers
open System.Net.WebSockets
open System.Text
open System.Threading
open System.Threading.Tasks

type Message =
    | Text of string
    | Binary of byte array

    override this.ToString() =
        match this with
        | Text text -> $"text ({text.Length} chars)"
        | Binary bytes -> $"binary ({bytes.Length} bytes)"

/// The outcome of reading one message.
type Received =
    | Message of Message
    /// The peer started the close handshake. Answer with `CloseOutputAsync` (or `CloseAsync`).
    | Closed of status: WebSocketCloseStatus option * description: string
    /// The message is longer than the limit. Close with `MessageTooBig`; its rest is unread.
    | TooLarge of limit: int

    override this.ToString() =
        match this with
        | Message m -> string m
        | Closed(status, description) ->
            let code =
                status |> Option.map (fun s -> string (int s)) |> Option.defaultValue "none"

            $"closed ({code}, '{description}')"
        | TooLarge limit -> $"too large (limit {limit} bytes)"

/// Bytes requested from the socket per read.
let private chunkSize = 16 * 1024

/// Reads one whole message, however it was fragmented, failing when it exceeds `limit` bytes.
/// Text messages are valid UTF-8: the WebSocket closes with `InvalidPayloadData` and throws otherwise.
let receive (limit: int) (socket: WebSocket) (cancellation: CancellationToken) : Task<Received> =
    task {
        let buffer = ArrayBufferWriter<byte>()
        let mutable outcome = None

        while outcome.IsNone do
            // Read at most one byte past the limit: enough to detect an oversized message.
            let room = min (chunkSize - 1) (limit - buffer.WrittenCount) + 1

            let! result =
                socket.ReceiveAsync(buffer.GetMemory(room).Slice(0, room), cancellation)

            if result.MessageType = WebSocketMessageType.Close then
                let description =
                    match socket.CloseStatusDescription with
                    | null -> ""
                    | d -> d

                outcome <- Some(Closed(Option.ofNullable socket.CloseStatus, description))
            else
                buffer.Advance result.Count

                if buffer.WrittenCount > limit then
                    outcome <- Some(TooLarge limit)
                elif result.EndOfMessage then
                    let message =
                        if result.MessageType = WebSocketMessageType.Text then
                            Text(Encoding.UTF8.GetString buffer.WrittenSpan)
                        else
                            Binary(buffer.WrittenSpan.ToArray())

                    outcome <- Some(Message message)

        return outcome.Value
    }

/// Sends one message as a single frame.
let send (socket: WebSocket) (message: Message) (cancellation: CancellationToken) : Task =
    let bytes, kind =
        match message with
        | Text text -> Encoding.UTF8.GetBytes text, WebSocketMessageType.Text
        | Binary bytes -> bytes, WebSocketMessageType.Binary

    socket.SendAsync(ReadOnlyMemory bytes, kind, true, cancellation).AsTask()
