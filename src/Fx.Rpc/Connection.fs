namespace Fx.Rpc

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects

/// Which side of a connection: the client sends `initialize` and opens odd channel ids.
[<RequireQualifiedAccess>]
type Role =
    | Client
    | Server

    override this.ToString() =
        match this with
        | Client -> "client"
        | Server -> "server"

/// The bounds of one side of a connection. Every queue is bounded; see docs/modules/rpc.md.
type ConnectionLimits =
    {
        /// Largest JSON-RPC text frame this side reads (UTF-8 bytes); announced to the peer, which
        /// refuses to send more (`CallError.TooLarge`).
        MaxMessageSize: int
        /// Requests from the peer that are queued or running; announced to the peer, which waits for a
        /// free slot before sending more. Beyond (a peer that does not wait, or batches), requests
        /// are answered `Overloaded` at once.
        MaxPendingRequests: int
        /// Received requests and notifications waiting for the dispatcher. When it is full the
        /// connection stops reading, which pushes back on the peer through the transport.
        MaxQueuedMessages: int
        /// Bytes queued for sending. Senders wait beyond it (a larger message waits for an empty
        /// queue).
        MaxOutboundBytes: int
        /// Binary channels this side accepts; `None` turns them off.
        Channels: ChannelSettings option
        /// How long the server waits for `initialize`, and the client for its result.
        HandshakeTimeout: TimeSpan
        /// How long a close waits for the transport: flushing, and the peer closing its side.
        CloseTimeout: TimeSpan
    }

    override this.ToString() =
        $"max message {this.MaxMessageSize}, {this.MaxPendingRequests} pending, {this.MaxQueuedMessages} queued"

module ConnectionLimits =
    /// 16 MiB messages, 256 pending requests, 1024 queued messages, 16 MiB outbound; channels with a
    /// 256 KiB window, 64 KiB frames, at most 256 open; 30 s handshake, 5 s close.
    let defaults: ConnectionLimits =
        {
            MaxMessageSize = 16 * 1024 * 1024
            MaxPendingRequests = 256
            MaxQueuedMessages = 1024
            MaxOutboundBytes = 16 * 1024 * 1024
            Channels =
                Some
                    {
                        InitialWindow = 256 * 1024
                        MaxFramePayload = 64 * 1024
                        MaxChannels = 256
                    }
            HandshakeTimeout = TimeSpan.FromSeconds 30.0
            CloseTimeout = TimeSpan.FromSeconds 5.0
        }

    /// The receive limits a transport for a connection with these limits needs.
    let frames (limits: ConnectionLimits) : FrameLimits =
        {
            MaxText = limits.MaxMessageSize
            MaxBinary =
                ChannelFrame.maxSize (
                    match limits.Channels with
                    | Some c -> c.MaxFramePayload
                    | None -> 0
                )
        }

/// Why a connection could not be established.
[<RequireQualifiedAccess>]
type ConnectError =
    /// The server answered `initialize` with an error: no common version, or its application
    /// refused the client (identity, …).
    | Refused of RpcError
    /// The handshake result is not valid.
    | InvalidHandshake of Issue list
    | TimedOut of after: TimeSpan
    | Disconnected of CloseReason

    override this.ToString() =
        match this with
        | Refused e -> "refused: " + string e
        | InvalidHandshake issues -> "invalid handshake:\n" + Issue.renderAtMost 10 issues
        | TimedOut after -> $"no handshake after {after.TotalSeconds} s"
        | Disconnected reason -> "disconnected: " + string reason

/// An entry of a batch sent with `Peer.RequestBatch`.
[<RequireQualifiedAccess>]
type BatchEntry =
    | Call of method: string * parameters: Json option
    | Notify of method: string * parameters: Json option

    override this.ToString() =
        match this with
        | Call(name, _) -> "call " + name
        | Notify(name, _) -> "notify " + name

/// What a handler is told about the request or notification it handles.
type RequestContext =
    {
        /// `None` for a notification.
        Id: RequestId option
        Method: string
        /// The params' `_meta`.
        Meta: Meta
        /// The connection, e.g. to call the peer back while handling a request.
        Peer: Peer
    }

    override this.ToString() =
        match this.Id with
        | Some id -> $"request {id} {this.Method}"
        | None -> $"notification {this.Method}"

/// How a call's wait ended.
and [<RequireQualifiedAccess>] internal Answer =
    | Answered of Result<Json, RpcError>
    | Lost of CloseReason
    | Expired
    | Abandoned

    override this.ToString() =
        match this with
        | Answered _ -> "answered"
        | Lost reason -> string reason
        | Expired -> "expired"
        | Abandoned -> "abandoned"

/// Where the response to a received request goes.
and [<RequireQualifiedAccess>] internal ReplyTo =
    | Direct
    | InBatch of BatchReply * index: int

    override this.ToString() =
        match this with
        | Direct -> "direct"
        | InBatch(_, index) -> $"batch entry {index}"

