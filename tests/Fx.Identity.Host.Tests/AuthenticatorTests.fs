/// Signing clients in (spec §3: principal auth via identity-provider extensions, then a host-issued
/// session token).
module Fx.Identity.Host.Tests.AuthenticatorTests

open Expecto
open Fx.Contracts
open Fx.Identity
open Fx.Identity.Host
open Fx.Identity.Host.Tests.Support

let private authenticator (f: Fakes) =
    Authenticator.create [ contributed f.Env.Clock names; contributed f.Env.Clock rogue ] host (SessionTokens())

let private signIn (f: Fakes) auth (facts: ConnectionFacts) (request: SignIn option) =
    runIn f.Env (Authenticator.signIn auth facts "tui" request)

[<Tests>]
let tests =
    testList
        "Authenticator"
        [
            test "a provider vouches, the host issues a token and a client id" {
                let f = fakes ()
                let auth = authenticator f

                let signedIn = ok (signIn f auth ConnectionFacts.inProcess (Some(named "alice")))

                Expect.equal signedIn.Session.Principal.Id (PrincipalId "user:names:alice") "alice"
                Expect.equal signedIn.Session.Roles [ Role.Owner ] "her roles"
                Expect.stringStarts signedIn.Session.Client "tui/" "the client's name, then an id"
                Expect.isSome signedIn.Token "a new token"

                let again = ok (signIn f auth ConnectionFacts.inProcess (Some(named "alice")))

                Expect.notEqual again.Session.Client signedIn.Session.Client "another client"
                Expect.notEqual again.Session.Id signedIn.Session.Id "another token"
            }

            test "a token resumes its session and client" {
                let f = fakes ()
                let auth = authenticator f

                let first = ok (signIn f auth ConnectionFacts.inProcess (Some(named "alice")))

                let token = first.Token |> Option.get

                let resumed =
                    ok (signIn f auth ConnectionFacts.inProcess (Some(SignIn.Token token)))

                Expect.equal resumed.Session first.Session "the same session"
                Expect.isNone resumed.Token "no new token"

                let viaListener =
                    ok (
                        signIn
                            f
                            auth
                            {
                                Connection = Connection.ofKind ConnectionKind.Loopback
                                Session = Some first.Session.Id
                            }
                            None
                    )

                Expect.equal viaListener.Session first.Session "the listener's bearer"
            }

            test "what is refused, and why" {
                let f = fakes ()
                let auth = authenticator f

                let refuse facts request =
                    signIn f auth facts request |> Result.map ignore

                let inProcess = ConnectionFacts.inProcess

                Expect.equal (refuse inProcess None) (Error IdentityFailure.Missing) "nothing said"

                Expect.equal
                    (refuse inProcess (Some(SignIn.Provider("github", Json.Null))))
                    (Error(IdentityFailure.UnknownProvider "github"))
                    "no such provider"

                Expect.equal
                    (refuse inProcess (Some(SignIn.Provider("names", Json.Null))))
                    (Error(IdentityFailure.Refused("names", AuthError.NoProof "no name")))
                    "the provider refuses"

                Expect.equal
                    (refuse inProcess (Some(named "carol")))
                    (Error(IdentityFailure.NoRole "user:names:carol"))
                    "no role"

                match refuse inProcess (Some(SignIn.Provider("rogue", Json.Null))) with
                | Error(IdentityFailure.InvalidPrincipal("rogue", _)) -> ()
                | other -> failtest ("a provider vouching for another's principal: " + string other)

                Expect.equal
                    (refuse inProcess (Some(SignIn.Token "not-a-token")))
                    (Error IdentityFailure.TokenRejected)
                    "a bad token"

                Expect.equal
                    (refuse
                        {
                            Connection = Connection.inProcess
                            Session = Some "nope"
                        }
                        None)
                    (Error IdentityFailure.TokenRejected)
                    "a listener session that is gone"
            }

            test "an expired token stops new sign-ins; a revoked one also stops the session" {
                let f = fakes ()
                let auth = authenticator f

                let first = ok (signIn f auth ConnectionFacts.inProcess (Some(named "alice")))

                f.Clock.Advance(System.TimeSpan.FromHours 13.0)

                Expect.equal
                    (signIn f auth ConnectionFacts.inProcess (Some(SignIn.Token(first.Token |> Option.get)))
                     |> Result.map ignore)
                    (Error IdentityFailure.TokenRejected)
                    "expired"

                Expect.equal (Authenticator.current auth first.Session) (Ok first.Session) "a live connection goes on"

                let second = ok (signIn f auth ConnectionFacts.inProcess (Some(named "alice")))

                runIn f.Env (SessionTokens.revoke auth.Tokens second.Session.Id) |> ignore

                Expect.equal (Authenticator.current auth second.Session) (Error IdentityFailure.TokenRejected) "revoked"
            }

            test "client names keep only safe characters" {
                Expect.equal (Authenticator.clientName "tui") "tui" "plain"
                Expect.equal (Authenticator.clientName "VS Code / ext") "VSCodeext" "spaces and slashes"
                Expect.equal (Authenticator.clientName "") "client" "none"
                Expect.equal (Authenticator.clientName "/ /") "client" "nothing left"
                Expect.equal (Authenticator.clientName (System.String('a', 50))).Length 32 "bounded"
            }

            test "the provider gets what the host established, never the client's word for it" {
                let f = fakes ()
                let mutable seen = None

                let spy =
                    { names with
                        Id = "spy"
                        Verify =
                            fun credential ->
                                seen <- Some credential
                                names.Verify credential
                        Roles = fun _ -> Fx.Effects.Eff.ret [ Role.Owner ]
                    }

                let auth =
                    Authenticator.create [ contributed f.Env.Clock spy ] host (SessionTokens())

                let connection =
                    {
                        Kind = ConnectionKind.UnixSocket
                        PeerAccount = Some "1000"
                        OwnerSecret = true
                    }

                signIn
                    f
                    auth
                    (ConnectionFacts.ofConnection connection)
                    (Some(SignIn.Provider("spy", Json.Object [ "name", Json.String "alice" ])))
                |> ignore

                match seen with
                | None -> failtest "not asked"
                | Some credential ->
                    Expect.equal credential.Connection connection "the connection"
                    Expect.equal credential.Host host "the host"
                    Expect.equal credential.Presented (Json.Object [ "name", Json.String "alice" ]) "what was presented"
            }
        ]
