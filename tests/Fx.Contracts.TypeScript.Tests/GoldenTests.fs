module Fx.Contracts.TypeScript.Tests.GoldenTests

open Expecto
open Fx.Contracts.TypeScript
open Fx.Contracts.TypeScript.Tests.Samples

let private moduleOf (types: Fx.Contracts.Schema list) : string =
    DeclarationFile.render
        {
            Layout = Layout.Module
            Types = types
            Members = []
        }

[<Tests>]
let tests =
    testList
        "Golden declarations"
        [
            test "a domain that uses every schema construct" { Golden.expect "domain.d.ts" (moduleOf domain) }

            test "names TypeScript cannot use as they are" { Golden.expect "naming.d.ts" (moduleOf naming) }

            test "Fx.Contracts: issues, limits and previews" { Golden.expect "contracts.d.ts" (moduleOf contracts) }

            test "Fx.Store: event envelopes and blob references" { Golden.expect "store.d.ts" (moduleOf store) }

            test "Fx.Variants: slots, resolved values, profiles, policies and config" {
                Golden.expect "variants.d.ts" (moduleOf variants)
            }

            test "a kernel's host bridge, as a global script" {
                Golden.expect "kernel.d.ts" (DeclarationFile.render kernel)
            }

            test "the codecs decode exactly the fixtures the types must accept" {
                for fixture in Fixtures.all do
                    Expect.equal fixture.Decoded fixture.Expected $"{fixture.Name}: decoded"
            }

            test "every rejected fixture's type is also checked with an accepted value" {
                // Otherwise a misspelt type would satisfy `@ts-expect-error` for the wrong reason.
                let accepted =
                    Fixtures.all
                    |> List.filter (fun f -> f.Expected)
                    |> List.map (fun f -> f.Type)
                    |> set

                for fixture in Fixtures.all |> List.filter (fun f -> not f.Expected) do
                    Expect.isTrue
                        (accepted.Contains fixture.Type)
                        $"{fixture.Name}: {fixture.Type} has no accepted value"
            }

            test "fixtures shared with the TypeScript checker" {
                Golden.expect "fixtures.ts" (Fixtures.render Fixtures.all)
            }
        ]
