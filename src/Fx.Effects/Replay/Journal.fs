namespace Fx.Effects.Replay

// A proof of the record/replay shape for `Clock` and `Ids` (spec §15; the full design is T131).
// Every capability call has data in and data out, so a recording interpreter wraps the real one and
// journals each call, and a replaying interpreter answers from the journal without touching the
// world. Replay checks that it is asked the same questions in the same order; anything else is a
// divergence, a defect.
//
// Limits that T131 lifts: the journal is one sequence, so it replays sequential effects only
// (concurrent children interleave their calls in a nondeterministic order: key entries by scope or
// span instead), and replayed delays complete at once, so a race between a delay and other work may
// be decided differently (journal each delay's outcome, not just its start).

open System
open System.Threading.Tasks
open Fx.Effects

/// One journaled capability call: the inputs that are not unit, and the output.
type JournalEntry =
    | ClockNow of Instant
    | ClockMonotonic of MonoTime
    | ClockDelay of duration: TimeSpan
    | IdsNewId of Id
    | IdsRandomBytes of bytes: byte array

    override this.ToString() =
        match this with
        | ClockNow now -> $"Clock.Now -> {now}"
        | ClockMonotonic mono -> $"Clock.Monotonic -> {mono}"
        | ClockDelay duration -> $"Clock.Delay {duration}"
        | IdsNewId id -> $"Ids.NewId -> {id}"
        | IdsRandomBytes bytes -> $"Ids.RandomBytes {bytes.Length} -> {Convert.ToHexString bytes}"

/// An append-only journal of capability calls. Thread-safe.
[<Sealed>]
type Journal() =
    let gate = obj ()
    let entries = ResizeArray<JournalEntry>()

    member _.Append(entry: JournalEntry) = lock gate (fun () -> entries.Add entry)

    member _.Entries: JournalEntry list = lock gate (fun () -> List.ofSeq entries)

/// Replay was asked something the journal does not hold at that point.
type ReplayDivergedException(message: string) =
    inherit Exception(message)

/// Reads a journal in order during replay. Thread-safe.
[<Sealed>]
type ReplayCursor(entries: JournalEntry list) =
    let gate = obj ()
    let mutable remaining = entries
    let mutable position = 0

    /// The next entry, if `pick` accepts it; otherwise replay has diverged.
    member _.Take(expected: string, pick: JournalEntry -> 'T option) : 'T =
        lock gate (fun () ->
            match remaining with
            | entry :: rest ->
                match pick entry with
                | Some value ->
                    remaining <- rest
                    position <- position + 1
                    value
                | None ->
                    raise (
                        ReplayDivergedException
                            $"entry {position}: replay asked for {expected}, the journal has {entry}"
                    )
            | [] ->
                raise (
                    ReplayDivergedException $"entry {position}: replay asked for {expected} after the journal ended"
                ))

    /// Whether replay has used every entry. Replay that ends early has diverged too.
    member _.Finished = lock gate (fun () -> List.isEmpty remaining)

module Record =
    /// Journals every call to the clock.
    let clock (journal: Journal) (clock: Clock) : Clock =
        {
            Now =
                fun () ->
                    let now = clock.Now()
                    journal.Append(ClockNow now)
                    now
            Monotonic =
                fun () ->
                    let mono = clock.Monotonic()
                    journal.Append(ClockMonotonic mono)
                    mono
            Delay =
                fun duration cancellation ->
                    journal.Append(ClockDelay duration)
                    clock.Delay duration cancellation
        }

    /// Journals every id and random byte.
    let ids (journal: Journal) (ids: Ids) : Ids =
        {
            NewId =
                fun () ->
                    let id = ids.NewId()
                    journal.Append(IdsNewId id)
                    id
            RandomBytes =
                fun count ->
                    let bytes = ids.RandomBytes count
                    journal.Append(IdsRandomBytes(Array.copy bytes))
                    bytes
        }

module Replay =
    /// Answers from the journal. Delays complete at once (or fail if already cancelled).
    let clock (cursor: ReplayCursor) : Clock =
        {
            Now =
                fun () ->
                    cursor.Take(
                        "Clock.Now",
                        function
                        | ClockNow now -> Some now
                        | _ -> None
                    )
            Monotonic =
                fun () ->
                    cursor.Take(
                        "Clock.Monotonic",
                        function
                        | ClockMonotonic mono -> Some mono
                        | _ -> None
                    )
            Delay =
                fun duration cancellation ->
                    cursor.Take(
                        $"Clock.Delay {duration}",
                        function
                        | ClockDelay recorded when recorded = duration -> Some()
                        | _ -> None
                    )

                    if cancellation.IsCancellationRequested then
                        Task.FromCanceled<unit> cancellation
                    else
                        Task.FromResult()
        }

    /// Answers from the journal.
    let ids (cursor: ReplayCursor) : Ids =
        {
            NewId =
                fun () ->
                    cursor.Take(
                        "Ids.NewId",
                        function
                        | IdsNewId id -> Some id
                        | _ -> None
                    )
            RandomBytes =
                fun count ->
                    cursor.Take(
                        $"Ids.RandomBytes {count}",
                        function
                        | IdsRandomBytes bytes when bytes.Length = count -> Some(Array.copy bytes)
                        | _ -> None
                    )
        }
