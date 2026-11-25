namespace Fx.Host

open Fx.Effects
open Fx.Machines.Host
open Fx.Store
open Fx.Store.Live

/// The host's stores: one event log (every stream: sessions, their context, grants, environments,
/// the session index) with its snapshots, and the blob store (profiles, large content).
[<NoEquality; NoComparison>]
type HostStores =
    {
        Log: EventLog<Payload>
        Snapshots: SnapshotStore
        Blobs: Blobs
        /// Finishes queued writes and closes the files.
        Close: unit -> unit
    }

    override _.ToString() = "HostStores"

[<RequireQualifiedAccess>]
module HostStores =
    /// The live stores: `SqliteEventStore` at `Events` and `FileBlobStore` at `Blobs` (their
    /// directories must exist; `Host.start` creates them).
    let live (clock: Clock) (ids: Ids) (paths: HostPaths) (durability: StoreDurability) : Result<HostStores, string> =
        let durability =
            match durability with
            | StoreDurability.Full -> Durability.Full
            | StoreDurability.Normal -> Durability.Normal

        let options =
            { EventStoreOptions.defaults with
                Durability = durability
            }

        match SqliteEventStore.openFile paths.Events options with
        | Error e -> Error $"the event store at {paths.Events} did not open: {e}"
        | Ok events ->
            match FileBlobStore.openAt paths.Blobs clock ids durability with
            | Error e ->
                events.Dispose()
                Error $"the blob store at {paths.Blobs} did not open: {e}"
            | Ok blobs ->
                Ok
                    {
                        Log = events.Log
                        Snapshots = events.Snapshots
                        Blobs = blobs.Blobs
                        Close =
                            fun () ->
                                blobs.Dispose()
                                events.Dispose()
                    }
