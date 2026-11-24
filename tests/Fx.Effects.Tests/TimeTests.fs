module Fx.Effects.Tests.TimeTests

open System
open Expecto
open Fx.Effects

[<Tests>]
let tests =
    testList
        "Instant and MonoTime"
        [
            test "instants print as fixed-width RFC 3339 in UTC" {
                Expect.equal (string Instant.unixEpoch) "1970-01-01T00:00:00.000000Z" "epoch"
                let instant = Instant.ofUnixMicroseconds 1_764_018_300_123_456L
                Expect.equal (string instant) "2025-11-24T21:05:00.123456Z" "microseconds"
            }

            test "conversions truncate to microseconds and normalize offsets" {
                let local =
                    DateTimeOffset(2026, 11, 24, 23, 5, 0, TimeSpan.FromHours 2.0).AddTicks 1234567L

                let instant = Instant.ofDateTimeOffset local
                Expect.equal (string instant) "2026-11-24T21:05:00.123456Z" "utc, truncated"
                Expect.equal (Instant.toDateTimeOffset instant).Offset TimeSpan.Zero "utc offset"
                Expect.equal (Instant.unixMilliseconds (Instant.ofUnixMicroseconds -1L)) -1L "floor before the epoch"
                Expect.equal (Instant.unixMilliseconds (Instant.ofUnixMicroseconds 1999L)) 1L "floor"
            }

            test "arithmetic" {
                let a = Instant.ofUnixMicroseconds 1_000_000L
                Expect.equal (a + TimeSpan.FromSeconds 1.5) (Instant.ofUnixMicroseconds 2_500_000L) "plus"
                Expect.equal (a - TimeSpan.FromSeconds 1.0) Instant.unixEpoch "minus"
                Expect.equal ((a + TimeSpan.FromMinutes 1.0) - a) (TimeSpan.FromMinutes 1.0) "difference"
                Expect.isLessThan a (a + TimeSpan.FromTicks 10L) "ordering"
                let m = MonoTime(TimeSpan.FromSeconds 3.0)
                Expect.equal ((m + TimeSpan.FromSeconds 2.0) - m) (TimeSpan.FromSeconds 2.0) "mono"
                Expect.equal (string m) "mono+3.000000s" "mono text"
            }

            testProperty "the text form round-trips and sorts like the instant" (fun (a: int64) (b: int64) ->
                // Years 0001 to 9999.
                let clamp (x: int64) =
                    -62_135_596_800_000_000L + abs (x % 315_537_897_600_000_000L)

                let a, b =
                    Instant.ofUnixMicroseconds (clamp a), Instant.ofUnixMicroseconds (clamp b)

                Instant.tryParse (string a) = Some a
                && sign (compare a b) = sign (String.CompareOrdinal(string a, string b)))

            test "parsing accepts only the canonical form" {
                Expect.isNone (Instant.tryParse "2026-11-24T21:05:00Z") "no fraction"
                Expect.isNone (Instant.tryParse "2026-11-24T21:05:00.123456+01:00") "offset"
                Expect.isSome (Instant.tryParse "2026-11-24T21:05:00.123456Z") "canonical"
            }
        ]
