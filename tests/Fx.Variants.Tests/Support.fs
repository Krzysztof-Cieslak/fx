/// Slots, an environment and generators shared by the variants tests.
module Fx.Variants.Tests.Support

open System
open FsCheck
open FsCheck.FSharp
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Variants

[<RequireQualifiedAccess>]
type Mode =
    | Fast
    | Thorough
    | Careful

    override this.ToString() =
        match this with
        | Fast -> "fast"
        | Thorough -> "thorough"
        | Careful -> "careful"

/// An enumerated slot.
let mode: Slot<Mode> =
    Slot.enum
        "test.mode"
        "How thorough the test is."
        [ Mode.Fast, "fast"; Mode.Thorough, "thorough"; Mode.Careful, "careful" ]
        Mode.Fast

/// An open slot with a refinement.
let depth: Slot<int> =
    Slot.create "test.depth" "How deep to go, from 1 to 10." (Codec.int |> Codec.between 1 10) 3

/// A privileged slot.
let sandbox: Slot<bool> =
    Slot.create "test.sandbox" "Whether to sandbox." Codec.bool true
    |> Slot.privileged

/// A decimal slot, whose values have several notations.
let ratio: Slot<decimal> = Slot.create "test.ratio" "A ratio." Codec.decimal 0.50m

/// An open slot restricted to some values.
let width: Slot<int> =
    Slot.create "test.width" "A width." Codec.int 80
    |> Slot.withChoices [ 80; 100; 120 ]

let slots: SlotInfo list =
    [
        Slot.info mode
        Slot.info depth
        Slot.info sandbox
        Slot.info ratio
        Slot.info width
    ]

let registry: SlotRegistry = SlotRegistry.ofSlots slots

let source (layer: Layer) (origin: string) = Source.create layer origin

let layer (l: Layer) (origin: string) (values: (string * Json) list) : Selections =
    Selections.create (source l origin) values

let str (s: string) = Json.String s

let num (text: string) =
    match Json.tryNumber text with
    | Some json -> json
    | None -> failwith $"not a number: {text}"

/// The resolution, or a failed test with the issues.
let resolveOk (policies: Policy list) (layers: Selections list) : Resolution =
    match Resolution.resolve registry policies layers with
    | Ok resolution -> resolution
    | Error issues -> failtest (VariantIssue.render issues)

/// The issues as text, or a failed test if resolution succeeded.
let resolveIssues (policies: Policy list) (layers: Selections list) : string list =
    match Resolution.resolve registry policies layers with
    | Ok resolution -> failtest $"resolved: {Resolution.explain resolution}"
    | Error issues -> issues |> List.map string

/// An environment with variants and a scope.
type Env =
    {
        Variants: Variants
        Scope: Scope
    }

    interface IVariants with
        member env.Variants = env.Variants

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "Env"

let env (variants: Variants) : Env =
    {
        Variants = variants
        Scope = Scope.root
    }

/// Runs an effect and waits for it (tests only).
let runIn (env: 'R) (effect: Eff<'R, 'T>) : 'T =
    (Eff.run env effect).GetAwaiter().GetResult()

// ---- Generators ----------------------------------------------------------------------------------

let private identifier: Gen<string> =
    gen {
        let! first = Gen.elements [ 'a' .. 'z' ]

        let! rest =
            Gen.listOfLength 5 (Gen.elements ([ 'a' .. 'z' ] @ [ 'A' .. 'Z' ] @ [ '0' .. '9' ] @ [ '-' ]))

        return String(Array.ofList (first :: rest))
    }

let slotId: Gen<SlotId> =
    gen {
        let! segments = Gen.choose (2, 4)
        let! parts = Gen.listOfLength segments identifier
        return SlotId.create (String.Join(".", parts))
    }

/// Canonical JSON values, small.
let canonicalJson: Gen<Json> =
    let scalar =
        Gen.oneof
            [
                Gen.constant Json.Null
                Gen.elements [ true; false ] |> Gen.map Json.Bool
                Gen.choose (-1000, 1000) |> Gen.map (fun n -> Json.int n)
                identifier |> Gen.map Json.String
            ]

    Gen.oneof
        [
            scalar
            Gen.listOfLength 2 scalar |> Gen.map Json.Array
            gen {
                let! a = scalar
                let! b = scalar
                return Json.Object [ "a", a; "b", b ]
            }
        ]
    |> Gen.map Json.canonicalize

let private sha256Text: Gen<string> =
    Gen.arrayOfLength 32 (Gen.choose (0, 255) |> Gen.map byte)
    |> Gen.map (fun bytes -> "sha256:" + Convert.ToHexStringLower bytes)

let profile: Gen<Profile> =
    gen {
        let! selections = Gen.listOf (Gen.zip slotId canonicalJson) |> Gen.map Map.ofList

        let! extensions =
            Gen.listOf (
                gen {
                    let! id = identifier
                    let! major = Gen.choose (0, 3)
                    let! hash = Gen.optionOf sha256Text

                    return
                        id,
                        {
                            Version = $"{major}.1.0"
                            Hash = hash
                        }
                }
            )
            |> Gen.map Map.ofList

        let! conformance =
            Gen.listOf (
                gen {
                    let! subject = identifier
                    let! suite = Gen.elements [ "kernel"; "wire"; "provider" ]
                    let! passed = Gen.choose (0, 50)
                    let! failed = Gen.choose (0, 3)

                    return
                        subject,
                        suite,
                        {
                            Passed = passed
                            Failed = failed
                            Skipped = 0
                        }
                }
            )

        let! lockfile = Gen.optionOf sha256Text
        let! core = Gen.elements [ "0.0.0"; "0.1.0"; "1.2.3-beta.1" ]

        let empty: Profile =
            {
                Format = Profile.CurrentFormat
                Core = core
                Selections = selections
                Extensions = extensions
                Conformance = Map.empty
                Lockfile = lockfile
            }

        return
            conformance
            |> List.fold (fun p (subject, suite, result) -> Profile.withConformance subject suite result p) empty
    }

type Arbitraries =
    static member Profile() : Arbitrary<Profile> = Arb.fromGen profile

/// 300 tests per property; set PROPERTY_TESTS to run more.
let config =
    { FsCheckConfig.defaultConfig with
        arbitrary = [ typeof<Arbitraries> ]
        maxTest =
            match Environment.GetEnvironmentVariable "PROPERTY_TESTS" with
            | null -> 300
            | count -> int count
    }

/// A property with the generators above.
let property name (test: 'T -> unit) = testPropertyWithConfig config name test
