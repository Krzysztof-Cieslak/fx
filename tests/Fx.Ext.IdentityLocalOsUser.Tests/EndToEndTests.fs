/// Signing in as this computer's account, end to end (spec §3, §17, §18): in process (embedded mode),
/// and over the local listener on a Unix socket (peer credentials, every OS), a named pipe (the client
/// process's token, Windows) and loopback TCP (the listener's token from its owner-only file); then
/// reconnecting with the session token as the listener's bearer.
module Fx.Ext.IdentityLocalOsUser.Tests.EndToEndTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Variants
open Fx.Variants.Fakes
open Fx.Rpc
open Fx.Sdk
open Fx.Identity
open Fx.Identity.Host
open Fx.Transport.Http
open Fx.Transport.Http.LocalServer
open Fx.Ext.IdentityLocalOsUser

type Env =
    {
        Clock: Clock
        Ids: Ids
        Variants: Variants
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IIds with
        member env.Ids = env.Ids

    interface IVariants with
        member env.Variants = env.Variants

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

let private env: Env =
    {
        Clock = SystemClock.clock
        Ids = SystemIds.create SystemClock.clock
        Variants = FakeVariants.create IdentitySlots.all []
        Scope = Scope.root
    }

/// This host: a machine id, and the account this process runs as.
let private host: HostIdentity =
    {
        Machine = "TESTHOST"
        Account =
            {
                Id = PeerCredentials.currentAccount ()
                Name = Some "me"
            }
    }

let private me = "user:local-os-user:TESTHOST/" + host.Account.Id

let private authenticator () : Authenticator =
    let provider: Contributed<IdentityProvider> =
        {
            Extension = LocalOsUser.id
            Type = IdentityProviders.contributionType.Name
            Id = LocalOsUser.ProviderId
            Value = LocalOsUser.provider
            Env = BasicExtensionEnv.forManifest SystemClock.clock LocalOsUser.manifest
        }

    Authenticator.create [ provider ] host (SessionTokens())

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

let private signIn =
    IdentityClient.members (SignIn.Provider(LocalOsUser.ProviderId, Json.Null))

let private listenerToken = "listener-token-0123456789abcdefghijklmnop"

/// The connection facts of a request on the local listener: what the composition root (T129) does.
let private factsOf (kind: ConnectionKind) (request: Request) : ConnectionFacts =
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

/// A host serving the client protocol on a local listener, with the session tokens as bearers too.
let private serve (auth: Authenticator) (listener: Listener) (kind: ConnectionKind) : Task<Server * Listener> =
    task {
        let endpoint =
            Endpoint.webSocket "/rpc" [ Protocol.webSocketSubprotocol ] (fun request socket ->
                task {
                    let facts = factsOf kind request
                    let connection = ClientConnection()

                    let transport =
                        WebSocketTransport.create (ConnectionLimits.frames ConnectionLimits.defaults) socket

                    let service =
                        Service.create { Name = "host"; Version = "0" } []
                        |> IdentityServer.withIdentity auth facts connection

                    let scoped =
                        { env with
                            Scope = Scope.ofCancellation request.Stopping
                        }

                    match! Eff.run scoped (Rpc.accept service transport) with
                    | Ok peer ->
                        let! _ = peer.Completion
                        ()
                    | Error _ -> ()
                })

        let policy =
            { LocalAccess.policy listenerToken with
                Sessions = SessionTokens.lookup auth.Tokens SystemClock.clock
            }

        let! server = LocalServer.start (LocalServer.options listener policy [ endpoint ])

        let target =
            match listener with
            | Loopback _ ->
                let address =
                    server.Addresses
                    |> List.find (fun a -> a.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))

                Loopback(Uri(address).Port)
            | other -> other

        return server, target
    }

/// How a connection attempt ended.
type private Outcome =
    | Connected of SessionInfo
    /// The handshake refused the client's identity.
    | Refused of IdentityFailure option
    /// The listener refused the bearer token (HTTP status).
    | Rejected of int

    override this.ToString() =
        match this with
        | Connected session -> "connected " + string session
        | Refused failure -> "refused " + string failure
        | Rejected status -> "rejected " + string status

/// Connects a client with a bearer token and `initialize` members.
let private connect (target: Listener) (bearer: string) (members: (string * Json) list) : Task<Outcome> =
    task {
        let! socket =
            LocalClient.connectWebSocket target "/rpc" bearer [ Protocol.webSocketSubprotocol ] CancellationToken.None

        match socket with
        | Error(LocalClient.Rejected status) -> return Rejected status
        | Error e -> return failtest (string e)
        | Ok socket ->
            let transport =
                WebSocketTransport.create (ConnectionLimits.frames ConnectionLimits.defaults) socket

            let client = Service.create { Name = "tui"; Version = "0" } []
            let! connected = Eff.run env (Rpc.connect client members transport)

            match connected with
            | Ok peer ->
                let session = IdentityClient.session peer
                let! _ = Eff.run env (Rpc.close peer)

                return
                    match session with
                    | Ok session -> Connected session
                    | Error issues -> failtest (Issue.renderAtMost 10 issues)
            | Error e ->
                transport.Abort()
                return Refused(IdentityClient.failure e)
    }

