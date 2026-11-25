/// The conformance suite over every transport, and what is particular to each.
module Fx.Rpc.Tests.TransportTests

open System
open System.IO.Pipelines
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Live
open Fx.Rpc
open Fx.Rpc.Tests.RpcConformance
open Fx.Transport.Http
open Fx.Aot.Scenarios.Rpc
open Fx.Aot.Scenarios.Rpc.TestService

let private serverEnv =
    {
        Clock = SystemClock.clock
        Scope = Scope.root
    }

/// Serves `TestService` on a transport, in the background; completes when that connection has ended.
let private serve (transport: Transport) : Task =
    task {
        match! Eff.run serverEnv (Rpc.accept (service ()) transport) with
        | Ok peer ->
            let! _ = peer.Completion
            ()
        | Error _ -> ()
    }

let inProcess () : Task<Link> =
    let client, server =
        InProcess.pair (256 * 1024) (ConnectionLimits.frames clientLimits) (ConnectionLimits.frames limits)

    let ended = serve server

    Task.FromResult
        {
            Transport = client
            Ended = ended
            Crash =
                fun () ->
                    server.Abort()
                    Task.CompletedTask
            Dispose =
                fun () ->
                    server.Abort()
                    client.Abort()
                    Task.CompletedTask
        }

/// A pipe that holds at most 64 KiB, like an OS pipe.
let private pipe () =
    System.IO.Pipelines.Pipe(
        PipeOptions(pauseWriterThreshold = 65536L, resumeWriterThreshold = 32768L, useSynchronizationContext = false)
    )

let memoryPipes () : Task<Link> =
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

    let ended = serve server

    Task.FromResult
        {
            Transport = client
            Ended = ended
            Crash =
                fun () ->
                    server.Abort()
                    Task.CompletedTask
            Dispose =
                fun () ->
                    server.Abort()
                    client.Abort()
                    Task.CompletedTask
        }

let childProcess () : Task<Link> =
    let child = Peer.start ()

    Task.FromResult
        {
            Transport = child.Transport
            Ended = child.Process.WaitForExitAsync()
            Crash =
                fun () ->
                    child.Process.Kill true
                    child.Process.WaitForExitAsync()
            Dispose =
                fun () ->
                    task {
                        child.Transport.Abort()

                        try
                            do! child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 10.0)
                        with _ ->
                            child.Process.Kill true

                        child.Process.Dispose()
                    }
        }

/// The same child, started through the shell capability (`LocalShell`): its standard input and
/// output are a `Proc`, wrapped by `ProcTransport`, as the process runtime of extensions does (T108).
let shellChildProcess () : Task<Link> =
    task {
        let info = Peer.startInfo ()
        let shell = LocalShell.create (LocalShell.defaultShell ()) AppContext.BaseDirectory

        let! spawned =
            shell.Spawn
                {
                    Program = Exec(info.FileName, List.ofSeq info.ArgumentList)
                    Cwd = None
                    Env = Inherit []
                }
                CancellationToken.None

        let proc =
            match spawned with
            | Ok proc -> proc
            | Error e -> failtest (string e)

        let transport =
            ProcTransport.create (ConnectionLimits.frames clientLimits) proc ignore

        return
            {
                Transport = transport
                Ended =
                    task {
                        let! _ = proc.Wait CancellationToken.None
                        ()
                    }
                Crash =
                    fun () ->
                        task {
                            do! proc.Kill()
                            let! _ = proc.Wait CancellationToken.None
                            ()
                        }
                Dispose =
                    fun () ->
                        task {
                            transport.Abort()

                            try
                                let! _ = proc.Wait(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds 10.0)
                                ()
                            with _ ->
                                do! proc.Kill()
                        }
            }
    }

let private token = "rpc-tests-token-0123456789-abcdefghijk"

/// `TestService` over WebSocket on a Kestrel listener, reached with `LocalClient`.
let private overWebSocket (listener: LocalServer.Listener) (cleanup: unit -> unit) : Task<Link> =
    task {
        let serverEnd =
            TaskCompletionSource<Transport>(TaskCreationOptions.RunContinuationsAsynchronously)

        let ended = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let endpoint =
            LocalServer.Endpoint.webSocket "/rpc" [ Protocol.webSocketSubprotocol ] (fun request socket ->
                task {
                    try
                        let transport = WebSocketTransport.create (ConnectionLimits.frames limits) socket
                        serverEnd.TrySetResult transport |> ignore

                        let env =
                            { serverEnv with
                                Scope = Scope.ofCancellation request.Stopping
                            }

                        match! Eff.run env (Rpc.accept (service ()) transport) with
                        | Ok peer ->
                            let! _ = peer.Completion
                            ()
                        | Error _ -> ()
                    finally
                        ended.TrySetResult() |> ignore
                })

        let! server =
            LocalServer.start (LocalServer.options listener (LocalAccess.policy token) [ endpoint ])

        let target =
            match listener with
            | LocalServer.Loopback _ ->
                let address =
                    server.Addresses
                    |> List.find (fun a -> a.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))

                LocalServer.Loopback(Uri(address).Port)
            | other -> other

        let! connected =
            LocalClient.connectWebSocket target "/rpc" token [ Protocol.webSocketSubprotocol ] CancellationToken.None

        let socket =
            match connected with
            | Ok socket -> socket
            | Error e -> failtest ("connect failed: " + string e)

        Expect.equal socket.SubProtocol Protocol.webSocketSubprotocol "subprotocol"
        let client = WebSocketTransport.create (ConnectionLimits.frames clientLimits) socket

        return
            {
                Transport = client
                Ended = ended.Task
                Crash =
                    fun () ->
                        task {
                            let! transport = serverEnd.Task
                            transport.Abort()
                        }
                Dispose =
                    fun () ->
                        task {
                            client.Abort()
                            do! (server :> IAsyncDisposable).DisposeAsync()
                            cleanup ()
                        }
            }
    }

