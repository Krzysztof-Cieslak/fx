/// Principal ids, principals and roles (spec §11.2).
module Fx.Identity.Tests.ModelTests

open Expecto
open FsCheck
open FsCheck.FSharp
open Fx.Identity

let private ok (result: Result<'T, string>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failtest e

let private kinds =
    [ PrincipalKind.User; PrincipalKind.Device; PrincipalKind.Service ]

let private providerSegment =
    Gen.elements [ "local"; "os"; "user"; "github"; "oidc"; "a1"; "9" ]

let private provider: Gen<string> =
    gen {
        let! first = providerSegment
        let! rest = Gen.listOfLength 2 (Gen.zip (Gen.elements [ "."; "-" ]) providerSegment)
        let! count = Gen.choose (0, 2)

        return
            first
            + (rest
               |> List.truncate count
               |> List.map (fun (sep, s) -> sep + s)
               |> String.concat "")
    }

let private subject: Gen<string> =
    gen {
        let! length = Gen.choose (1, 40)
        let! chars = Gen.listOfLength length (Gen.choose (int '!', int '~') |> Gen.map char)
        return System.String(Array.ofList chars)
    }

[<Tests>]
let tests =
    testList
        "Model"
        [
            test "a principal id names its kind, provider and subject" {
                let id =
                    ok (PrincipalId.create PrincipalKind.User "local-os-user" "7Q2M9X4KD1/1000")

                Expect.equal id.Value "user:local-os-user:7Q2M9X4KD1/1000" "the form"

                Expect.equal
                    (PrincipalId.tryParts id.Value)
                    (Some(PrincipalKind.User, "local-os-user", "7Q2M9X4KD1/1000"))
                    "parts"

                Expect.equal (PrincipalId.kindOf id.Value) (Some PrincipalKind.User) "kind"
                Expect.isTrue (PrincipalId.isPerson id.Value) "a person"

                let sid =
                    ok (
                        PrincipalId.create
                            PrincipalKind.User
                            "local-os-user"
                            "M1/S-1-5-21-1004336348-1177238915-682003330-512"
                    )

                Expect.equal (PrincipalId.parse sid.Value) (Ok sid) "a Windows SID"
            }

            test "subjects may hold colons; providers may not" {
                let id =
                    ok (PrincipalId.create PrincipalKind.Service "oidc.example" "https://idp.example/sub:42")

                Expect.equal
                    (PrincipalId.tryParts id.Value)
                    (Some(PrincipalKind.Service, "oidc.example", "https://idp.example/sub:42"))
                    "split at the first two colons"

                Expect.isError (PrincipalId.create PrincipalKind.User "a:b" "x") "a colon in the provider"
            }

            test "malformed ids are refused" {
                for text in
                    [
                        ""
                        "system"
                        "user:alice"
                        "user::x"
                        "user:local:"
                        "person:local:x"
                        "User:local:x"
                        "user:Local:x"
                        "user:-local:x"
                        "user:local-:x"
                        "user:lo..cal:x"
                        "user:local:has space"
                        "user:local:tab\there"
                        "user:local:" + System.String('x', PrincipalId.MaxSubject + 1)
                        "user:" + System.String('a', PrincipalId.MaxProvider + 1) + ":x"
                        "user:local:\u00e9"
                    ] do
                    Expect.isError (PrincipalId.parse text) text
            }

            test "the kind of ids recorded before principals existed" {
                Expect.equal (PrincipalId.kindOf "user:alice") (Some PrincipalKind.User) "a test id"
                Expect.equal (PrincipalId.kindOf "device:phone") (Some PrincipalKind.Device) "a device"
                Expect.equal (PrincipalId.kindOf "system") None "the host"
                Expect.equal (PrincipalId.kindOf "user:") None "no more after the kind"
                Expect.equal (PrincipalId.kindOf "alice") None "no kind"
                Expect.isFalse (PrincipalId.isPerson PrincipalId.System) "the host is not a person"
                Expect.isFalse (PrincipalId.isPerson "service:ci:runner") "a program is not a person"
            }

            testProperty "ids round-trip through their parts"
            <| Prop.forAll (Arb.fromGen (Gen.zip3 (Gen.elements kinds) provider subject)) (fun (kind, p, s) ->
                let id = ok (PrincipalId.create kind p s)

                PrincipalId.tryParts id.Value = Some(kind, p, s)
                && PrincipalId.parse id.Value = Ok id)

            test "a principal's id agrees with its kind and provider" {
                let principal =
                    ok (
                        Principal.create
                            PrincipalKind.User
                            "local-os-user"
                            "m1/1000"
                            "alice"
                            (Map.ofList [ "account", [ "1000" ] ])
                    )

                Expect.equal principal.Id.Value "user:local-os-user:m1/1000" "id"
                Expect.equal (Principal.validate principal) (Ok()) "valid"

                Expect.isError
                    (Principal.validate
                        { principal with
                            Kind = PrincipalKind.Device
                        })
                    "another kind"

                Expect.isError (Principal.validate { principal with Provider = "other" }) "another provider"
                Expect.isError (Principal.validate { principal with Display = " " }) "no display text"

                Expect.isError
                    (Principal.validate
                        { principal with
                            Claims = Map.ofList [ "x", List.replicate (Principal.MaxClaimValues + 1) "v" ]
                        })
                    "too many values"

                Expect.equal (string principal) "alice (user:local-os-user:m1/1000)" "shown"
            }

            test "roles are ordered" {
                Expect.isTrue (Role.allows Role.Viewer [ Role.Owner ]) "an owner may view"
                Expect.isTrue (Role.allows Role.Collaborator [ Role.Viewer; Role.Collaborator ]) "a collaborator"
                Expect.isFalse (Role.allows Role.Owner [ Role.Collaborator ]) "a collaborator does not own"
                Expect.isFalse (Role.allows Role.Viewer []) "no role allows nothing"
                Expect.equal (Role.highest [ Role.Viewer; Role.Owner; Role.Collaborator ]) (Some Role.Owner) "highest"
                Expect.equal (Role.highest []) None "none"
            }

            test "secrets are never shown" {
                let signIn = SignIn.Token "s3cr3t-s3cr3t-s3cr3t-s3cr3t-s3cr3t-s3cr3t"
                Expect.isFalse ((string signIn).Contains "s3cr3t") "a token"

                let provider = SignIn.Provider("github", Fx.Contracts.Json.String "s3cr3t-s3cr3t")

                Expect.isFalse ((string provider).Contains "s3cr3t") "a credential"

                let credential =
                    {
                        Presented = Fx.Contracts.Json.String "s3cr3t"
                        Connection = Connection.inProcess
                        Host =
                            {
                                Machine = "m1"
                                Account = { Id = "1000"; Name = Some "alice" }
                            }
                    }

                Expect.isFalse ((string credential).Contains "s3cr3t") "a presented credential"
            }

            test "machine ids" {
                Expect.isTrue (HostIdentity.isMachine "7Q2M9X4KD1") "an id"
                Expect.isTrue (HostIdentity.isMachine "dev-box_1.local") "a name"
                Expect.isFalse (HostIdentity.isMachine "") "empty"
                Expect.isFalse (HostIdentity.isMachine "a/b") "a slash"
                Expect.isFalse (HostIdentity.isMachine (System.String('a', 65))) "too long"
            }
        ]
