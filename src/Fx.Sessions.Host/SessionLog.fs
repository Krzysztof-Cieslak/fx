namespace Fx.Sessions.Host

open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Sessions

/// How sessions are stored and served: the event schema of session streams, the snapshot schema of
/// their state, the projection clients and subscribers fold, and the JSON of what a subscriber
/// receives (spec §5).
module SessionLog =
    /// The event schema of session streams. Version 1; a change to the JSON of an event is a new
    /// version with an upcaster (`EventSchema.evolve`), and a test that reads what version 1 wrote.
    let schema: EventSchema<Event> = EventSchema.create Codecs.event

    /// The snapshot schema of a session's state. Bump the version whenever `SessionState`'s JSON or
    /// `Session.evolve` changes meaning: old snapshots are then ignored and rebuilt from the log.
    let snapshot: SnapshotSchema<SessionState> =
        SnapshotSchema.create Session.StreamKind 2 Codecs.state

    /// A session's state as a projection: what actors start from and subscribers catch up with.
    let projection: Projection<SessionState, Event> =
        Projection.create Session.initial Session.evolve snapshot

    /// The session events of a store's log.
    let typed (log: EventLog<Payload>) : EventLog<Event> = EventLog.typed schema log

    /// An event with its position and envelope, as clients receive it.
    let recorded: Codec<Recorded<Event>> =
        Fx.Store.Codecs.recorded Codecs.event
        |> Codec.describe "A session event with its position, time, sender, key and cause."
        |> Codec.named "RecordedSessionEvent"

    /// What a subscriber receives (spec §5: subscribe replays, then goes live; a large gap gets the
    /// state from a snapshot first): `{"kind": "reset", "seq", "state"}` or `{"kind": "event", …}`.
    let feed: Codec<Feed<SessionState, Event>> =
        Codec.union "kind" {
            let! reset =
                Codec.case2
                    "reset"
                    (fun seq state -> Feed.Reset { Seq = seq; State = state })
                    (Field.required "seq" (Codec.int64 |> Codec.atLeast 1L))
                    (Field.required "state" Codecs.state)

            and! next = Codec.caseOf "event" Feed.Next recorded

            return
                fun value ->
                    match value with
                    | Feed.Reset snapshot -> reset snapshot.Seq snapshot.State
                    | Feed.Next event -> next event
        }
        |> Codec.describe
            "What a session subscriber receives: the state at a sequence number, replacing what it had, or the next event."
        |> Codec.named "SessionFeed"

    /// The input with this id: the input of the event at that sequence number, if it received one.
    let input<'R when 'R :> IScope> (log: EventLog<Event>) (stream: StreamId) (id: int64) : Eff<'R, Input option> =
        eff {
            let! events = EventLog.read log stream (id - 1L) 1

            return
                events
                |> List.tryHead
                |> Option.filter (fun recorded -> recorded.Seq = id)
                |> Option.bind (fun recorded -> Session.inputOf recorded.Event)
        }

    /// Waits until the log records the request's resolution after a sequence number (the event that
    /// opened it), and returns it.
    let resolution<'R when 'R :> IScope>
        (log: EventLog<Event>)
        (stream: StreamId)
        (after: int64)
        (request: string)
        : Eff<'R, Resolution> =
        eff {
            let found = ref None

            let! _ =
                EventLog.subscribe log stream after (fun recorded ->
                    eff {
                        match recorded.Event with
                        | Event.RequestResolved resolved when resolved.Request = request ->
                            found.Value <- Some resolved.Resolution
                            return false
                        | _ -> return true
                    })

            return
                match found.Value with
                | Some resolution -> resolution
                | None -> invalidOp ("The subscription for request " + request + " ended without its resolution.")
        }
