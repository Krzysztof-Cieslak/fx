namespace Fx.Store

open Fx.Effects
open Fx.Machines
open Fx.Machines.Host

/// Actors whose state is snapshotted (spec §5: snapshot + tail): they start from the latest snapshot
/// they can read instead of replaying their stream from the first event, and keys of the commits
/// before it are looked up in the log (`Actor.startFrom`).
module ActorSnapshots =
    /// The latest snapshot of the actor's stream that `schema` can read, unless it is past the head (the
    /// log is the truth); the machine's initial state if there is none.
    let latest<'R, 'Cmd, 'Evt, 'S, 'Intent when 'R :> IScope>
        (snapshots: SnapshotStore)
        (schema: SnapshotSchema<'S>)
        (spec: ActorSpec<'R, 'Cmd, 'Evt, 'S, 'Intent>)
        : Eff<'R, Snapshot<'S>> =
        eff {
            let! head = EventLog.head spec.Log spec.Stream
            let! saved = Snapshots.load snapshots schema spec.Stream

            return
                match saved with
                | Some snapshot when snapshot.Seq <= head -> snapshot
                | _ ->
                    {
                        Seq = 0L
                        State = spec.Machine.Initial
                    }
        }

    /// Starts an actor from its latest snapshot (`latest`), then the events after it.
    let start<'R, 'Cmd, 'Evt, 'S, 'Intent when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (snapshots: SnapshotStore)
        (schema: SnapshotSchema<'S>)
        (spec: ActorSpec<'R, 'Cmd, 'Evt, 'S, 'Intent>)
        : Eff<'R, Actor<'Cmd, 'Evt, 'S>> =
        eff {
            let! from = latest snapshots schema spec
            return! Actor.startFrom from spec
        }

    /// Saves the actor's state (`Actor.snapshot`, always at a commit boundary) as its stream's snapshot,
    /// and returns its sequence number.
    let save<'R, 'Cmd, 'Evt, 'S when 'R :> IScope>
        (snapshots: SnapshotStore)
        (schema: SnapshotSchema<'S>)
        (actor: Actor<'Cmd, 'Evt, 'S>)
        : Eff<'R, int64> =
        eff {
            let snapshot = Actor.snapshot actor
            do! Snapshots.save snapshots schema actor.Stream snapshot
            return snapshot.Seq
        }
