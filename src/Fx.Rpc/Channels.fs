namespace Fx.Rpc

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// Why a channel operation failed.
[<RequireQualifiedAccess>]
type ChannelError =
    /// One of the sides does not support channels.
    | NotNegotiated
    /// As many channels are open as the peer accepts.
    | TooMany of limit: int
    /// No channel with this id waits to be accepted.
    | Unknown of channel: uint32
    /// The peer reset the channel (or refused to open it: code `ResetCode.Refused`).
    | ResetByPeer of code: uint32 * reason: string
    /// This side reset the channel.
    | ResetLocally of code: uint32 * reason: string
    /// Writing after `CloseOutput`.
    | OutputClosed
    | ConnectionClosed of CloseReason

    override this.ToString() =
        match this with
        | NotNegotiated -> "channels were not negotiated"
        | TooMany limit -> $"too many channels (limit {limit})"
        | Unknown id -> $"no channel {id} to accept"
        | ResetByPeer(code, reason) -> $"reset by the peer ({code}: {reason})"
        | ResetLocally(code, reason) -> $"reset ({code}: {reason})"
        | OutputClosed -> "the channel's output is closed"
        | ConnectionClosed reason -> "connection closed: " + string reason

/// Reset codes. Applications may use their own codes from 100 up.
module ResetCode =
    /// The application reset the channel.
    [<Literal>]
    let Cancelled = 0u

    /// The peer sent more data than its credit allowed.
    [<Literal>]
    let FlowControl = 1u

    /// The receiver has too many channels open.
    [<Literal>]
    let Refused = 2u

    /// The peer sent data after closing its output.
    [<Literal>]
    let Protocol = 3u

/// What a channel needs from its connection.
type internal IChannelHost =
    /// Queues a frame in order with the JSON-RPC messages; waits for room. False once the connection
    /// no longer sends.
    abstract Send: Frame * CancellationToken -> Task<bool>
    /// Queues a frame before everything else, without waiting.
    abstract SendUrgent: Frame -> unit
    /// The channel is done in both directions.
    abstract Release: uint32 -> unit

