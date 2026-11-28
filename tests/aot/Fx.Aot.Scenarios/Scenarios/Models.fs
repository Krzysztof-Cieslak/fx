/// The canonical model (T118, docs/modules/models.md): its codecs, the fold of events into
/// responses, the event-stream parsers under cuts, cassette replay through the canonical wire and
/// ModelAccess (egress hooks included), and the live HTTP client streaming from a loopback server.
module Fx.Aot.Scenarios.Models

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Aot.Scenarios.Show
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Ext.ModelCanonical
open Fx.Models
open Fx.Models.Tests
open Fx.Sdk

let private utf8 = UTF8Encoding(false)

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromMinutes 1.0).GetAwaiter().GetResult()

let private show (result: Result<'T, 'E>) : string =
    match result with
    | Ok value -> $"ok {value}"
    | Error e -> $"error {e}"

/// Encodes, prints the canonical JSON, decodes and checks the round trip.
let private roundTrip (name: string) (codec: Codec<'T>) (value: 'T) : string list =
    let text = Json.serializeCanonical (codec.Encode value)

    // Decode the writer's text: canonical text sorts the members of JSON values (tool schemas).
    let back =
        match Codec.decodeText codec (Codec.encodeText codec value) with
        | Ok decoded -> if decoded = value then "same" else "different"
        | Error issues -> "issues: " + Issue.renderAtMost 3 issues

    [ $"{name} {escape text}"; $"{name} round trip: {back}" ]

let codecs =
    Scenario.lines "models-codecs" (fun () ->
        [
            yield! roundTrip "request" ChatRequest.codec Samples.request
            yield! roundTrip "response" ChatResponse.codec Samples.response
            yield! roundTrip "model" ModelInfo.codec Samples.info
            yield! roundTrip "provider" ProviderDef.codec Samples.provider

            for event in Samples.events do
                yield! roundTrip "event" ChatEvent.codec event

            for error in Samples.errors do
                yield! roundTrip "error" ChatError.codec error

            for stop in Samples.stops do
                yield! roundTrip "stop" StopReason.codec stop

            let preview = ChatRequest.codec.Preview Limits.defaults Samples.request
            $"preview {escape preview.Text} (truncated {preview.Truncated})"
            let schema = Json.serializeCanonical (Schema.toJsonSchema ChatEvent.codec.Schema)
            $"event schema {schema.Length} chars, {escape (schema.Substring(0, 120))}"

            match Codec.decodeText ChatEvent.codec """{"kind":"delta","index":-1}""" with
            | Error issues -> "issues: " + Issue.renderAtMost 5 issues
            | Ok e -> $"decoded {e}"
        ])

let fold =
    Scenario.lines "models-fold" (fun () ->
        [
            $"result {show (ChatAggregate.result (ChatAggregate.ofEvents Samples.events))}"
            $"same as the sample: {ChatAggregate.result (ChatAggregate.ofEvents Samples.events) = Ok Samples.response}"

            for part in Samples.response.Parts do
                $"part {part}"

            $"check: {ChatEvents.check Samples.events |> List.length} broken rules"
            let replayed = ChatEvents.ofResponse Samples.response
            $"canonical stream {replayed.Length} events, folds back: {ChatAggregate.result (ChatAggregate.ofEvents replayed) = Ok Samples.response}"

            let broken =
                [
                    ChatEvent.PartStarted(1, PartStart.Text)
                    ChatEvent.Delta(0, "x")
                    ChatEvent.UsageUpdated { Usage.zero with Output = 3 }
                    ChatEvent.UsageUpdated Usage.zero
                    ChatEvent.Finished StopReason.ToolUse
                    ChatEvent.PartEnded 1
                ]

            for violation in ChatEvents.check broken do
                $"violation {violation}"

            $"broken result {show (ChatAggregate.result (ChatAggregate.ofEvents broken))}"
            $"coalesced {ChatEvents.coalesce Samples.events |> List.length} events"
            $"cost {Usage.cost Samples.info.Cost.Value Samples.usage}"

            match
                Fit.request
                    Samples.info
                    { Samples.request with
                        MaxOutputTokens = Some 100_000
                    }
            with
            | Ok(_, changes) ->
                for change in changes do
                    $"fit {change}"
            | Error e -> $"fit error {e}"

            let messages, changes =
                Replay.prepare
                    Replay.defaults
                    (ModelRef.create "openai" "gpt-5.1")
                    "openai.responses"
                    Samples.request.Messages

            $"replay kept {messages |> List.sumBy (fun m -> m.Parts.Length)} parts"

            for change in changes do
                $"replay {change}"
        ])

