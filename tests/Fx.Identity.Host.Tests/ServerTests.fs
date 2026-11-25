/// Identity in the client protocol's `initialize` over a JSON-RPC connection (in process, as in
/// embedded mode): sign in, get a token, reconnect with it, be refused, be cut off by a revocation.
module Fx.Identity.Host.Tests.ServerTests

open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Rpc
open Fx.Identity
open Fx.Identity.Host
open Fx.Identity.Host.Tests.Support

/// Who is calling, as a handler sees it: the attribution of the caller's inputs.
let private whoami: Method<unit, string, IdentityFailure> =
    Method.define "test/whoami" Codec.unit Codec.string
    |> Method.withErrors Codecs.identityFailure

/// Serves one connection as the host server would: a `ClientConnection` per connection, its sign-in in
/// `initialize`, and handlers that ask it who is calling (owners only).
let private connect (f: Fakes) (auth: Authenticator) (facts: ConnectionFacts) (signIn: SignIn option) =
    let limits = ConnectionLimits.frames ConnectionLimits.defaults
    let clientSide, serverSide = InProcess.pair 65536 limits limits
    let connection = ClientConnection()

    let handlers =
        [
            Handler.request whoami (fun _ () ->
                Eff.ret (
                    IdentityServer.caller auth Role.Owner connection
                    |> Result.map (fun by -> by.Principal + " via " + by.Via)
                ))
        ]

    let server =
        Service.create { Name = "host"; Version = "0" } handlers
        |> IdentityServer.withIdentity auth facts connection

    let client = Service.create { Name = "tui"; Version = "0" } []
    let members = signIn |> Option.map IdentityClient.members |> Option.defaultValue []
    let accepting = Eff.run f.Env (Rpc.accept server serverSide)
    let connected = wait (Eff.run f.Env (Rpc.connect client members clientSide))
    let accepted = wait accepting
    connected, accepted, connection

let private auth (f: Fakes) =
    Authenticator.create [ contributed f.Env.Clock names ] host (SessionTokens())

