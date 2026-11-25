/// The identity-provider conformance suite on the test provider, so the suite itself is tested.
module Fx.Identity.Host.Tests.ConformanceTests

open Expecto
open Fx.Contracts
open Fx.Identity
open Fx.Identity.Host.Tests.Support
open Fx.Identity.Host.Tests.Conformance

let private credential (presented: Json) : Credential =
    {
        Presented = presented
        Connection = Connection.inProcess
        Host = host
    }

[<Tests>]
let tests =
    IdentityProviderConformance.tests
        "names"
        names
        {
            Accepted =
                [
                    "alice", credential (Json.Object [ "name", Json.String "alice" ]), PrincipalId "user:names:alice"
                    "bob", credential (Json.Object [ "name", Json.String "bob" ]), PrincipalId "user:names:bob"
                ]
            Refused =
                [
                    "no name", credential Json.Null
                    "a name with a space", credential (Json.Object [ "name", Json.String "a b" ])
                ]
        }
