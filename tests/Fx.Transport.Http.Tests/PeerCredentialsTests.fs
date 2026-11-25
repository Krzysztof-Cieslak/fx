/// The OS account of a local listener's peers (spec §18; the proof of `fx.identity.local-os-user`),
/// on every transport this platform has, and the owner-only token file.
module Fx.Transport.Http.Tests.PeerCredentialsTests

open System
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Transport.Http
open Fx.Transport.Http.LocalServer
open Fx.Transport.Http.PeerCredentials

let private token = "peer-credentials-token-0123456789abcdef"

/// A fresh directory only this user can enter, under the temporary directory.
let private ownDirectory () : string =
    let dir =
        Path.Combine(Path.GetTempPath(), "fxpc-" + Guid.NewGuid().ToString("N").Substring(0, 10))

    if OperatingSystem.IsWindows() then
        Directory.CreateDirectory dir |> ignore
    else
        Directory.CreateDirectory(dir, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        |> ignore

    dir

/// What the server saw of the one client that connects to `listener`.
let private observe (listener: Listener) : Task<PeerAccount option * LocalAccess.Bearer> =
    task {
        let seen = TaskCompletionSource<PeerAccount option * LocalAccess.Bearer>()

        let endpoint =
            Endpoint.webSocket "/peer" [] (fun request socket ->
                seen.TrySetResult((request.Peer, request.Access.Bearer)) |> ignore
                socket.CloseAsync(Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", CancellationToken.None))

        let! server =
            LocalServer.start (LocalServer.options listener (LocalAccess.policy token) [ endpoint ])

        try
            let target =
                match listener with
                | Loopback _ ->
                    let address =
                        server.Addresses
                        |> List.find (fun a -> a.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))

                    Loopback(Uri(address).Port)
                | other -> other

            let! connected =
                LocalClient.connectWebSocket target "/peer" token [] CancellationToken.None

            match connected with
            | Error e -> return failtest (string e)
            | Ok socket ->
                try
                    return! seen.Task.WaitAsync(TimeSpan.FromSeconds 30.0)
                finally
                    socket.Dispose()
        finally
            (server :> IAsyncDisposable).DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds 30.0)
            |> ignore
    }

/// `id -u`: the effective user id, as the system's own tool says.
let private idU () : string =
    let start =
        ProcessStartInfo("id", [ "-u" ], RedirectStandardOutput = true, UseShellExecute = false)

    use proc = Process.Start start |> nonNull
    let output = proc.StandardOutput.ReadToEnd().Trim()
    proc.WaitForExit()
    output

[<Tests>]
let tests =
    testList
        "PeerCredentials"
        [
            test "this process's account" {
                let account = currentAccount ()

                if OperatingSystem.IsWindows() then
                    use identity = Security.Principal.WindowsIdentity.GetCurrent()
                    Expect.equal account (identity.User |> nonNull).Value "the user's SID"
                    Expect.stringStarts account "S-1-" "a SID"
                else
                    Expect.equal account (idU ()) "id -u"
            }

            testTask "a Unix socket's peer is this process's account" {
                let dir = ownDirectory ()

                try
                    let! peer, bearer = observe (UnixSocket(Path.Combine(dir, "peer.sock")))
                    Expect.equal bearer LocalAccess.Bearer.Listener "the listener's token"

                    match peer with
                    | None -> failtest "the system did not say who the peer is"
                    | Some peer ->
                        Expect.equal peer.Account (currentAccount ()) "the same account"
                        Expect.equal peer.Process (Some Environment.ProcessId) "this process"
                finally
                    try
                        Directory.Delete(dir, true)
                    with _ ->
                        ()
            }

            testTask "a named pipe's client is this process's account (Windows)" {
                if OperatingSystem.IsWindows() then
                    let name = "fxpc-" + Guid.NewGuid().ToString("N").Substring(0, 10)
                    let! peer, _ = observe (NamedPipe name)

                    match peer with
                    | None -> failtest "the system did not say who the client is"
                    | Some peer ->
                        Expect.equal peer.Account (currentAccount ()) "the same account"
                        Expect.equal peer.Process (Some Environment.ProcessId) "this process"
                else
                    skiptest "named pipes are Windows only"
            }

            test "the peer of a raw Unix socket" {
                let dir = ownDirectory ()

                try
                    let endpoint = UnixDomainSocketEndPoint(Path.Combine(dir, "raw.sock"))

                    use listener =
                        new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

                    listener.Bind endpoint
                    listener.Listen 1

                    use client =
                        new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

                    let connecting = client.ConnectAsync endpoint
                    use server = listener.Accept()
                    connecting.Wait(TimeSpan.FromSeconds 30.0) |> ignore

                    if OperatingSystem.IsWindows() then
                        let pid = Array.zeroCreate<byte> 4
                        let length = server.IOControl(0x58000100, null, pid)
                        let reported = BitConverter.ToUInt32(pid, 0)

                        Expect.equal
                            (int reported)
                            Environment.ProcessId
                            $"SIO_AF_UNIX_GETPEERPID gives this process (reported {length} bytes)"

                    let peer = ofUnixSocket server
                    Expect.equal (peer |> Option.map _.Account) (Some(currentAccount ())) "the same account"
                    Expect.equal (peer |> Option.bind _.Process) (Some Environment.ProcessId) "this process"
                finally
                    try
                        Directory.Delete(dir, true)
                    with _ ->
                        ()
            }

            test "the client of a raw named pipe (Windows)" {
                if OperatingSystem.IsWindows() then
                    let name = "fxpc-" + Guid.NewGuid().ToString("N").Substring(0, 10)

                    use server =
                        new NamedPipeServerStream(
                            name,
                            PipeDirection.InOut,
                            1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous
                        )

                    use client =
                        new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)

                    let waiting = server.WaitForConnectionAsync()
                    client.Connect(30000)
                    waiting.Wait(TimeSpan.FromSeconds 30.0) |> ignore
                    let peer = ofNamedPipe server
                    Expect.equal (peer |> Option.map _.Account) (Some(currentAccount ())) "the same account"
                    Expect.equal (peer |> Option.bind _.Process) (Some Environment.ProcessId) "this process"
                else
                    skiptest "named pipes are Windows only"
            }

            testTask "loopback TCP does not say" {
                let! peer, _ = observe (Loopback 0)
                Expect.equal peer None "no peer credentials over TCP"
            }

            test "the token file is the owner's only, and replaced whole" {
                let dir = ownDirectory ()

                try
                    let path = Path.Combine(dir, "listener.token")
                    TokenFile.write path token
                    Expect.equal (TokenFile.read path) token "read back"
                    Expect.isTrue (TokenFile.isOwnerOnly path) "owner only"

                    if not (OperatingSystem.IsWindows()) then
                        Expect.equal
                            (File.GetUnixFileMode path)
                            (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                            "mode 0600"

                    TokenFile.write path "another-token-0123456789abcdefghijkl"
                    Expect.equal (TokenFile.read path) "another-token-0123456789abcdefghijkl" "replaced"
                    Expect.isTrue (TokenFile.isOwnerOnly path) "still owner only"
                    Expect.isFalse (File.Exists(path + ".new")) "no staging file left"
                finally
                    Directory.Delete(dir, true)
            }

            test "a file others can read is not owner-only" {
                let dir = ownDirectory ()

                try
                    let path = Path.Combine(dir, "open.token")
                    File.WriteAllText(path, token)

                    if OperatingSystem.IsWindows() then
                        Expect.isFalse (TokenFile.isOwnerOnly path) "inherited entries"
                    else
                        File.SetUnixFileMode(
                            path,
                            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead
                        )

                        Expect.isFalse (TokenFile.isOwnerOnly path) "group-readable"
                finally
                    Directory.Delete(dir, true)
            }
        ]
