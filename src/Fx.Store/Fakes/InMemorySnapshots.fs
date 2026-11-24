namespace Fx.Store.Fakes

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Fx.Machines
open Fx.Store

/// A snapshot store in memory, for tests and simulations. Thread-safe; every call completes
/// synchronously. Passes the same conformance suite as the SQLite store's snapshots.
[<Sealed>]
type InMemorySnapshots() =
    let gate = obj ()
    let snapshots = Dictionary<struct (StreamId * string), StoredSnapshot>()

    let save (stream: StreamId) (name: string) (snapshot: StoredSnapshot) (cancellation: CancellationToken) =
        cancellation.ThrowIfCancellationRequested()

        lock gate (fun () ->
            let key = struct (stream, name)

            match snapshots.TryGetValue key with
            | true, kept when kept.Version = snapshot.Version && kept.Seq > snapshot.Seq -> ()
            | _ -> snapshots[key] <- snapshot)

        Task.FromResult()

    let load (stream: StreamId) (name: string) (cancellation: CancellationToken) =
        cancellation.ThrowIfCancellationRequested()

        lock gate (fun () ->
            match snapshots.TryGetValue(struct (stream, name)) with
            | true, kept -> Some kept
            | false, _ -> None)
        |> Task.FromResult

    member val Store: SnapshotStore = { Save = save; Load = load }

    /// How many snapshots it keeps.
    member _.Count = lock gate (fun () -> snapshots.Count)

    override _.ToString() = "InMemorySnapshots"
