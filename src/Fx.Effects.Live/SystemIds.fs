namespace Fx.Effects.Live

open System
open System.Buffers.Binary
open System.Security.Cryptography
open Fx.Effects

/// Real ids and randomness: timestamps from the given clock, random bits from the OS's
/// cryptographically strong generator. The only reader of system randomness.
module SystemIds =
    let private randomBytes (count: int) : byte array = RandomNumberGenerator.GetBytes count

    let private random80 () =
        let bytes = randomBytes 10

        struct (BinaryPrimitives.ReadUInt16BigEndian(ReadOnlySpan(bytes, 0, 2)),
                BinaryPrimitives.ReadUInt64BigEndian(ReadOnlySpan(bytes, 2, 8)))

    /// Ids timestamped by `clock` (normally `SystemClock.clock`).
    let create (clock: Clock) : Ids =
        let generator =
            IdGenerator((fun () -> Instant.unixMilliseconds (clock.Now())), random80)

        {
            NewId = generator.Next
            RandomBytes = randomBytes
        }