let private revoke (tokens: SessionTokens) (id: string) =
    wait (Eff.run env (SessionTokens.revoke tokens id)) |> ignore

let private stop (server: Server) =
    (server :> IAsyncDisposable).DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds 30.0)
    |> ignore

/// Signs in over a listener, then reconnects with the session token as the bearer and no identity.
let private overListener (listener: Listener) (kind: ConnectionKind) (proof: string) : Task =
    task {
        let auth = authenticator ()
        let! server, target = serve auth listener kind

        try
            let! first = connect target listenerToken signIn

            match first with
            | Connected session ->
                Expect.equal session.Principal.Id.Value me "this computer's account"
                Expect.equal session.Principal.Claims["proof"] [ proof ] "the proof"
                Expect.equal session.Roles [ Role.Owner ] "owns the host"
                let token = session.Token |> Option.defaultWith (fun () -> failtest "no token")

                let! again = connect target token []
                Expect.equal again (Connected { session with Token = None }) "resumed with the bearer"

                revoke auth.Tokens session.Id
                let! revoked = connect target token []
                Expect.equal revoked (Rejected 401) "a revoked token is not a bearer any more"
            | other -> failtest (string other)
        finally
            stop server
    }

let private ownDirectory () : string =
    let dir =
        Path.Combine(Path.GetTempPath(), "fxid-" + Guid.NewGuid().ToString("N").Substring(0, 10))

    if OperatingSystem.IsWindows() then
        Directory.CreateDirectory dir |> ignore
    else
        Directory.CreateDirectory(dir, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        |> ignore

    dir

[<Tests>]
let tests =
    testList
        "End to end"
        [
            test "in process (embedded mode)" {
                let auth = authenticator ()
                let limits = ConnectionLimits.frames ConnectionLimits.defaults
                let clientSide, serverSide = InProcess.pair 65536 limits limits
                let connection = ClientConnection()

                let server =
                    Service.create { Name = "host"; Version = "0" } []
                    |> IdentityServer.withIdentity auth ConnectionFacts.inProcess connection

                let accepting = Eff.run env (Rpc.accept server serverSide)

                let peer =
                    wait (
                        Eff.run env (Rpc.connect (Service.create { Name = "tui"; Version = "0" } []) signIn clientSide)
                    )

                wait accepting |> ignore

                match peer with
                | Error e -> failtest (string e)
                | Ok peer ->
                    match IdentityClient.session peer with
                    | Error issues -> failtest (Issue.renderAtMost 10 issues)
                    | Ok session ->
                        Expect.equal session.Principal.Id.Value me "this computer's account"
                        Expect.equal session.Principal.Claims["proof"] [ "in-process" ] "in process"

                        Expect.equal
                            (IdentityServer.caller auth Role.Owner connection |> Result.map _.Principal)
                            (Ok me)
                            "its inputs are attributed to it"

                    wait (Eff.run env (Rpc.close peer)) |> ignore
            }

            testTask "over a Unix socket: the peer's account" {
                let dir = ownDirectory ()

                try
                    do!
                        overListener
                            (UnixSocket(Path.Combine(dir, "host.sock")))
                            ConnectionKind.UnixSocket
                            "peer-credentials"
                finally
                    try
                        Directory.Delete(dir, true)
                    with _ ->
                        ()
            }

            testTask "over a named pipe: the client process's account (Windows)" {
                if OperatingSystem.IsWindows() then
                    let name = "fxid-" + Guid.NewGuid().ToString("N").Substring(0, 10)
                    do! overListener (NamedPipe name) ConnectionKind.NamedPipe "peer-credentials"
                else
                    skiptest "named pipes are Windows only"
            }

            testTask "over loopback TCP: the listener's token from its owner-only file" {
                let dir = ownDirectory ()

                try
                    let file = Path.Combine(dir, "listener.token")
                    TokenFile.write file listenerToken
                    Expect.isTrue (TokenFile.isOwnerOnly file) "owner only"
                    Expect.equal (TokenFile.read file) listenerToken "what a client reads"
                    do! overListener (Loopback 0) ConnectionKind.Loopback "owner-secret"
                finally
                    Directory.Delete(dir, true)
            }

            testTask "a host running as another account refuses the client" {
                let dir = ownDirectory ()

                let auth =
                    { authenticator () with
                        Host =
                            { host with
                                Account = { Id = "424242"; Name = None }
                            }
                    }

                let! server, target =
                    serve auth (UnixSocket(Path.Combine(dir, "host.sock"))) ConnectionKind.UnixSocket

                try
                    let! outcome = connect target listenerToken signIn

                    match outcome with
                    | Refused(Some(IdentityFailure.Refused(_, AuthError.Rejected _))) -> ()
                    | other -> failtest (string other)
                finally
                    stop server

                    try
                        Directory.Delete(dir, true)
                    with _ ->
                        ()
            }
        ]
