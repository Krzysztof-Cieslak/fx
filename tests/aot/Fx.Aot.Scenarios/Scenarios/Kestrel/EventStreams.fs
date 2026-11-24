/// Server-sent events on Kestrel: headers, one flush per event (the server writes the next event only
/// after the client has the previous one), comments, resuming with Last-Event-ID, a client that
/// disconnects and a server that stops.
module Fx.Aot.Scenarios.Kestrel.EventStreams

open System
open System.Net.Http
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Net.Http.Headers
open Fx.Aot.Scenarios
open Fx.Aot.Scenarios.Kestrel.Support
open Fx.Transport.Http
open Fx.Transport.Http.LocalServer

let private lastEvent = 4

/// Streams events `0..lastEvent` (or those after Last-Event-ID), waiting for the client's ack after
/// each one, then an `end` event. Its log goes to `logs` when it ends.
let private stream (acks: Channel<int>) (logs: Channel<string list>) (request: Request) : Task =
    task {
        let context = request.Context
        let log = ResizeArray<string>()

        use cancel =
            CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, request.Stopping)

        let first =
            match Events.lastEventId context with
            | Some id ->
                log.Add $"server: resuming after event {id}"
                int id + 1
            | None -> 0

        let mutable sent = "none"

        try
            try
                do! Events.start context

                for i in first..lastEvent do
                    if i = 3 then
                        do! Events.comment context "keep-alive" cancel.Token

                    let event =
                        { Sse.event (
                              if i = lastEvent then
                                  "multi-line\nevent 4"
                              else
                                  $"event {i}"
                          ) with
                            Id = Some(string i)
                            Type = if i % 2 = 0 then Some "tick" else None
                        }

                    do! Events.write context event cancel.Token
                    sent <- string i
                    // The client acknowledges each event it has parsed: it was flushed, not buffered.
                    let! acked = acks.Reader.ReadAsync(cancel.Token)

                    if acked <> i then
                        log.Add $"server: ack {acked} for event {i}"

                do!
                    Events.write
                        context
                        { Sse.event "bye" with
                            Type = Some "end"
                        }
                        cancel.Token

                log.Add "server: stream complete"
            with :? OperationCanceledException ->
                if context.RequestAborted.IsCancellationRequested then
                    log.Add $"server: client disconnected after event {sent}"
                else
                    log.Add $"server: stopping after event {sent}"
        finally
            logs.Writer.TryWrite(List.ofSeq log) |> ignore
    }

let events =
    Scenario.task "kestrel-sse" (fun () ->
        task {
            let acks = Channel.CreateUnbounded<int>()
            let logs = Channel.CreateUnbounded<string list>()
            let! server = startLoopback id [ Endpoint.get "/events" (stream acks logs) ]
            use http = authorizedClient Tcp (baseUri server)
            let log = ResizeArray<string>()

            let serverLog () =
                task {
                    let! lines = within (logs.Reader.ReadAsync().AsTask())
                    log.AddRange lines
                }

            let next (label: string) (reader: EventReader) =
                task {
                    let! event = reader.Next()

                    let shown =
                        match event with
                        | Some e -> e
                        | None -> "end of stream"

                    log.Add $"{label}: {shown}"
                }

            let ack (i: int) = acks.Writer.TryWrite i |> ignore

            // A: a fresh stream; the client leaves after event 2 without acknowledging it.
            let! a = openEvents http (new HttpRequestMessage(HttpMethod.Get, "/events"))
            log.Add $"A: status {int a.Response.StatusCode}"
            log.Add $"A: content type {header a.Response HeaderNames.ContentType}"
            log.Add $"A: cache control {header a.Response HeaderNames.CacheControl}"
            let accel = header a.Response "X-Accel-Buffering"
            log.Add $"A: proxy buffering {accel}"

            for i in 0..2 do
                do! next "A" a

                if i < 2 then
                    ack i

            do! (a :> IAsyncDisposable).DisposeAsync()
            do! serverLog ()

            // B: the client resumes after event 2 and reads to the end of the stream.
            let resume = new HttpRequestMessage(HttpMethod.Get, "/events")
            resume.Headers.Add("Last-Event-ID", "2")
            let! b = openEvents http resume

            for i in 3..lastEvent do
                do! next "B" b
                ack i

            do! next "B" b
            do! next "B" b
            do! (b :> IAsyncDisposable).DisposeAsync()
            do! serverLog ()

            // C: the server stops while a stream is open: the stream ends cleanly instead of hanging
            // until the shutdown timeout and then being aborted.
            let! c = openEvents http (new HttpRequestMessage(HttpMethod.Get, "/events"))
            do! next "C" c
            let stopping = server.StopAsync()
            do! next "C" c
            do! withinTask stopping
            do! (c :> IAsyncDisposable).DisposeAsync()
            do! serverLog ()
            do! (server :> IAsyncDisposable).DisposeAsync()
            return List.ofSeq log
        })

let scenarios = [ events ]
