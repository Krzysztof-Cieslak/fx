/// WebSocket on Kestrel: text and binary echo, subprotocols, fragmented and large messages, the close
/// handshake from either side, size limits, invalid UTF-8 and aborted connections.
module Fx.Aot.Scenarios.Kestrel.WebSockets

open System
open System.Net.WebSockets
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Fx.Aot.Scenarios
open Fx.Aot.Scenarios.Kestrel.Support
open Fx.Transport.Http
open Fx.Transport.Http.LocalServer
open Fx.Transport.Http.WebSocketMessages

/// The largest message the echo server accepts.
let private limit = 2 * 1024 * 1024

let private none = CancellationToken.None

let private show (message: Message) =
    match message with
    | Text text when text.Length <= 64 -> $"text '{Show.escape text}'"
    | Text text -> $"text ({text.Length} chars, sha256 {digest (Encoding.UTF8.GetBytes text)})"
    | Binary bytes -> $"binary ({bytes.Length} bytes, sha256 {digest bytes})"

let private showReceived (received: Received) =
    match received with
    | Message m -> show m
    | other -> string other

/// Echoes every message. "close, please" makes the server start the close handshake. Each
/// connection's server-side log goes to `logs` when the handler ends.
let private echo (logs: Channel<string list>) (request: Request) (socket: WebSocket) : Task =
    task {
        let log = ResizeArray<string>()
        let mutable running = true

        try
            try
                while running do
                    match! receive limit socket request.Stopping with
                    | Message(Text "close, please") ->
                        log.Add "server: starting the close handshake"
                        do! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "server closing", request.Stopping)
                        running <- false
                    | Message m -> do! send socket m request.Stopping
                    | Closed(status, description) ->
                        let code =
                            match status with
                            | Some s -> string (int s)
                            | None -> "none"

                        log.Add $"server: client closed ({code}, '{description}')"
                        do! socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, description, request.Stopping)
                        running <- false
                    | TooLarge limit ->
                        log.Add $"server: message over {limit} bytes"
                        do! socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", request.Stopping)
                        running <- false
            with :? WebSocketException as e ->
                log.Add $"server: {e.WebSocketErrorCode}"
        finally
            log.Add $"server: final state {socket.State}"
            logs.Writer.TryWrite(List.ofSeq log) |> ignore
    }

let private sendFragments (socket: WebSocket) (kind: WebSocketMessageType) (parts: byte array list) : Task =
    task {
        let last = List.length parts - 1

        for i, part in List.indexed parts do
            do! socket.SendAsync(ArraySegment part, kind, (i = last), none)
    }

let webSockets =
    Scenario.task "kestrel-websocket" (fun () ->
        task {
            let logs = Channel.CreateUnbounded<string list>()

            use! server =
                startLoopback id [ Endpoint.webSocket "/echo" [ "echo.v1"; "echo.v2" ] (echo logs) ]

            let uri = webSocketUri Tcp server "/echo"
            let log = ResizeArray<string>()

            /// Waits for the handler of the last connection to end, then adds its log.
            let serverLog () =
                task {
                    let! lines = within (logs.Reader.ReadAsync().AsTask())
                    log.AddRange lines
                }

            let connect (configure: ClientWebSocketOptions -> unit) =
                task {
                    let options (o: ClientWebSocketOptions) =
                        withToken o
                        configure o

                    match! connectWebSocket Tcp uri options with
                    | Ok socket -> return socket
                    | Error status -> return failwith $"connect failed with {status}"
                }

            let roundTrip (socket: WebSocket) (label: string) (send: unit -> Task) =
                task {
                    do! send ()
                    let! echoed = within (receive limit socket none)
                    log.Add $"{label}: echoed {showReceived echoed}"
                }

            // 1. Echo, then the client closes.
            use! socket =
                connect (fun o ->
                    o.AddSubProtocol "echo.v2"
                    o.AddSubProtocol "echo.v1")

            log.Add $"subprotocol = {socket.SubProtocol}, state = {socket.State}"
            do! roundTrip socket "text" (fun () -> send socket (Text "hello") none)
            do! roundTrip socket "unicode text" (fun () -> send socket (Text "zażółć ✓ 😀") none)
            do! roundTrip socket "empty text" (fun () -> send socket (Text "") none)
            do! roundTrip socket "binary" (fun () -> send socket (Binary(Array.init 256 byte)) none)

            let large = Array.init (1024 * 1024) (fun i -> byte (i * 7 % 256))
            log.Add $"fragmented binary sent: 3 frames, {large.Length} bytes, sha256 {digest large}"

            do!
                roundTrip socket "fragmented binary" (fun () ->
                    sendFragments
                        socket
                        WebSocketMessageType.Binary
                        [ large[..99_999]; large[100_000..899_999]; large[900_000..] ])

            let text = String.replicate (256 * 1024) "ab"
            do! roundTrip socket "large text" (fun () -> send socket (Text text) none)
            do! withinTask (socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client done", none))

            log.Add
                $"client closed: state {socket.State}, status {socket.CloseStatus}, '{socket.CloseStatusDescription}'"

            do! serverLog ()

            // 2. The server starts the close handshake.
            use! socket = connect ignore
            log.Add $"no subprotocol offered: subprotocol = {Show.orNull socket.SubProtocol}"
            do! send socket (Text "close, please") none
            let! closing = within (receive limit socket none)
            log.Add $"client received: {closing}, state {socket.State}"
            do! withinTask (socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", none))
            log.Add $"client answered: state {socket.State}"
            do! serverLog ()

            // 3. A message over the server's limit.
            use! socket = connect ignore
            do! send socket (Binary(Array.zeroCreate (limit + 1))) none
            let! closing = within (receive limit socket none)
            log.Add $"over the limit: client received {closing}"
            do! withinTask (socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", none))
            do! serverLog ()

            // 4. Invalid UTF-8 in a text message: the server's WebSocket fails the receive, sends a 1007
            // close frame and aborts. The abort can overtake the close frame, so the client sees either.
            use! socket = connect ignore
            do! socket.SendAsync(ArraySegment [| 0xC3uy; 0x28uy |], WebSocketMessageType.Text, true, none)

            let! ended =
                task {
                    try
                        match! within (receive limit socket none) with
                        | Closed(Some WebSocketCloseStatus.InvalidPayloadData, _) -> return true
                        | _ -> return false
                    with :? WebSocketException as e when
                        e.WebSocketErrorCode = WebSocketError.ConnectionClosedPrematurely ->
                        return true
                }

            log.Add $"invalid UTF-8: connection ended by a 1007 close frame or an abort = {ended}"
            do! serverLog ()

            // 5. The client vanishes without a close handshake.
            use! socket = connect ignore
            do! roundTrip socket "before abort" (fun () -> send socket (Text "still there?") none)
            socket.Abort()
            log.Add $"client aborted: state {socket.State}"
            do! serverLog ()

            return List.ofSeq log
        })

let scenarios = [ webSockets ]
