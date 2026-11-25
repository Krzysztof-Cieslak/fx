namespace Fx.Host

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Rpc
open Fx.Identity
open Fx.Identity.Host
open Fx.Transport.Http
open Fx.Transport.Http.LocalServer

/// Where a serving host listens (`<data>/listener.json`), so local clients find it: the kind of
/// listener and its address (a socket path, a pipe name or a loopback port). The token is in its own
/// owner-only file (`HostPaths.ListenerToken`).
type ListenerInfo =
    {
        Kind: string
        Address: string
    }

    override this.ToString() = $"{this.Kind} {this.Address}"

/// The local listener of `fx serve` (spec §17, §18): the client protocol on a WebSocket at `/rpc`
/// behind the listener's token (or a session token the host issued), `Origin` and `Host` checks.
[<RequireQualifiedAccess>]
module Listening =
    /// The WebSocket's path.
    [<Literal>]
    let Path = "/rpc"

    let infoCodec: Codec<ListenerInfo> =
        Codec.record {
            let! kind = Codec.field "kind" (fun (i: ListenerInfo) -> i.Kind) Codec.string

            and! address =
                Codec.field "address" (fun (i: ListenerInfo) -> i.Address) Codec.string

            return { Kind = kind; Address = address }
        }

    let info (listener: Listener) : ListenerInfo =
        match listener with
        | Loopback port ->
            {
                Kind = "loopback"
                Address = string port
            }
        | UnixSocket path -> { Kind = "unix-socket"; Address = path }
        | NamedPipe name -> { Kind = "named-pipe"; Address = name }

    let listener (info: ListenerInfo) : Result<Listener, string> =
        match info.Kind with
        | "loopback" ->
            match
                Int32.TryParse(
                    info.Address,
                    Globalization.NumberStyles.None,
                    Globalization.CultureInfo.InvariantCulture
                )
            with
            | true, port when port > 0 && port < 65536 -> Ok(Loopback port)
            | _ -> Error $"not a port: {info.Address}"
        | "unix-socket" -> Ok(UnixSocket info.Address)
        | "named-pipe" -> Ok(NamedPipe info.Address)
        | other -> Error $"unknown listener kind {other}"

    /// What the host establishes about a connection to the listener (identity.md): the transport's
    /// kind, the peer's OS account where the system reports it, and whether it presented the
    /// listener's token; or the session token it presented as its bearer.
    let facts (listener: Listener) (request: Request) : ConnectionFacts =
        let kind =
            match listener with
            | Loopback _ -> ConnectionKind.Loopback
            | UnixSocket _ -> ConnectionKind.UnixSocket
            | NamedPipe _ -> ConnectionKind.NamedPipe

        {
            Connection =
                {
                    Kind = kind
                    PeerAccount = request.Peer |> Option.map _.Account
                    OwnerSecret = request.Access.Bearer = LocalAccess.Bearer.Listener
                }
            Session =
                match request.Access.Bearer with
                | LocalAccess.Bearer.Session id -> Some id
                | LocalAccess.Bearer.Listener -> None
        }

    /// The listener a host uses unless told otherwise: a named pipe of its own on Windows, its Unix
    /// socket elsewhere (peer credentials prove the client's account on both).
    let preferred (paths: HostPaths) (machine: string) : Listener =
        if OperatingSystem.IsWindows() then
            NamedPipe(Product.Name + "-" + machine)
        else
            UnixSocket paths.Socket

    /// A listener token: 256 random bits, base64url.
    let newToken (ids: Ids) : string =
        Convert.ToBase64String(ids.RandomBytes 32).TrimEnd('=').Replace('+', '-').Replace('/', '_')

/// A host serving the client protocol on its local listener.
type Served =
    {
        Listener: Listener
        Server: Server
    }

    override this.ToString() = $"serving on {this.Listener}"

