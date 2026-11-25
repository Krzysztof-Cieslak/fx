/// Session tokens (spec §3, §17): random, kept hashed, expired by the clock, revoked by id or
/// principal, and resolved by the local listener.
module Fx.Identity.Host.Tests.TokenTests

open System
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Identity
open Fx.Identity.Host
open Fx.Identity.Host.Tests.Support

let private principal (name: string) =
    ok (Principal.create PrincipalKind.User "names" name name Map.empty)

let private minute = TimeSpan.FromMinutes 1.0

let private isBase64Url (text: string) =
    text |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '_')

[<Tests>]
let tests =
    testList
        "Session tokens"
        [
            test "a token is 256 random bits in base64url, and the host keeps only its hash" {
                let f = fakes ()
                let tokens = SessionTokens()

                let token, session =
                    runIn f.Env (SessionTokens.issue tokens (principal "alice") [ Role.Owner ] "tui/1")

                Expect.equal token.Length 43 "32 bytes"
                Expect.isTrue (isBase64Url token) "base64url: fits a bearer header and a subprotocol"
                Expect.isFalse ((string tokens).Contains token) "not shown"
                Expect.isFalse ((string session).Contains token) "not in the session"
                Expect.isFalse (tokens.Table.ByHash.ContainsKey token) "not a key"
                Expect.isTrue (tokens.Table.ByHash.ContainsKey(TokenTable.hash token)) "its hash is"

                let other, _ =
                    runIn f.Env (SessionTokens.issue tokens (principal "alice") [ Role.Owner ] "tui/2")

                Expect.notEqual other token "each token is new"
            }

            test "a token resumes its session until it expires" {
                let f = fakes ()
                let tokens = SessionTokens()

                let token, session =
                    runIn f.Env (SessionTokens.issue tokens (principal "alice") [ Role.Owner ] "tui/1")

                Expect.equal (session.Expires - session.Issued) (720L * 60L * 1_000_000L) "12 hours by default"
                Expect.equal (runIn f.Env (SessionTokens.verify tokens token)) (Ok session) "live"
                Expect.equal (runIn f.Env (SessionTokens.find tokens session.Id)) (Ok session) "by id"

                Expect.equal
                    (runIn f.Env (SessionTokens.verify tokens (token + "x")))
                    (Error TokenError.Unknown)
                    "another token"

                f.Clock.Advance(TimeSpan.FromHours 12.0 - TimeSpan.FromTicks 10L)
                Expect.isOk (runIn f.Env (SessionTokens.verify tokens token)) "just before"
                f.Clock.Advance(TimeSpan.FromTicks 10L)
                Expect.equal (runIn f.Env (SessionTokens.verify tokens token)) (Error TokenError.Expired) "expired"
                Expect.equal (runIn f.Env (SessionTokens.active tokens)) [] "none active"
            }

            test "the lifetime is a privileged variant slot" {
                Expect.isTrue IdentitySlots.sessionTokenTtl.Privileged "privileged"

                let f = fakesWith [ "identity.sessionTokenTtl", Json.int 5 ]
                let tokens = SessionTokens()

                let token, _ =
                    runIn f.Env (SessionTokens.issue tokens (principal "alice") [ Role.Owner ] "tui/1")

                f.Clock.Advance(5.0 * minute)
                Expect.equal (runIn f.Env (SessionTokens.verify tokens token)) (Error TokenError.Expired) "5 minutes"
            }

            test "expired tokens are forgotten when the next is issued" {
                let f = fakesWith [ "identity.sessionTokenTtl", Json.int 1 ]
                let tokens = SessionTokens()

                let old, oldSession =
                    runIn f.Env (SessionTokens.issue tokens (principal "alice") [ Role.Owner ] "tui/1")

                f.Clock.Advance(2.0 * minute)

                runIn f.Env (SessionTokens.issue tokens (principal "bob") [ Role.Viewer ] "web/1")
                |> ignore

                Expect.equal (runIn f.Env (SessionTokens.verify tokens old)) (Error TokenError.Unknown) "forgotten"
                Expect.isFalse (tokens.Table.HashOf.ContainsKey oldSession.Id) "its id too"
                Expect.equal tokens.Table.ByHash.Count 1 "one left"
            }

            test "revoking a token refuses it and ends its connections" {
                let f = fakes ()
                let tokens = SessionTokens()

                let token, session =
                    runIn f.Env (SessionTokens.issue tokens (principal "alice") [ Role.Owner ] "tui/1")

                let revocation = tokens.Revocation session.Id
                Expect.isFalse revocation.IsCancellationRequested "live"
                Expect.isTrue (runIn f.Env (SessionTokens.revoke tokens session.Id)) "revoked"
                Expect.isTrue revocation.IsCancellationRequested "its connections end"
                Expect.isTrue (SessionTokens.isRevoked tokens session.Id) "remembered"
                Expect.equal (runIn f.Env (SessionTokens.verify tokens token)) (Error TokenError.Revoked) "refused"
                Expect.isFalse (runIn f.Env (SessionTokens.revoke tokens session.Id)) "once"
                Expect.isFalse (runIn f.Env (SessionTokens.revoke tokens "nope")) "unknown"
            }

            test "signing a principal out everywhere revokes all its tokens" {
                let f = fakes ()
                let tokens = SessionTokens()
                let alice = principal "alice"

                let a1, s1 = runIn f.Env (SessionTokens.issue tokens alice [ Role.Owner ] "tui/1")
                let a2, s2 = runIn f.Env (SessionTokens.issue tokens alice [ Role.Owner ] "web/1")

                let b, _ =
                    runIn f.Env (SessionTokens.issue tokens (principal "bob") [ Role.Viewer ] "web/2")

                let revoked = runIn f.Env (SessionTokens.revokePrincipal tokens alice.Id)
                Expect.equal revoked (List.sort [ s1.Id; s2.Id ]) "both of alice's"

                for token in [ a1; a2 ] do
                    Expect.equal (runIn f.Env (SessionTokens.verify tokens token)) (Error TokenError.Revoked) "alice"

                Expect.isOk (runIn f.Env (SessionTokens.verify tokens b)) "bob stays"
            }

            test "the local listener resolves a live token to its id" {
                let f = fakes ()
                let tokens = SessionTokens()

                let token, session =
                    runIn f.Env (SessionTokens.issue tokens (principal "alice") [ Role.Owner ] "tui/1")

                let lookup = SessionTokens.lookup tokens f.Env.Clock
                Expect.equal (lookup token) (Some session.Id) "live"
                Expect.equal (lookup "nope") None "unknown"
                runIn f.Env (SessionTokens.revoke tokens session.Id) |> ignore
                Expect.equal (lookup token) None "revoked"
            }

            test "a session attributes its client's inputs" {
                let f = fakes ()
                let tokens = SessionTokens()

                let _, session =
                    runIn f.Env (SessionTokens.issue tokens (principal "alice") [ Role.Owner ] "tui/1")

                let by = ClientSession.attribution session
                Expect.equal by.Principal "user:names:alice" "the principal"
                Expect.equal by.Via "tui/1" "the client"
            }
        ]
