module Fx.Variants.Tests.VariantsTests

open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Replay
open Fx.Variants
open Fx.Variants.Fakes
open Fx.Variants.Tests.Support
open Fx.Variants.Tests.Conformance

let private resolution =
    resolveOk [] [ layer Layer.Host "h" [ "test.mode", str "careful"; "test.depth", num "7" ] ]

/// The reads of `effect` run on `variants` through a recorder.
let private recordReads (variants: Variants) (effect: Eff<Env, 'T>) : 'T * Resolved list =
    let reads = VariantReads()
    let result = runIn (env (Variants.record reads variants)) effect
    result, reads.Reads

let private work: Eff<Env, string> =
    eff {
        let! m = Variants.get mode
        let! d = Variants.get depth
        let! again = Variants.get mode
        let! w, source = Variants.read width
        return $"{m} {d} {again} {w} {source}"
    }

[<Tests>]
let tests =
    testList
        "Variants"
        [
            VariantsConformance.tests "ofResolution" VariantsConformance.Unknown.NotRegistered Variants.ofResolution

            VariantsConformance.tests
                "record"
                VariantsConformance.Unknown.NotRegistered
                (Variants.ofResolution >> Variants.record (VariantReads()))

            VariantsConformance.tests "replay" VariantsConformance.Unknown.Diverged (fun r -> Variants.replay r.Values)

            test "effects read typed values" {
                let result = runIn (env (Variants.ofResolution resolution)) work
                Expect.equal result "careful 7 careful 80 default" "values"
            }

            test "the recorder keeps each slot's first read, in order" {
                let result, reads = recordReads (Variants.ofResolution resolution) work
                Expect.equal result "careful 7 careful 80 default" "values"

                Expect.equal
                    (reads |> List.map string)
                    [
                        "test.mode = \"careful\" (host (h))"
                        "test.depth = 7 (host (h))"
                        "test.width = 80 (default)"
                    ]
                    "reads"
            }

            test "replaying the recorded reads gives the same result, whatever the config says now" {
                let result, reads = recordReads (Variants.ofResolution resolution) work
                let now = resolveOk [] [ layer Layer.Host "h" [ "test.mode", str "fast" ] ]
                Expect.equal (runIn (env (Variants.ofResolution now)) work) "fast 3 fast 80 default" "differs now"
                Expect.equal (runIn (env (Variants.replay reads)) work) result "replayed"
            }

            test "replay diverges when the code reads another slot" {
                let _, reads = recordReads (Variants.ofResolution resolution) work

                let other: Eff<Env, decimal> = Variants.get ratio

                Expect.throwsT<ReplayDivergedException>
                    (fun () -> runIn (env (Variants.replay reads)) other |> ignore)
                    "diverged"
            }

            test "recorded reads round-trip through JSON" {
                let _, reads = recordReads (Variants.ofResolution resolution) work
                let json = Codec.encode Variants.readsCodec reads
                Expect.equal (Codec.decode Variants.readsCodec json) (Ok reads) "round trip"

                Expect.equal
                    (Json.serialize json)
                    """[{"slot":"test.mode","value":"careful","source":{"layer":"host","origin":"h"}},{"slot":"test.depth","value":7,"source":{"layer":"host","origin":"h"}},{"slot":"test.width","value":80,"source":{"layer":"default","origin":""}}]"""
                    "json"
            }

            test "concurrent reads are recorded once each" {
                let reads = VariantReads()
                let e = env (Variants.record reads (Variants.ofResolution resolution))

                let all: Eff<Env, int list> = Eff.all [ for _ in 1..20 -> Variants.get depth ]

                Expect.equal (runIn e all) (List.replicate 20 7) "values"
                Expect.equal reads.Reads.Length 1 "one read"
            }

            test "reading an unregistered slot or with the wrong type is a defect" {
                let e = env (Variants.ofResolution resolution)
                let other = Slot.create "test.other" "Other." Codec.int 1
                let mistyped = Slot.create "test.mode" "Mode as a number." Codec.int 1

                Expect.throwsT<UnregisteredSlotException> (fun () -> runIn e (Variants.get other) |> ignore) "get"
                Expect.throwsT<UnregisteredSlotException> (fun () -> runIn e (Variants.read other) |> ignore) "read"

                Expect.throwsT<UnregisteredSlotException>
                    (fun () -> runIn e (Variants.resolved other.Id) |> ignore)
                    "resolved"

                Expect.throwsT<SlotTypeMismatchException> (fun () -> runIn e (Variants.get mistyped) |> ignore) "type"
            }

            test "the fake resolves typed selections over defaults" {
                let fake = FakeVariants.create slots [ Selections.select mode Mode.Thorough ]
                Expect.equal (runIn (env fake) (Variants.get mode)) Mode.Thorough "selected"
                Expect.equal (runIn (env fake) (Variants.get depth)) 3 "default"

                Expect.throwsT<System.ArgumentException>
                    (fun () -> FakeVariants.create slots [ "test.depth", num "0" ] |> ignore)
                    "invalid selection"
            }
        ]
