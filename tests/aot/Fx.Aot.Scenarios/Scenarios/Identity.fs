/// Identity (T122, docs/modules/identity.md): principal ids and the codecs of principals and of
/// `initialize`'s identity members, `fx.identity.local-os-user`'s rules, session tokens on the fake
/// clock, sign-in over an in-process JSON-RPC connection, and the peer credentials of the local
/// listener (a Unix socket everywhere, a named pipe on Windows) with the owner-only token file.
module Fx.Aot.Scenarios.Identity

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Variants
open Fx.Variants.Fakes
open Fx.Rpc
open Fx.Sdk
open Fx.Identity
open Fx.Identity.Host
open Fx.Transport.Http
open Fx.Transport.Http.LocalServer
open Fx.Ext.IdentityLocalOsUser

type private Env =
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

    override _.ToString() = "Env"

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromMinutes 1.0).GetAwaiter().GetResult()

let private host: HostIdentity =
    {
        Machine = "7Q2M9X4KD1"
        Account = { Id = "1000"; Name = Some "alice" }
    }

let private over (kind: ConnectionKind) (peer: string option) (secret: bool) : Credential =
    {
        Presented = Json.Null
        Connection =
            {
                Kind = kind
                PeerAccount = peer
                OwnerSecret = secret
            }
        Host = host
    }

