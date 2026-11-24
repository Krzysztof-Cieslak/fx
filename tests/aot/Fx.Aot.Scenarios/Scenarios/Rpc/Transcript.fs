/// A fixed conversation with `TestService` that prints what happened, the same over every transport:
/// handshake, typed calls and errors, `_meta`, ordering, batches, cancellation, channels, limits and
/// the end of the connection.
module Fx.Aot.Scenarios.Rpc.Transcript

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Rpc
open Fx.Aot.Scenarios
open Fx.Aot.Scenarios.Rpc.TestService

/// Fails the scenario instead of hanging.
let within (work: Task<'T>) : Task<'T> =
    work.WaitAsync(TimeSpan.FromSeconds 30.0)

let withinTask (work: Task) : Task =
    work.WaitAsync(TimeSpan.FromSeconds 30.0)

let private show (outcome: Result<'T, CallError<'E>>) (value: 'T -> string) : string =
    match outcome with
    | Ok v -> "ok " + value v
    | Error(CallError.Failed e) -> "failed " + string e
    | Error(CallError.Rejected e) -> $"rejected {e.Code} {Show.escape e.Message}"
    | Error other -> "error " + Show.escape (string other)

let private digest (bytes: byte array) =
    Convert.ToHexString(SHA256.HashData bytes).Substring(0, 16).ToLowerInvariant()

/// How the conversation ends.
type Ending =
    /// The client closes the connection.
    | Close
    /// The server crashes while a call is pending.
    | Crash of (unit -> Task)

    override this.ToString() =
        match this with
        | Close -> "close"
        | Crash _ -> "crash"

/// Runs the conversation over the client's end of a fresh link to `TestService`.
let run (transport: Transport) (ending: Ending) : Task<string list> =
    task {
        let lines = ResizeArray<string>()
        let say (line: string) = lines.Add line
        let state = ClientState()

        let env =
            {
                Clock = SystemClock.clock
                Scope = Scope.root
            }

        let client =
            Service.create clientInfo state.Handlers |> Service.withLimits clientLimits

        let! connected = within (Eff.run env (Rpc.connect client [] transport))

        let peer =
            match connected with
            | Ok peer -> peer
            | Error e -> failwith ("connect failed: " + string e)

        let call m value =
            within (Eff.run env (Rpc.call peer m value))

        let remote = peer.Remote
        let channels = remote.Capabilities.Channels.Value

        say
            $"connected: version {remote.ProtocolVersion}, {remote.Peer.Name} {remote.Peer.Version}, {remote.Capabilities.Methods.Length} methods, channels {channels.InitialWindow}/{channels.MaxFramePayload}/{channels.MaxChannels}"

        let! echo = call Methods.echo { Text = "héllo 🌍" }
        say ("echo: " + show echo (fun t -> Show.escape t.Text))
        let! sum = call Methods.add { A = 40; B = 2 }
        say ("add: " + show sum string)
        let! zero = call Methods.divide { A = 1; B = 0 }
        say ("divide by zero: " + show zero string)
        let! overflow = call Methods.divide { A = Int32.MinValue; B = -1 }
        say ("overflow: " + show overflow string)

        let! invalid =
            within (
                peer.Request(Methods.add.Name, Some(Json.Object [ "a", Json.String "x" ]), None, CancellationToken.None)
            )

        say ("invalid params: " + show invalid Json.serialize)

        let! missing =
            within (peer.Request("test/missing", None, None, CancellationToken.None))

        say ("missing: " + show missing Json.serialize)
        let! failed = call Methods.fail ()
        say ("defect: " + show failed string)
        let! pong = call Methods.ping ()
        say ("extension: " + show pong id)

        let meta = Map.ofList [ metaKey, Json.Object [ "n", Json.int 1 ] ]

        let! reply =
            within (
                Eff.run
                    env
                    (Rpc.callWith
                        { CallOptions.defaults with
                            Meta = meta
                        }
                        peer
                        Methods.echo
                        { Text = "m" })
            )

        say ("meta: " + show reply (fun r -> Json.serialize (Codec.encode Meta.codec r.Meta)))

        let! upper = call Methods.callback { Text = "hello" }
        say ("callback: " + show upper (fun t -> t.Text))

        for i in 1..50 do
            let! _ =
                within (Eff.run env (Rpc.notify peer Notifications.note { Text = string i }))

            ()

        let! notes = call Methods.notes ()

        say (
            "notes in order: "
            + show notes (fun n -> string (n = [ for i in 1..50 -> string i ]))
        )

        let! _ = call Methods.ticks { Count = 50 }
        let! ticks = within (state.Ticks 50)
        say ("ticks in order: " + string (ticks = [ 0..49 ]))

        let! batch =
            within (
                Eff.run
                    env
                    (Rpc.batch
                        peer
                        [
                            Batch.call Methods.add { A = 1; B = 2 }
                            Batch.notify Notifications.note { Text = "batched" }
                            Batch.call Methods.divide { A = 1; B = 0 }
                            BatchEntry.Call("test/missing", None)
                        ])
            )

        match batch with
        | Ok results ->
            let shown =
                results
                |> List.map (fun r ->
                    match r with
                    | Ok json -> "ok " + Json.serialize json
                    | Error e -> $"error {e.Code}")

            say ("batch: " + String.Join(" | ", shown))
        | Error e -> say ("batch: " + string e)

        let calls =
            [ for i in 1..100 -> Eff.run env (Rpc.call peer Methods.add { A = i; B = i }) ]

        let! results = within (Task.WhenAll calls)

        let correct =
            results |> Array.mapi (fun i r -> r = Ok(2 * (i + 1))) |> Array.forall id

        say ("100 concurrent calls correlated: " + string correct)

        use cancel = new CancellationTokenSource()

        let waiting =
            Eff.run
                { env with
                    Scope = Scope.ofCancellation cancel.Token
                }
                (Rpc.call peer Methods.wait { Key = "w" })

        do! withinTask (state.Started "w")
        cancel.Cancel()

        let! cancelled =
            task {
                try
                    let! _ = within waiting
                    return "completed"
                with :? OperationCanceledException ->
                    return "cancelled"
            }

        do! withinTask (state.Stopped "w")
        say ("cancel: " + cancelled + ", the handler saw it")

        let! opened = within (Eff.run env (Rpc.openChannel peer))

        match opened with
        | Ok channel ->
            let echoing =
                Eff.run env (Rpc.call peer Methods.echoChannel { Channel = channel.Id })

            let data = Array.init 200000 (fun i -> byte (i % 241))
            let reading = readAll channel
            let! written = within (channel.Write(ReadOnlyMemory data, CancellationToken.None))
            let! closed = within (channel.CloseOutput CancellationToken.None)
            let! back = within reading
            let! total = within echoing

            match written, closed, back with
            | Ok(), Ok(), Ok bytes ->
                say
                    $"channel echo: {bytes.Length} bytes, same: {bytes = data}, digest {digest bytes}, server counted {show total string}"
            | _ -> say "channel echo failed"
        | Error e -> say ("open channel: " + string e)

        let! sent = call Methods.sendChannel { Size = 150000 }

        match sent with
        | Ok ref ->
            match peer.AcceptChannel ref.Channel with
            | Ok channel ->
                let! data = within (readAll channel)

                match data with
                | Ok bytes ->
                    let ok = bytes |> Array.mapi (fun i b -> b = pattern (int64 i)) |> Array.forall id
                    say $"server channel {ref.Channel % 2u = 0u}: {bytes.Length} bytes, pattern {ok}"
                | Error e -> say ("server channel: " + string e)
            | Error e -> say ("accept: " + string e)
        | Error e -> say ("send channel: " + string e)

        let! tooLarge = call Methods.length { Text = String('a', 2 * 1024 * 1024) }
        say ("request over the limit: " + show tooLarge string)
        let! largeReply = call Methods.big { Size = 300 * 1024 }
        say ("response over the limit: " + show largeReply (fun s -> string s.Length))

        match ending with
        | Close ->
            let! reason = within (peer.CloseAsync())
            say ("close: " + string reason)
            let! after = call Methods.add { A = 1; B = 1 }
            say ("after close: " + show after string)
        | Crash crash ->
            let pending = Eff.run env (Rpc.call peer Methods.wait { Key = "crash" })
            do! withinTask (state.Started "crash")
            do! withinTask (crash ())
            let! outcome = within pending

            let shown =
                match outcome with
                | Error(CallError.Disconnected _) -> "disconnected"
                | other -> string other

            say ("crash: pending call " + shown)
            let! _ = within peer.Completion
            say "crash: connection ended"

        return List.ofSeq lines
    }

/// Serves `TestService` on a transport in this process, in the background.
let serve (transport: Transport) : unit =
    let env =
        {
            Clock = SystemClock.clock
            Scope = Scope.root
        }

    Eff.run env (Rpc.accept (service ()) transport) |> ignore
