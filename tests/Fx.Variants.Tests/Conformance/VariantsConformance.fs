/// The conformance suite of the variants capability: a function from an interpreter to tests. Every
/// interpreter of `Variants` passes it.
module Fx.Variants.Tests.Conformance.VariantsConformance

open Expecto
open Fx.Contracts
open Fx.Effects.Replay
open Fx.Variants
open Fx.Variants.Tests.Support

/// How an interpreter answers for a slot it does not have.
[<RequireQualifiedAccess>]
type Unknown =
    /// `None`: the slot is not registered.
    | NotRegistered
    /// `ReplayDivergedException`: the recorded run did not read it.
    | Diverged

    override this.ToString() =
        match this with
        | NotRegistered -> "not registered"
        | Diverged -> "diverged"

let private resolution: Resolution =
    resolveOk
        [ Policy.empty "org" |> Policy.pin width 100 ]
        [
            layer Layer.Host "host.json" [ "test.mode", str "thorough"; "test.ratio", num "0.750" ]
            layer Layer.Session "/mode" [ "test.mode", str "careful" ]
        ]

/// Tests an interpreter made from a resolution by `make`.
let tests (name: string) (unknown: Unknown) (make: Resolution -> Variants) : Test =
    testList
        name
        [
            test "reads every registered slot's value and source" {
                let variants = make resolution

                for expected in resolution.Values do
                    Expect.equal (variants.Read expected.Slot) (Some expected) (string expected.Slot)
            }

            test "reads are stable" {
                let variants = make resolution
                let first = resolution.Values |> List.map (fun r -> variants.Read r.Slot)
                let second = resolution.Values |> List.map (fun r -> variants.Read r.Slot)
                Expect.equal second first "same answers"
            }

            test "typed reads decode the values" {
                let e = env (make resolution)
                Expect.equal (runIn e (Variants.get mode)) Mode.Careful "mode"
                Expect.equal (runIn e (Variants.get ratio)) 0.75m "ratio"
                Expect.equal (runIn e (Variants.get depth)) 3 "depth (default)"

                Expect.equal
                    (runIn e (Variants.read width))
                    (100, Source.create Layer.Policy "org")
                    "width (pinned) and its source"
            }

            test "a slot it does not have" {
                let variants = make resolution
                let other = SlotId.create "test.unknown"

                match unknown with
                | Unknown.NotRegistered -> Expect.equal (variants.Read other) None "not registered"
                | Unknown.Diverged ->
                    Expect.throwsT<ReplayDivergedException> (fun () -> variants.Read other |> ignore) "diverged"
            }
        ]