/// One multiplexed, ordered, bidirectional byte stream (spec §17: PTY output, blobs, port forwarding),
/// with credit flow control in each direction:
///
/// - the receiver grants a window (its `ChannelSettings.InitialWindow`) and more credit as the
///   application reads, so a sender can never have more than one window in flight;
/// - a slow reader therefore stalls only its own channel's writer: the connection keeps reading,
///   because what a channel buffers is bounded by the credit it granted.
///
/// Each direction closes on its own (`CloseOutput`); `Reset` aborts both.
[<Sealed>]
type Channel internal (id: uint32, host: IChannelHost, receiveWindow: int, sendCredit: int, maxPayload: int) =
    let gate = obj ()
    let inbound = Queue<ArraySegment<byte>>()
    let mutable remaining = int64 receiveWindow
    let mutable consumed = 0L
    let mutable inputEnded = false
    let mutable reader: TaskCompletionSource<unit> option = None
    let mutable credit = int64 sendCredit
    let mutable creditWaiter: TaskCompletionSource<unit> option = None
    let mutable outputClosed = false
    let mutable failure: ChannelError option = None
    let writing = new SemaphoreSlim(1, 1)
    // Grant more credit once half of the window has been read.
    let threshold = max 1L (int64 receiveWindow / 2L)

    let dataFrame (data: ReadOnlyMemory<byte>) (offset: int) (count: int) =
        ChannelFrame.data id (data.Slice(offset, count).Span)

    let signal (waiter: TaskCompletionSource<unit> option) =
        match waiter with
        | Some w -> w.TrySetResult() |> ignore
        | None -> ()

    let wakeAll () =
        let r = reader
        let c = creditWaiter
        reader <- None
        creditWaiter <- None
        r, c

    /// Fails the channel: whether it was not failed before, whether both directions had ended already,
    /// and the waiters to wake (after the channel is released). Under no lock.
    let markFailed (error: ChannelError) =
        lock gate (fun () ->
            match failure with
            | Some _ -> false, false, (None, None)
            | None ->
                failure <- Some error
                inbound.Clear()
                true, outputClosed && inputEnded, wakeAll ())

    let wake (waiters: TaskCompletionSource<unit> option * TaskCompletionSource<unit> option) =
        let r, c = waiters
        signal r
        signal c

    member _.Id = id

    /// The next piece of data, `None` once the peer closed its output and everything was read, or why
    /// the channel failed. One reader at a time.
    member _.Read(cancellation: CancellationToken) : Task<Result<ReadOnlyMemory<byte> option, ChannelError>> =
        task {
            let mutable result = None

            while result.IsNone do
                let step =
                    lock gate (fun () ->
                        if inbound.Count > 0 then
                            let segment = inbound.Dequeue()
                            consumed <- consumed + int64 segment.Count

                            let grant =
                                if consumed >= threshold then
                                    let n = consumed
                                    consumed <- 0L
                                    remaining <- remaining + n
                                    Some n
                                else
                                    None

                            Choice1Of2(Ok(Some(ReadOnlyMemory(segment.Array, segment.Offset, segment.Count))), grant)
                        else
                            match failure with
                            | Some error -> Choice1Of2(Error error, None)
                            | None when inputEnded -> Choice1Of2(Ok None, None)
                            | None ->
                                let waiting =
                                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                                reader <- Some waiting
                                Choice2Of2 waiting.Task)

                match step with
                | Choice1Of2(outcome, grant) ->
                    match grant with
                    | Some n -> host.SendUrgent(Frame.Binary(ChannelFrame.encode (ChannelFrame.Credit(id, uint32 n))))
                    | None -> ()

                    result <- Some outcome
                | Choice2Of2 waiting -> do! waiting.WaitAsync cancellation

            return result.Value
        }

    /// Sends bytes in order, as fast as the peer's credit allows: waits while it has none.
    member _.Write(data: ReadOnlyMemory<byte>, cancellation: CancellationToken) : Task<Result<unit, ChannelError>> =
        task {
            do! writing.WaitAsync cancellation

            try
                let mutable offset = 0
                let mutable error = None

                while error.IsNone && offset < data.Length do
                    let step =
                        lock gate (fun () ->
                            match failure with
                            | Some e -> Choice1Of3 e
                            | None when outputClosed -> Choice1Of3 ChannelError.OutputClosed
                            | None when credit > 0L ->
                                let n = int (min credit (int64 (min maxPayload (data.Length - offset))))
                                credit <- credit - int64 n
                                Choice2Of3 n
                            | None ->
                                let waiting =
                                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                                creditWaiter <- Some waiting
                                Choice3Of3 waiting.Task)

                    match step with
                    | Choice1Of3 e -> error <- Some e
                    | Choice2Of3 n ->
                        let frame = dataFrame data offset n
                        let! sent = host.Send(Frame.Binary frame, cancellation)

                        if sent then
                            offset <- offset + n
                        else
                            error <-
                                Some(
                                    lock gate (fun () ->
                                        failure
                                        |> Option.defaultValue (ChannelError.ConnectionClosed CloseReason.Local))
                                )
                    | Choice3Of3 waiting -> do! waiting.WaitAsync cancellation

                return
                    match error with
                    | Some e -> Error e
                    | None -> Ok()
            finally
                writing.Release() |> ignore
        }

    /// Ends this side's output after the data already written: the peer reads the end. Idempotent.
    /// A side that never writes closes its output as soon as it has the channel: until both
    /// directions have ended, the channel counts against `maxChannels` on both sides.
    member _.CloseOutput(cancellation: CancellationToken) : Task<Result<unit, ChannelError>> =
        task {
            do! writing.WaitAsync cancellation

            try
                let step =
                    lock gate (fun () ->
                        match failure with
                        | Some e -> Error e
                        | None when outputClosed -> Ok None
                        | None ->
                            outputClosed <- true
                            Ok(Some inputEnded))

                match step with
                | Error e -> return Error e
                | Ok None -> return Ok()
                | Ok(Some finished) ->
                    // Released before the close goes out: once the peer sees the end, this side no
                    // longer counts the channel.
                    if finished then
                        host.Release id

                    let! sent =
                        host.Send(Frame.Binary(ChannelFrame.encode (ChannelFrame.Close id)), cancellation)

                    if sent then
                        return Ok()
                    else
                        return Error(ChannelError.ConnectionClosed CloseReason.Local)
            finally
                writing.Release() |> ignore
        }

    /// Aborts both directions: buffered data is dropped, the peer's reads and writes fail with
    /// `ResetByPeer`. Does nothing if the channel already failed; sends nothing once both directions
    /// have ended (there is nothing left to abort).
    member _.Reset(code: uint32, reason: string) : unit =
        let first, finished, waiters = markFailed (ChannelError.ResetLocally(code, reason))

        if first then
            host.Release id

            if not finished then
                // In order, not urgent: a reset must never overtake the channel's open frame.
                host.Send(
                    Frame.Binary(ChannelFrame.encode (ChannelFrame.Reset(id, code, reason))),
                    CancellationToken.None
                )
                |> ignore

            wake waiters

    /// From the connection's reader: data from the peer. `Some code` resets the channel.
    member internal _.Deliver(data: ArraySegment<byte>) : uint32 option =
        let outcome =
            lock gate (fun () ->
                if failure.IsSome then
                    Ok None
                elif inputEnded then
                    Error ResetCode.Protocol
                elif int64 data.Count > remaining then
                    Error ResetCode.FlowControl
                else
                    remaining <- remaining - int64 data.Count

                    if data.Count > 0 then
                        inbound.Enqueue data

                    let r = reader
                    reader <- None
                    Ok r)

        match outcome with
        | Ok waiting ->
            signal waiting
            None
        | Error code -> Some code

    member internal _.Grant(bytes: uint32) : bool =
        let ok, waiting =
            lock gate (fun () ->
                if credit + int64 bytes > int64 Int32.MaxValue then
                    false, None
                else
                    credit <- credit + int64 bytes
                    let c = creditWaiter
                    creditWaiter <- None
                    true, c)

        signal waiting
        ok

    /// From the connection's reader: the peer closed its output. Released before the reader wakes, so
    /// whoever sees the end of a channel whose output is closed sees it released.
    member internal _.EndInput() : unit =
        let release, waiting =
            lock gate (fun () ->
                inputEnded <- true
                let r = reader
                reader <- None
                outputClosed, r)

        if release then
            host.Release id

        signal waiting

    /// The channel failed from outside (the peer reset it, the connection ended); the connection has
    /// released it already.
    member internal _.Fail(error: ChannelError) : unit =
        let _, _, waiters = markFailed error
        wake waiters

    override _.ToString() = $"channel {id}"