let private streamText =
    "\uFEFFevent: a\r\ndata: one\r\ndata: two\r\n\r\n: comment\ndata: ż🦀\rretry: 50\rid: 7\r\rdata: {\"k\": [1, 2]}\n\ndata: unfinished"

let sse =
    Scenario.lines "models-sse" (fun () ->
        let bytes = utf8.GetBytes streamText

        [
            for chunking in
                [
                    Chunking.Whole
                    Chunking.Every 1
                    Chunking.Every 3
                    Chunking.At [ 5; 17; 18; 40 ]
                ] do
                let parser = SseParser()
                let events = ResizeArray<SseEvent>()

                for piece in Chunking.split chunking bytes do
                    match parser.Push piece with
                    | Ok more -> events.AddRange more
                    | Error e -> failwith (string e)

                let dropped = parser.Finish()

                let shown =
                    events
                    |> Seq.map (fun e ->
                        let id = e.Id |> Option.defaultValue "-"
                        let retry = e.Retry |> Option.map string |> Option.defaultValue "-"
                        $"{e.Event}|{escape e.Data}|{id}|{retry}")
                    |> String.concat " ; "

                $"{chunking}: {shown} (dropped unfinished: {dropped})"

            let ndjson = NdjsonParser()

            for piece in Chunking.split (Chunking.Every 4) (utf8.GetBytes "{\"a\":1}\r\n[true,null]\n\"ż\"") do
                match ndjson.Push piece with
                | Ok values ->
                    for v in values do
                        $"ndjson {escape (Json.serialize v)}"
                | Error e -> $"ndjson error {e}"

            match ndjson.Finish() with
            | Ok values ->
                for v in values do
                    $"ndjson last {escape (Json.serialize v)}"
            | Error e -> $"ndjson error {e}"

            let tooLarge = SseParser(8).Push(utf8.GetBytes "data: 0123456789\n")
            $"too large {show tooLarge}"
        ])

let private endpoint = "https://models.example.test/v1"
let private model = ModelRef.create "example" "echo-1"

let private chat =
    { ChatRequest.create model [ Message.userText "call ping" ] with
        Tools =
            [
                {
                    Name = "echo"
                    Description = "Echoes."
                    Parameters = Json.Object [ "type", Json.String "object" ]
                    Strict = false
                }
            ]
    }

let private recorded (request: ChatRequest) : Interaction =
    {
        Request =
            {
                Method = "POST"
                Url = endpoint + Canonical.ChatPath
                Headers = [ "content-type", "application/json" ]
                Body = RecordedBody.Json(ChatRequest.codec.Encode request)
            }
        Outcome =
            RecordedOutcome.Response
                {
                    Status = 200
                    Headers = [ "content-type", "text/event-stream" ]
                    Body = Canonical.render (Canonical.echo request)
                    End = BodyEnd.Complete
                }
    }

let private events' (events: ChatEvent list) : string =
    events |> List.map (string >> escape) |> String.concat "; "

let private drain (stream: ChatStream) : ChatEvent list =
    let events = ResizeArray<ChatEvent>()
    let mutable reading = true

    while reading do
        match wait (stream.Next CancellationToken.None) with
        | Some e -> events.Add e
        | None -> reading <- false

    wait (stream.Close())
    List.ofSeq events

let private provider = ProviderDef.create "example" Canonical.Family endpoint

/// A hook's environment.
type private HookEnv =
    {
        Clock: Clock
        Scope: Scope
    }

    interface IExtensionEnv with
        member _.Extension = "acme.egress"
        member _.Capabilities = ExtensionCapabilities.none "acme.egress"

    interface Fx.Variants.IVariants with
        member _.Variants = (ExtensionCapabilities.none "acme.egress").Variants

    interface IPackages with
        member _.Packages = (ExtensionCapabilities.none "acme.egress").Packages

    interface IGrants with
        member _.Grants = (ExtensionCapabilities.none "acme.egress").Grants

    interface IClock with
        member env.Clock = env.Clock

    interface IScopeWith<IExtensionEnv> with
        member env.Scope = env.Scope

        member env.WithScope scope =
            { env with Scope = scope } :> IExtensionEnv