[<Tests>]
let tests =
    testList
        "Identity in initialize"
        [
            test "a client signs in, learns who it is, and its calls are attributed to it" {
                let f = fakes ()
                let auth = auth f

                let connected, _, connection =
                    connect f auth ConnectionFacts.inProcess (Some(named "alice"))

                let peer = ok connected
                let session = ok (IdentityClient.session peer)
                Expect.equal session.Principal.Id (PrincipalId "user:names:alice") "who"
                Expect.equal session.Roles [ Role.Owner ] "roles"
                Expect.isSome session.Token "a token"
                Expect.equal (connection.Session |> Option.map _.Id) (Some session.Id) "the server's side"

                let caller = runIn f.Env (Rpc.call peer whoami ())
                Expect.equal caller (Ok("user:names:alice via " + session.Client)) "attributed"
                runIn f.Env (Rpc.close peer) |> ignore
            }

            test "a client reconnects with its token: same session and client, no new token" {
                let f = fakes ()
                let auth = auth f
                let first, _, _ = connect f auth ConnectionFacts.inProcess (Some(named "alice"))
                let first = ok first
                let session = ok (IdentityClient.session first)
                runIn f.Env (Rpc.close first) |> ignore

                let again, _, _ =
                    connect f auth ConnectionFacts.inProcess (Some(SignIn.Token(session.Token |> Option.get)))

                let again = ok again
                let resumed = ok (IdentityClient.session again)
                Expect.equal resumed { session with Token = None } "resumed"

                Expect.equal
                    (runIn f.Env (Rpc.call again whoami ()))
                    (Ok("user:names:alice via " + session.Client))
                    "the same client"

                runIn f.Env (Rpc.close again) |> ignore
            }

            test "the listener's verified session needs no identity member" {
                let f = fakes ()
                let auth = auth f
                let first, _, _ = connect f auth ConnectionFacts.inProcess (Some(named "alice"))
                let session = ok (IdentityClient.session (ok first))

                let viaListener, _, _ =
                    connect
                        f
                        auth
                        {
                            Connection = Connection.ofKind ConnectionKind.Loopback
                            Session = Some session.Id
                        }
                        None

                Expect.equal (ok (IdentityClient.session (ok viaListener))).Id session.Id "resumed"
            }

            test "a refused identity fails the handshake with the reason" {
                let f = fakes ()
                let auth = auth f

                let refused (signIn: SignIn option) =
                    let connected, _, connection = connect f auth ConnectionFacts.inProcess signIn
                    Expect.isNone connection.Session "nobody signed in"

                    match connected with
                    | Ok _ -> failtest "connected"
                    | Error e -> IdentityClient.failure e

                Expect.equal (refused None) (Some IdentityFailure.Missing) "no identity"
                Expect.equal (refused (Some(named "carol"))) (Some(IdentityFailure.NoRole "user:names:carol")) "no role"

                Expect.equal
                    (refused (Some(SignIn.Provider("github", Json.Null))))
                    (Some(IdentityFailure.UnknownProvider "github"))
                    "unknown provider"

                Expect.equal (refused (Some(SignIn.Token "nope"))) (Some IdentityFailure.TokenRejected) "a bad token"
            }

            test "a malformed identity member is invalid" {
                let f = fakes ()
                let auth = auth f
                let limits = ConnectionLimits.frames ConnectionLimits.defaults
                let clientSide, serverSide = InProcess.pair 65536 limits limits
                let connection = ClientConnection()

                let server =
                    Service.create { Name = "host"; Version = "0" } []
                    |> IdentityServer.withIdentity auth ConnectionFacts.inProcess connection

                let accepting = Eff.run f.Env (Rpc.accept server serverSide)

                let connected =
                    wait (
                        Eff.run
                            f.Env
                            (Rpc.connect
                                (Service.create { Name = "tui"; Version = "0" } [])
                                [ Members.Identity, Json.Object [ "kind", Json.String "password" ] ]
                                clientSide)
                    )

                match connected with
                | Error e ->
                    match IdentityClient.failure e with
                    | Some(IdentityFailure.Invalid(issue :: _)) ->
                        Expect.equal (issue.Path |> List.tryHead) (Some(PathSegment.Key Members.Identity)) "where"
                    | other -> failtest (string other)
                | Ok _ -> failtest "connected"

                // The server stays in the handshake for a retry until it times out; end it.
                clientSide.Abort()
                wait accepting |> ignore
            }

            test "a viewer is refused what needs an owner" {
                let f = fakes ()
                let auth = auth f
                let connected, _, _ = connect f auth ConnectionFacts.inProcess (Some(named "bob"))
                let peer = ok connected

                Expect.equal
                    (runIn f.Env (Rpc.call peer whoami ()))
                    (Error(CallError.Failed(IdentityFailure.NoRole "user:names:bob")))
                    "bob views"

                runIn f.Env (Rpc.close peer) |> ignore
            }

            test "revoking the token refuses the connection's next call" {
                let f = fakes ()
                let auth = auth f

                let connected, _, connection =
                    connect f auth ConnectionFacts.inProcess (Some(named "alice"))

                let peer = ok connected
                Expect.isOk (runIn f.Env (Rpc.call peer whoami ())) "before"
                let session = connection.Session |> Option.get
                let revocation = auth.Tokens.Revocation session.Id
                runIn f.Env (SessionTokens.revoke auth.Tokens session.Id) |> ignore
                Expect.isTrue revocation.IsCancellationRequested "the host server ends the connection"

                Expect.equal
                    (runIn f.Env (Rpc.call peer whoami ()))
                    (Error(CallError.Failed IdentityFailure.TokenRejected))
                    "after"

                runIn f.Env (Rpc.close peer) |> ignore
            }
        ]