/// Work for the dispatcher, in the order it was received.
and [<RequireQualifiedAccess>] internal Work =
    | Call of
        id: RequestId *
        method: string *
        parameters: Json option *
        cancel: CancellationTokenSource *
        reply: ReplyTo
    | Signal of method: string * parameters: Json option

    override this.ToString() =
        match this with
        | Call(id, name, _, _, _) -> $"call {id} {name}"
        | Signal(name, _) -> $"signal {name}"

/// Collects the responses of a received batch and sends them as one array, in entry order.
and [<Sealed>] internal BatchReply(count: int, send: Message list -> Task) =
    let gate = obj ()
    let responses: Message option array = Array.create count None
    let mutable missing = count

    member _.Set(index: int, response: Message) : Task =
        let complete =
            lock gate (fun () ->
                if responses[index].IsNone then
                    responses[index] <- Some response
                    missing <- missing - 1

                missing = 0)

        if complete then
            send (responses |> Array.choose id |> List.ofArray)
        else
            Task.CompletedTask

/// One JSON-RPC connection over a transport (spec §17): both sides may call, notify, handle and
/// open channels. Create it with `Rpc.connect` (client) or `Rpc.accept` (server), which return it
/// after the handshake.
///
/// - **Reading** never waits for handlers. Responses complete calls at once; requests and
///   notifications go to a bounded queue; channel data goes to its channel, bounded by credit.
/// - **Dispatching** keeps arrival order: notifications are handled one at a time, in order; a request
///   starts after every earlier notification was handled, then runs concurrently on the thread pool.
/// - **Writing** is one loop over a bounded queue, so sends never overlap; credit and cancellation
///   frames go first.
/// - **Closing** ends calls with `Disconnected`, channels with `ConnectionClosed`, and cancels
///   handlers (a local close) or lets them finish (the peer closed its side).
and [<Sealed>] Peer
    internal
    (
        role: Role,
        limits: ConnectionLimits,
        info: PeerInfo,
        methods: string list,
        transport: Transport,
        clock: Clock,
        dispatch: RequestContext -> Json option -> CancellationToken -> Task<Result<Json, RpcError>>,
        acceptClient: Remote -> CancellationToken -> Task<Result<(string * Json) list, RpcError>>,
        scope: CancellationToken
    ) as self =

    let gate = obj ()
    let outbound = BoundedQueue<Frame>(int64 limits.MaxOutboundBytes)
    let work = BoundedQueue<Work>(int64 limits.MaxQueuedMessages)
    let pending = Dictionary<RequestId, Answer -> unit>()
    let running = Dictionary<RequestId, CancellationTokenSource>()
    let handlers = TaskGroup()
    let lifetime = CancellationTokenSource.CreateLinkedTokenSource scope

    let handshake =
        TaskCompletionSource<Result<Remote, ConnectError>>(TaskCreationOptions.RunContinuationsAsynchronously)

    let completion =
        TaskCompletionSource<CloseReason>(TaskCreationOptions.RunContinuationsAsynchronously)

    let readerDone =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let writerDone =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let dispatcherDone =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable remote: Remote option = None
    let mutable isOpen = false
    let mutable closeReason: CloseReason option = None
    let mutable nextId = 0L

    let channels =
        ChannelTable(
            (role = Role.Client),
            limits.Channels,
            (fun frame cancellation ->
                let size =
                    match frame with
                    | Frame.Binary bytes -> int64 bytes.Length
                    | Frame.Text text -> int64 text.Length

                outbound.Enqueue(frame, size, cancellation)),
            (fun frame -> outbound.EnqueueUrgent frame |> ignore)
        )

    // Slots for requests to the peer, as many as it accepts in flight; set by the handshake.
    let mutable slots: SemaphoreSlim option = None

    let capabilities =
        {
            MaxMessageSize = limits.MaxMessageSize
            MaxPendingRequests = Some limits.MaxPendingRequests
            Channels = limits.Channels
            Methods = methods
        }

    let utf8Size (text: string) = Encoding.UTF8.GetByteCount text

    let currentReason () =
        lock gate (fun () -> closeReason |> Option.defaultValue CloseReason.Local)

    let remoteLimit () =
        lock gate (fun () -> remote |> Option.map (fun r -> r.Capabilities.MaxMessageSize))

    /// Queues text in order; waits for room. False once the connection no longer sends.
    let sendText (text: string) (cancellation: CancellationToken) : Task<bool> =
        outbound.Enqueue(Frame.Text text, int64 (utf8Size text), cancellation)

    let sendUrgentText (text: string) =
        outbound.EnqueueUrgent(Frame.Text text) |> ignore

    /// A response that fits the peer's limit: an oversized result becomes `ResponseTooLarge`.
    let responseText (message: Message) : string =
        let text = Wire.serialize message

        match message, remoteLimit () with
        | Message.Response(id, _), Some limit ->
            let size = utf8Size text

            if size > limit then
                Wire.serialize (Message.Response(id, Error(RpcError.responseTooLarge size limit)))
            else
                text
        | _ -> text

    let sendResponse (message: Message) : Task =
        task {
            let! _ = sendText (responseText message) CancellationToken.None
            ()
        }

    let sendBatch (messages: Message list) : Task =
        task {
            let text = Wire.serializeBatch messages

            let text =
                match remoteLimit () with
                | Some limit when utf8Size text > limit ->
                    // Too large as a whole: every result gives way to an error.
                    let size = utf8Size text

                    messages
                    |> List.map (fun m ->
                        match m with
                        | Message.Response(id, Ok _) ->
                            Message.Response(id, Error(RpcError.responseTooLarge size limit))
                        | other -> other)
                    |> Wire.serializeBatch
                | _ -> text

            let! _ = sendText text CancellationToken.None
            ()
        }

    let reply (target: ReplyTo) (message: Message) : Task =
        match target with
        | ReplyTo.Direct -> sendResponse message
        | ReplyTo.InBatch(batch, index) -> batch.Set(index, message)

    let sendCancel (id: RequestId) =
        let parameters =
            Json.Object
                [
                    "id",
                    (match id with
                     | RequestId.Number n -> Json.int64 n
                     | RequestId.String s -> Json.String s)
                ]

        if lock gate (fun () -> isOpen) then
            sendUrgentText (Wire.serialize (Message.Notification(Protocol.CancelRequest, Some parameters)))

    let removePending (id: RequestId) =
        lock gate (fun () -> pending.Remove id |> ignore)

    let withinTimeout (work: Task) (timeout: TimeSpan) : Task<bool> =
        task {
            if work.IsCompleted then
                return true
            else
                use stop = new CancellationTokenSource()
                let delay = clock.Delay timeout stop.Token
                let! winner = Task.WhenAny(work, delay)
                stop.Cancel()

                try
                    do! delay
                with _ ->
                    ()

                return Object.ReferenceEquals(winner, work)
        }

    // ---- Receiving ------------------------------------------------------------------------------

    let runRequest
        (id: RequestId)
        (name: string)
        (parameters: Json option)
        (cancel: CancellationTokenSource)
        (target: ReplyTo)
        : Task =
        task {
            let! outcome =
                task {
                    try
                        match Meta.split parameters with
                        | Error issue -> return Error(RpcError.invalidParams [ issue ])
                        | Ok(parameters, meta) ->
                            let context =
                                {
                                    Id = Some id
                                    Method = name
                                    Meta = meta
                                    Peer = self
                                }

                            return! dispatch context parameters cancel.Token
                    with
                    | :? OperationCanceledException when cancel.IsCancellationRequested ->
                        return Error RpcError.cancelled
                    | e -> return Error(RpcError.internalError e)
                }

            lock gate (fun () -> running.Remove id |> ignore)
            cancel.Dispose()
            do! reply target (Message.Response(id, outcome))
        }

    let runNotification (name: string) (parameters: Json option) : Task =
        task {
            match Meta.split parameters with
            | Error _ -> ()
            | Ok(parameters, meta) ->
                let context =
                    {
                        Id = None
                        Method = name
                        Meta = meta
                        Peer = self
                    }

                try
                    let! _ = dispatch context parameters lifetime.Token
                    ()
                with _ ->
                    // Nobody to answer: a notification handler's failure stays with it.
                    ()
        }

    let dispatchLoop () : Task =
        task {
            let mutable go = true

            while go do
                let! next = work.Take()

                match next with
                | ValueNone -> go <- false
                | ValueSome(struct (item, size)) ->
                    work.Release size

                    match item with
                    | Work.Signal(name, parameters) -> do! runNotification name parameters
                    | Work.Call(id, name, parameters, cancel, target) ->
                        // On the thread pool: a handler's synchronous part never holds up the dispatcher.
                        handlers.Track(Task.Run(Func<Task>(fun () -> runRequest id name parameters cancel target)))

            dispatcherDone.TrySetResult() |> ignore
        }

    /// The server's side of the handshake, inline in the reader: nothing else is read meanwhile.
    let initialize (id: RequestId) (parameters: Json option) : Task =
        task {
            let fail (error: RpcError) =
                sendResponse (Message.Response(id, Error error))

            let members = parameters |> Option.defaultValue (Json.Object [])

            match Meta.split parameters with
            | Error issue -> do! fail (RpcError.invalidParams [ issue ])
            | Ok(withoutMeta, _) ->
                match Params.decode Handshake.initializeParams withoutMeta with
                | Error issues -> do! fail (RpcError.invalidParams issues)
                | Ok hello ->
                    match Handshake.choose hello.ProtocolVersions Protocol.versions with
                    | None -> do! fail (Handshake.unsupported Protocol.versions)
                    | Some version ->
                        let client =
                            {
                                ProtocolVersion = version
                                Peer = hello.Peer
                                Capabilities = hello.Capabilities
                                Members = members
                            }

                        let! accepted =
                            task {
                                try
                                    return! acceptClient client lifetime.Token
                                with e ->
                                    return Error(RpcError.internalError e)
                            }

                        match accepted with
                        | Error e -> do! fail e
                        | Ok extra ->
                            let result =
                                Codec.encode
                                    Handshake.initializeResult
                                    {
                                        ProtocolVersion = version
                                        Peer = info
                                        Capabilities = capabilities
                                    }
                                |> Handshake.withMembers extra

                            lock gate (fun () ->
                                remote <- Some client

                                slots <-
                                    client.Capabilities.MaxPendingRequests
                                    |> Option.map (fun n -> new SemaphoreSlim(n, n))

                                isOpen <- closeReason.IsNone)

                            channels.Negotiate client.Capabilities.Channels
                            // Queued before the application learns of the connection, so the result
                            // goes out before anything the application sends.
                            do! sendResponse (Message.Response(id, Ok result))
                            handshake.TrySetResult(Ok client) |> ignore
        }

    let cancelRequest (parameters: Json option) =
        let id =
            match parameters |> Option.bind (Json.tryField "id") with
            | Some(Json.Number n) -> JsonNumber.tryToInt64 n |> Option.map RequestId.Number
            | Some(Json.String s) -> Some(RequestId.String s)
            | _ -> None

        match id with
        | Some id ->
            match lock gate (fun () -> running.TryGetValue id) with
            | true, cancel ->
                // Callbacks run on the thread pool, never in the reader.
                try
                    cancel.CancelAsync() |> ignore
                with :? ObjectDisposedException ->
                    ()
            | false, _ -> ()
        | None -> ()

    let onRequest (id: RequestId) (name: string) (parameters: Json option) (target: ReplyTo) : Task<Message option> =
        let reject (error: RpcError) =
            Task.FromResult(Some(Message.Response(id, Error error)))

        let opened, closing = lock gate (fun () -> isOpen, closeReason.IsSome)

        if closing then
            reject RpcError.shuttingDown
        elif not opened then
            if role = Role.Server && name = Protocol.Initialize then
                match target with
                | ReplyTo.Direct ->
                    task {
                        do! initialize id parameters
                        return None
                    }
                | ReplyTo.InBatch _ -> reject (RpcError.invalidRequest "initialize cannot be part of a batch")
            else
                reject RpcError.notInitialized
        elif name = Protocol.Initialize then
            reject (RpcError.invalidRequest "the connection is already initialized")
        else
            let admitted =
                lock gate (fun () ->
                    if running.ContainsKey id then
                        Error(RpcError.invalidRequest $"a request with id {id} is already in flight")
                    elif running.Count >= limits.MaxPendingRequests then
                        Error(RpcError.overloaded limits.MaxPendingRequests)
                    else
                        let cancel = CancellationTokenSource.CreateLinkedTokenSource lifetime.Token
                        running[id] <- cancel
                        Ok cancel)

            match admitted with
            | Error e -> reject e
            | Ok cancel ->
                task {
                    let! queued =
                        work.Enqueue(Work.Call(id, name, parameters, cancel, target), 1L, CancellationToken.None)

                    if queued then
                        return None
                    else
                        lock gate (fun () -> running.Remove id |> ignore)
                        cancel.Dispose()
                        return Some(Message.Response(id, Error RpcError.shuttingDown))
                }

    let onNotification (name: string) (parameters: Json option) : Task =
        if name = Protocol.CancelRequest then
            cancelRequest parameters
            Task.CompletedTask
        elif lock gate (fun () -> isOpen) then
            task {
                let! _ = work.Enqueue(Work.Signal(name, parameters), 1L, CancellationToken.None)
                ()
            }
        else
            Task.CompletedTask

    let onResponse (id: RequestId) (result: Result<Json, RpcError>) =
        let complete =
            lock gate (fun () ->
                match pending.TryGetValue id with
                | true, complete ->
                    pending.Remove id |> ignore
                    Some complete
                | false, _ -> None)

        // A response to an unknown id: a call that timed out or was cancelled. Dropped.
        match complete with
        | Some complete -> complete (Answer.Answered result)
        | None -> ()

    /// Handles one entry; returns an immediate answer, if any.
    let onEntry (entry: Parsed) (target: ReplyTo) : Task<Message option> =
        match entry with
        | Parsed.Valid(Message.Request(id, name, parameters)) -> onRequest id name parameters target
        | Parsed.Valid(Message.Notification(name, parameters)) ->
            task {
                do! onNotification name parameters
                return None
            }
        | Parsed.Valid(Message.Response(id, result)) ->
            onResponse id result
            Task.FromResult None
        // The peer could not read something this side sent; there is nothing to correlate.
        | Parsed.Valid(Message.ErrorWithoutId _) -> Task.FromResult None
        | Parsed.InvalidRequest(Some id, error) -> Task.FromResult(Some(Message.Response(id, Error error)))
        | Parsed.InvalidRequest(None, error) -> Task.FromResult(Some(Message.ErrorWithoutId error))
        | Parsed.InvalidResponse _ -> Task.FromResult None

    let onBatch (entries: Parsed list) : Task =
        task {
            let answered =
                entries
                |> List.filter (fun entry ->
                    match entry with
                    | Parsed.Valid(Message.Request _)
                    | Parsed.InvalidRequest _ -> true
                    | _ -> false)
                |> List.length

            let batch = BatchReply(answered, (fun messages -> sendBatch messages))
            let mutable index = 0

            for entry in entries do
                match entry with
                | Parsed.Valid(Message.Request _)
                | Parsed.InvalidRequest _ ->
                    let slot = index
                    index <- index + 1
                    let! immediate = onEntry entry (ReplyTo.InBatch(batch, slot))

                    match immediate with
                    | Some message -> do! batch.Set(slot, message)
                    | None -> ()
                | _ ->
                    let! _ = onEntry entry ReplyTo.Direct
                    ()
        }

    let onText (text: string) : Task =
        match Wire.parse text with
        | Incoming.Unreadable error -> sendResponse (Message.ErrorWithoutId error)
        | Incoming.Single entry ->
            task {
                let! immediate = onEntry entry ReplyTo.Direct

                match immediate with
                | Some message -> do! sendResponse message
                | None -> ()
            }
        | Incoming.Batch entries -> onBatch entries

    let onBinary (bytes: byte array) : string option =
        if not (lock gate (fun () -> isOpen || closeReason.IsSome)) then
            Some "a channel frame before the handshake"
        else
            match ChannelFrame.decode bytes with
            | Error reason -> Some reason
            | Ok frame -> channels.Receive frame

    // ---- Closing --------------------------------------------------------------------------------

    let mutable shutdown: CloseReason -> Task<CloseReason> = fun _ -> completion.Task

    let readLoop () : Task =
        task {
            let mutable stop = None

            while stop.IsNone do
                let! received =
                    task {
                        try
                            return! transport.Receive CancellationToken.None
                        with e ->
                            return Received.Failed(TransportError.Broken(e.GetType().Name))
                    }

                match received with
                | Received.Frame(Frame.Text text) -> do! onText text
                | Received.Frame(Frame.Binary bytes) ->
                    match onBinary bytes with
                    | Some violation -> stop <- Some(CloseReason.ProtocolViolation violation)
                    | None -> ()
                | Received.End -> stop <- Some CloseReason.Peer
                | Received.Failed(TransportError.Broken reason) -> stop <- Some(CloseReason.TransportFailed reason)
                | Received.Failed(TransportError.TooLarge limit) ->
                    stop <- Some(CloseReason.ProtocolViolation $"a frame larger than {limit} bytes")
                | Received.Failed(TransportError.Malformed reason) -> stop <- Some(CloseReason.ProtocolViolation reason)

            readerDone.TrySetResult() |> ignore
            shutdown stop.Value |> ignore
        }

    let writeLoop () : Task =
        task {
            let mutable go = true

            while go do
                let! next = outbound.Take()

                match next with
                | ValueNone -> go <- false
                | ValueSome(struct (frame, size)) ->
                    let! sent =
                        task {
                            try
                                return! transport.Send frame CancellationToken.None
                            with e ->
                                return Error(TransportError.Broken(e.GetType().Name))
                        }

                    outbound.Release size

                    match sent with
                    | Ok() -> ()
                    | Error e ->
                        go <- false
                        shutdown (CloseReason.TransportFailed(string e)) |> ignore

            writerDone.TrySetResult() |> ignore
        }

    /// Answers the calls that were queued but not started.
    let refuseQueued () : Task =
        task {
            for item in work.Drain() do
                match item with
                | Work.Call(id, _, _, cancel, target) ->
                    lock gate (fun () -> running.Remove id |> ignore)
                    cancel.Dispose()
                    do! reply target (Message.Response(id, Error RpcError.shuttingDown))
                | Work.Signal _ -> ()
        }

    let flushAndClose (how: Shutdown) : Task =
        task {
            outbound.Complete()
            let! flushed = withinTimeout writerDone.Task limits.CloseTimeout

            if flushed then
                use stop = new CancellationTokenSource()
                let closing = transport.CloseOutput how stop.Token
                let! closed = withinTimeout closing limits.CloseTimeout

                if not closed then
                    stop.Cancel()
        }

    let finish (reason: CloseReason) : Task =
        task {
            match reason with
            | CloseReason.Local ->
                do! refuseQueued ()
                do! lifetime.CancelAsync()
                do! dispatcherDone.Task
                do! handlers.WaitAll()
                do! flushAndClose Shutdown.Normal
                // Keep reading until the peer closes its side too, so it sees every response.
                let! _ = withinTimeout readerDone.Task limits.CloseTimeout
                ()
            | CloseReason.Peer ->
                // Requests that arrived are still answered: the peer may only have closed its output
                // (`echo request | program`). A peer that crashed looks the same on a pipe, so the
                // handlers get `CloseTimeout` to finish before they are cancelled.
                work.Complete()

                let handled =
                    task {
                        do! dispatcherDone.Task
                        do! handlers.WaitAll()
                    }

                let! drained = withinTimeout handled limits.CloseTimeout

                if not drained then
                    do! lifetime.CancelAsync()
                    do! dispatcherDone.Task
                    do! handlers.WaitAll()

                do! flushAndClose Shutdown.Normal
            | CloseReason.ProtocolViolation _
            | CloseReason.TransportFailed _
            | CloseReason.HandshakeFailed _ ->
                work.Fail()
                do! lifetime.CancelAsync()

                match reason with
                | CloseReason.ProtocolViolation violation ->
                    // Let the peer know why, where the transport can say it (a WebSocket close code).
                    outbound.Fail()
                    let! stopped = withinTimeout writerDone.Task limits.CloseTimeout

                    if stopped then
                        let how =
                            if violation.StartsWith("a frame larger", StringComparison.Ordinal) then
                                Shutdown.TooLarge
                            else
                                Shutdown.ProtocolError

                        use stop = new CancellationTokenSource()
                        let! _ = withinTimeout (transport.CloseOutput how stop.Token) limits.CloseTimeout
                        stop.Cancel()
                | _ -> outbound.Fail()

                transport.Abort()
                do! dispatcherDone.Task
                do! handlers.WaitAll()

            transport.Abort()
        }

    do
        shutdown <-
            fun reason ->
                let first =
                    lock gate (fun () ->
                        match closeReason with
                        | Some _ -> false
                        | None ->
                            closeReason <- Some reason
                            isOpen <- false
                            true)

                if first then
                    let lost =
                        lock gate (fun () ->
                            let calls = List.ofSeq pending.Values
                            pending.Clear()
                            calls)

                    for complete in lost do
                        complete (Answer.Lost reason)

                    handshake.TrySetResult(Error(ConnectError.Disconnected reason)) |> ignore
                    channels.CloseAll reason

                    let finishing =
                        task {
                            try
                                do! finish reason
                            with _ ->
                                transport.Abort()

                            lifetime.Dispose()
                            completion.TrySetResult reason |> ignore
                        }

                    finishing |> ignore

                completion.Task

    member internal _.Start() : unit =
        if scope.CanBeCanceled then
            let registration = scope.Register(fun () -> shutdown CloseReason.Local |> ignore)

            completion.Task.ContinueWith(
                (fun (_: Task<CloseReason>) -> registration.Dispose()),
                TaskContinuationOptions.ExecuteSynchronously
            )
            |> ignore

        // Off the caller's thread: the loops run for the connection's whole life.
        Task.Run(Func<Task>(fun () -> readLoop ())) |> ignore
        Task.Run(Func<Task>(fun () -> writeLoop ())) |> ignore
        Task.Run(Func<Task>(fun () -> dispatchLoop ())) |> ignore

    /// The client's side of the handshake.
    member internal _.Initialize(members: (string * Json) list) : Task<Result<Remote, ConnectError>> =
        task {
            let parameters =
                Codec.encode
                    Handshake.initializeParams
                    {
                        ProtocolVersions = Protocol.versions
                        Peer = info
                        Capabilities = capabilities
                    }
                |> Handshake.withMembers members

            let id =
                lock gate (fun () ->
                    let id = RequestId.Number nextId
                    nextId <- nextId + 1L
                    id)

            // Runs in the reader as the result arrives, so the connection is open before the next
            // frame (perhaps a request from the server) is read.
            let complete (answer: Answer) =
                let outcome =
                    match answer with
                    | Answer.Answered(Ok json) ->
                        match Meta.split (Some json) with
                        | Error issue -> Error(ConnectError.InvalidHandshake [ issue ])
                        | Ok(withoutMeta, _) ->
                            match Params.decode Handshake.initializeResult withoutMeta with
                            | Error issues -> Error(ConnectError.InvalidHandshake issues)
                            | Ok result when not (List.contains result.ProtocolVersion Protocol.versions) ->
                                Error(
                                    ConnectError.InvalidHandshake
                                        [
                                            Issue.at
                                                [ PathSegment.Key "protocolVersion" ]
                                                $"the server chose {Json.quote result.ProtocolVersion}, which was not offered"
                                        ]
                                )
                            | Ok result ->
                                let server =
                                    {
                                        ProtocolVersion = result.ProtocolVersion
                                        Peer = result.Peer
                                        Capabilities = result.Capabilities
                                        Members = json
                                    }

                                lock gate (fun () ->
                                    remote <- Some server

                                    slots <-
                                        server.Capabilities.MaxPendingRequests
                                        |> Option.map (fun n -> new SemaphoreSlim(n, n))

                                    isOpen <- closeReason.IsNone)

                                channels.Negotiate server.Capabilities.Channels
                                Ok server
                    | Answer.Answered(Error e) -> Error(ConnectError.Refused e)
                    | Answer.Lost reason -> Error(ConnectError.Disconnected reason)
                    | Answer.Expired -> Error(ConnectError.TimedOut limits.HandshakeTimeout)
                    | Answer.Abandoned -> Error(ConnectError.Disconnected CloseReason.Local)

                handshake.TrySetResult outcome |> ignore

            lock gate (fun () -> pending[id] <- complete)

            let! _ =
                sendText
                    (Wire.serialize (Message.Request(id, Protocol.Initialize, Some parameters)))
                    CancellationToken.None

            return! self.AwaitHandshake()
        }

    /// Waits for the handshake to finish, at most `HandshakeTimeout`.
    member internal _.AwaitHandshake() : Task<Result<Remote, ConnectError>> =
        task {
            let! finished = withinTimeout handshake.Task limits.HandshakeTimeout

            if finished then
                return handshake.Task.Result
            else
                let outcome = Error(ConnectError.TimedOut limits.HandshakeTimeout)

                if handshake.TrySetResult outcome then
                    shutdown (CloseReason.HandshakeFailed "timed out") |> ignore

                return handshake.Task.Result
        }

    /// This side's role.
    member _.Role = role

    /// What the handshake told about the peer. Throws before the handshake.
    member _.Remote: Remote =
        match lock gate (fun () -> remote) with
        | Some r -> r
        | None -> invalidOp "The connection has not completed its handshake."

    /// Whether the peer announced that it handles a method.
    member this.Supports(name: string) : bool =
        List.contains name this.Remote.Capabilities.Methods

    /// Completes when the connection has ended and everything it started has finished.
    member _.Completion: Task<CloseReason> = completion.Task

    /// Sends a request and waits for its response: the raw form of `Rpc.call`. `params` must be an
    /// object or an array; attach `_meta` with `Meta.attach`. Cancelling the token cancels the
    /// request at the peer (`$/cancelRequest`) and throws `OperationCanceledException`; a timeout
    /// (on the connection's clock) does the same and returns `TimedOut`.
    member _.Request
        (name: string, parameters: Json option, timeout: TimeSpan option, cancellation: CancellationToken)
        : Task<Result<Json, CallError<NoError>>> =
        task {
            cancellation.ThrowIfCancellationRequested()

            let start =
                lock gate (fun () ->
                    if isOpen then
                        let id = RequestId.Number nextId
                        nextId <- nextId + 1L
                        Ok id
                    else
                        Error(closeReason |> Option.defaultValue CloseReason.Local))

            match start with
            | Error reason -> return Error(CallError.Disconnected reason)
            | Ok id ->
                let text = Wire.serialize (Message.Request(id, name, parameters))
                let size = utf8Size text
                let limit = remoteLimit () |> Option.defaultValue Int32.MaxValue

                if size > limit then
                    return Error(CallError.TooLarge(size, limit))
                else
                    let answer =
                        TaskCompletionSource<Answer>(TaskCreationOptions.RunContinuationsAsynchronously)

                    // A slot is held until the peer answers, even after a timeout or cancellation: the
                    // peer still counts the request until its handler has finished.
                    let slot = lock gate (fun () -> slots)

                    match slot with
                    | Some s -> do! s.WaitAsync cancellation
                    | None -> ()

                    let release () =
                        match slot with
                        | Some s -> s.Release() |> ignore
                        | None -> ()

                    let registered =
                        lock gate (fun () ->
                            if isOpen then
                                pending[id] <-
                                    fun a ->
                                        answer.TrySetResult a |> ignore
                                        release ()

                                true
                            else
                                false)

                    if not registered then
                        release ()
                        return Error(CallError.Disconnected(currentReason ()))
                    else
                        let forget () =
                            if lock gate (fun () -> pending.Remove id) then
                                release ()

                        let! queued =
                            task {
                                try
                                    return! outbound.Enqueue(Frame.Text text, int64 size, cancellation)
                                with :? OperationCanceledException ->
                                    forget ()
                                    return raise (OperationCanceledException(cancellation))
                            }

                        if not queued then
                            forget ()
                            return Error(CallError.Disconnected(currentReason ()))
                        else
                            use expiry = new CancellationTokenSource()

                            match timeout with
                            | Some after ->
                                (clock.Delay after expiry.Token)
                                    .ContinueWith(
                                        (fun (delay: Task<unit>) ->
                                            if delay.IsCompletedSuccessfully then
                                                answer.TrySetResult Answer.Expired |> ignore),
                                        TaskContinuationOptions.ExecuteSynchronously
                                    )
                                |> ignore
                            | None -> ()

                            use _ =
                                cancellation.Register(fun () -> answer.TrySetResult Answer.Abandoned |> ignore)

                            let! outcome = answer.Task
                            expiry.Cancel()

                            match outcome with
                            | Answer.Answered(Ok json) -> return Ok json
                            | Answer.Answered(Error e) -> return Error(CallError.Rejected e)
                            | Answer.Lost reason -> return Error(CallError.Disconnected reason)
                            // The call stays pending: the peer's late answer frees its slot.
                            | Answer.Expired ->
                                sendCancel id
                                return Error(CallError.TimedOut timeout.Value)
                            | Answer.Abandoned ->
                                sendCancel id
                                return raise (OperationCanceledException(cancellation))
        }

    /// Sends a notification: the raw form of `Rpc.notify`. Waits while the outbound queue is full.
    member _.Notify
        (name: string, parameters: Json option, cancellation: CancellationToken)
        : Task<Result<unit, SendError>> =
        task {
            if not (lock gate (fun () -> isOpen)) then
                return Error(SendError.Disconnected(currentReason ()))
            else
                let text = Wire.serialize (Message.Notification(name, parameters))
                let size = utf8Size text
                let limit = remoteLimit () |> Option.defaultValue Int32.MaxValue

                if size > limit then
                    return Error(SendError.TooLarge(size, limit))
                else
                    let! queued = outbound.Enqueue(Frame.Text text, int64 size, cancellation)

                    if queued then
                        return Ok()
                    else
                        return Error(SendError.Disconnected(currentReason ()))
        }

    /// Sends a JSON-RPC batch and waits for every response: one result per `Call` entry, in order.
    member _.RequestBatch
        (entries: BatchEntry list, cancellation: CancellationToken)
        : Task<Result<Result<Json, RpcError> list, CallError<NoError>>> =
        task {
            cancellation.ThrowIfCancellationRequested()

            let numbered =
                lock gate (fun () ->
                    if isOpen then
                        entries
                        |> List.map (fun entry ->
                            match entry with
                            | BatchEntry.Call(name, parameters) ->
                                let id = RequestId.Number nextId
                                nextId <- nextId + 1L
                                Some id, Message.Request(id, name, parameters)
                            | BatchEntry.Notify(name, parameters) -> None, Message.Notification(name, parameters))
                        |> Ok
                    else
                        Error(closeReason |> Option.defaultValue CloseReason.Local))

            match numbered with
            | Error reason -> return Error(CallError.Disconnected reason)
            | Ok [] -> return Ok []
            | Ok numbered ->
                let text = Wire.serializeBatch (List.map snd numbered)
                let size = utf8Size text
                let limit = remoteLimit () |> Option.defaultValue Int32.MaxValue

                if size > limit then
                    return Error(CallError.TooLarge(size, limit))
                else
                    let ids = numbered |> List.choose fst

                    let answers =
                        ids
                        |> List.map (fun id ->
                            id, TaskCompletionSource<Answer>(TaskCreationOptions.RunContinuationsAsynchronously))

                    lock gate (fun () ->
                        for id, answer in answers do
                            pending[id] <- fun a -> answer.TrySetResult a |> ignore)

                    let forget () =
                        for id, _ in answers do
                            removePending id

                    let! queued =
                        task {
                            try
                                return! outbound.Enqueue(Frame.Text text, int64 size, cancellation)
                            with :? OperationCanceledException ->
                                forget ()
                                return raise (OperationCanceledException(cancellation))
                        }

                    if not queued then
                        forget ()
                        return Error(CallError.Disconnected(currentReason ()))
                    else
                        use _ =
                            cancellation.Register(fun () ->
                                for _, answer in answers do
                                    answer.TrySetResult Answer.Abandoned |> ignore)

                        let results = ResizeArray()
                        let mutable failure = None

                        for id, answer in answers do
                            let! outcome = answer.Task

                            match outcome with
                            | Answer.Answered result -> results.Add result
                            | Answer.Lost reason -> failure <- Some(CallError.Disconnected reason)
                            | Answer.Expired
                            | Answer.Abandoned ->
                                removePending id
                                sendCancel id

                        cancellation.ThrowIfCancellationRequested()

                        match failure with
                        | Some error -> return Error error
                        | None -> return Ok(List.ofSeq results)
        }

    /// Opens a binary channel. Send its `Id` to the peer (in a request or a result), which takes the
    /// channel with `AcceptChannel`; data may be written at once.
    member _.OpenChannel(cancellation: CancellationToken) : Task<Result<Channel, ChannelError>> =
        channels.Open cancellation

    /// Takes a channel the peer opened.
    member _.AcceptChannel(id: uint32) : Result<Channel, ChannelError> = channels.Accept id

    /// How many channels count against `maxChannels` on this side: opened here or by the peer, and
    /// not yet released (both directions ended, or reset). For diagnostics and tests.
    member _.OpenChannels: int = channels.Count

    /// Closes the connection (see the type's documentation) and waits until it has ended.
    member _.CloseAsync() : Task<CloseReason> = shutdown CloseReason.Local

    override _.ToString() =
        let state =
            lock gate (fun () ->
                match closeReason, isOpen with
                | Some reason, _ -> string reason
                | None, true -> "open"
                | None, false -> "handshaking")

        $"{role} over {transport.Name} ({state})"
