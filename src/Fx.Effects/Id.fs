namespace Fx.Effects

open System

/// A 128-bit unique id that sorts by creation time, laid out like a ULID: a 48-bit Unix millisecond
/// timestamp, then 80 random bits. Written as 26 Crockford base32 characters, e.g.
/// `01JDB8Z3M4X2Q9V7K5N6R8T0WA`; the text sorts like the id. Typed ids wrap it (`SessionId of Id`).
[<Struct>]
type Id =
    {
        /// The timestamp (top 48 bits) and the first 16 random bits.
        High: uint64
        /// The remaining 64 random bits.
        Low: uint64
    }

    override this.ToString() =
        let alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
        let chars = Array.zeroCreate<char> 26

        for k in 0..25 do
            let shift = 5 * k

            let digit =
                if shift + 5 <= 64 then
                    (this.Low >>> shift) &&& 31UL
                elif shift < 64 then
                    ((this.Low >>> shift) ||| (this.High <<< (64 - shift))) &&& 31UL
                else
                    (this.High >>> (shift - 64)) &&& 31UL

            chars[25 - k] <- alphabet[int digit]

        String(chars)

module Id =
    /// The smallest id. Never generated.
    let zero = { High = 0UL; Low = 0UL }

    /// The largest timestamp an id can hold (year 10889).
    let maxTimestamp = (1L <<< 48) - 1L

    /// Builds an id from a Unix millisecond timestamp and 80 random bits (`randomHigh` holds 16).
    let create (unixMilliseconds: int64) (randomHigh: uint16) (randomLow: uint64) : Id =
        if unixMilliseconds < 0L || unixMilliseconds > maxTimestamp then
            invalidArg (nameof unixMilliseconds) "The timestamp does not fit in 48 bits."

        {
            High = (uint64 unixMilliseconds <<< 16) ||| uint64 randomHigh
            Low = randomLow
        }

    /// When the id was created, to the millisecond.
    let timestamp (id: Id) : Instant =
        Instant.ofUnixMilliseconds (int64 (id.High >>> 16))

    let private digit (c: char) =
        match Char.ToUpperInvariant c with
        | c when c >= '0' && c <= '9' -> int c - int '0'
        | 'O' -> 0
        | 'I'
        | 'L' -> 1
        | c ->
            let index = "ABCDEFGHJKMNPQRSTVWXYZ".IndexOf c
            if index >= 0 then index + 10 else -1

    /// Parses the 26-character form, case-insensitively (Crockford: `O` is 0, `I` and `L` are 1).
    let tryParse (text: string) : Id option =
        if text.Length <> 26 || digit text[0] > 7 then
            None
        else
            let mutable high = 0UL
            let mutable low = 0UL
            let mutable valid = true

            for c in text do
                let d = digit c

                if d < 0 then
                    valid <- false
                else
                    high <- (high <<< 5) ||| (low >>> 59)
                    low <- (low <<< 5) ||| uint64 d

            if valid then Some { High = high; Low = low } else None

/// Generates ids that strictly increase, even within one millisecond or when the clock goes back:
/// then the previous id's random part is incremented. Pure logic; the interpreter supplies the time
/// and the randomness. Thread-safe.
type IdGenerator(unixMilliseconds: unit -> int64, random: unit -> struct (uint16 * uint64)) =
    let gate = obj ()
    let mutable last = Id.zero

    member _.Next() : Id =
        lock gate (fun () ->
            let lastTime = int64 (last.High >>> 16)
            let now = unixMilliseconds ()

            let next =
                if now > lastTime then
                    let struct (high, low) = random ()
                    Id.create now high low
                elif last.Low <> UInt64.MaxValue then
                    { last with Low = last.Low + 1UL }
                elif (last.High &&& 0xFFFFUL) <> 0xFFFFUL then
                    { High = last.High + 1UL; Low = 0UL }
                else
                    // 2^80 ids in one millisecond: borrow the next millisecond.
                    Id.create (lastTime + 1L) 0us 0UL

            last <- next
            next)