let private opt (value: 'T option) : string =
    match value with
    | Some v -> "some " + string v
    | None -> "none"

let private outcome (result: Result<'T, 'E>) : string =
    match result with
    | Ok value -> "ok " + string value
    | Error e -> "error " + string e

/// Ids, principals, codecs and the provider's rules: pure.
let principals =
    Scenario.lines "identity-principals" (fun () ->
        [
            for kind, provider, subject in
                [
                    PrincipalKind.User, "local-os-user", "7Q2M9X4KD1/1000"
                    PrincipalKind.Device, "device-pairing", "k:1"
                    PrincipalKind.Service, "a.b-c", "x"
                    PrincipalKind.User, "Bad", "x"
                    PrincipalKind.User, "ok", "has space"
                ] do
                $"create {kind} {provider} {Show.escape subject}: {outcome (PrincipalId.create kind provider subject)}"

            for text in
                [
                    "user:local-os-user:m/1"
                    "user:alice"
                    "system"
                    "service:ci:r:1"
                    "device::x"
                ] do
                $"parse {text}: {outcome (PrincipalId.parse text)}; kind {opt (PrincipalId.kindOf text)}; person {PrincipalId.isPerson text}"

            let alice =
                Principal.create
                    PrincipalKind.User
                    "local-os-user"
                    "7Q2M9X4KD1/1000"
                    "alice"
                    (Map.ofList [ "account", [ "1000" ]; "proof", [ "in-process" ] ])
                |> Result.defaultWith failwith

            Codec.encodeText Codecs.principal alice
            outcome (Codec.decodeText Codecs.principal (Codec.encodeText Codecs.principal alice))

            Codec.encodeText Codecs.signIn (SignIn.Provider("local-os-user", Json.Null))
            Codec.encodeText Codecs.signIn (SignIn.Token "tok")

            Codec.encodeText
                Codecs.identityFailure
                (IdentityFailure.Refused("local-os-user", AuthError.Rejected "another account"))

            Codec.encodeText
                Codecs.sessionInfo
                {
                    Id = "01JDB8Z3M4X2Q9V7K5N6R8T0WA"
                    Token = None
                    Principal = alice
                    Roles = [ Role.Owner ]
                    Client = "tui/1"
                    Expires = 1L
                }

            $"roles allow: {Role.allows Role.Collaborator [ Role.Viewer ]} {Role.allows Role.Viewer [ Role.Owner ]}"

            for name, credential in
                [
                    "in-process", over ConnectionKind.InProcess None false
                    "stdio", over ConnectionKind.Stdio None false
                    "unix peer", over ConnectionKind.UnixSocket (Some "1000") false
                    "unix stranger", over ConnectionKind.UnixSocket (Some "0") true
                    "loopback token", over ConnectionKind.Loopback None true
                    "loopback bare", over ConnectionKind.Loopback None false
                    "remote", over ConnectionKind.Remote None true
                ] do
                let verified = LocalOsUser.verify credential

                let shown =
                    match verified with
                    | Ok p ->
                        let proof = p.Claims["proof"] |> String.concat ","
                        let roles = LocalOsUser.roles p |> List.map string |> String.concat ","
                        $"ok {p.Id} proof {proof} roles {roles}"
                    | Error e -> "error " + string e

                $"local-os-user {name}: {shown}"
        ])

/// Session tokens on the fake clock: issue, verify, the listener's lookup, expiry, revocation.
let tokens =
    Scenario.task "identity-tokens" (fun () ->
        Task.Run<string list>(
            Func<string list>(fun () ->
                let clock = FakeClock.Create()

                let env =
                    {
                        Clock = clock.Clock
                        Ids = FakeIds(5UL, clock.Clock).Ids
                        Variants = FakeVariants.create IdentitySlots.all [ "identity.sessionTokenTtl", Json.int 60 ]
                        Scope = Scope.root
                    }

                let run (effect: Eff<Env, 'T>) : 'T = wait (Eff.run env effect)
                let tokens = SessionTokens()

                let alice =
                    Principal.create PrincipalKind.User "names" "alice" "alice" Map.empty
                    |> Result.defaultWith failwith

                let token, session = run (SessionTokens.issue tokens alice [ Role.Owner ] "tui/1")
                let lookup = SessionTokens.lookup tokens clock.Clock
                let out = Collections.Generic.List<string>()
                out.Add $"token: {token.Length} chars, hash {TokenTable.hash token}"
                out.Add $"session: {session}"
                out.Add $"lifetime: {session.Expires - session.Issued} us"
                out.Add $"verify: {outcome (run (SessionTokens.verify tokens token))}"
                let wrong = opt (lookup (token + "x"))
                out.Add $"lookup: {lookup token = Some session.Id}, wrong {wrong}"
                clock.Advance(TimeSpan.FromMinutes 59.0)
                out.Add $"after 59 min: {outcome (run (SessionTokens.verify tokens token) |> Result.map _.Id)}"
                clock.Advance(TimeSpan.FromMinutes 1.0)
                out.Add $"after 60 min: {outcome (run (SessionTokens.verify tokens token))}"
                let second, s2 = run (SessionTokens.issue tokens alice [ Role.Owner ] "web/1")
                out.Add $"second: {second <> token}, table {tokens.Table}"
                let revocation = tokens.Revocation s2.Id

                out.Add
                    $"revoke: {run (SessionTokens.revoke tokens s2.Id)}, cancelled {revocation.IsCancellationRequested}"

                out.Add $"verify revoked: {outcome (run (SessionTokens.verify tokens second))}"
                out.Add $"lookup revoked: {opt (lookup second)}"
                List.ofSeq out)
        ))

/// Sign-in in `initialize` over an in-process connection, a reconnect with the token, refusals.
let initialize =
    Scenario.task "identity-initialize" (fun () ->
        Task.Run<string list>(
            Func<string list>(fun () ->
                let clock = FakeClock.Create()

                let env =
                    {
                        Clock = clock.Clock
                        Ids = FakeIds(9UL, clock.Clock).Ids
                        Variants = FakeVariants.create IdentitySlots.all []
                        Scope = Scope.root
                    }

                let provider: Contributed<IdentityProvider> =
                    {
                        Extension = LocalOsUser.id
                        Type = IdentityProviders.contributionType.Name
                        Id = LocalOsUser.ProviderId
                        Value = LocalOsUser.provider
                        Env = BasicExtensionEnv.forManifest clock.Clock LocalOsUser.manifest
                    }

                let auth = Authenticator.create [ provider ] host (SessionTokens())

                let connect (facts: ConnectionFacts) (members: (string * Json) list) : string =
                    let limits = ConnectionLimits.frames ConnectionLimits.defaults
                    let clientSide, serverSide = InProcess.pair 65536 limits limits
                    let connection = ClientConnection()

                    let server =
                        Service.create { Name = "host"; Version = "0" } []
                        |> IdentityServer.withIdentity auth facts connection

                    let accepting = Eff.run env (Rpc.accept server serverSide)

                    let connected =
                        wait (
                            Eff.run
                                env
                                (Rpc.connect (Service.create { Name = "tui"; Version = "0" } []) members clientSide)
                        )

                    match connected with
                    | Ok peer ->
                        wait accepting |> ignore

                        let shown =
                            match IdentityClient.session peer with
                            | Ok session ->
                                let caller = IdentityServer.caller auth Role.Owner connection |> Result.map string

                                let roles = session.Roles |> List.map string |> String.concat ","
                                $"session {session.Principal.Id} roles {roles} token {session.Token.IsSome} caller {outcome caller}"
                            | Error issues -> "no session " + Issue.renderAtMost 10 issues

                        wait (Eff.run env (Rpc.close peer)) |> ignore
                        shown
                    | Error e ->
                        clientSide.Abort()
                        wait accepting |> ignore
                        $"refused {opt (IdentityClient.failure e)}"

                let signIn =
                    IdentityClient.members (SignIn.Provider(LocalOsUser.ProviderId, Json.Null))

                let first = connect ConnectionFacts.inProcess signIn

                let sessions =
                    auth.Tokens.Table.ByHash |> Map.toList |> List.map (fun (_, e) -> e.Session.Id)

                [
                    "in process: " + first
                    $"sessions: {sessions.Length}"
                    "loopback without proof: "
                    + connect (ConnectionFacts.ofConnection (Connection.ofKind ConnectionKind.Loopback)) signIn
                    "no identity: " + connect ConnectionFacts.inProcess []
                    "unknown provider: "
                    + connect
                        ConnectionFacts.inProcess
                        (IdentityClient.members (SignIn.Provider("github", Json.Null)))
                    "bad token: "
                    + connect ConnectionFacts.inProcess (IdentityClient.members (SignIn.Token "nope"))
                    "listener session: "
                    + connect
                        {
                            Connection = Connection.ofKind ConnectionKind.Loopback
                            Session = List.tryHead sessions
                        }
                        []
                ])
        ))

let private listenerToken = "identity-scenario-token-0123456789abcdef"

/// What a local listener sees of its one client: the peer's account against this process's.
let private peerOf (listener: Listener) : Task<string list> =
    task {
        let seen =
            TaskCompletionSource<PeerCredentials.PeerAccount option>(TaskCreationOptions.RunContinuationsAsynchronously)

        let endpoint =
            Endpoint.webSocket "/peer" [] (fun request socket ->
                seen.TrySetResult request.Peer |> ignore
                socket.CloseAsync(Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", CancellationToken.None))

        let! server =
            LocalServer.start (LocalServer.options listener (LocalAccess.policy listenerToken) [ endpoint ])

        try
            let! connected =
                LocalClient.connectWebSocket listener "/peer" listenerToken [] CancellationToken.None

            match connected with
            | Error e -> return [ "connect failed: " + string e ]
            | Ok socket ->
                try
                    let! peer = seen.Task.WaitAsync(TimeSpan.FromSeconds 30.0)
                    let account = PeerCredentials.currentAccount ()

                    return
                        [
                            match peer with
                            | Some peer ->
                                $"peer is this account: {peer.Account = account}"
                                $"peer is this process: {peer.Process = Some Environment.ProcessId}"
                            | None -> "no peer credentials"
                        ]
                finally
                    socket.Dispose()
        finally
            (server :> IAsyncDisposable).DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds 30.0)
            |> ignore
    }

let private ownDirectory () : string =
    let dir = Path.Combine(Path.GetTempPath(), "fxid-" + Kestrel.Support.uniqueName "")

    if OperatingSystem.IsWindows() then
        Directory.CreateDirectory dir |> ignore
    else
        Directory.CreateDirectory(dir, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        |> ignore

    dir

/// The peer of a Unix socket, and the owner-only token file.
let peerCredentials =
    Scenario.task "identity-peer-credentials" (fun () ->
        task {
            let dir = ownDirectory ()

            try
                let account = PeerCredentials.currentAccount ()

                let form =
                    if OperatingSystem.IsWindows() then
                        account.StartsWith("S-1-", StringComparison.Ordinal)
                    else
                        account |> Seq.forall Char.IsAsciiDigit

                let! unix = peerOf (UnixSocket(Path.Combine(dir, "id.sock")))
                let file = Path.Combine(dir, "listener.token")
                TokenFile.write file listenerToken

                return
                    [
                        $"this account has the platform's form: {form}"
                        yield! unix |> List.map (fun line -> "unix socket: " + line)
                        $"token file owner-only: {TokenFile.isOwnerOnly file}, reads back: {TokenFile.read file = listenerToken}"
                    ]
            finally
                try
                    Directory.Delete(dir, true)
                with _ ->
                    ()
        })

/// The client of a named pipe (Windows).
let namedPipe =
    Scenario.task "identity-named-pipe" (fun () -> peerOf (NamedPipe(Kestrel.Support.uniqueName "fxid-")))
    |> Scenario.onlyOn [ Fx.Aot.Scenarios.Os.Windows ]

let scenarios: Scenario list =
    [ principals; tokens; initialize; peerCredentials; namedPipe ]
