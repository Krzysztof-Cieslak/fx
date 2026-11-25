/// The TypeScript types of principals and of `initialize`'s identity members (spec §17: generated
/// types for clients), from the codecs. `bun scripts/declarations.ts` type-checks the golden file.
module Fx.Identity.Tests.DeclarationTests

open Expecto
open Fx.Contracts.TypeScript
open Fx.Identity

let private file: DeclarationFile =
    {
        Layout = Layout.Module
        Types =
            [
                Codecs.principal.Schema
                Codecs.role.Schema
                Codecs.connection.Schema
                Codecs.hostIdentity.Schema
                Codecs.authError.Schema
                Codecs.signIn.Schema
                Codecs.identityFailure.Schema
                Codecs.sessionInfo.Schema
            ]
        Members = []
    }

[<Tests>]
let tests =
    testList
        "Identity declarations"
        [
            test "the identity types are generated from the codecs" {
                Golden.expect "identity.d.ts" (DeclarationFile.render file)
            }
        ]
