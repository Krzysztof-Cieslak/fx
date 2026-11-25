namespace Fx.Machines.Host

open System
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Machines

/// What every event of one commit records besides the event itself: when, who and why. A commit is
/// the events of one command, appended atomically.
type EventMeta =
    {
        /// When the actor handled the command, on the host's clock (just before it appended).
        At: Instant
        /// Who sent the command.
        By: Attribution
        /// The command's idempotency key. An actor rebuilds its deduplication index from these.
        Key: IdempotencyKey
        /// The event the command reacted to, if any.
        Cause: EventRef option
    }

    override this.ToString() =
        let cause =
            match this.Cause with
            | Some cause -> " after " + string cause
            | None -> ""

        string this.At + " " + this.Key.Value + " by " + string this.By + cause

/// An event in a log, with its position and metadata.
type Recorded<'Evt> =
    {
        Stream: StreamId
        /// 1 for the first event of a stream; consecutive, with no gaps.
        Seq: int64
        Event: 'Evt
        Meta: EventMeta
    }

    member this.Ref: EventRef = EventRef.create this.Stream this.Seq

    override this.ToString() =
        string this.Ref + " " + string this.Event + " (" + string this.Meta + ")"

/// Why an append failed.
type AppendError =
    /// The stream's head was not the expected sequence number: another writer appended first.
    | Conflict of expected: int64 * actual: int64
    /// The stream already has a commit with this idempotency key: the command took effect then, with
    /// these events. Nothing was appended.
    | Duplicate of key: IdempotencyKey * appended: Appended

    override this.ToString() =
        match this with
        | Conflict(expected, actual) -> "conflict: expected head " + string expected + ", found " + string actual
        | Duplicate(key, appended) -> "duplicate: " + key.Value + " " + string appended

/// An actor found its stream changed by another writer: two actors ran on one stream (spec §17:
/// takeover is fenced). The actor stops; a new one rebuilds from the log.
type ConcurrencyException(stream: StreamId, error: AppendError) =
    inherit Exception(string stream + ": " + string error)

    member _.Stream = stream
    member _.Error = error

/// Append-only event streams with sequence numbers (spec §5): what an actor needs from an event store.
/// A record of functions, data in and out, the cancellation token last.
///
/// Interpreters: `InMemoryEventLog` (this project) and the durable SQLite store (`SqliteEventStore`
/// in Fx.Store.Live). Every interpreter passes `EventLogConformance` (tests/Fx.Machines.Host.Tests).
type EventLog<'Evt> =
    {
        /// Appends one commit if the stream's head is the expected sequence number (0 for an empty
        /// stream), atomically, and returns the new head. The list is never empty.
        ///
        /// The commit's key is checked first: a stream never holds two commits with one key, so a
        /// retried append gets `Duplicate` with the events of the first, whatever its expected head.
        /// Otherwise a wrong expected head is a `Conflict`.
        Append: StreamId -> int64 -> EventMeta -> 'Evt list -> CancellationToken -> Task<Result<int64, AppendError>>
        /// At most `max` events after a sequence number, in order. Fewer, or none, at the head.
        Read: StreamId -> int64 -> int -> CancellationToken -> Task<Recorded<'Evt> list>
        /// The sequence number of the last event, 0 for an empty stream.
        Head: StreamId -> CancellationToken -> Task<int64>
        /// Completes, with the head, once the head is past a sequence number: at once if it already is.
        WaitPast: StreamId -> int64 -> CancellationToken -> Task<int64>
        /// The commit a key appended to a stream, if the stream has one: what `Append` would answer
        /// with `Duplicate`. An actor that starts from a snapshot looks up the keys it has not seen.
        FindCommit: StreamId -> IdempotencyKey -> CancellationToken -> Task<Appended option>
    }

    override _.ToString() = "EventLog"

module EventLog =
    /// How many events a page holds when the host reads a whole stream.
    [<Literal>]
    let PageSize = 512

    let append<'R, 'Evt when 'R :> IScope>
        (log: EventLog<'Evt>)
        (stream: StreamId)
        (expected: int64)
        (meta: EventMeta)
        (events: 'Evt list)
        : Eff<'R, Result<int64, AppendError>> =
        fun env -> log.Append stream expected meta events env.Scope.Cancellation

    let read<'R, 'Evt when 'R :> IScope>
        (log: EventLog<'Evt>)
        (stream: StreamId)
        (after: int64)
        (max: int)
        : Eff<'R, Recorded<'Evt> list> =
        fun env -> log.Read stream after max env.Scope.Cancellation

    let head<'R, 'Evt when 'R :> IScope> (log: EventLog<'Evt>) (stream: StreamId) : Eff<'R, int64> =
        fun env -> log.Head stream env.Scope.Cancellation

    let waitPast<'R, 'Evt when 'R :> IScope> (log: EventLog<'Evt>) (stream: StreamId) (after: int64) : Eff<'R, int64> =
        fun env -> log.WaitPast stream after env.Scope.Cancellation

    let findCommit<'R, 'Evt when 'R :> IScope>
        (log: EventLog<'Evt>)
        (stream: StreamId)
        (key: IdempotencyKey)
        : Eff<'R, Appended option> =
        fun env -> log.FindCommit stream key env.Scope.Cancellation

    /// The next events after a sequence number: at once if there are some, otherwise as soon as the
    /// next commit lands.
    let next<'R, 'Evt when 'R :> IScope>
        (log: EventLog<'Evt>)
        (stream: StreamId)
        (after: int64)
        (max: int)
        : Eff<'R, Recorded<'Evt> list> =
        eff {
            let mutable page = []
            let mutable waiting = true

            while waiting do
                let! events = read log stream after max

                if events.IsEmpty then
                    let! _ = waitPast log stream after
                    ()
                else
                    page <- events
                    waiting <- false

            return page
        }

    /// Every event after a sequence number, read in pages.
    let readAll<'R, 'Evt when 'R :> IScope>
        (log: EventLog<'Evt>)
        (stream: StreamId)
        (after: int64)
        : Eff<'R, Recorded<'Evt> list> =
        eff {
            let pages = ResizeArray<Recorded<'Evt> list>()
            let mutable position = after
            let mutable reading = true

            while reading do
                let! page = read log stream position PageSize

                match List.tryLast page with
                | Some last ->
                    pages.Add page
                    position <- last.Seq
                | None -> reading <- false

            return List.concat pages
        }

    /// Follows a stream (spec §5 `subscribe(afterSeq)`): hands `handle` every event after a sequence
    /// number in order, first those already in the log, then each new one as it is appended. Stops
    /// when `handle` returns false, and returns the last sequence number handled; runs until the scope
    /// is cancelled otherwise. A subscriber that falls behind reads pages, so it never holds up writers.
    let subscribe<'R, 'Evt when 'R :> IScope>
        (log: EventLog<'Evt>)
        (stream: StreamId)
        (after: int64)
        (handle: Recorded<'Evt> -> Eff<'R, bool>)
        : Eff<'R, int64> =
        eff {
            let mutable position = after
            let mutable following = true

            while following do
                let! page = next log stream position PageSize
                let mutable rest = page

                while following && not rest.IsEmpty do
                    let event = List.head rest
                    rest <- List.tail rest
                    let! more = handle event
                    position <- event.Seq
                    following <- more

            return position
        }
