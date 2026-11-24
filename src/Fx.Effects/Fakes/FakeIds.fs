namespace Fx.Effects.Fakes

open Fx.Effects

/// SplitMix64: a small pseudo-random generator, deterministic for a seed. Not cryptographic.
[<Sealed>]
type SplitMix64(seed: uint64) =
    let mutable state = seed

    member _.Next() : uint64 =
        state <- state + 0x9E3779B97F4A7C15UL
        let mutable z = state
        z <- (z ^^^ (z >>> 30)) * 0xBF58476D1CE4E5B9UL
        z <- (z ^^^ (z >>> 27)) * 0x94D049BB133111EBUL
        z ^^^ (z >>> 31)

/// Ids and random bytes that are the same in every run with the same seed and clock: the timestamp
/// comes from the clock (e.g. a `FakeClock`), the random bits from SplitMix64.
[<Sealed>]
type FakeIds(seed: uint64, clock: Clock) =
    let gate = obj ()
    let random = SplitMix64 seed

    let next () = lock gate (fun () -> random.Next())

    let generator =
        IdGenerator(
            (fun () -> Instant.unixMilliseconds (clock.Now())),
            fun () ->
                let high = next ()
                let low = next ()
                struct (uint16 high, low)
        )

    member val Ids: Ids =
        {
            NewId = generator.Next
            RandomBytes =
                fun count ->
                    lock gate (fun () ->
                        let bytes = Array.zeroCreate<byte> count

                        for i in 0 .. count - 1 do
                            bytes[i] <- byte (random.Next())

                        bytes)
        }