let private hook (id: string) (value: 'T) : Contributed<'T> =
    {
        Extension = "acme.egress"
        Type = "hook"
        Id = id
        Value = value
        Env =
            {
                Clock = (FakeClock.Create()).Clock
                Scope = Scope.root
            }
    }

let cassette =
    Scenario.lines "models-cassette" (fun () ->
        [
            for chunking in
                [
                    Chunking.Whole
                    Chunking.Every 1
                    Chunking.Every 5
                    Chunking.At [ 3; 100; 101; 250 ]
                ] do
                let http = CassetteHttp({ Interactions = [ recorded chat ] }, chunking)

                let access =
                    ModelAccess.create [ Canonical.wire ] [ provider ] http.Http (FakeClock.Create()).Clock

                let events =
                    drain (wait ((ModelAccess.llm access).Stream chat CancellationToken.None))

                $"{chunking}: {events.Length} events, coalesced {(ChatEvents.coalesce events).Length}"
                $"{chunking}: {show (ChatAggregate.result (ChatAggregate.ofEvents events))}"
                $"{chunking}: remaining {http.Remaining.Length}, open bodies {http.OpenBodies}"

            let http = CassetteHttp({ Interactions = [ recorded chat ] })

            let events =
                drain (
                    wait (
                        (ModelAccess.create [ Canonical.wire ] [ provider ] http.Http (FakeClock.Create()).Clock
                         |> ModelAccess.llm)
                            .Stream
                            chat
                            CancellationToken.None
                    )
                )

            for event in events do
                $"event {escape (Json.serialize (ChatEvent.codec.Encode event))}"

            let deny: Gate<ChatRequest> =
                {
                    Check = fun _ -> Eff.ret (Decision.Deny "excluded")
                }

            let redact: Transform<ChatRequest> =
                Transform.ofFunction 0 (fun r ->
                    { r with
                        Messages = [ Message.userText "[redacted]" ]
                    })

            let guarded =
                { ModelAccess.create [ Canonical.wire ] [ provider ] http.Http (FakeClock.Create()).Clock with
                    Egress =
                        {
                            Transforms = [ hook "redact" redact ]
                            Gates = [ hook "deny" deny ]
                        }
                }

            let denied =
                drain (wait ((ModelAccess.llm guarded).Stream chat CancellationToken.None))

            $"denied {events' denied}"

            let mismatch =
                try
                    let other = CassetteHttp({ Interactions = [ recorded chat ] })

                    let access =
                        ModelAccess.create [ Canonical.wire ] [ provider ] other.Http (FakeClock.Create()).Clock

                    wait ((ModelAccess.llm access).Stream { chat with Messages = [] } CancellationToken.None)
                    |> ignore

                    "no mismatch"
                with e ->
                    e.GetType().Name

            $"mismatch {mismatch}"
        ])

/// A loopback canonical server: answers each request with the echo, as chunked event streams
/// flushed event by event.
let private serve (listener: TcpListener) (stop: CancellationToken) =
    task {
        try
            while true do
                let! client = listener.AcceptTcpClientAsync stop

                task {
                    use client = client
                    use stream = client.GetStream()
                    let buffer = Array.zeroCreate<byte> 65536
                    use received = new IO.MemoryStream()
                    let mutable request = None

                    while request.IsNone do
                        let! n = stream.ReadAsync(buffer, 0, buffer.Length, stop)

                        if n = 0 then
                            failwith "closed"

                        received.Write(buffer, 0, n)
                        let all = received.ToArray()
                        let text = Encoding.Latin1.GetString all
                        let headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal)

                        if headEnd >= 0 then
                            let length =
                                text.Substring(0, headEnd).Split("\r\n")
                                |> Array.tryPick (fun line ->
                                    if line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) then
                                        Some(int (line.Substring(15).Trim()))
                                    else
                                        None)
                                |> Option.defaultValue 0

                            if all.Length - headEnd - 4 >= length then
                                request <- Some(utf8.GetString(all, headEnd + 4, length))

                    let write (bytes: byte array) =
                        task {
                            do! stream.WriteAsync(bytes, 0, bytes.Length, stop)
                            do! stream.FlushAsync stop
                        }

                    match Codec.decodeText ChatRequest.codec (Option.get request) with
                    | Ok chat ->
                        do!
                            write (
                                Encoding.ASCII.GetBytes
                                    "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
                            )

                        for event in Canonical.echo chat do
                            let piece = utf8.GetBytes(Canonical.render [ event ])
                            do! write (Encoding.ASCII.GetBytes $"{piece.Length:x}\r\n")
                            do! write piece
                            do! write (Encoding.ASCII.GetBytes "\r\n")

                        do! write (Encoding.ASCII.GetBytes "0\r\n\r\n")
                    | Error _ ->
                        let body =
                            utf8.GetBytes(Canonical.errorBody (ChatError.InvalidRequest "not a chat request"))

                        do!
                            write (
                                Encoding.ASCII.GetBytes
                                    $"HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"
                            )

                        do! write body
                }
                |> ignore
        with _ ->
            ()
    }

