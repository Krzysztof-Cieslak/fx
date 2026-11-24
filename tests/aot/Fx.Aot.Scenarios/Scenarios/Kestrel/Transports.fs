/// Local transports other than TCP: Unix domain sockets (Linux, macOS and Windows) and Windows named
/// pipes, with HTTP, server-sent events and WebSocket over each. The token is still required.
module Fx.Aot.Scenarios.Kestrel.Transports

open System
open System.IO
open System.Net.WebSockets
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Fx.Aot.Scenarios
open Fx.Aot.Scenarios.Kestrel.Support
open Fx.Transport.Http
open Fx.Transport.Http.LocalServer
open Fx.Transport.Http.WebSocketMessages

let private endpoints =
    [
        Endpoint.get "/hello" (fun r -> r.Context.Response.WriteAsync "hello")
        Endpoint.get "/events" (fun r ->
            task {
                do! Events.start r.Context
                do! Events.write r.Context (Sse.event "over the socket") r.Context.RequestAborted
            })
        Endpoint.webSocket "/echo" [] (fun r socket ->
            task {
                match! receive 1024 socket r.Stopping with
                | Message m -> do! send socket m r.Stopping
                | _ -> ()

                do! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", r.Stopping)
            })
    ]

/// HTTP without and with the token, an event stream and a WebSocket echo through `connect`.
let private exercise (connect: Connect) (server: Server) (log: ResizeArray<string>) : Task =
    task {
        let address = Uri "http://localhost/"
        use anonymous = client connect address
        use! rejected = within (anonymous.GetAsync "/hello")
        let! described = describe rejected
        log.Add $"{connect} GET /hello without token: {described}"

        use http = authorizedClient connect address
        use! hello = within (http.GetAsync "/hello")
        let! described = describe hello
        log.Add $"{connect} GET /hello: {described}"

        let! events =
            openEvents http (new Net.Http.HttpRequestMessage(Net.Http.HttpMethod.Get, "/events"))

        let! first = events.Next()
        let! second = events.Next()
        do! (events :> IAsyncDisposable).DisposeAsync()

        let show = Option.defaultValue "end of stream"

        log.Add $"{connect} events: {show first}, then {show second}"

        match! connectWebSocket connect (webSocketUri connect server "/echo") withToken with
        | Error status -> log.Add $"{connect} WebSocket rejected {status}"
        | Ok socket ->
            use socket = socket
            do! send socket (Text "ping") CancellationToken.None
            let! echo = within (receive 1024 socket CancellationToken.None)
            let! closed = within (receive 1024 socket CancellationToken.None)
            do! socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
            log.Add $"{connect} WebSocket: echo {echo}, then {closed}"
    }

/// Creates a directory only the current user can enter (on Unix), runs `f` with it, deletes it.
let private inPrivateDir (f: string -> Task<'T>) : Task<'T> =
    task {
        // Socket paths are limited to about 100 bytes (sun_path), so keep the path short.
        let dir = Path.Combine(Path.GetTempPath(), uniqueName "aot-uds-")

        if OperatingSystem.IsWindows() then
            Directory.CreateDirectory dir |> ignore
        else
            Directory.CreateDirectory(
                dir,
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
            )
            |> ignore

        try
            return! f dir
        finally
            Directory.Delete(dir, true)
    }

let unixSocket =
    Scenario.task "kestrel-unix-socket" (fun () ->
        inPrivateDir (fun dir ->
            task {
                let log = ResizeArray<string>()
                let path = Path.Combine(dir, "host.sock")
                let options = LocalServer.options (UnixSocket path) policy endpoints
                let! server = LocalServer.start options

                for address in server.Addresses do
                    log.Add("address = " + address.Replace(path, "<path>"))

                log.Add $"socket file while listening = {File.Exists path}"
                do! exercise (Unix path) server log
                do! (server :> IAsyncDisposable).DisposeAsync()
                log.Add $"socket file after stop = {File.Exists path}"

                // A stale socket file makes the next bind fail, so a restart must find it gone.
                let! again = LocalServer.start options
                log.Add "restart on the same path = ok"
                do! (again :> IAsyncDisposable).DisposeAsync()
                return List.ofSeq log
            }))

let namedPipe =
    Scenario.task "kestrel-named-pipe" (fun () ->
        task {
            let log = ResizeArray<string>()
            let name = uniqueName "aot-scenario-"

            let! server =
                LocalServer.start (LocalServer.options (NamedPipe name) policy endpoints)

            for address in server.Addresses do
                log.Add("address = " + address.Replace(name, "<name>"))

            do! exercise (Pipe name) server log
            do! (server :> IAsyncDisposable).DisposeAsync()
            return List.ofSeq log
        })
    // Kestrel's named-pipe transport exists only on Windows.
    |> Scenario.onlyOn [ Os.Windows ]

let scenarios = [ unixSocket; namedPipe ]
