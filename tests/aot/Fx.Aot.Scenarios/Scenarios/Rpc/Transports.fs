/// The JSON-RPC conversation (`Transcript`) over each transport: in process, a stream over in-memory
/// pipes, a child process on stdio (this program's `rpc-peer`), and WebSocket on loopback Kestrel.
module Fx.Aot.Scenarios.Rpc.Transports

open System
open System.IO
open System.IO.Pipelines
open System.Net.Http
open System.Net.WebSockets
open System.Threading
open System.Threading.Tasks
open Fx.Rpc
open Fx.Transport.Http
open Fx.Aot.Scenarios
open Fx.Aot.Scenarios.Kestrel.Support
open Fx.Aot.Scenarios.Rpc.TestService

let private inProcess () : Task<string list> =
    task {
        let client, server =
            InProcess.pair (256 * 1024) (ConnectionLimits.frames clientLimits) (ConnectionLimits.frames limits)

        Transcript.serve server
        return! Transcript.run client Transcript.Close
    }

/// A pipe that holds at most 64 KiB, like an OS pipe.
let private pipe () =
    System.IO.Pipelines.Pipe(
        PipeOptions(pauseWriterThreshold = 65536L, resumeWriterThreshold = 32768L, useSynchronizationContext = false)
    )

let private stream () : Task<string list> =
    task {
        let toServer = pipe ()
        let toClient = pipe ()

        let server =
            StreamTransport.create
                (ConnectionLimits.frames limits)
                (toServer.Reader.AsStream())
                (toClient.Writer.AsStream())

        let client =
            StreamTransport.create
                (ConnectionLimits.frames clientLimits)
                (toClient.Reader.AsStream())
                (toServer.Writer.AsStream())

        Transcript.serve server
        let! closed = Transcript.run client Transcript.Close

        // A second conversation that ends with the server crashing.
        let toServer = pipe ()
        let toClient = pipe ()

        let server =
            StreamTransport.create
                (ConnectionLimits.frames limits)
                (toServer.Reader.AsStream())
                (toClient.Writer.AsStream())

        let client =
            StreamTransport.create
                (ConnectionLimits.frames clientLimits)
                (toClient.Reader.AsStream())
                (toServer.Writer.AsStream())

        Transcript.serve server

        let! crashed =
            Transcript.run
                client
                (Transcript.Crash(fun () ->
                    server.Abort()
                    Task.CompletedTask))

        return
            closed
            @ [ "--- crash" ]
            @ (crashed
               |> List.filter (fun l -> l.StartsWith("crash", StringComparison.Ordinal)))
    }

let private childProcess () : Task<string list> =
    task {
        let closing = Peer.start ()
        let! closed = Transcript.run closing.Transport Transcript.Close

        let! exited =
            within (closing.Process.WaitForExitAsync().ContinueWith(fun (_: Task) -> closing.Process.ExitCode))

        let crashing = Peer.start ()

        let! crashed =
            Transcript.run
                crashing.Transport
                (Transcript.Crash(fun () ->
                    crashing.Process.Kill true
                    crashing.Process.WaitForExitAsync()))

        crashing.Process.Dispose()
        closing.Process.Dispose()

        return
            closed
            @ [ $"peer exit code: {exited}"; "--- crash" ]
            @ (crashed
               |> List.filter (fun l -> l.StartsWith("crash", StringComparison.Ordinal)))
    }

/// Serves `TestService` over WebSocket on `listener`, connects with `LocalClient`, and runs the
/// conversation.
let private overWebSocket (listener: LocalServer.Listener) : Task<string list> =
    task {
        let endpoint =
            LocalServer.Endpoint.webSocket "/rpc" [ Protocol.webSocketSubprotocol ] (fun request socket ->
                task {
                    let transport = WebSocketTransport.create (ConnectionLimits.frames limits) socket

                    let env =
                        {
                            Clock = Fx.Effects.Live.SystemClock.clock
                            Scope = Fx.Effects.Scope.ofCancellation request.Stopping
                        }

                    match! Fx.Effects.Eff.run env (Rpc.accept (service ()) transport) with
                    | Ok peer ->
                        let! _ = peer.Completion
                        ()
                    | Error _ -> ()
                })

        let! server = LocalServer.start (LocalServer.options listener policy [ endpoint ])

        try
            let target =
                match listener with
                | LocalServer.Loopback _ -> LocalServer.Loopback (baseUri server).Port
                | other -> other

            // A wrong token is refused before the upgrade.
            let! refused =
                LocalClient.connectWebSocket
                    target
                    "/rpc"
                    (String('x', 40))
                    [ Protocol.webSocketSubprotocol ]
                    CancellationToken.None

            let refusal =
                match refused with
                | Ok _ -> "wrong token: connected"
                | Error e -> "wrong token: " + string e

            let! connected =
                LocalClient.connectWebSocket
                    target
                    "/rpc"
                    token
                    [ Protocol.webSocketSubprotocol ]
                    CancellationToken.None

            match connected with
            | Error e -> return [ refusal; "connect: " + string e ]
            | Ok socket ->
                let subprotocol = Show.orNull socket.SubProtocol

                let transport =
                    WebSocketTransport.create (ConnectionLimits.frames clientLimits) socket

                let! lines = Transcript.run transport Transcript.Close
                transport.Abort()
                return refusal :: $"subprotocol: {subprotocol}" :: lines
        finally
            (server :> IAsyncDisposable).DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds 30.0)
            |> ignore
    }

let private webSocket () : Task<string list> = overWebSocket (LocalServer.Loopback 0)

/// Over a Unix domain socket in a directory only this user can enter (Linux, macOS, Windows).
let private webSocketUnix () : Task<string list> =
    task {
        // Socket paths are limited to about 100 bytes (sun_path), so keep the path short.
        let dir = Path.Combine(Path.GetTempPath(), uniqueName "rpc-uds-")

        if OperatingSystem.IsWindows() then
            Directory.CreateDirectory dir |> ignore
        else
            Directory.CreateDirectory(
                dir,
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
            )
            |> ignore

        try
            return! overWebSocket (LocalServer.UnixSocket(Path.Combine(dir, "rpc.sock")))
        finally
            Directory.Delete(dir, true)
    }

let scenarios: Scenario list =
    [
        Scenario.task "rpc-in-process" inProcess
        Scenario.task "rpc-stream" stream
        Scenario.task "rpc-stdio-child" childProcess
        Scenario.task "rpc-websocket" webSocket
        Scenario.task "rpc-websocket-unix-socket" webSocketUnix
    ]
