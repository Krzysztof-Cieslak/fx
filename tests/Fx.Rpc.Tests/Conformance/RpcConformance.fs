/// The protocol conformance suite: a function from a way to reach `TestService` to tests. Every
/// transport runs it (in process, streams over in-memory pipes, a child process over stdio,
/// WebSocket over loopback), so they behave the same: the basis of transport parity (spec §16.4,
/// T108).
module Fx.Rpc.Tests.RpcConformance

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Rpc
open Fx.Aot.Scenarios.Rpc.TestService

/// A fresh connection to a server that runs `TestService.service ()` with `TestService.limits`.
type Link =
    {
        /// The client's end, not yet initialized.
        Transport: Transport
        /// Ends the server abruptly, as a crash does.
        Crash: unit -> Task
        /// Releases the server (and its process) after the test.
        Dispose: unit -> Task
    }

    override _.ToString() = "Link"

/// Fails a test instead of hanging it.
let within (work: Task<'T>) : Task<'T> =
    work.WaitAsync(TimeSpan.FromSeconds 30.0)

let withinTask (work: Task) : Task =
    work.WaitAsync(TimeSpan.FromSeconds 30.0)

type Session =
    {
        Peer: Peer
        State: ClientState
        Env: Env
    }

    override _.ToString() = "Session"

let private using (connect: unit -> Task<Link>) (body: Link -> Task<unit>) : Task<unit> =
    task {
        let! link = within (connect ())

        try
            do! body link
        finally
            (link.Dispose()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
    }

let openSessionWith (clock: Clock) (link: Link) : Task<Session> =
    task {
        let state = ClientState()
        let env = { Clock = clock; Scope = Scope.root }

        let service =
            Service.create clientInfo state.Handlers |> Service.withLimits clientLimits

        let! connected = within (Eff.run env (Rpc.connect service [] link.Transport))

        match connected with
        | Ok peer ->
            return
                {
                    Peer = peer
                    State = state
                    Env = env
                }
        | Error e -> return failtest ("connect failed: " + string e)
    }

let openSession (link: Link) = openSessionWith SystemClock.clock link

let private run (session: Session) (effect: Eff<Env, 'T>) : Task<'T> = within (Eff.run session.Env effect)

let private expectOk (outcome: Result<'T, 'E>) : 'T =
    match outcome with
    | Ok value -> value
    | Error e -> failtest ("expected Ok, got " + string e)

let private rejectedCode (outcome: Result<'T, CallError<'E>>) : int =
    match outcome with
    | Error(CallError.Rejected e) -> e.Code
    | other -> failtest ("expected a rejection, got " + string other)

// ---- Raw frames ---------------------------------------------------------------------------------

let private sendRaw (transport: Transport) (text: string) : Task<unit> =
    task {
        let! sent = transport.Send (Frame.Text text) CancellationToken.None
        Expect.isOk sent "sent"
    }

let private receiveRaw (transport: Transport) : Task<Json> =
    task {
        let! received = within (transport.Receive CancellationToken.None)

        match received with
        | Received.Frame(Frame.Text text) ->
            match Json.parse text with
            | Ok json -> return json
            | Error e -> return failtest ("not JSON: " + string e)
        | other -> return failtest ("expected a text frame, got " + string other)
    }

let private initializeRaw (transport: Transport) : Task<unit> =
    task {
        do!
            sendRaw
                transport
                """{"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersions":["1"],"peer":{"name":"raw","version":"0"},"capabilities":{"maxMessageSize":1048576}}}"""

        let! response = receiveRaw transport
        Expect.equal (Json.tryField "id" response) (Some(Json.String "init")) "initialize answered"
        Expect.isSome (Json.tryField "result" response) "initialized"
    }

let private errorCode (response: Json) : int option =
    response
    |> Json.tryField "error"
    |> Option.bind (Json.tryField "code")
    |> Option.bind (fun code ->
        match code with
        | Json.Number n -> JsonNumber.tryToInt n
        | _ -> None)

let private errorMessage (response: Json) : string =
    match response |> Json.tryField "error" |> Option.bind (Json.tryField "message") with
    | Some(Json.String message) -> message
    | _ -> ""

/// Reads frames until the transport ends or fails; true if it did within the time limit.
let private endsSoon (transport: Transport) : Task<bool> =
    task {
        let mutable ended = false
        let mutable reads = 0

        while not ended && reads < 1000 do
            let! received = within (transport.Receive CancellationToken.None)
            reads <- reads + 1

            match received with
            | Received.End
            | Received.Failed _ -> ended <- true
            | Received.Frame _ -> ()

        return ended
    }

let private bytesOf (size: int) =
    Array.init size (fun i -> byte (i % 253))

/// The tests, each on a fresh link from `connect`.
let tests (name: string) (connect: unit -> Task<Link>) : Test =
    let session (body: Session -> Task<unit>) : Task<unit> =
        using connect (fun link ->
            task {
                let! session = openSession link
                do! body session
                let! _ = within (session.Peer.CloseAsync())
                ()
            })

    testList
        name
        [
            testList
                "handshake"
                [
                    testTask "agrees on the version and exchanges capabilities" {
                        do!
                            session (fun s ->
                                task {
                                    let remote = s.Peer.Remote
                                    Expect.equal remote.ProtocolVersion "1" "version"
                                    Expect.equal remote.Peer info "peer"

                                    Expect.equal
                                        remote.Capabilities.MaxMessageSize
                                        limits.MaxMessageSize
                                        "message size"

                                    Expect.equal remote.Capabilities.Channels limits.Channels "channels"

                                    for m in [ Methods.echo.Name; Methods.ping.Name; Notifications.note.Name ] do
                                        Expect.isTrue (s.Peer.Supports m) m

                                    Expect.isFalse (s.Peer.Supports "test/missing") "unknown"

                                    Expect.equal
                                        remote.Capabilities.Methods
                                        (List.sortWith
                                            (fun a b -> String.CompareOrdinal(a, b))
                                            remote.Capabilities.Methods)
                                        "sorted"

                                    Expect.equal s.Peer.Role Role.Client "role"
                                })
                    }

                    testTask "a request before initialize is refused" {
                        do!
                            using connect (fun link ->
                                task {
                                    do!
                                        sendRaw
                                            link.Transport
                                            """{"jsonrpc":"2.0","id":1,"method":"test/echo","params":{"text":"a"}}"""

                                    let! response = receiveRaw link.Transport
                                    Expect.equal (errorCode response) (Some Protocol.ErrorCode.NotInitialized) "code"
                                    do! initializeRaw link.Transport

                                    do!
                                        sendRaw
                                            link.Transport
                                            """{"jsonrpc":"2.0","id":2,"method":"test/echo","params":{"text":"a"}}"""

                                    let! response = receiveRaw link.Transport
                                    Expect.isSome (Json.tryField "result" response) "works after initialize"
                                })
                    }

                    testTask "an unsupported version is refused with the supported ones" {
                        do!
                            using connect (fun link ->
                                task {
                                    do!
                                        sendRaw
                                            link.Transport
                                            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersions":["0.9"],"peer":{"name":"old","version":"0"},"capabilities":{"maxMessageSize":4096}}}"""

                                    let! response = receiveRaw link.Transport

                                    Expect.equal
                                        (errorCode response)
                                        (Some Protocol.ErrorCode.UnsupportedVersion)
                                        "code"

                                    let supported =
                                        response
                                        |> Json.tryField "error"
                                        |> Option.bind (Json.tryField "data")
                                        |> Option.bind (Json.tryField "supported")

                                    Expect.equal supported (Some(Json.Array [ Json.String "1" ])) "supported"
                                })
                    }

                    testTask "invalid initialize params are explained" {
                        do!
                            using connect (fun link ->
                                task {
                                    do!
                                        sendRaw
                                            link.Transport
                                            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersions":[],"peer":{"name":"x"},"capabilities":{"maxMessageSize":10}}}"""

                                    let! response = receiveRaw link.Transport
                                    Expect.equal (errorCode response) (Some Protocol.ErrorCode.InvalidParams) "code"
                                    let message = errorMessage response
                                    Expect.stringContains message "$.protocolVersions" "versions"
                                    Expect.stringContains message "$.peer.version: missing required field" "version"
                                    Expect.stringContains message "$.capabilities.maxMessageSize" "size"
                                })
                    }
                ]

            testList
                "calls"
                [
                    testTask "typed results" {
                        do!
                            session (fun s ->
                                task {
                                    let! echoed = run s (Rpc.call s.Peer Methods.echo { Text = "héllo 🌍" })
                                    Expect.equal (expectOk echoed) { Text = "héllo 🌍" } "echo"
                                    let! sum = run s (Rpc.call s.Peer Methods.add { A = 2; B = 40 })
                                    Expect.equal (expectOk sum) 42 "a number result"
                                    let! pong = run s (Rpc.call s.Peer Methods.ping ())
                                    Expect.equal (expectOk pong) "pong" "extension method"
                                })
                    }

                    testTask "_meta travels with params and results" {
                        do!
                            session (fun s ->
                                task {
                                    let meta = Map.ofList [ metaKey, Json.Object [ "trace", Json.String "t-1" ] ]

                                    let! reply =
                                        run
                                            s
                                            (Rpc.callWith
                                                { CallOptions.defaults with
                                                    Meta = meta
                                                }
                                                s.Peer
                                                Methods.echo
                                                { Text = "m" })

                                    let reply = expectOk reply
                                    Expect.equal reply.Value { Text = "m" } "value without _meta"
                                    Expect.equal reply.Meta meta "meta echoed"
                                })
                    }

                    testTask "typed errors are expected failures" {
                        do!
                            session (fun s ->
                                task {
                                    let! zero = run s (Rpc.call s.Peer Methods.divide { A = 1; B = 0 })
                                    Expect.equal zero (Error(CallError.Failed DivideByZero)) "divide by zero"

                                    let! overflow =
                                        run s (Rpc.call s.Peer Methods.divide { A = Int32.MinValue; B = -1 })

                                    Expect.equal
                                        overflow
                                        (Error(CallError.Failed(Overflow Int32.MaxValue)))
                                        "with data"

                                    let! fine = run s (Rpc.call s.Peer Methods.divide { A = 9; B = 3 })
                                    Expect.equal fine (Ok 3) "fine"
                                })
                    }

                    testTask "invalid params are answered with issue paths" {
                        do!
                            session (fun s ->
                                task {
                                    let! outcome =
                                        within (
                                            s.Peer.Request(
                                                Methods.add.Name,
                                                Some(Json.Object [ "a", Json.String "x" ]),
                                                None,
                                                CancellationToken.None
                                            )
                                        )

                                    match outcome with
                                    | Error(CallError.Rejected e) ->
                                        Expect.equal e.Code Protocol.ErrorCode.InvalidParams "code"

                                        Expect.equal
                                            (RpcError.issues e |> List.map string)
                                            [
                                                "$.a: expected integer, got string \"x\""
                                                "$.b: missing required field"
                                            ]
                                            "issues"

                                        Expect.stringContains e.Message "$.b: missing required field" "message"
                                    | other -> failtest (string other)
                                })
                    }

                    testTask "unknown methods are not found" {
                        do!
                            session (fun s ->
                                task {
                                    let! outcome =
                                        within (s.Peer.Request("test/missing", None, None, CancellationToken.None))

                                    Expect.equal (rejectedCode outcome) Protocol.ErrorCode.MethodNotFound "code"
                                    // A notification name is not a request.
                                    let! asRequest =
                                        within (
                                            s.Peer.Request(Notifications.note.Name, None, None, CancellationToken.None)
                                        )

                                    Expect.equal (rejectedCode asRequest) Protocol.ErrorCode.MethodNotFound "kind"
                                })
                    }

                    testTask "defects are internal errors without their message" {
                        do!
                            session (fun s ->
                                task {
                                    let! outcome = run s (Rpc.call s.Peer Methods.fail ())

                                    match outcome with
                                    | Error(CallError.Rejected e) ->
                                        Expect.equal e.Code Protocol.ErrorCode.InternalError "code"
                                        Expect.equal e.Message "internal error (InvalidOperationException)" "type only"
                                    | other -> failtest (string other)

                                    let! after = run s (Rpc.call s.Peer Methods.add { A = 1; B = 1 })
                                    Expect.equal after (Ok 2) "still serving"
                                })
                    }

                    testTask "both sides call each other" {
                        do!
                            session (fun s ->
                                task {
                                    let! upper = run s (Rpc.call s.Peer Methods.callback { Text = "hello" })
                                    Expect.equal upper (Ok { Text = "HELLO" }) "callback"
                                })
                    }

                    testTask "concurrent calls are correlated" {
                        do!
                            session (fun s ->
                                task {
                                    let calls =
                                        [
                                            for i in 1..200 ->
                                                if i % 3 = 0 then
                                                    task {
                                                        let! r =
                                                            Eff.run
                                                                s.Env
                                                                (Rpc.call
                                                                    s.Peer
                                                                    Methods.echo
                                                                    { Text = String('e', i * 100) })

                                                        return r |> Result.map (fun t -> t.Text.Length)
                                                    }
                                                else
                                                    Eff.run s.Env (Rpc.call s.Peer Methods.add { A = i; B = i })
                                        ]

                                    let! results = within (Task.WhenAll calls)

                                    for i in 1..200 do
                                        let expected = if i % 3 = 0 then i * 100 else 2 * i
                                        Expect.equal results[i - 1] (Ok expected) $"call {i}"
                                })
                    }

                    testTask "batches answer each call in order" {
                        do!
                            session (fun s ->
                                task {
                                    let! outcome =
                                        run
                                            s
                                            (Rpc.batch
                                                s.Peer
                                                [
                                                    Batch.call Methods.add { A = 1; B = 2 }
                                                    Batch.notify Notifications.note { Text = "in a batch" }
                                                    Batch.call Methods.divide { A = 1; B = 0 }
                                                    BatchEntry.Call("test/missing", None)
                                                ])

                                    match expectOk outcome with
                                    | [ sum; quotient; missing ] ->
                                        Expect.equal (Rpc.decodeOutcome Methods.add sum) (Ok 3) "sum"

                                        Expect.equal
                                            (Rpc.decodeOutcome Methods.divide quotient)
                                            (Error(CallError.Failed DivideByZero))
                                            "typed error"

                                        Expect.equal
                                            (missing |> Result.mapError (fun e -> e.Code))
                                            (Error Protocol.ErrorCode.MethodNotFound)
                                            "missing"
                                    | other -> failtest $"{other.Length} results"

                                    let! notes = run s (Rpc.call s.Peer Methods.notes ())
                                    Expect.equal notes (Ok [ "in a batch" ]) "the notification was handled"
                                })
                    }
                ]

            testList
                "ordering"
                [
                    testTask "notifications are handled in order, before later requests" {
                        do!
                            session (fun s ->
                                task {
                                    for i in 1..300 do
                                        let! sent = run s (Rpc.notify s.Peer Notifications.note { Text = string i })
                                        expectOk sent

                                    let! notes = run s (Rpc.call s.Peer Methods.notes ())
                                    Expect.equal notes (Ok [ for i in 1..300 -> string i ]) "in order"
                                })
                    }

                    testTask "the server's notifications arrive in order" {
                        do!
                            session (fun s ->
                                task {
                                    let! count = run s (Rpc.call s.Peer Methods.ticks { Count = 200 })
                                    Expect.equal count (Ok 200) "count"
                                    let! ticks = within (s.State.Ticks 200)
                                    Expect.equal ticks [ 0..199 ] "in order"
                                })
                    }
                ]

            testList
                "cancellation"
                [
                    testTask "cancelling a call cancels its handler" {
                        do!
                            session (fun s ->
                                task {
                                    use cancel = new CancellationTokenSource()

                                    let env =
                                        { s.Env with
                                            Scope = Scope.ofCancellation cancel.Token
                                        }

                                    let call = Eff.run env (Rpc.call s.Peer Methods.wait { Key = "c1" })
                                    do! withinTask (s.State.Started "c1")
                                    cancel.Cancel()

                                    let! thrown =
                                        task {
                                            try
                                                let! _ = within call
                                                return false
                                            with :? OperationCanceledException ->
                                                return true
                                        }

                                    Expect.isTrue thrown "the call throws OperationCanceledException"
                                    do! withinTask (s.State.Stopped "c1")
                                    let! after = run s (Rpc.call s.Peer Methods.add { A = 1; B = 2 })
                                    Expect.equal after (Ok 3) "still serving"
                                })
                    }

                    testTask "a timeout on the clock cancels the request" {
                        do!
                            using connect (fun link ->
                                task {
                                    let clock = FakeClock.Create()
                                    let! s = openSessionWith clock.Clock link

                                    let call =
                                        Eff.run
                                            s.Env
                                            (Rpc.callWith
                                                (CallOptions.timeout (TimeSpan.FromSeconds 5.0))
                                                s.Peer
                                                Methods.wait
                                                { Key = "t1" })

                                    do! withinTask (s.State.Started "t1")
                                    clock.Advance(TimeSpan.FromSeconds 5.0)
                                    let! outcome = within call

                                    Expect.equal
                                        outcome
                                        (Error(CallError.TimedOut(TimeSpan.FromSeconds 5.0)))
                                        "timed out"

                                    do! withinTask (s.State.Stopped "t1")
                                    let! _ = within (s.Peer.CloseAsync())
                                    ()
                                })
                    }
                ]

            testList
                "limits"
                [
                    testTask "calls beyond the peer's pending limit wait for a slot" {
                        do!
                            session (fun s ->
                                task {
                                    Expect.equal
                                        s.Peer.Remote.Capabilities.MaxPendingRequests
                                        (Some limits.MaxPendingRequests)
                                        "announced"

                                    use cancel = new CancellationTokenSource()

                                    let env =
                                        { s.Env with
                                            Scope = Scope.ofCancellation cancel.Token
                                        }

                                    let waits =
                                        [
                                            for i in 1 .. limits.MaxPendingRequests ->
                                                Eff.run env (Rpc.call s.Peer Methods.wait { Key = $"o{i}" })
                                        ]

                                    for i in 1 .. limits.MaxPendingRequests do
                                        do! withinTask (s.State.Started $"o{i}")

                                    let extra = Eff.run s.Env (Rpc.call s.Peer Methods.add { A = 1; B = 1 })
                                    do! Task.Delay 100
                                    Expect.isFalse extra.IsCompleted "waits for a slot"
                                    cancel.Cancel()

                                    for wait in waits do
                                        try
                                            let! _ = within wait
                                            ()
                                        with :? OperationCanceledException ->
                                            ()

                                    let! added = within extra
                                    Expect.equal added (Ok 2) "sent once a slot was free"

                                    for i in 1 .. limits.MaxPendingRequests do
                                        do! withinTask (s.State.Stopped $"o{i}")
                                })
                    }

                    testTask "requests beyond the pending limit are answered Overloaded" {
                        do!
                            using connect (fun link ->
                                task {
                                    let t = link.Transport
                                    do! initializeRaw t

                                    for i in 1 .. limits.MaxPendingRequests + 2 do
                                        do!
                                            sendRaw
                                                t
                                                ("{\"jsonrpc\":\"2.0\",\"id\":"
                                                 + string i
                                                 + ",\"method\":\"test/wait\",\"params\":{\"key\":\"r"
                                                 + string i
                                                 + "\"}}")

                                    // Read until both extra requests are answered; the others only start.
                                    let mutable overloaded = []

                                    while overloaded.Length < 2 do
                                        let! frame = receiveRaw t

                                        match Json.tryField "id" frame with
                                        | Some id when errorCode frame = Some Protocol.ErrorCode.Overloaded ->
                                            overloaded <- id :: overloaded
                                        | Some _ -> failtest ("unexpected answer: " + Json.serialize frame)
                                        | None -> ()

                                    Expect.equal
                                        (List.sortBy Json.serialize overloaded)
                                        [
                                            Json.int (limits.MaxPendingRequests + 1)
                                            Json.int (limits.MaxPendingRequests + 2)
                                        ]
                                        "the extra requests"

                                    for i in 1 .. limits.MaxPendingRequests do
                                        do!
                                            sendRaw
                                                t
                                                ("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":"
                                                 + string i
                                                 + "}}")

                                    let mutable cancelled = 0

                                    while cancelled < limits.MaxPendingRequests do
                                        let! frame = receiveRaw t

                                        if errorCode frame = Some Protocol.ErrorCode.RequestCancelled then
                                            cancelled <- cancelled + 1

                                    do!
                                        sendRaw
                                            t
                                            """{"jsonrpc":"2.0","id":"after","method":"test/add","params":{"a":1,"b":1}}"""

                                    let mutable answered = None

                                    while answered.IsNone do
                                        let! frame = receiveRaw t

                                        if Json.tryField "id" frame = Some(Json.String "after") then
                                            answered <- Json.tryField "result" frame

                                    Expect.equal answered (Some(Json.int 2)) "serving again"
                                })
                    }

                    testTask "large messages up to the limits" {
                        do!
                            session (fun s ->
                                task {
                                    let large = String('a', 900 * 1024)
                                    let! length = run s (Rpc.call s.Peer Methods.length { Text = large })
                                    Expect.equal length (Ok large.Length) "900 KiB request"
                                    let medium = String('m', 200 * 1024)
                                    let! echoed = run s (Rpc.call s.Peer Methods.echo { Text = medium })
                                    Expect.equal echoed (Ok { Text = medium }) "200 KiB both ways"

                                    let! big = run s (Rpc.call s.Peer Methods.big { Size = 200 * 1024 })
                                    Expect.equal (big |> Result.map String.length) (Ok(200 * 1024)) "200 KiB response"
                                })
                    }

                    testTask "a request over the peer's limit is not sent" {
                        do!
                            session (fun s ->
                                task {
                                    let! outcome =
                                        run s (Rpc.call s.Peer Methods.echo { Text = String('a', 2 * 1024 * 1024) })

                                    match outcome with
                                    | Error(CallError.TooLarge(size, limit)) ->
                                        Expect.equal limit limits.MaxMessageSize "the server's limit"
                                        Expect.isGreaterThan size limit "size"
                                    | other -> failtest (string other)

                                    let! after = run s (Rpc.call s.Peer Methods.add { A = 1; B = 1 })
                                    Expect.equal after (Ok 2) "still connected"
                                })
                    }

                    testTask "a response over the client's limit becomes an error" {
                        do!
                            session (fun s ->
                                task {
                                    let! outcome = run s (Rpc.call s.Peer Methods.big { Size = 300 * 1024 })
                                    Expect.equal (rejectedCode outcome) Protocol.ErrorCode.ResponseTooLarge "too large"
                                    let! after = run s (Rpc.call s.Peer Methods.add { A = 1; B = 1 })
                                    Expect.equal after (Ok 2) "still connected"
                                })
                    }
                ]

            testList
                "channels"
                [
                    testTask "data flows both ways in order" {
                        do!
                            session (fun s ->
                                task {
                                    let! channel = run s (Rpc.openChannel s.Peer)
                                    let channel = expectOk channel

                                    let echo =
                                        Eff.run s.Env (Rpc.call s.Peer Methods.echoChannel { Channel = channel.Id })

                                    let data = bytesOf (1024 * 1024 + 17)
                                    let reading = readAll channel

                                    let mutable offset = 0

                                    while offset < data.Length do
                                        let n = min 50000 (data.Length - offset)

                                        let! written =
                                            run s (Rpc.writeChannel channel (ReadOnlyMemory(data, offset, n)))

                                        expectOk written
                                        offset <- offset + n

                                    let! closed = run s (Rpc.closeChannelOutput channel)
                                    expectOk closed
                                    let! back = within reading
                                    Expect.equal (expectOk back) data "echoed in order"
                                    let! total = within echo
                                    Expect.equal total (Ok(int64 data.Length)) "server count"
                                })
                    }

                    testTask "the server opens channels" {
                        do!
                            session (fun s ->
                                task {
                                    let size = 300 * 1024
                                    let! opened = run s (Rpc.call s.Peer Methods.sendChannel { Size = size })
                                    let id = (expectOk opened).Channel
                                    Expect.equal (id % 2u) 0u "server ids are even"
                                    let! channel = run s (Rpc.acceptChannel s.Peer id)
                                    let! data = within (readAll (expectOk channel))
                                    let data = expectOk data
                                    Expect.equal data.Length size "size"

                                    Expect.isTrue
                                        (data
                                         |> Array.mapi (fun i b -> b = pattern (int64 i))
                                         |> Array.forall (fun ok -> ok))
                                        "pattern"

                                    Expect.isError (s.Peer.AcceptChannel id) "accepted once"
                                })
                    }

                    testTask "a slow reader stalls only its channel" {
                        do!
                            session (fun s ->
                                task {
                                    let! channel = run s (Rpc.openChannel s.Peer)
                                    let channel = expectOk channel
                                    let! held = run s (Rpc.call s.Peer Methods.hold { Channel = channel.Id })
                                    expectOk held
                                    let window = limits.Channels.Value.InitialWindow
                                    let data = bytesOf (4 * window)
                                    let writing = Eff.run s.Env (Rpc.writeChannel channel (ReadOnlyMemory data))

                                    // The server reads nothing: the writer stops at the window, while the
                                    // connection keeps serving.
                                    for i in 1..20 do
                                        let! sum = run s (Rpc.call s.Peer Methods.add { A = i; B = 1 })
                                        Expect.equal sum (Ok(i + 1)) "calls still work"

                                    Expect.isFalse writing.IsCompleted "the writer waits for credit"

                                    let drained =
                                        Eff.run s.Env (Rpc.call s.Peer Methods.drain { Channel = channel.Id })

                                    let! written = within writing
                                    expectOk written
                                    let! closed = run s (Rpc.closeChannelOutput channel)
                                    expectOk closed
                                    let! total = within drained
                                    Expect.equal total (Ok(int64 data.Length)) "everything arrived"
                                })
                    }

                    testTask "a reset fails the other side" {
                        do!
                            session (fun s ->
                                task {
                                    let! channel = run s (Rpc.openChannel s.Peer)
                                    let channel = expectOk channel
                                    let! held = run s (Rpc.call s.Peer Methods.hold { Channel = channel.Id })
                                    expectOk held
                                    let! written = run s (Rpc.writeChannel channel (ReadOnlyMemory(bytesOf 1000)))
                                    expectOk written
                                    channel.Reset(ResetCode.Cancelled, "changed my mind")
                                    let! total = run s (Rpc.call s.Peer Methods.drain { Channel = channel.Id })
                                    Expect.equal total (Ok -1L) "the server's read fails"
                                    let! again = run s (Rpc.writeChannel channel (ReadOnlyMemory(bytesOf 10)))

                                    Expect.equal
                                        again
                                        (Error(ChannelError.ResetLocally(ResetCode.Cancelled, "changed my mind")))
                                        "local writes fail"
                                })
                    }

                    testTask "channels are limited and multiplexed" {
                        do!
                            session (fun s ->
                                task {
                                    let limit =
                                        min limits.Channels.Value.MaxChannels clientLimits.Channels.Value.MaxChannels

                                    let! opened =
                                        within (
                                            Task.WhenAll
                                                [ for _ in 1..limit -> Eff.run s.Env (Rpc.openChannel s.Peer) ]
                                        )

                                    let channels = opened |> Array.map expectOk
                                    let! extra = run s (Rpc.openChannel s.Peer)
                                    Expect.equal extra (Error(ChannelError.TooMany limit)) "beyond the limit"

                                    let exchange (index: int) (channel: Channel) =
                                        task {
                                            let echo =
                                                Eff.run
                                                    s.Env
                                                    (Rpc.call s.Peer Methods.echoChannel { Channel = channel.Id })

                                            let data = Array.init (40000 + index) (fun i -> byte ((i + index) % 256))
                                            let reading = readAll channel
                                            let! written = channel.Write(ReadOnlyMemory data, CancellationToken.None)
                                            expectOk written
                                            let! closed = channel.CloseOutput CancellationToken.None
                                            expectOk closed
                                            let! back = within reading
                                            Expect.equal (expectOk back) data $"channel {index}"
                                            let! _ = within echo
                                            ()
                                        }

                                    do! withinTask (Task.WhenAll(channels |> Array.mapi exchange))
                                })
                    }
                ]

            testList
                "malformed input"
                [
                    testTask "is answered as JSON-RPC 2.0 says" {
                        do!
                            using connect (fun link ->
                                task {
                                    let t = link.Transport
                                    do! initializeRaw t

                                    do! sendRaw t "{not json"
                                    let! parse = receiveRaw t
                                    Expect.equal (errorCode parse) (Some Protocol.ErrorCode.ParseError) "parse error"
                                    Expect.equal (Json.tryField "id" parse) (Some Json.Null) "null id"

                                    do! sendRaw t """{"jsonrpc":"2.0","id":5,"method":7}"""
                                    let! invalid = receiveRaw t

                                    Expect.equal
                                        (errorCode invalid)
                                        (Some Protocol.ErrorCode.InvalidRequest)
                                        "invalid request"

                                    Expect.equal (Json.tryField "id" invalid) (Some(Json.int 5)) "its id"

                                    Expect.stringContains
                                        (errorMessage invalid)
                                        "$.method: expected a string, got number 7"
                                        "explained"

                                    do! sendRaw t """{"jsonrpc":"1.0","id":"v","method":"test/add"}"""
                                    let! version = receiveRaw t
                                    Expect.equal (errorCode version) (Some Protocol.ErrorCode.InvalidRequest) "version"

                                    do! sendRaw t "[]"
                                    let! empty = receiveRaw t

                                    Expect.equal
                                        (errorCode empty)
                                        (Some Protocol.ErrorCode.InvalidRequest)
                                        "empty batch"

                                    do! sendRaw t """{"jsonrpc":"2.0","id":6,"method":"test/add","params":[1,2]}"""
                                    let! positional = receiveRaw t

                                    Expect.equal
                                        (errorCode positional)
                                        (Some Protocol.ErrorCode.InvalidParams)
                                        "by position"

                                    do!
                                        sendRaw
                                            t
                                            """{"jsonrpc":"2.0","id":7,"method":"test/echo","params":{"text":"a","_meta":5}}"""

                                    let! meta = receiveRaw t

                                    Expect.equal
                                        (errorCode meta)
                                        (Some Protocol.ErrorCode.InvalidParams)
                                        "_meta not an object"

                                    Expect.stringContains (errorMessage meta) "$._meta: expected an object" "explained"

                                    // Answers in entry order; notifications get none; a batch needs no valid entry first.
                                    do!
                                        sendRaw
                                            t
                                            """[{"jsonrpc":"2.0","id":1,"method":"test/add","params":{"a":1,"b":2}},{"jsonrpc":"2.0","method":"test/note","params":{"text":"n"}},{"foo":1},{"jsonrpc":"2.0","id":2,"method":"test/add","params":{"a":1}}]"""

                                    let! batch = receiveRaw t

                                    match batch with
                                    | Json.Array [ first; second; third ] ->
                                        Expect.equal (Json.tryField "result" first) (Some(Json.int 3)) "first"
                                        Expect.equal (Json.tryField "id" second) (Some Json.Null) "invalid entry"

                                        Expect.equal
                                            (errorCode second)
                                            (Some Protocol.ErrorCode.InvalidRequest)
                                            "invalid entry code"

                                        Expect.equal (Json.tryField "id" third) (Some(Json.int 2)) "third"

                                        Expect.equal
                                            (errorCode third)
                                            (Some Protocol.ErrorCode.InvalidParams)
                                            "third code"
                                    | other -> failtest ("expected 3 answers: " + string other)

                                    do!
                                        sendRaw
                                            t
                                            """[{"jsonrpc":"2.0","method":"test/note","params":{"text":"only"}}]"""

                                    do! sendRaw t """{"jsonrpc":"2.0","id":"after","method":"test/notes"}"""
                                    let! after = receiveRaw t

                                    Expect.equal
                                        (Json.tryField "id" after)
                                        (Some(Json.String "after"))
                                        "no answer to notifications"

                                    Expect.equal
                                        (Json.tryField "result" after)
                                        (Some(Json.Array [ Json.String "n"; Json.String "only" ]))
                                        "notes"

                                    do! sendRaw t """{"jsonrpc":"2.0","id":8,"result":1}"""
                                    do! sendRaw t """{"jsonrpc":"2.0","id":9,"method":"initialize","params":{}}"""
                                    let! twice = receiveRaw t

                                    Expect.equal
                                        (Json.tryField "id" twice)
                                        (Some(Json.int 9))
                                        "stray responses are dropped"

                                    Expect.equal
                                        (errorCode twice)
                                        (Some Protocol.ErrorCode.InvalidRequest)
                                        "initialized once"
                                })
                    }

                    testTask "a frame over the limit ends the connection" {
                        do!
                            using connect (fun link ->
                                task {
                                    do! initializeRaw link.Transport

                                    let text =
                                        """{"jsonrpc":"2.0","method":"test/note","params":{"text":"""
                                        + "\""
                                        + String('x', limits.MaxMessageSize)
                                        + "\"}}"

                                    let! _ = link.Transport.Send (Frame.Text text) CancellationToken.None
                                    let! ended = endsSoon link.Transport
                                    Expect.isTrue ended "the server closed the connection"
                                })
                    }
                ]

            testList
                "lifetime"
                [
                    testTask "a crashed peer fails pending calls and channels" {
                        do!
                            using connect (fun link ->
                                task {
                                    let! s = openSession link
                                    let! channel = run s (Rpc.openChannel s.Peer)
                                    let channel = expectOk channel
                                    let pendingCall = Eff.run s.Env (Rpc.call s.Peer Methods.wait { Key = "crash" })
                                    do! withinTask (s.State.Started "crash")
                                    do! withinTask (link.Crash())
                                    let! outcome = within pendingCall

                                    match outcome with
                                    | Error(CallError.Disconnected _) -> ()
                                    | other -> failtest ("expected Disconnected: " + string other)

                                    let! reason = within s.Peer.Completion

                                    match reason with
                                    | CloseReason.Peer
                                    | CloseReason.TransportFailed _ -> ()
                                    | other -> failtest ("unexpected reason: " + string other)

                                    let! read = within (channel.Read CancellationToken.None)

                                    match read with
                                    | Error(ChannelError.ConnectionClosed _) -> ()
                                    | other -> failtest ("expected ConnectionClosed: " + string other)

                                    let! later = run s (Rpc.call s.Peer Methods.add { A = 1; B = 1 })

                                    match later with
                                    | Error(CallError.Disconnected _) -> ()
                                    | other -> failtest ("expected Disconnected: " + string other)
                                })
                    }

                    testTask "closing ends the connection" {
                        do!
                            using connect (fun link ->
                                task {
                                    let! s = openSession link
                                    let! reason = within (s.Peer.CloseAsync())
                                    Expect.equal reason CloseReason.Local "local"
                                    let! again = within (s.Peer.CloseAsync())
                                    Expect.equal again CloseReason.Local "idempotent"
                                    let! later = run s (Rpc.call s.Peer Methods.add { A = 1; B = 1 })

                                    Expect.equal
                                        later
                                        (Error(CallError.Disconnected CloseReason.Local))
                                        "no calls after"

                                    let! note = run s (Rpc.notify s.Peer Notifications.note { Text = "x" })

                                    Expect.equal
                                        note
                                        (Error(SendError.Disconnected CloseReason.Local))
                                        "no notifications after"
                                })
                    }

                    testTask "a peer that closes its output still gets its answers" {
                        do!
                            using connect (fun link ->
                                task {
                                    let t = link.Transport
                                    do! initializeRaw t

                                    do!
                                        sendRaw
                                            t
                                            """{"jsonrpc":"2.0","id":1,"method":"test/add","params":{"a":20,"b":22}}"""

                                    do! t.CloseOutput Shutdown.Normal CancellationToken.None
                                    let! response = receiveRaw t
                                    Expect.equal (Json.tryField "result" response) (Some(Json.int 42)) "answered"
                                    let! ended = endsSoon t
                                    Expect.isTrue ended "then the server ends"
                                })
                    }
                ]
        ]