/// The channels of one connection: ids, opening and accepting, routing of received frames.
[<Sealed>]
type internal ChannelTable
    (
        isClient: bool,
        local: ChannelSettings option,
        send: Frame -> CancellationToken -> Task<bool>,
        sendUrgent: Frame -> unit
    ) =
    let gate = obj ()
    let channels = Dictionary<uint32, Channel>()
    let waiting = HashSet<uint32>()

    let host =
        { new IChannelHost with
            member _.Send(frame, cancellation) = send frame cancellation
            member _.SendUrgent frame = sendUrgent frame

            member _.Release id =
                lock gate (fun () ->
                    channels.Remove id |> ignore
                    waiting.Remove id |> ignore)
        }

    let mutable remote: ChannelSettings option = None
    let mutable nextLocal = if isClient then 1u else 2u
    let mutable highestRemote = 0u
    let mutable closed: CloseReason option = None

    let isLocalId (id: uint32) = (id % 2u = 1u) = isClient

    let settings () =
        match local, remote with
        | Some l, Some r -> Some(l, r)
        | _ -> None

    /// Called once the handshake has the peer's settings.
    member _.Negotiate(settings: ChannelSettings option) =
        lock gate (fun () -> remote <- settings)

    /// Opens a channel; the peer accepts it with the id (sent in a request or result).
    member _.Open(cancellation: CancellationToken) : Task<Result<Channel, ChannelError>> =
        cancellation.ThrowIfCancellationRequested()

        let opened =
            lock gate (fun () ->
                match closed, settings () with
                | Some reason, _ -> Error(ChannelError.ConnectionClosed reason)
                | None, None -> Error ChannelError.NotNegotiated
                | None, Some(l, r) ->
                    let limit = min l.MaxChannels r.MaxChannels

                    if channels.Count >= limit || nextLocal > UInt32.MaxValue - 2u then
                        Error(ChannelError.TooMany limit)
                    else
                        let id = nextLocal
                        nextLocal <- nextLocal + 2u
                        let channel = Channel(id, host, l.InitialWindow, r.InitialWindow, r.MaxFramePayload)
                        channels[id] <- channel
                        // The peer requires ids in increasing order: queue the open frame under the
                        // lock that allocated the id, so concurrent opens cannot swap. Urgent, so it
                        // never waits; nothing of the channel can be queued before it anyway.
                        sendUrgent (Frame.Binary(ChannelFrame.encode (ChannelFrame.Open id)))
                        Ok channel)

        Task.FromResult opened

    /// Takes a channel the peer opened. Each one can be accepted once; one the peer reset before is
    /// gone.
    member _.Accept(id: uint32) : Result<Channel, ChannelError> =
        lock gate (fun () ->
            match closed with
            | Some reason -> Error(ChannelError.ConnectionClosed reason)
            | None ->
                match channels.TryGetValue id with
                | true, channel when waiting.Remove id -> Ok channel
                | _ -> Error(ChannelError.Unknown id))

    /// The channels that count against `maxChannels` on this side: not yet released.
    member _.Count: int = lock gate (fun () -> channels.Count)

    /// Routes a received frame. `Some reason` is a protocol violation that ends the connection.
    member _.Receive(frame: ChannelFrame) : string option =
        let id = frame.Channel

        let route =
            lock gate (fun () ->
                match settings () with
                | None -> Error "a channel frame, but channels were not negotiated"
                | Some(l, r) ->
                    match frame with
                    | ChannelFrame.Open _ ->
                        if isLocalId id || id <= highestRemote then
                            Error $"channel {id} opened with a used or wrong id"
                        else
                            highestRemote <- id

                            if channels.Count >= l.MaxChannels then
                                Ok(Choice2Of2 ResetCode.Refused)
                            else
                                channels[id] <- Channel(id, host, l.InitialWindow, r.InitialWindow, r.MaxFramePayload)
                                waiting.Add id |> ignore
                                Ok(Choice1Of2 None)
                    | _ ->
                        match channels.TryGetValue id with
                        | true, channel -> Ok(Choice1Of2(Some channel))
                        // A frame for a channel that existed (reset, refused, closed) may cross a reset.
                        | false, _ when (isLocalId id && id < nextLocal) || (not (isLocalId id) && id <= highestRemote) ->
                            Ok(Choice1Of2 None)
                        | false, _ -> Error $"a frame for channel {id}, which was never opened")

        let reset (channel: Channel) (code: uint32) (reason: string) = channel.Reset(code, reason)

        match route with
        | Error violation -> Some violation
        | Ok(Choice2Of2 code) ->
            host.SendUrgent(Frame.Binary(ChannelFrame.encode (ChannelFrame.Reset(id, code, "too many channels"))))
            None
        | Ok(Choice1Of2 None) -> None
        | Ok(Choice1Of2(Some channel)) ->
            match frame with
            | ChannelFrame.Open _ -> None
            | ChannelFrame.Data(_, data) ->
                match channel.Deliver data with
                | Some code when code = ResetCode.FlowControl -> reset channel code "data beyond the granted credit"
                | Some code -> reset channel code "data after the end of the channel"
                | None -> ()

                None
            | ChannelFrame.Credit(_, bytes) ->
                if not (channel.Grant bytes) then
                    reset channel ResetCode.FlowControl "credit beyond 2^31 - 1 bytes"

                None
            | ChannelFrame.Close _ ->
                channel.EndInput()
                None
            | ChannelFrame.Reset(_, code, reason) ->
                lock gate (fun () ->
                    channels.Remove id |> ignore
                    waiting.Remove id |> ignore)

                channel.Fail(ChannelError.ResetByPeer(code, reason))
                None

    /// The connection ended: every channel fails.
    member _.CloseAll(reason: CloseReason) : unit =
        let all =
            lock gate (fun () ->
                closed <- Some reason
                let all = List.ofSeq channels.Values
                channels.Clear()
                waiting.Clear()
                all)

        for channel in all do
            channel.Fail(ChannelError.ConnectionClosed reason)