let webSocket () : Task<Link> =
    overWebSocket (LocalServer.Loopback 0) ignore

/// Over a Unix domain socket in a fresh directory only this user can enter.
let webSocketUnix () : Task<Link> =
    let dir =
        IO.Path.Combine(IO.Path.GetTempPath(), "rpc-" + Guid.NewGuid().ToString("N").Substring(0, 12))

    if OperatingSystem.IsWindows() then
        IO.Directory.CreateDirectory dir |> ignore
    else
        IO.Directory.CreateDirectory(
            dir,
            IO.UnixFileMode.UserRead
            ||| IO.UnixFileMode.UserWrite
            ||| IO.UnixFileMode.UserExecute
        )
        |> ignore

    overWebSocket (LocalServer.UnixSocket(IO.Path.Combine(dir, "rpc.sock"))) (fun () ->
        try
            IO.Directory.Delete(dir, true)
        with _ ->
            ())

[<Tests>]
let conformance =
    testList
        "Conformance"
        [
            RpcConformance.tests "in-process" inProcess
            RpcConformance.tests "stream over pipes" memoryPipes
            RpcConformance.tests "stdio child process" childProcess
            RpcConformance.tests "child process through the shell capability" shellChildProcess
            RpcConformance.tests "websocket" webSocket
            RpcConformance.tests "websocket over a unix socket" webSocketUnix
        ]

[<Tests>]
let particular =
    testList
        "Transports"
        [
            testTask "a wrong token is refused before the upgrade" {
                let! server =
                    LocalServer.start (LocalServer.options (LocalServer.Loopback 0) (LocalAccess.policy token) [])

                try
                    let address =
                        (server: LocalServer.Server).Addresses
                        |> List.find (fun (a: string) -> a.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))

                    let target = LocalServer.Loopback(Uri(address).Port)

                    let! wrong =
                        LocalClient.connectWebSocket target "/rpc" (String('x', 40)) [] CancellationToken.None

                    Expect.equal (wrong |> Result.map ignore) (Error(LocalClient.Rejected 401)) "token"

                    let! missing =
                        LocalClient.connectWebSocket target "/missing" token [] CancellationToken.None

                    Expect.equal (missing |> Result.map ignore) (Error(LocalClient.Rejected 404)) "path"
                finally
                    (server :> IAsyncDisposable).DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds 30.0)
                    |> ignore
            }

            testTask "nothing listening is unreachable" {
                let path =
                    IO.Path.Combine(IO.Path.GetTempPath(), "rpc-none-" + Guid.NewGuid().ToString("N").Substring(0, 8))

                let! outcome =
                    LocalClient.connectWebSocket (LocalServer.UnixSocket path) "/rpc" token [] CancellationToken.None

                match outcome with
                | Error(LocalClient.Unreachable _) -> ()
                | other -> failtest (string (other |> Result.map ignore))
            }

            testTask "a stream peer may speak plain NDJSON with CRLF" {
                let toServer = pipe ()
                let toClient = pipe ()

                let server =
                    StreamTransport.create
                        (ConnectionLimits.frames limits)
                        (toServer.Reader.AsStream())
                        (toClient.Writer.AsStream())

                let _ = serve server
                let input = toServer.Writer.AsStream()
                let output = toClient.Reader.AsStream()

                let lines =
                    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersions":["1"],"peer":{"name":"sh","version":"0"},"capabilities":{"maxMessageSize":4096}}}"""
                    + "\r\n\r\n"
                    + """{"jsonrpc":"2.0","id":2,"method":"test/add","params":{"a":1,"b":2}}"""
                    + "\r\n"

                do! input.WriteAsync(ReadOnlyMemory(Encoding.UTF8.GetBytes lines))
                do! input.FlushAsync()
                input.Dispose()
                let reader = new IO.StreamReader(output, Encoding.UTF8)
                let! first = within (reader.ReadLineAsync())
                let! second = within (reader.ReadLineAsync())
                let! last = within (reader.ReadLineAsync())
                Expect.stringContains (string first) "\"id\":1,\"result\":{\"protocolVersion\":\"1\"" "initialized"
                Expect.equal (string second) """{"jsonrpc":"2.0","id":2,"result":3}""" "answered after the input ended"
                Expect.isNull last "then the output ends"
            }

            testTask "stream frames over the limit end the connection" {
                let toServer = pipe ()
                let toClient = pipe ()

                let server =
                    StreamTransport.create
                        (ConnectionLimits.frames limits)
                        (toServer.Reader.AsStream())
                        (toClient.Writer.AsStream())

                let _ = serve server
                let input = toServer.Writer.AsStream()
                // A binary frame header announcing more than the limit.
                do! input.WriteAsync(ReadOnlyMemory([| 0uy; 0x7Fuy; 0uy; 0uy; 0uy |]))
                do! input.FlushAsync()
                let output = toClient.Reader.AsStream()
                let buffer = Array.zeroCreate<byte> 16
                let! read = within (output.ReadAsync(buffer, 0, buffer.Length))
                Expect.equal read 0 "the server closed its output"
            }

        ]
