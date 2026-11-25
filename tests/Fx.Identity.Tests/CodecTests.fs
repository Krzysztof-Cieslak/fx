/// The JSON of principals and of `initialize`'s identity members.
module Fx.Identity.Tests.CodecTests

open Expecto
open Fx.Contracts
open Fx.Identity

let private roundTrip (codec: Codec<'T>) (value: 'T) =
    let text = Codec.encodeText codec value
    Expect.equal (Codec.decodeText codec text) (Ok value) ("round trip of " + text)
    text

let private rejects (codec: Codec<'T>) (text: string) =
    Expect.isError (Codec.decodeText codec text) ("rejects " + text)

let alice: Principal =
    {
        Id = PrincipalId "user:local-os-user:m1/1000"
        Display = "alice"
        Kind = PrincipalKind.User
        Provider = "local-os-user"
        Claims = Map.ofList [ "account", [ "1000" ]; "machine", [ "m1" ]; "proof", [ "peer-credentials" ] ]
    }

[<Tests>]
let tests =
    testList
        "Codecs"
        [
            test "principals" {
                let text = roundTrip Codecs.principal alice

                Expect.equal
                    text
                    """{"id":"user:local-os-user:m1/1000","display":"alice","kind":"user","provider":"local-os-user","claims":{"account":["1000"],"machine":["m1"],"proof":["peer-credentials"]}}"""
                    "JSON"

                rejects
                    Codecs.principal
                    """{"id":"user:alice","display":"alice","kind":"user","provider":"local-os-user"}"""

                rejects
                    Codecs.principal
                    """{"id":"user:local-os-user:m1/1000","display":"alice","kind":"device","provider":"local-os-user"}"""

                Expect.equal
                    (Codec.decodeText
                        Codecs.principal
                        """{"id":"user:local-os-user:m1/1000","display":"alice","kind":"user","provider":"local-os-user"}""")
                    (Ok { alice with Claims = Map.empty })
                    "claims default to none"
            }

            test "roles, kinds and connections" {
                Expect.equal
                    (roundTrip (Codec.list Codecs.role) [ Role.Viewer; Role.Collaborator; Role.Owner ])
                    """["viewer","collaborator","owner"]"""
                    "roles"

                rejects Codecs.role "\"admin\""

                Expect.equal
                    (roundTrip
                        Codecs.connection
                        {
                            Kind = ConnectionKind.UnixSocket
                            PeerAccount = Some "1000"
                            OwnerSecret = true
                        })
                    """{"kind":"unix-socket","peerAccount":"1000","ownerSecret":true}"""
                    "a connection"

                roundTrip Codecs.connection Connection.inProcess |> ignore

                Expect.equal
                    (roundTrip
                        Codecs.hostIdentity
                        {
                            Machine = "m1"
                            Account =
                                {
                                    Id = "S-1-5-21-1-2-3-1001"
                                    Name = None
                                }
                        })
                    """{"machine":"m1","account":{"id":"S-1-5-21-1-2-3-1001"}}"""
                    "a host"

                rejects Codecs.hostIdentity """{"machine":"a/b","account":{"id":"1000"}}"""
            }

            test "sign-ins" {
                Expect.equal
                    (roundTrip Codecs.signIn (SignIn.Provider("local-os-user", Json.Null)))
                    """{"kind":"provider","provider":"local-os-user"}"""
                    "a provider without a credential"

                roundTrip Codecs.signIn (SignIn.Provider("github", Json.Object [ "code", Json.String "abc" ]))
                |> ignore

                Expect.equal
                    (roundTrip Codecs.signIn (SignIn.Token "tok"))
                    """{"kind":"token","token":"tok"}"""
                    "a token"

                rejects Codecs.signIn """{"kind":"token","token":""}"""
                rejects Codecs.signIn """{"kind":"password"}"""
            }

            test "failures" {
                for failure in
                    [
                        IdentityFailure.Missing
                        IdentityFailure.Invalid [ Issue.create "bad" ]
                        IdentityFailure.UnknownProvider "github"
                        IdentityFailure.Refused("local-os-user", AuthError.NoProof "a remote connection")
                        IdentityFailure.Refused("local-os-user", AuthError.Rejected "another account")
                        IdentityFailure.Refused("github", AuthError.Expired)
                        IdentityFailure.Refused("github", AuthError.Unavailable "offline")
                        IdentityFailure.InvalidPrincipal("x", "bad id")
                        IdentityFailure.NoRole "user:x:y"
                        IdentityFailure.TokenRejected
                    ] do
                    roundTrip Codecs.identityFailure failure |> ignore
            }

            test "sessions" {
                let session =
                    {
                        Id = "01JDB8Z3M4X2Q9V7K5N6R8T0WA"
                        Token = Some "s3cr3t"
                        Principal = alice
                        Roles = [ Role.Owner ]
                        Client = "tui/01JDB8Z3M4X2Q9V7K5N6R8T0WB"
                        Expires = 1_800_000_000_000_000L
                    }

                roundTrip Codecs.sessionInfo session |> ignore
                let resumed = roundTrip Codecs.sessionInfo { session with Token = None }
                Expect.isFalse (resumed.Contains "\"token\"") "no token when resumed"
                Expect.isFalse ((string session).Contains "s3cr3t") "the token is not shown"
            }
        ]
