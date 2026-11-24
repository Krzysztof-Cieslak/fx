namespace Fx.Store

open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host

/// A state as a snapshot store keeps it: its JSON, the version of the codec that wrote it, and the
/// sequence number of the last event folded into it.
type StoredSnapshot =
    {
        Seq: int64
        Version: int
        /// Canonical JSON.
        State: Json
    }

    override this.ToString() =
        "snapshot at " + string this.Seq + " v" + string this.Version

/// Where folded states of streams are kept, so that loading a long stream does not replay it from
/// the start (spec §5: snapshot + tail). One snapshot per stream and name: several views of one
/// stream (an actor's state, a client's projection) keep theirs apart. Snapshots are a cache: the
/// log stays the truth, and any snapshot can be dropped and rebuilt from it.
///
/// Interpreters: `InMemorySnapshots` (Fx.Store.Fakes) and the SQLite store's (`SqliteEventStore`,
/// Fx.Store.Live), in the database of the log it snapshots. Both pass `SnapshotsConformance`.
type SnapshotStore =
    {
        /// Keeps a snapshot as the latest for its stream and name. A snapshot of the same version at an
        /// earlier sequence number than the one kept is ignored, so concurrent savers never go back;
        /// a snapshot of another version always replaces it.
        Save: StreamId -> string -> StoredSnapshot -> CancellationToken -> Task<unit>
        /// The snapshot kept for a stream and name, of whatever version.
        Load: StreamId -> string -> CancellationToken -> Task<StoredSnapshot option>
    }

    override _.ToString() = "SnapshotStore"

/// How a state is snapshotted: the name its snapshots are kept under, and the codec with its version.
///
/// Bump the version whenever old snapshots would be read wrongly: the state's shape changed, or its
/// meaning did (`Evolve` changed). Snapshots of another version are ignored and the state is rebuilt
/// from the log, never mis-decoded.
type SnapshotSchema<'S> =
    {
        Name: string
        Version: int
        Codec: Codec<'S>
    }

    override this.ToString() =
        "SnapshotSchema(" + this.Name + " v" + string this.Version + ")"

/// What loading a snapshot found.
[<RequireQualifiedAccess>]
type SnapshotLoad<'S> =
    | Found of Snapshot<'S>
    /// None was saved.
    | Missing
    /// One of another version was saved; it is ignored.
    | OtherVersion of version: int
    /// One of this version was saved but the codec cannot read it; it is ignored.
    | Unreadable of reason: string

    override this.ToString() =
        match this with
        | Found snapshot -> "found at " + string snapshot.Seq
        | Missing -> "missing"
        | OtherVersion version -> "other version " + string version
        | Unreadable reason -> "unreadable: " + reason

module SnapshotSchema =
    let create (name: string) (version: int) (codec: Codec<'S>) : SnapshotSchema<'S> =
        if System.String.IsNullOrEmpty name then
            invalidArg (nameof name) "A snapshot schema needs a name."

        if version < 1 then
            invalidArg (nameof version) "Versions start at 1."

        {
            Name = name
            Version = version
            Codec = codec
        }

module Snapshots =
    /// The stored form of a state.
    let encode (schema: SnapshotSchema<'S>) (snapshot: Snapshot<'S>) : StoredSnapshot =
        {
            Seq = snapshot.Seq
            Version = schema.Version
            State = Json.canonicalize (schema.Codec.Encode snapshot.State)
        }

    /// What a stored snapshot holds for this schema.
    let decode (schema: SnapshotSchema<'S>) (stored: StoredSnapshot option) : SnapshotLoad<'S> =
        match stored with
        | None -> SnapshotLoad.Missing
        | Some stored when stored.Version <> schema.Version -> SnapshotLoad.OtherVersion stored.Version
        | Some stored ->
            match schema.Codec.Decode stored.State with
            | Ok state -> SnapshotLoad.Found { Seq = stored.Seq; State = state }
            | Error issues -> SnapshotLoad.Unreadable(Issue.renderAtMost 5 issues)

    let save<'R, 'S when 'R :> IScope>
        (store: SnapshotStore)
        (schema: SnapshotSchema<'S>)
        (stream: StreamId)
        (snapshot: Snapshot<'S>)
        : Eff<'R, unit> =
        fun env -> store.Save stream schema.Name (encode schema snapshot) env.Scope.Cancellation

    /// What the store holds for the stream under the schema's name.
    let inspect<'R, 'S when 'R :> IScope>
        (store: SnapshotStore)
        (schema: SnapshotSchema<'S>)
        (stream: StreamId)
        : Eff<'R, SnapshotLoad<'S>> =
        fun env ->
            task {
                let! stored = store.Load stream schema.Name env.Scope.Cancellation
                return decode schema stored
            }

    /// The latest snapshot this schema can read, if any.
    let load (store: SnapshotStore) (schema: SnapshotSchema<'S>) (stream: StreamId) : Eff<'R, Snapshot<'S> option> =
        inspect store schema stream
        |> Eff.map (function
            | SnapshotLoad.Found snapshot -> Some snapshot
            | _ -> None)
