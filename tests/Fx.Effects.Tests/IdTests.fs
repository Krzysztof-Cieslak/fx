module Fx.Effects.Tests.IdTests

open System
open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Tests.Conformance
open Fx.Effects.Tests.TestEnv

let private id high low = { High = high; Low = low }

[<Tests>]
let tests =
    testList
        "Id, Ids and FakeIds"
        [
            test "the text form is 26 Crockford base32 characters" {
                Expect.equal (string Id.zero) "00000000000000000000000000" "zero"
                Expect.equal (string (id UInt64.MaxValue UInt64.MaxValue)) "7ZZZZZZZZZZZZZZZZZZZZZZZZZ" "max"
                Expect.equal (string (id 0UL 1UL)) "00000000000000000000000001" "one"
                Expect.equal (string (id 0UL 32UL)) "00000000000000000000000010" "base 32"
            }

            test "the timestamp is the top 48 bits" {
                let created = Id.create 1_764_018_300_123L 0xABCDus 42UL
                Expect.equal (Id.timestamp created) (Instant.ofUnixMilliseconds 1_764_018_300_123L) "timestamp"
                Expect.equal (string created) "01KAVTY96VNF6G00000000001A" "text"
            }

            test "parsing is case-insensitive, maps confusable letters and rejects the rest" {
                let text = "01KAVWFV8VNMQ0000000000001"
                let parsed = Id.tryParse text
                Expect.isSome parsed "canonical"
                Expect.equal (Id.tryParse (text.ToLowerInvariant())) parsed "lower case"
                Expect.equal (Id.tryParse "0IKAVWFV8VNMQOOOOOOOOOOOOL") parsed "O, I and L"
                Expect.isNone (Id.tryParse "8ZZZZZZZZZZZZZZZZZZZZZZZZZ") "overflow"
                Expect.isNone (Id.tryParse "01KAVWFV8VNMQ000000000000U") "U is not a digit"
                Expect.isNone (Id.tryParse "01KAVWFV8VNMQ00000000000") "too short"
            }

            testProperty
                "text round-trips and sorts like the id"
                (fun (a: uint64) (b: uint64) (c: uint64) (d: uint64) ->
                    let x, y = id a b, id c d

                    Id.tryParse (string x) = Some x
                    && sign (compare x y) = sign (String.CompareOrdinal(string x, string y)))

            test "the generator increases within a millisecond and when the clock goes back" {
                let mutable now = 1000L
                let generator = IdGenerator((fun () -> now), (fun () -> struct (5us, 10UL)))
                let first = generator.Next()
                let second = generator.Next()
                Expect.equal second { first with Low = 11UL } "same millisecond: incremented"
                now <- 900L
                let third = generator.Next()
                Expect.equal third { first with Low = 12UL } "clock went back: incremented"
                now <- 1001L
                Expect.equal (Id.timestamp (generator.Next())) (Instant.ofUnixMilliseconds 1001L) "new millisecond"
            }

            test "the generator carries into the random high bits" {
                let generator =
                    IdGenerator((fun () -> 7L), (fun () -> struct (5us, UInt64.MaxValue)))

                let first = generator.Next()
                Expect.equal (generator.Next()) { High = first.High + 1UL; Low = 0UL } "carried"
            }

            test "fake ids are the same for the same seed and clock" {
                let ids seed =
                    let clock = FakeClock.Create()
                    let fake = FakeIds(seed, clock.Clock)

                    [
                        string (fake.Ids.NewId())
                        string (fake.Ids.NewId())
                        Convert.ToHexString(fake.Ids.RandomBytes 4)
                    ]

                Expect.equal (ids 1UL) (ids 1UL) "deterministic"
                Expect.notEqual (ids 1UL) (ids 2UL) "seeded"
            }

            test "Ids.newId and Ids.randomBytes go through the environment" {
                let env = create().Env
                let a, b = runIn env (Eff.both Ids.newId Ids.newId)
                Expect.isLessThan a b "increasing"
                Expect.equal (runIn env (Ids.randomBytes 3)).Length 3 "bytes"
            }

            IdsConformance.tests "FakeIds" (fun () -> FakeIds(7UL, FakeClock.Create().Clock).Ids)
        ]