let live =
    Scenario.task "models-live-http" (fun () ->
        task {
            use listener = new TcpListener(IPAddress.Loopback, 0)
            listener.Start()
            let port = (listener.LocalEndpoint :?> IPEndPoint).Port
            use stop = new CancellationTokenSource()
            let serving = serve listener stop.Token
            use client = SystemHttp.client ()
            let base' = $"http://127.0.0.1:{port}/v1"
            let provider = ProviderDef.create "example" Canonical.Family base'

            let access =
                { ModelAccess.create [ Canonical.wire ] [ provider ] (SystemHttp.create client) SystemClock.clock with
                    Timeouts =
                        {
                            Response = Some(TimeSpan.FromSeconds 30.0)
                            Idle = Some(TimeSpan.FromSeconds 30.0)
                        }
                }

            let llm = ModelAccess.llm access
            let lines = ResizeArray<string>()

            let! stream =
                (llm.Stream chat CancellationToken.None).WaitAsync(TimeSpan.FromMinutes 1.0)

            let events = drain stream
            lines.Add $"events {events.Length}"
            lines.Add $"result {show (ChatAggregate.result (ChatAggregate.ofEvents events))}"

            let! text =
                (llm.Stream (ChatRequest.create model [ Message.userText "Zażółć 🦀 gęślą" ]) CancellationToken.None)
                    .WaitAsync(TimeSpan.FromMinutes 1.0)

            match ChatAggregate.result (ChatAggregate.ofEvents (drain text)) with
            | Ok response -> lines.Add $"echo {escape (ChatResponse.text response)} {response.Usage}"
            | Error failure -> lines.Add $"echo failed {failure}"

            let outside =
                { provider with
                    Endpoints = [ Canonical.Family, $"http://127.0.0.1:{port}/elsewhere" ]
                }

            let confined =
                ModelAccess.llm
                    { access with
                        Providers = [ outside ]
                        Wires =
                            [
                                SseWire.create
                                    { Canonical.adapter with
                                        Request =
                                            fun (ctx, chat) ->
                                                Canonical.request (ctx, chat)
                                                |> Result.map (fun r -> { r with Url = base' + "/chat" })
                                    }
                            ]
                    }

            let! refused =
                (confined.Stream chat CancellationToken.None).WaitAsync(TimeSpan.FromMinutes 1.0)

            let shown = (events' (drain refused)).Replace(base', "<server>")
            lines.Add $"confined {shown}"

            let unreachable =
                { provider with
                    Endpoints = [ Canonical.Family, "http://127.0.0.1:1/v1" ]
                }

            let! failed =
                (ModelAccess.llm
                    { access with
                        Providers = [ unreachable ]
                    })
                    .Stream
                    chat
                    CancellationToken.None

            match drain failed with
            | [ ChatEvent.Failed(ChatError.Transport(HttpError.ConnectFailed _)) ] ->
                lines.Add "unreachable: connection failed"
            | other -> lines.Add $"unreachable: {events' other}"

            stop.Cancel()
            listener.Stop()
            do! serving.WaitAsync(TimeSpan.FromMinutes 1.0)
            return List.ofSeq lines
        })

let scenarios = [ codecs; fold; sse; cassette; live ]
