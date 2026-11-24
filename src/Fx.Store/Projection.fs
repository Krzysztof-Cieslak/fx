namespace Fx.Store

open Fx.Effects
open Fx.Machines
open Fx.Machines.Host

/// A state folded from a stream's events, and how it is snapshotted: what a view or a client keeps
/// of a stream (spec §5, §17: clients share `Evolve`).
type Projection<'S, 'Evt> =
    {
        Initial: 'S
        Evolve: 'S -> 'Evt -> 'S
        Snapshot: SnapshotSchema<'S>
    }

    override this.ToString() =
        "Projection(" + string this.Snapshot + ")"

/// What a subscriber that may skip a large gap receives (spec §5: snapshot + tail).
[<RequireQualifiedAccess>]
type Feed<'S, 'Evt> =
    /// The state after every event up to `Seq`. It replaces what the subscriber had; the events after
    /// `Seq` follow.
    | Reset of Snapshot<'S>
    /// The next event.
    | Next of Recorded<'Evt>

    override this.ToString() =
        match this with
        | Reset snapshot -> "reset to " + string snapshot.Seq
        | Next recorded -> "next " + string recorded.Ref

module Projection =
    let create (initial: 'S) (evolve: 'S -> 'Evt -> 'S) (snapshot: SnapshotSchema<'S>) : Projection<'S, 'Evt> =
        {
            Initial = initial
            Evolve = evolve
            Snapshot = snapshot
        }

    /// A machine's state as a projection.
    let ofMachine (snapshot: SnapshotSchema<'S>) (machine: Machine<'Cmd, 'Evt, 'S, 'Intent>) : Projection<'S, 'Evt> =
        create machine.Initial machine.Evolve snapshot

    /// The state of the empty stream.
    let initial (projection: Projection<'S, 'Evt>) : Snapshot<'S> =
        { Seq = 0L; State = projection.Initial }

    /// Folds the events after a snapshot, up to the head (read in pages).
    let catchUp<'R, 'S, 'Evt when 'R :> IScope>
        (log: EventLog<'Evt>)
        (projection: Projection<'S, 'Evt>)
        (stream: StreamId)
        (from: Snapshot<'S>)
        : Eff<'R, Snapshot<'S>> =
        eff {
            let mutable current = from
            let mutable reading = true

            while reading do
                let! page = EventLog.read log stream current.Seq EventLog.PageSize

                if page.IsEmpty then
                    reading <- false
                else
                    for recorded in page do
                        if recorded.Seq <> current.Seq + 1L then
                            invalidOp $"{stream}: expected event {current.Seq + 1L}, read {recorded.Seq}."

                        current <-
                            {
                                Seq = recorded.Seq
                                State = projection.Evolve current.State recorded.Event
                            }

            return current
        }

    /// The latest snapshot to start from: a readable one, unless it is past the head (the log is the
    /// truth), else the initial state.
    let private start
        (log: EventLog<'Evt>)
        (snapshots: SnapshotStore)
        (projection: Projection<'S, 'Evt>)
        (stream: StreamId)
        : Eff<'R, Snapshot<'S>> =
        eff {
            let! head = EventLog.head log stream
            let! saved = Snapshots.load snapshots projection.Snapshot stream

            return
                match saved with
                | Some snapshot when snapshot.Seq <= head -> snapshot
                | _ -> initial projection
        }

    /// The current state of a stream: the latest usable snapshot, then the events after it.
    let load<'R, 'S, 'Evt when 'R :> IScope>
        (log: EventLog<'Evt>)
        (snapshots: SnapshotStore)
        (projection: Projection<'S, 'Evt>)
        (stream: StreamId)
        : Eff<'R, Snapshot<'S>> =
        eff {
            let! from = start log snapshots projection stream
            return! catchUp log projection stream from
        }

    /// Loads the state, and saves it as the stream's snapshot when at least `every` events were folded
    /// after the last one: what a host does with long streams, on start or from time to time.
    let refresh<'R, 'S, 'Evt when 'R :> IScope>
        (log: EventLog<'Evt>)
        (snapshots: SnapshotStore)
        (projection: Projection<'S, 'Evt>)
        (every: int64)
        (stream: StreamId)
        : Eff<'R, Snapshot<'S>> =
        if every < 1L then
            invalidArg (nameof every) "Save after at least one event."

        eff {
            let! from = start log snapshots projection stream
            let! current = catchUp log projection stream from

            if current.Seq - from.Seq >= every then
                do! Snapshots.save snapshots projection.Snapshot stream current

            return current
        }

    /// Follows a stream from a position (spec §5 `subscribe(afterSeq)`): replays, then goes live. A
    /// subscriber more than `maxGap` events behind the head gets the latest snapshot past its position
    /// first, if there is one (`Feed.Reset`), and the events after it. Stops when `handle` returns
    /// false and returns the last position handled; runs until the scope is cancelled otherwise.
    let subscribe<'R, 'S, 'Evt when 'R :> IScope>
        (log: EventLog<'Evt>)
        (snapshots: SnapshotStore)
        (projection: Projection<'S, 'Evt>)
        (maxGap: int64)
        (stream: StreamId)
        (after: int64)
        (handle: Feed<'S, 'Evt> -> Eff<'R, bool>)
        : Eff<'R, int64> =
        eff {
            let mutable position = after
            let mutable following = true
            let! head = EventLog.head log stream

            if head - after > maxGap then
                let! saved = Snapshots.load snapshots projection.Snapshot stream

                match saved with
                | Some snapshot when snapshot.Seq > after && snapshot.Seq <= head ->
                    let! more = handle (Feed.Reset snapshot)
                    position <- snapshot.Seq
                    following <- more
                | _ -> ()

            if following then
                let! last =
                    EventLog.subscribe log stream position (fun recorded -> handle (Feed.Next recorded))

                position <- last

            return position
        }
