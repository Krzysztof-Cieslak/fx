namespace Fx.Effects

open System
open System.Globalization

/// A point in time: whole microseconds since the Unix epoch, in UTC.
///
/// Microseconds are the finest resolution every supported OS clock provides, so instants have the
/// same precision on Linux, macOS and Windows. They also fit a JavaScript number exactly (2^53 µs is
/// about 285 years), so instants cross into TypeScript without loss. There is no time zone: convert to
/// local time only for display, in the client.
[<Struct>]
type Instant =
    | Instant of unixMicroseconds: int64

    member this.UnixMicroseconds =
        let (Instant us) = this
        us

    /// RFC 3339 in UTC with exactly six fraction digits, e.g. `2026-11-24T21:05:00.123456Z`. Fixed
    /// width, so the text sorts like the instant.
    override this.ToString() =
        DateTimeOffset.UnixEpoch
            .AddTicks(this.UnixMicroseconds * 10L)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)

    static member (+)(instant: Instant, duration: TimeSpan) =
        Instant(instant.UnixMicroseconds + duration.Ticks / 10L)

    static member (-)(instant: Instant, duration: TimeSpan) =
        Instant(instant.UnixMicroseconds - duration.Ticks / 10L)

    static member (-)(later: Instant, earlier: Instant) =
        TimeSpan.FromTicks((later.UnixMicroseconds - earlier.UnixMicroseconds) * 10L)

module Instant =
    let private format = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'"

    let unixEpoch = Instant 0L

    let ofUnixMicroseconds (us: int64) = Instant us

    let ofUnixMilliseconds (ms: int64) = Instant(ms * 1000L)

    /// Whole milliseconds since the Unix epoch, rounded down.
    let unixMilliseconds (instant: Instant) =
        let us = instant.UnixMicroseconds
        if us >= 0L then us / 1000L else (us - 999L) / 1000L

    /// Truncates to whole microseconds; any offset is converted to UTC.
    let ofDateTimeOffset (time: DateTimeOffset) =
        Instant((time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10L)

    let toDateTimeOffset (instant: Instant) =
        DateTimeOffset.UnixEpoch.AddTicks(instant.UnixMicroseconds * 10L)

    /// Parses the format `Instant.ToString` writes, and nothing else.
    let tryParse (text: string) : Instant option =
        match
            DateTimeOffset.TryParseExact(
                text,
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            )
        with
        | true, time -> Some(ofDateTimeOffset time)
        | false, _ -> None

/// A reading of a monotonic clock: the time since an origin that is fixed for the clock's lifetime.
/// It never goes backwards and ignores wall-clock adjustments, so use it to measure durations and
/// deadlines. Only differences between readings of the same clock mean anything: never persist one.
[<Struct>]
type MonoTime =
    | MonoTime of sinceOrigin: TimeSpan

    member this.SinceOrigin =
        let (MonoTime t) = this
        t

    override this.ToString() =
        "mono+"
        + this.SinceOrigin.TotalSeconds.ToString("0.000000", CultureInfo.InvariantCulture)
        + "s"

    static member (+)(time: MonoTime, duration: TimeSpan) = MonoTime(time.SinceOrigin + duration)

    static member (-)(later: MonoTime, earlier: MonoTime) = later.SinceOrigin - earlier.SinceOrigin
