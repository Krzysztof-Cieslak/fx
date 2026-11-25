namespace Fx.Keyring.DBus

open System
open System.Collections.Generic
open System.IO
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks

/// An error reply: the error's name (e.g. `org.freedesktop.DBus.Error.ServiceUnknown`) and message.
type DBusError =
    {
        Name: string
        Message: string
    }

    override this.ToString() =
        if this.Message = "" then
            this.Name
        else
            $"{this.Name}: {this.Message}"

/// A client connection to a message bus: one caller at a time, calls answered in order. The keyring
/// opens one per operation, so it never shares a connection between threads.
///
/// Signals that arrive while a call waits for its reply are kept for `WaitForSignal`; method calls
/// from the bus are ignored (nothing calls this client).
[<Sealed>]
type BusConnection private (socket: Socket, stream: NetworkStream) =
    let mutable serial = 0u
    let signals = Queue<Message>()
    let header = Array.zeroCreate<byte> 16

    let receive (cancellation: CancellationToken) : Task<Message> =
        task {
            do! stream.ReadExactlyAsync(header.AsMemory(), cancellation)

            match Wire.frameLength (ReadOnlySpan header) with
            | Error e -> return raise (IOException $"D-Bus: {e}")
            | Ok length ->
                let data = Array.zeroCreate<byte> length
                header.CopyTo(data, 0)
                do! stream.ReadExactlyAsync(data.AsMemory(16), cancellation)

                match Wire.decode data with
                | Ok message -> return message
                | Error e -> return raise (IOException $"D-Bus: {e}")
        }

    let send (message: Message) (cancellation: CancellationToken) : Task<uint32> =
        task {
            serial <- serial + 1u
            let bytes = Wire.encode { message with Serial = serial }
            do! stream.WriteAsync(bytes.AsMemory(), cancellation)
            return serial
        }

    /// Calls a method and waits for its reply: the reply's body, or the error.
    member _.Call (message: Message) (cancellation: CancellationToken) : Task<Result<DBusValue list, DBusError>> =
        task {
            let! sent = send message cancellation
            let mutable reply = None

            while reply.IsNone do
                let! m = receive cancellation

                match m.Type, m.ReplySerial with
                | MessageType.MethodReturn, Some s when s = sent -> reply <- Some(Ok m.Body)
                | MessageType.Error, Some s when s = sent ->
                    let text =
                        match m.Body with
                        | DBusValue.String text :: _ -> text
                        | _ -> ""

                    reply <-
                        Some(
                            Error
                                {
                                    Name = m.ErrorName |> Option.defaultValue "unknown"
                                    Message = text
                                }
                        )
                | MessageType.Signal, _ -> signals.Enqueue m
                | _ -> ()

            return reply.Value
        }

    /// Waits for a signal that matches (the bus delivers only signals a match rule asked for).
    member _.WaitForSignal (matches: Message -> bool) (cancellation: CancellationToken) : Task<Message> =
        task {
            let mutable found = None

            while found.IsNone && signals.Count > 0 do
                let m = signals.Dequeue()

                if matches m then
                    found <- Some m

            while found.IsNone do
                let! m = receive cancellation

                if m.Type = MessageType.Signal && matches m then
                    found <- Some m

            return found.Value
        }

    interface IDisposable with
        member _.Dispose() =
            stream.Dispose()
            socket.Dispose()

    override _.ToString() = "BusConnection"

    /// Connects to a bus, authenticates with `EXTERNAL` (the socket's credentials) and says `Hello`.
    static member Connect (address: string) (cancellation: CancellationToken) : Task<Result<BusConnection, string>> =
        task {
            match BusAddress.parse address with
            | Error e -> return Error e
            | Ok endpoints ->
                let mutable result = Error "no endpoint"

                for endpoint in endpoints do
                    if Result.isError result then
                        let! attempt = BusConnection.ConnectTo endpoint cancellation
                        result <- attempt

                return result
        }

    static member private ConnectTo (endpoint: BusEndpoint) (cancellation: CancellationToken) =
        task {
            let path =
                match endpoint with
                | BusEndpoint.Path path -> path
                | BusEndpoint.Abstract name -> "\000" + name

            let socket =
                new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

            try
                do! socket.ConnectAsync(UnixDomainSocketEndPoint path, cancellation)
                let stream = new NetworkStream(socket, ownsSocket = false)
                let connection = new BusConnection(socket, stream)

                match! connection.Authenticate cancellation with
                | Error e ->
                    (connection :> IDisposable).Dispose()
                    return Error e
                | Ok() ->
                    let hello =
                        Message.call "org.freedesktop.DBus" "/org/freedesktop/DBus" "org.freedesktop.DBus" "Hello" []

                    match! connection.Call hello cancellation with
                    | Ok _ -> return Ok connection
                    | Error e ->
                        (connection :> IDisposable).Dispose()
                        return Error $"Hello failed: {e}"
            with
            | :? SocketException as e ->
                socket.Dispose()
                return Error $"cannot connect to {endpoint}: {e.SocketErrorCode}"
            | :? IOException ->
                socket.Dispose()
                return Error $"the bus at {endpoint} closed the connection"
        }

    member private _.Authenticate(cancellation: CancellationToken) : Task<Result<unit, string>> =
        task {
            let uid = string (Fx.Keyring.Native.Libc.geteuid ())
            let hex = Convert.ToHexStringLower(Encoding.ASCII.GetBytes uid)
            let request = Encoding.ASCII.GetBytes("\000AUTH EXTERNAL " + hex + "\r\n")
            do! stream.WriteAsync(request.AsMemory(), cancellation)

            // A line of at most 512 bytes, ending with \r\n.
            let line = ResizeArray<byte>()
            let one = Array.zeroCreate<byte> 1
            let mutable ended = false

            while not ended do
                do! stream.ReadExactlyAsync(one.AsMemory(), cancellation)
                line.Add one[0]

                if line.Count >= 2 && line[line.Count - 2] = 13uy && line[line.Count - 1] = 10uy then
                    ended <- true
                elif line.Count > 512 then
                    raise (IOException "D-Bus: authentication reply too long")

            let reply = Encoding.ASCII.GetString(line.ToArray()).TrimEnd()

            if reply.StartsWith("OK ", StringComparison.Ordinal) then
                let flush = Encoding.ASCII.GetBytes "BEGIN\r\n"
                do! stream.WriteAsync(flush.AsMemory(), cancellation)
                return Ok()
            else
                return Error $"the bus rejected EXTERNAL authentication ({reply})"
        }