[<RequireQualifiedAccess>]
module Serve =
    /// Serves the client protocol on a listener (port 0 picks one): writes the listener's token into
    /// its owner-only file and `listener.json` for clients, then accepts connections until stopped.
    let start (host: Host) (listener: Listener) : Task<Result<Served, string>> =
        task {
            let paths = host.Options.Paths
            let token = Listening.newToken host.Options.Ids
            let env = host.Env

            // A socket file left by a host that is gone (this one holds the data directory's lock).
            match listener with
            | UnixSocket path ->
                match! Eff.run env (Fs.delete path) with
                | Ok()
                | Error(FsError.NotFound _) -> ()
                | Error e -> invalidOp $"the socket {path} could not be removed: {e}"
            | Loopback _
            | NamedPipe _ -> ()

            TokenFile.write paths.ListenerToken token

            let policy =
                { LocalAccess.policy token with
                    Sessions = SessionTokens.lookup host.Authenticator.Tokens host.Options.Clock
                }

            let endpoint =
                Endpoint.webSocket Listening.Path [ Protocol.webSocketSubprotocol ] (fun request socket ->
                    task {
                        let transport =
                            WebSocketTransport.create (ConnectionLimits.frames ConnectionLimits.defaults) socket

                        do! ClientServer.serve host (Listening.facts listener request) transport request.Stopping
                    })

            let! server = LocalServer.start (LocalServer.options listener policy [ endpoint ])

            let actual =
                match listener with
                | Loopback 0 ->
                    server.Addresses
                    |> List.tryPick (fun address ->
                        match Uri.TryCreate(address, UriKind.Absolute) with
                        | true, NonNull uri -> Some(Loopback uri.Port)
                        | _ -> None)
                    |> Option.defaultValue listener
                | other -> other

            let text =
                Json.serialize (Codec.encode Listening.infoCodec (Listening.info actual)) + "\n"

            match!
                Eff.run env (Fx.Components.InstallFiles.replaceFile paths.ListenerInfo (Encoding.UTF8.GetBytes text))
            with
            | Error e ->
                do! server.StopAsync()
                return Error $"{paths.ListenerInfo} could not be written: {e}"
            | Ok() -> return Ok { Listener = actual; Server = server }
        }

    let stop (served: Served) : Task = served.Server.StopAsync()

/// Clients of a host (a TUI, another process, a test): in this process (embedded mode, spec §3) or
/// over the local listener, signed in as the OS user through `local-os-user`.
[<RequireQualifiedAccess>]
module HostClient =
    /// The `initialize` members of a client of the OS user: its sign-in and what it can answer.
    let members (answers: string list) : (string * Json) list =
        IdentityClient.members (SignIn.Provider(Fx.Ext.IdentityLocalOsUser.LocalOsUser.ProviderId, Json.Null))
        @ [ ClientProtocol.AnswersMember, Json.Array(answers |> List.map Json.String) ]

    /// A client in the host's process over the in-process transport; the host serves it until the
    /// connection ends or the host stops.
    let embedded<'C when 'C :> IClock and 'C :> IScopeWith<'C>>
        (host: Host)
        (client: Service<'C>)
        (answers: string list)
        : Eff<'C, Result<Peer, ConnectError>> =
        let frames = ConnectionLimits.frames ConnectionLimits.defaults
        let clientEnd, serverEnd = InProcess.pair (1 <<< 20) frames frames

        let _served =
            ClientServer.serve host ConnectionFacts.inProcess serverEnd host.Env.Scope.Cancellation

        Rpc.connect client (members answers) clientEnd

    /// Where the host serving a data directory listens, and its token (from the files it wrote).
    let locate<'R when 'R :> IFsRead and 'R :> IScope> (paths: HostPaths) : Eff<'R, Result<Listener * string, string>> =
        effResult {
            let! text =
                Fs.readText paths.ListenerInfo
                |> EffResult.mapError (fun e -> $"no host is serving here ({e})")

            let! info =
                Codec.decodeText Listening.infoCodec text
                |> Result.mapError (fun issues -> $"{paths.ListenerInfo}: " + Issue.render issues)

            let! listener = Listening.listener info
            let! token = Fs.readText paths.ListenerToken |> EffResult.mapError string
            return listener, token.Trim()
        }

    /// A client over the local listener, presenting its token (or a session token as the bearer).
    let connect<'C when 'C :> IClock and 'C :> IScopeWith<'C>>
        (listener: Listener)
        (bearer: string)
        (client: Service<'C>)
        (members: (string * Json) list)
        : Eff<'C, Result<Peer, string>> =
        fun env ->
            task {
                match!
                    LocalClient.connectWebSocket
                        listener
                        Listening.Path
                        bearer
                        [ Protocol.webSocketSubprotocol ]
                        env.Scope.Cancellation
                with
                | Error e -> return Error(string e)
                | Ok socket ->
                    let transport =
                        WebSocketTransport.create (ConnectionLimits.frames ConnectionLimits.defaults) socket

                    match! Eff.run env (Rpc.connect client members transport) with
                    | Ok peer -> return Ok peer
                    | Error e ->
                        socket.Dispose()
                        return Error(string e)
            }
