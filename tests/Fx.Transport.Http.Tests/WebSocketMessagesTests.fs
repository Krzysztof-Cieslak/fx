module Fx.Transport.Http.Tests.WebSocketMessagesTests

open System
open System.IO
open System.IO.Pipelines
open System.Net.WebSockets
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Transport.Http
open Fx.Transport.Http.WebSocketMessages

/// One end of an in-memory connection: reads what the other end writes.
type private Duplex(input: Stream, output: Stream) =
    inherit Stream()
    override _.CanRead = true
    override _.CanWrite = true
    override _.CanSeek = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = output.Flush()
    override _.FlushAsync(cancellation) = output.FlushAsync cancellation
    override _.Read(buffer: byte array, offset, count) = input.Read(buffer, offset, count)
    override _.ReadAsync(buffer: Memory<byte>, cancellation) = input.ReadAsync(buffer, cancellation)
    override _.Write(buffer: byte array, offset, count) = output.Write(buffer, offset, count)
    override _.WriteAsync(buffer: ReadOnlyMemory<byte>, cancellation) = output.WriteAsync(buffer, cancellation)
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())

/// A connected client and server WebSocket in memory.
let private connected () : WebSocket * WebSocket =
    let toServer = Pipe()
    let toClient = Pipe()

    let socket isServer (input: Pipe) (output: Pipe) =
        WebSocket.CreateFromStream(
            new Duplex(input.Reader.AsStream(), output.Writer.AsStream()),
            WebSocketCreationOptions(IsServer = isServer, KeepAliveInterval = TimeSpan.Zero)
        )

    socket false toClient toServer, socket true toServer toClient

let private none = CancellationToken.None

let private sendFragments (socket: WebSocket) (kind: WebSocketMessageType) (parts: byte array list) : Task =
    task {
        let last = List.length parts - 1

        for i, part in List.indexed parts do
            do! socket.SendAsync(ArraySegment part, kind, (i = last), none)
    }

let private run (t: Task<'T>) : 'T =
    t.WaitAsync(TimeSpan.FromSeconds 10.0).GetAwaiter().GetResult()

[<Tests>]
let tests =
    testList
        "WebSocketMessages"
        [
            test "text and binary messages round-trip" {
                let client, server = connected ()
                (send client (Text "héllo ✓") none).Wait()
                Expect.equal (run (receive 1024 server none)) (Message(Text "héllo ✓")) "text"
                (send server (Binary [| 0uy; 1uy; 255uy |]) none).Wait()
                Expect.equal (run (receive 1024 client none)) (Message(Binary [| 0uy; 1uy; 255uy |])) "binary"
            }

            test "fragments are reassembled into one message" {
                let client, server = connected ()
                let received = receive 1024 server none
                (sendFragments client WebSocketMessageType.Text [ "frag"B; "men"B; "ted"B ]).Wait()
                Expect.equal (run received) (Message(Text "fragmented")) "message"
            }

            test "a message larger than the read size is read whole" {
                let client, server = connected ()
                let payload = Array.init (1024 * 1024 + 7) byte
                let received = receive (2 * 1024 * 1024) server none
                (sendFragments client WebSocketMessageType.Binary [ payload[..99_999]; payload[100_000..] ]).Wait()

                match run received with
                | Message(Binary bytes) ->
                    Expect.equal bytes.Length payload.Length "length"
                    Expect.equal (SHA256.HashData bytes) (SHA256.HashData payload) "hash"
                | other -> failtest $"unexpected {other}"
            }

            test "a message at the limit passes; one byte more is too large" {
                let client, server = connected ()
                let received = receive 10 server none
                (send client (Binary(Array.zeroCreate 10)) none).Wait()
                Expect.equal (run received) (Message(Binary(Array.zeroCreate 10))) "at the limit"

                let tooLarge = receive 10 server none
                let sending = send client (Binary(Array.zeroCreate 11)) none
                Expect.equal (run tooLarge) (TooLarge 10) "over the limit"
                sending.Wait()
            }

            test "the peer's close is reported with its status and description" {
                let client, server = connected ()
                let received = receive 1024 server none

                let closing =
                    client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", none)

                Expect.equal (run received) (Closed(Some WebSocketCloseStatus.NormalClosure, "bye")) "closed"

                closing.Wait()
                Expect.equal (string (run received)) "closed (1000, 'bye')" "shown"
            }
        ]
