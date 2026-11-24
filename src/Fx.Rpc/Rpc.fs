namespace Fx.Rpc

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects

/// The server side of one method or notification, built from its typed definition.
type Handler<'R> =
    internal
        {
            Name: string
            IsRequest: bool
            /// Runs in an environment whose scope is the request's (cancelled with it).
            Run: 'R -> RequestContext -> Json option -> Task<Result<Json, RpcError>>
        }

    override this.ToString() =
        (if this.IsRequest then "request " else "notification ") + this.Name

module Handler =
    let private applicationError (m: Method<'P, 'Res, 'E>) (error: 'E) : RpcError =
        RpcError.create Protocol.ErrorCode.ApplicationError (string error)
        |> RpcError.withData (m.Error.Encode error)

    /// A request handler whose result carries `_meta` (the result must then encode to an object).
    /// Invalid params never reach it: they are answered `InvalidParams` with the codec's issues.
    let requestReply
        (m: Method<'P, 'Res, 'E>)
        (handle: RequestContext -> 'P -> Eff<'R, Result<Reply<'Res>, 'E>>)
        : Handler<'R> =
        {
            Name = m.Name
            IsRequest = true
            Run =
                fun env context parameters ->
                    match Params.decode m.Params parameters with
                    | Error issues -> Task.FromResult(Error(RpcError.invalidParams issues))
                    | Ok value ->
                        task {
                            let! outcome = Eff.run env (handle context value)

                            match outcome with
                            | Ok reply ->
                                let json = Meta.attach reply.Meta (Some(m.Result.Encode reply.Value))
                                return Ok json.Value
                            | Error e -> return Error(applicationError m e)
                        }
        }

    /// A request handler. Its `Error` is the method's typed error, an expected failure; defects
    /// (exceptions) are answered `InternalError` without their message.
    let request (m: Method<'P, 'Res, 'E>) (handle: RequestContext -> 'P -> Eff<'R, Result<'Res, 'E>>) : Handler<'R> =
        requestReply m (fun context value -> handle context value |> Eff.map (Result.map Reply.ofValue))

    /// A notification handler. Notifications are handled one at a time, in the order they arrive;
    /// invalid params and failures are dropped (there is nobody to answer).
    let notification (n: Notification<'P>) (handle: RequestContext -> 'P -> Eff<'R, unit>) : Handler<'R> =
        {
            Name = n.Name
            IsRequest = false
            Run =
                fun env context parameters ->
                    match Params.decode n.Params parameters with
                    | Error issues -> Task.FromResult(Error(RpcError.invalidParams issues))
                    | Ok value ->
                        task {
                            do! Eff.run env (handle context value)
                            return Ok Json.Null
                        }
        }

/// What one side of a connection offers.
type Service<'R> =
    {
        Info: PeerInfo
        ConnectionLimits: ConnectionLimits
        Handlers: Handler<'R> list
        /// On the server: decides on a client's `initialize` (e.g. checks its identity, spec §17) and
        /// returns members to add to the result (e.g. a session token). Unused on the client.
        Accept: Remote -> Eff<'R, Result<(string * Json) list, RpcError>>
    }

    override this.ToString() =
        $"{this.Info.Name} {this.Info.Version} ({this.Handlers.Length} handlers)"

module Service =
    /// A service with the default limits that accepts every client.
    let create (info: PeerInfo) (handlers: Handler<'R> list) : Service<'R> =
        {
            Info = info
            ConnectionLimits = ConnectionLimits.defaults
            Handlers = handlers
            Accept = fun _ -> Eff.ret (Ok [])
        }

    let withLimits (limits: ConnectionLimits) (service: Service<'R>) : Service<'R> =
        { service with
            ConnectionLimits = limits
        }

    let withAccept (accept: Remote -> Eff<'R, Result<(string * Json) list, RpcError>>) (service: Service<'R>) =
        { service with Accept = accept }

/// Options of one call.
type CallOptions =
    {
        /// Give up after this long on the connection's clock; the request is cancelled at the peer.
        Timeout: TimeSpan option
        /// Sent as the params' `_meta`.
        Meta: Meta
    }

    override this.ToString() =
        match this.Timeout with
        | Some t -> $"timeout {t.TotalSeconds} s, {this.Meta.Count} meta keys"
        | None -> $"no timeout, {this.Meta.Count} meta keys"

module CallOptions =
    let defaults: CallOptions = { Timeout = None; Meta = Meta.empty }

    let timeout (after: TimeSpan) : CallOptions = { defaults with Timeout = Some after }

/// The typed, effectful API of the protocol (spec §17).
///
/// A connection lives in the scope of the effect that opened it: cancelling that scope closes it,
/// and its handlers run in child scopes of it (each request's scope is cancelled with the request).
/// The environment's clock times out calls and bounds the handshake and the close.
module Rpc =

    let private checkLimits (limits: ConnectionLimits) =
        if limits.MaxMessageSize < Handshake.minMessageSize then
            invalidArg (nameof limits) $"MaxMessageSize must be at least {Handshake.minMessageSize}."

        if
            limits.MaxPendingRequests < 1
            || limits.MaxQueuedMessages < 1
            || limits.MaxOutboundBytes < 1
        then
            invalidArg (nameof limits) "Queue limits must be positive."

        match limits.Channels with
        | Some c when
            c.InitialWindow < 1
            || c.MaxFramePayload < 1
            || c.MaxFramePayload > Handshake.maxFramePayloadLimit
            || c.MaxChannels < 0
            ->
            invalidArg (nameof limits) "Invalid channel settings."
        | _ -> ()

    let private start<'R when 'R :> IClock and 'R :> IScopeWith<'R>>
        (role: Role)
        (service: Service<'R>)
        (transport: Transport)
        (env: 'R)
        : Peer =
        checkLimits service.ConnectionLimits
        let table = Dictionary<string, Handler<'R>>(StringComparer.Ordinal)

        for handler in service.Handlers do
            if not (table.TryAdd(handler.Name, handler)) then
                invalidArg (nameof service) $"Two handlers for {Json.quote handler.Name}."

        let methods =
            service.Handlers
            |> List.map (fun h -> h.Name)
            |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

        let scoped (cancellation: CancellationToken) : 'R =
            env.WithScope
                { env.Scope with
                    Cancellation = cancellation
                }

        let dispatch (context: RequestContext) (parameters: Json option) (cancellation: CancellationToken) =
            match table.TryGetValue context.Method with
            | true, handler when handler.IsRequest = context.Id.IsSome ->
                handler.Run (scoped cancellation) context parameters
            | _ -> Task.FromResult(Error(RpcError.methodNotFound context.Method))

        let accept (remote: Remote) (cancellation: CancellationToken) =
            Eff.run (scoped cancellation) (service.Accept remote)

        let peer =
            Peer(
                role,
                service.ConnectionLimits,
                service.Info,
                methods,
                transport,
                env.Clock,
                dispatch,
                accept,
                env.Scope.Cancellation
            )

        peer.Start()
        peer

    /// Connects as the client: sends `initialize` with `members` added to its params (identity, …)
    /// and returns the connection once the server accepted it.
    let connect<'R when 'R :> IClock and 'R :> IScopeWith<'R>>
        (service: Service<'R>)
        (members: (string * Json) list)
        (transport: Transport)
        : Eff<'R, Result<Peer, ConnectError>> =
        fun env ->
            task {
                let peer = start Role.Client service transport env
                let! outcome = peer.Initialize members

                match outcome with
                | Ok _ -> return Ok peer
                | Error e ->
                    let! _ = peer.CloseAsync()
                    return Error e
            }

    /// Serves as the server: waits for the client's `initialize` (at most `HandshakeTimeout`), asks
    /// `Service.Accept`, and returns the connection once the handshake is done.
    let accept<'R when 'R :> IClock and 'R :> IScopeWith<'R>>
        (service: Service<'R>)
        (transport: Transport)
        : Eff<'R, Result<Peer, ConnectError>> =
        fun env ->
            task {
                let peer = start Role.Server service transport env
                let! outcome = peer.AwaitHandshake()

                match outcome with
                | Ok _ -> return Ok peer
                | Error e ->
                    let! _ = peer.CloseAsync()
                    return Error e
            }

    let private decodeReply (m: Method<'P, 'Res, 'E>) (json: Json) : Result<Reply<'Res>, CallError<'E>> =
        match Meta.split (Some json) with
        | Error issue -> Error(CallError.InvalidReply [ issue ])
        | Ok(value, meta) ->
            match m.Result.Decode(value |> Option.defaultValue Json.Null) with
            | Ok decoded -> Ok { Value = decoded; Meta = meta }
            | Error issues -> Error(CallError.InvalidReply issues)

    let private typedError (m: Method<'P, 'Res, 'E>) (error: CallError<NoError>) : CallError<'E> =
        match error with
        | CallError.Rejected e when e.Code = Protocol.ErrorCode.ApplicationError ->
            match m.Error.Decode(e.Data |> Option.defaultValue Json.Null) with
            | Ok typed -> CallError.Failed typed
            | Error issues -> CallError.InvalidReply(issues |> List.map (Issue.under (PathSegment.Key "data")))
        | CallError.Rejected e -> CallError.Rejected e
        | CallError.Failed none -> invalidOp $"{none}"
        | CallError.InvalidReply issues -> CallError.InvalidReply issues
        | CallError.TooLarge(size, limit) -> CallError.TooLarge(size, limit)
        | CallError.TimedOut after -> CallError.TimedOut after
        | CallError.Disconnected reason -> CallError.Disconnected reason

    /// Decodes the outcome of one request (e.g. from a batch) with a method's codecs.
    let decodeOutcome (m: Method<'P, 'Res, 'E>) (outcome: Result<Json, RpcError>) : Result<'Res, CallError<'E>> =
        match outcome with
        | Ok json -> decodeReply m json |> Result.map (fun r -> r.Value)
        | Error e -> Error(typedError m (CallError.Rejected e))

    /// Calls a method with options: a timeout, `_meta`. Returns the result with its `_meta`.
    /// Cancelling the scope cancels the request at the peer and fails with `OperationCanceledException`.
    let callWith
        (options: CallOptions)
        (peer: Peer)
        (m: Method<'P, 'Res, 'E>)
        (value: 'P)
        : Eff<#IScope, Result<Reply<'Res>, CallError<'E>>> =
        fun env ->
            task {
                let parameters = Params.encode m.Params value |> Meta.attach options.Meta

                let! outcome =
                    peer.Request(m.Name, parameters, options.Timeout, env.Scope.Cancellation)

                return
                    match outcome with
                    | Ok json -> decodeReply m json
                    | Error e -> Error(typedError m e)
            }

    /// Calls a method and waits for its result.
    let call (peer: Peer) (m: Method<'P, 'Res, 'E>) (value: 'P) : Eff<#IScope, Result<'Res, CallError<'E>>> =
        fun env ->
            task {
                let! outcome = callWith CallOptions.defaults peer m value env
                return outcome |> Result.map (fun r -> r.Value)
            }

    /// Sends a notification with `_meta`.
    let notifyWith (meta: Meta) (peer: Peer) (n: Notification<'P>) (value: 'P) : Eff<#IScope, Result<unit, SendError>> =
        fun env -> peer.Notify(n.Name, Params.encode n.Params value |> Meta.attach meta, env.Scope.Cancellation)

    /// Sends a notification. Notifications from one sender are handled in the order they were sent.
    let notify (peer: Peer) (n: Notification<'P>) (value: 'P) : Eff<#IScope, Result<unit, SendError>> =
        notifyWith Meta.empty peer n value

    /// Sends a JSON-RPC batch (see `Batch`) and returns one outcome per call, in order.
    let batch
        (peer: Peer)
        (entries: BatchEntry list)
        : Eff<#IScope, Result<Result<Json, RpcError> list, CallError<NoError>>> =
        fun env -> peer.RequestBatch(entries, env.Scope.Cancellation)

    /// Closes the connection and waits until it has ended.
    let close (peer: Peer) : Eff<'R, CloseReason> = fun _ -> peer.CloseAsync()

    /// Opens a binary channel; send its id to the peer, which accepts it.
    let openChannel (peer: Peer) : Eff<#IScope, Result<Channel, ChannelError>> =
        fun env -> peer.OpenChannel env.Scope.Cancellation

    /// Accepts a channel the peer opened.
    let acceptChannel (peer: Peer) (id: uint32) : Eff<'R, Result<Channel, ChannelError>> =
        fun _ -> Task.FromResult(peer.AcceptChannel id)

    /// Reads the next piece of a channel's data; `None` at its end.
    let readChannel (channel: Channel) : Eff<#IScope, Result<ReadOnlyMemory<byte> option, ChannelError>> =
        fun env -> channel.Read env.Scope.Cancellation

    /// Writes to a channel, waiting for credit.
    let writeChannel (channel: Channel) (data: ReadOnlyMemory<byte>) : Eff<#IScope, Result<unit, ChannelError>> =
        fun env -> channel.Write(data, env.Scope.Cancellation)

    /// Ends a channel's output.
    let closeChannelOutput (channel: Channel) : Eff<#IScope, Result<unit, ChannelError>> =
        fun env -> channel.CloseOutput env.Scope.Cancellation

    /// A channel id in params or results: a JSON integer.
    let channelId: Codec<uint32> =
        Codec.int64
        |> Codec.between 0L (int64 UInt32.MaxValue)
        |> Codec.map uint32 int64
        |> Codec.describe "A binary channel of the connection."

/// Typed entries for `Rpc.batch`.
module Batch =
    let call (m: Method<'P, 'Res, 'E>) (value: 'P) : BatchEntry =
        BatchEntry.Call(m.Name, Params.encode m.Params value)

    let notify (n: Notification<'P>) (value: 'P) : BatchEntry =
        BatchEntry.Notify(n.Name, Params.encode n.Params value)
