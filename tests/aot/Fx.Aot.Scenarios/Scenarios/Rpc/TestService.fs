/// The service every transport is tested against (tests/Fx.Rpc.Tests runs the conformance suite on
/// it): in process, in a child process (this program's `rpc-peer` command, on the JIT and natively)
/// and in the `rpc-*` scenarios. Its methods exercise each part of the protocol; its limits are
/// small, so tests reach them cheaply. It runs as a Native AOT binary, so it follows the AOT rules.
module Fx.Aot.Scenarios.Rpc.TestService

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Rpc

/// The environment of both sides in the tests: a clock and a scope.
type Env =
    {
        Clock: Clock
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"Env({env.Scope})"

type Text = { Text: string }

type Pair = { A: int; B: int }

type Key = { Key: string }

type Count = { Count: int }

type Size = { Size: int }

type ChannelRef = { Channel: uint32 }

type Fill = { Into: uint32; Size: int }

/// The typed errors of `test/divide`.
type MathError =
    | DivideByZero
    | Overflow of limit: int

    override this.ToString() =
        match this with
        | DivideByZero -> "division by zero"
        | Overflow limit -> $"overflow beyond {limit}"

module Codecs =
    let text: Codec<Text> =
        Codec.record {
            let! text = Codec.field "text" (fun (t: Text) -> t.Text) Codec.string
            return { Text = text }
        }

    let pair: Codec<Pair> =
        Codec.record {
            let! a = Codec.field "a" (fun (p: Pair) -> p.A) Codec.int
            and! b = Codec.field "b" (fun p -> p.B) Codec.int
            return { A = a; B = b }
        }

    let key: Codec<Key> =
        Codec.record {
            let! key = Codec.field "key" (fun (k: Key) -> k.Key) Codec.string
            return { Key = key }
        }

    let count: Codec<Count> =
        Codec.record {
            let! count =
                Codec.field "count" (fun (c: Count) -> c.Count) (Codec.int |> Codec.between 0 10000)

            return { Count = count }
        }

    let size: Codec<Size> =
        Codec.record {
            let! size =
                Codec.field "size" (fun (s: Size) -> s.Size) (Codec.int |> Codec.atLeast 0)

            return { Size = size }
        }

    let channel: Codec<ChannelRef> =
        Codec.record {
            let! channel = Codec.field "channel" (fun (c: ChannelRef) -> c.Channel) Rpc.channelId
            return { Channel = channel }
        }

    let fill: Codec<Fill> =
        Codec.record {
            let! into = Codec.field "into" (fun (f: Fill) -> f.Into) Rpc.channelId

            and! size =
                Codec.field "size" (fun (f: Fill) -> f.Size) (Codec.int |> Codec.atLeast 0)

            return { Into = into; Size = size }
        }

    let mathError: Codec<MathError> =
        Codec.union "kind" {
            let! divideByZero = Codec.case0 "divideByZero" DivideByZero
            and! overflow = Codec.case1 "overflow" Overflow (Field.required "limit" Codec.int)

            return
                fun value ->
                    match value with
                    | DivideByZero -> divideByZero
                    | Overflow limit -> overflow limit
        }

/// Requests the service handles.
module Methods =
    let echo = Method.define "test/echo" Codecs.text Codecs.text
    let add = Method.define "test/add" Codecs.pair Codec.int

    let divide =
        Method.define "test/divide" Codecs.pair Codec.int
        |> Method.withErrors Codecs.mathError

    /// Throws: a defect.
    let fail = Method.define "test/fail" Codec.unit Codec.unit
    /// Sends `test/started`, then waits until cancelled; then records the key and sends `test/stopped`.
    let wait = Method.define "test/wait" Codecs.key Codec.unit
    /// Sends `count` `test/tick` notifications, then returns `count`.
    let ticks = Method.define "test/ticks" Codecs.count Codec.int
    /// Calls the client's `client/upper` and returns its result.
    let callback = Method.define "test/callback" Codecs.text Codecs.text
    /// A string of `size` x's.
    let big = Method.define "test/big" Codecs.size Codec.string
    /// The length of the text.
    let length = Method.define "test/length" Codecs.text Codec.int
    /// The `test/note` texts received so far, in order.
    let notes = Method.define "test/notes" Codec.unit (Codec.list Codec.string)
    /// Accepts the channel, writes back everything it reads, closes its output at the end and returns
    /// the number of bytes.
    let echoChannel = Method.define "test/echoChannel" Codecs.channel Codec.int64
    /// Opens a channel, returns it, then writes `size` bytes of `pattern` and closes it.
    let sendChannel = Method.define "test/sendChannel" Codecs.size Codecs.channel
    /// Accepts the channel and holds it without reading.
    let hold = Method.define "test/hold" Codecs.channel Codec.unit
    /// Reads a held channel to its end: the number of bytes, or -1 if it failed.
    let drain = Method.define "test/drain" Codecs.channel Codec.int64
    /// A download: accepts the channel (whose opener only reads), writes `size` bytes of `pattern`,
    /// closes its output and returns the size.
    let fillChannel = Method.define "test/fillChannel" Codecs.fill Codec.int
    /// An upload: accepts the channel, closes its own output at once (it only reads), reads to the end
    /// and returns the number of bytes.
    let sinkChannel = Method.define "test/sinkChannel" Codecs.channel Codec.int64
    /// The channels that count against the limit on the service's side.
    let openChannels = Method.define "test/openChannels" Codec.unit Codec.int
    /// Tries to accept the channel: "accepted" (then resets it), or why not.
    let acceptChannel = Method.define "test/acceptChannel" Codecs.channel Codec.string

    /// An extension method in the product's namespace: returns "pong".
    let ping =
        Method.define (Protocol.ownExtensionMethod "test/ping") Codec.unit Codec.string

/// Notifications in both directions.
module Notifications =
    /// Client to server: recorded for `test/notes`.
    let note = Notification.define "test/note" Codecs.text
    /// Server to client, from `test/ticks`.
    let tick = Notification.define "test/tick" Codecs.count
    /// Server to client: a `test/wait` started.
    let started = Notification.define "test/started" Codecs.key
    /// Server to client: a `test/wait` saw its cancellation.
    let stopped = Notification.define "test/stopped" Codecs.key

/// What the client handles.
module Client =
    let upper = Method.define "client/upper" Codecs.text Codecs.text

/// The service's limits: small, so that tests reach them.
let limits: ConnectionLimits =
    {
        MaxMessageSize = 1024 * 1024
        MaxPendingRequests = 16
        MaxQueuedMessages = 64
        MaxOutboundBytes = 1024 * 1024
        Channels =
            Some
                {
                    InitialWindow = 64 * 1024
                    MaxFramePayload = 16 * 1024
                    MaxChannels = 8
                }
        HandshakeTimeout = TimeSpan.FromSeconds 30.0
        CloseTimeout = TimeSpan.FromSeconds 5.0
    }

let info =
    {
        Name = "test-service"
        Version = "1.0"
    }

/// Byte `i` of the data `test/sendChannel` writes.
let pattern (i: int64) : byte = byte ((i * 7L + 3L) % 251L)

let private patternChunk (offset: int64) (length: int) : byte array =
    Array.init length (fun i -> pattern (offset + int64 i))

/// Writes `size` bytes of `pattern` in pieces of up to `piece` bytes, then closes the output.
let writePattern (channel: Channel) (size: int64) (piece: int) : Task<Result<unit, ChannelError>> =
    task {
        let mutable written = 0L
        let mutable error = None

        while error.IsNone && written < size do
            let n = int (min (int64 piece) (size - written))

            let! outcome =
                channel.Write(ReadOnlyMemory(patternChunk written n), CancellationToken.None)

            match outcome with
            | Ok() -> written <- written + int64 n
            | Error e -> error <- Some e

        match error with
        | Some e -> return Error e
        | None -> return! channel.CloseOutput CancellationToken.None
    }

/// Reads a channel to its end: the bytes, or the error.
let readAll (channel: Channel) : Task<Result<byte array, ChannelError>> =
    task {
        let data = ResizeArray<byte>()
        let mutable outcome = None

        while outcome.IsNone do
            let! next = channel.Read CancellationToken.None

            match next with
            | Ok(Some piece) -> data.AddRange(piece.ToArray())
            | Ok None -> outcome <- Some(Ok(data.ToArray()))
            | Error e -> outcome <- Some(Error e)

        return outcome.Value
    }

let private notifyRaw (peer: Peer) (n: Notification<'P>) (value: 'P) : Task =
    task {
        let! _ = peer.Notify(n.Name, Some(n.Params.Encode value), CancellationToken.None)
        ()
    }

/// A fresh service with its own state, for one connection.
let service () : Service<Env> =
    let gate = obj ()
    let notes = ResizeArray<string>()
    let held = Dictionary<uint32, Channel>()

    let accept (peer: Peer) (id: uint32) =
        match peer.AcceptChannel id with
        | Ok channel -> channel
        | Error e -> invalidOp (string e)

    let handlers =
        [
            Handler.requestReply Methods.echo (fun context value -> Eff.ret (Ok { Value = value; Meta = context.Meta }))

            Handler.request Methods.add (fun _ p -> Eff.ret (Ok(p.A + p.B)))

            Handler.request Methods.divide (fun _ p ->
                if p.B = 0 then
                    Eff.ret (Error DivideByZero)
                elif p.A = Int32.MinValue && p.B = -1 then
                    Eff.ret (Error(Overflow Int32.MaxValue))
                else
                    Eff.ret (Ok(p.A / p.B)))

            Handler.request Methods.fail (fun _ () ->
                fun _ -> raise (InvalidOperationException "secret details that must not leak"))

            Handler.request Methods.wait (fun context key ->
                fun (env: Env) ->
                    task {
                        do! notifyRaw context.Peer Notifications.started key

                        try
                            do! TaskCompletionSource().Task.WaitAsync env.Scope.Cancellation
                            return Ok()
                        with :? OperationCanceledException ->
                            do! notifyRaw context.Peer Notifications.stopped key
                            return raise (OperationCanceledException env.Scope.Cancellation)
                    })

            Handler.request Methods.ticks (fun context count ->
                fun (env: Env) ->
                    task {
                        for n in 0 .. count.Count - 1 do
                            let! _ = Rpc.notify context.Peer Notifications.tick { Count = n } env
                            ()

                        return Ok count.Count
                    })

            Handler.request Methods.callback (fun context value ->
                fun (env: Env) ->
                    task {
                        match! Rpc.call context.Peer Client.upper value env with
                        | Ok upper -> return Ok upper
                        | Error e -> return raise (InvalidOperationException(string e))
                    })

            Handler.request Methods.big (fun _ size -> Eff.ret (Ok(String('x', size.Size))))

            Handler.request Methods.length (fun _ text -> Eff.ret (Ok text.Text.Length))

            Handler.request Methods.notes (fun _ () -> Eff.ret (Ok(lock gate (fun () -> List.ofSeq notes))))

            Handler.notification Notifications.note (fun _ text ->
                lock gate (fun () -> notes.Add text.Text)
                Eff.unit)

            Handler.request Methods.echoChannel (fun context ref ->
                fun (env: Env) ->
                    task {
                        let channel = accept context.Peer ref.Channel
                        let mutable total = 0L
                        let mutable reading = true

                        while reading do
                            let! next = channel.Read env.Scope.Cancellation

                            match next with
                            | Ok(Some piece) ->
                                total <- total + int64 piece.Length
                                let! written = channel.Write(piece, env.Scope.Cancellation)

                                match written with
                                | Ok() -> ()
                                | Error e -> invalidOp (string e)
                            | Ok None -> reading <- false
                            | Error e -> invalidOp (string e)

                        let! _ = channel.CloseOutput env.Scope.Cancellation
                        return Ok total
                    })

            Handler.request Methods.sendChannel (fun context size ->
                fun (env: Env) ->
                    task {
                        match! context.Peer.OpenChannel env.Scope.Cancellation with
                        | Ok channel ->
                            // Writes after answering, so the client learns the id while data flows.
                            Task.Run(fun () -> writePattern channel (int64 size.Size) 10000 :> Task)
                            |> ignore

                            return Ok { Channel = channel.Id }
                        | Error e -> return raise (InvalidOperationException(string e))
                    })

            Handler.request Methods.hold (fun context ref ->
                let channel = accept context.Peer ref.Channel
                lock gate (fun () -> held[ref.Channel] <- channel)
                Eff.ret (Ok()))

            Handler.request Methods.drain (fun _ ref ->
                fun (_: Env) ->
                    task {
                        let channel = lock gate (fun () -> held[ref.Channel])
                        let! data = readAll channel

                        match data with
                        | Ok bytes -> return Ok(int64 bytes.Length)
                        | Error _ -> return Ok -1L
                    })

            Handler.request Methods.fillChannel (fun context fill ->
                fun (_: Env) ->
                    task {
                        let channel = accept context.Peer fill.Into

                        match! writePattern channel (int64 fill.Size) 10000 with
                        | Ok() -> return Ok fill.Size
                        | Error e -> return invalidOp (string e)
                    })

            Handler.request Methods.sinkChannel (fun context ref ->
                fun (env: Env) ->
                    task {
                        let channel = accept context.Peer ref.Channel

                        match! channel.CloseOutput env.Scope.Cancellation with
                        | Error e -> return invalidOp (string e)
                        | Ok() ->
                            match! readAll channel with
                            | Ok bytes -> return Ok(int64 bytes.Length)
                            | Error e -> return invalidOp (string e)
                    })

            Handler.request Methods.openChannels (fun context () -> Eff.ret (Ok context.Peer.OpenChannels))

            Handler.request Methods.acceptChannel (fun context ref ->
                match context.Peer.AcceptChannel ref.Channel with
                | Ok channel ->
                    channel.Reset(ResetCode.Cancelled, "accepted")
                    Eff.ret (Ok "accepted")
                | Error e -> Eff.ret (Ok(string e)))

            Handler.request Methods.ping (fun _ () -> Eff.ret (Ok "pong"))
        ]

    Service.create info handlers |> Service.withLimits limits

/// The client side's handlers: `client/upper` and collectors for the server's notifications.
type ClientState() =
    let gate = obj ()
    let ticks = ResizeArray<int>()
    let waiting = Dictionary<string, TaskCompletionSource<unit>>()

    let signal (name: string) =
        let source =
            lock gate (fun () ->
                match waiting.TryGetValue name with
                | true, source -> source
                | false, _ ->
                    let source =
                        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                    waiting[name] <- source
                    source)

        source

    /// Completes when `count` ticks arrived.
    member _.Ticks(count: int) : Task<int list> =
        task {
            do! (signal $"ticks:{count}").Task
            return lock gate (fun () -> List.ofSeq ticks)
        }

    /// Completes when the server's `test/wait` with this key started.
    member _.Started(key: string) : Task = (signal ("started:" + key)).Task

    /// Completes when the server's `test/wait` with this key saw its cancellation.
    member _.Stopped(key: string) : Task = (signal ("stopped:" + key)).Task

    member _.Handlers: Handler<Env> list =
        [
            Handler.request Client.upper (fun _ text -> Eff.ret (Ok { Text = text.Text.ToUpperInvariant() }))

            Handler.notification Notifications.tick (fun _ tick ->
                let count =
                    lock gate (fun () ->
                        ticks.Add tick.Count
                        ticks.Count)

                (signal $"ticks:{count}").TrySetResult() |> ignore
                Eff.unit)

            Handler.notification Notifications.started (fun _ key ->
                (signal ("started:" + key.Key)).TrySetResult() |> ignore
                Eff.unit)

            Handler.notification Notifications.stopped (fun _ key ->
                (signal ("stopped:" + key.Key)).TrySetResult() |> ignore
                Eff.unit)
        ]

/// The client's limits: smaller messages than the server's, so that responses can exceed them.
let clientLimits: ConnectionLimits =
    { limits with
        MaxMessageSize = 256 * 1024
        Channels =
            Some
                {
                    InitialWindow = 32 * 1024
                    MaxFramePayload = 8 * 1024
                    MaxChannels = 8
                }
    }

let clientInfo =
    {
        Name = "test-client"
        Version = "1.0"
    }

/// The `_meta` key the tests use, in the product's namespace.
let metaKey = Protocol.metaKey "test"
