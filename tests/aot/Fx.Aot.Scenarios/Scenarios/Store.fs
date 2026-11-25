/// Fx.Store and Fx.Store.Live (T105, docs/modules/store.md): the SQLite event store (appends,
/// duplicates, conflicts, reopening, upcasting old events, subscriptions across threads), snapshots
/// and projections, and the file blob store. Stores live in a fresh temporary directory whose path is
/// never printed; times printed are the fixed ones the scenarios write.
module Fx.Aot.Scenarios.Store

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Aot.Scenarios.Show
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Machines
open Fx.Machines.Host
open Fx.Sqlite
open Fx.Store
open Fx.Store.Live

type Env =
    {
        Clock: Clock
        Blobs: Blobs
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IBlobs with
        member env.Blobs = env.Blobs

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "Env"

/// Version 1 wrote `{"kind": "added", "name", "qty"}`; version 2 calls it `count` and adds `removed`.
type ItemV1 =
    | AddedV1 of name: string * qty: int

    override this.ToString() =
        match this with
        | AddedV1(name, qty) -> $"AddedV1 {name} {qty}"

type Item =
    | Added of name: string * count: int
    | Removed of name: string

    override this.ToString() =
        match this with
        | Added(name, count) -> $"Added {name} {count}"
        | Removed name -> $"Removed {name}"

let private codecV1: Codec<ItemV1> =
    Codec.union "kind" {
        let! added =
            Codec.case2
                "added"
                (fun name qty -> AddedV1(name, qty))
                (Field.required "name" Codec.string)
                (Field.required "qty" Codec.int)

        return
            fun value ->
                match value with
                | AddedV1(name, qty) -> added name qty
    }

let private codec: Codec<Item> =
    Codec.union "kind" {
        let! added =
            Codec.case2
                "added"
                (fun name count -> Added(name, count))
                (Field.required "name" Codec.string)
                (Field.required "count" Codec.int)

        and! removed = Codec.case1 "removed" Removed (Field.required "name" Codec.string)

        return
            fun value ->
                match value with
                | Added(name, count) -> added name count
                | Removed name -> removed name
    }

let private renameQty: Upcaster =
    fun json ->
        match json with
        | Json.Object members ->
            Ok(
                Json.Object(
                    members
                    |> List.map (fun (name, value) -> (if name = "qty" then "count" else name), value)
                )
            )
        | other -> Error("not an object: " + Json.kind other)

let private schemaV1 = EventSchema.create codecV1
let private schema = schemaV1 |> EventSchema.evolve renameQty codec
let private none = CancellationToken.None

let private wait (task: Task<'T>) =
    if not (task.Wait(TimeSpan.FromSeconds 30.0)) then
        failwith "timed out"

    task.Result

let private meta (key: string) (second: int64) : EventMeta =
    {
        At = Instant.ofUnixMicroseconds (1_764_000_000_000_000L + second * 1_000_000L + 123L)
        By = Attribution.create "user:alice" "tui"
        Key = IdempotencyKey key
        Cause =
            if second > 1L then
                Some(EventRef.create (StreamId "run/1") second)
            else
                None
    }

let private either (ok: 'T -> string) (error: 'E -> string) (result: Result<'T, 'E>) : string =
    match result with
    | Ok v -> "ok " + ok v
    | Error e -> "error " + error e

/// A fresh temporary directory, deleted afterwards. Its path is never printed.
let private inTempDir (run: string -> string list) : string list =
    let dir =
        Path.Combine(Path.GetTempPath(), "store-scenario-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    try
        run dir
    finally
        try
            Directory.Delete(dir, true)
        with :? IOException ->
            ()

let private openStore (path: string) (durability: Durability) =
    match
        SqliteEventStore.openFile
            path
            { EventStoreOptions.defaults with
                Durability = durability
            }
    with
    | Ok store -> store
    | Error error -> failwith (string error)

let private readAll (log: EventLog<'Evt>) (stream: StreamId) = wait (log.Read stream 0L 1000 none)

let events =
    Scenario.lines "store-events" (fun () ->
        inTempDir (fun dir ->
            let path = Path.Combine(dir, "events.db")
            let stream = StreamId "cart/1"
            let first = openStore path Durability.Full
            let old = EventLog.typed schemaV1 first.Log

            let appended (label: string) (result: Result<int64, AppendError>) =
                label + ": " + either string string result

            let lines =
                [
                    appended
                        "v1 commit"
                        (wait (old.Append stream 0L (meta "k1" 1L) [ AddedV1("apple", 3); AddedV1("pear", 1) ] none))
                    let log = EventLog.typed schema first.Log
                    appended "v2 commit" (wait (log.Append stream 2L (meta "k2" 2L) [ Removed "pear" ] none))
                    appended "duplicate" (wait (log.Append stream 3L (meta "k1" 3L) [ Removed "apple" ] none))
                    appended "stale head" (wait (log.Append stream 1L (meta "k3" 3L) [ Removed "apple" ] none))

                    appended
                        "other stream"
                        (wait (log.Append (StreamId "cart/2") 0L (meta "k1" 4L) [ Added("fig", 9) ] none))

                    (try
                        log.Append stream 3L (meta "k4" 4L) [ Added(String('x', 2 <<< 20), 1) ] none
                        |> ignore

                        "too large: queued"
                     with :? ArgumentException ->
                         "too large: refused")

                    $"head {wait (log.Head stream none)}"
                ]

            first.Dispose()
            let second = openStore path Durability.Normal
            let log = EventLog.typed schema second.Log

            let reread =
                [
                    for recorded in readAll log stream do
                        yield "read " + escape (string recorded)

                    for recorded in readAll second.Log stream do
                        yield "stored " + escape (Codec.encodeText (Codecs.recorded Codecs.payload) recorded)

                    yield $"head after reopening {wait (log.Head stream none)}"
                    yield
                        "keys survive: "
                        + either string string (wait (log.Append stream 3L (meta "k2" 5L) [ Removed "x" ] none))

                    let unreadable =
                        wait (second.Log.Append stream 3L (meta "k9" 6L) [ { Version = 7; Data = Json.Null } ] none)

                    yield "a future version: " + either string string unreadable

                    yield
                        try
                            wait (log.Read stream 3L 10 none) |> ignore
                            "read it"
                        with :? AggregateException as e ->
                            match e.InnerException with
                            | :? UnreadableEventException as e -> "unreadable " + escape e.Message
                            | other -> "unexpected " + (nonNull other).GetType().Name
                ]

            second.Dispose()
            lines @ reread))

let subscribe =
    Scenario.task "store-subscribe" (fun () ->
        task {
            return
                inTempDir (fun dir ->
                    use store = openStore (Path.Combine(dir, "events.db")) Durability.Normal
                    let log = EventLog.typed (EventSchema.create Codec.int) store.Log
                    let stream = StreamId "numbers/1"

                    for i in 1..10 do
                        wait (log.Append stream (int64 i - 1L) (meta $"k{i}" 0L) [ i ] none) |> ignore

                    let seen = Collections.Concurrent.ConcurrentQueue<int64>()

                    let env =
                        {
                            Clock = SystemClock.clock
                            Blobs = Unchecked.defaultof<Blobs>
                            Scope = Scope.root
                        }

                    let following =
                        Eff.run
                            env
                            (EventLog.subscribe log stream 4L (fun recorded ->
                                eff {
                                    if recorded.Event <> int recorded.Seq then
                                        failwith "wrong event"

                                    seen.Enqueue recorded.Seq
                                    return recorded.Seq < 200L
                                }))

                    let writer =
                        Task.Run(fun () ->
                            for i in 11..200 do
                                wait (log.Append stream (int64 i - 1L) (meta $"k{i}" 0L) [ i ] none) |> ignore)

                    let last = wait following
                    wait (writer.ContinueWith(fun (_: Task) -> 0)) |> ignore
                    let seq = List.ofSeq seen

                    [
                        $"stopped at {last}"
                        $"saw {seq.Length} events, 5 to 200 in order: {seq = [ 5L .. 200L ]}"
                        $"waiting after: {(log.WaitPast stream 200L none).IsCompleted}"
                    ])
        })

type Cart =
    {
        Items: Map<string, int>
        Folded: int
    }

    override this.ToString() =
        let items =
            this.Items
            |> Seq.map (fun pair -> $"{pair.Key}={pair.Value}")
            |> String.concat ","

        $"{{{items}}} after {this.Folded}"

let private cartCodec: Codec<Cart> =
    Codec.record {
        let! items =
            Codec.field "items" (fun (c: Cart) -> c.Items) (Codec.stringMap Codec.int)

        and! folded = Codec.field "folded" (fun c -> c.Folded) Codec.int
        return { Items = items; Folded = folded }
    }

let private evolve (cart: Cart) (item: Item) =
    {
        Items =
            match item with
            | Added(name, count) -> cart.Items |> Map.add name count
            | Removed name -> cart.Items |> Map.remove name
        Folded = cart.Folded + 1
    }

let snapshots =
    Scenario.lines "store-snapshots" (fun () ->
        inTempDir (fun dir ->
            use store = openStore (Path.Combine(dir, "events.db")) Durability.Normal
            let log = EventLog.typed schema store.Log
            let stream = StreamId "cart/9"

            let add (first: int) (last: int) =
                for i in first..last do
                    wait (log.Append stream (int64 i - 1L) (meta $"k{i}" 0L) [ Added($"item{i % 5}", i) ] none)
                    |> ignore

            let env =
                {
                    Clock = SystemClock.clock
                    Blobs = Unchecked.defaultof<Blobs>
                    Scope = Scope.root
                }

            let run effect = wait (Eff.run env effect)

            let inspect schema =
                match run (Snapshots.inspect store.Snapshots schema stream) with
                | SnapshotLoad.Found snapshot -> "found " + string snapshot
                | other -> string other

            let v1 =
                Projection.create { Items = Map.empty; Folded = 0 } evolve (SnapshotSchema.create "cart" 1 cartCodec)

            let v2 =
                { v1 with
                    Snapshot = SnapshotSchema.create "cart" 2 cartCodec
                }

            let feed (projection: Projection<Cart, Item>) (after: int64) (last: int64) =
                let seen = ResizeArray<string>()

                let stopped =
                    run (
                        Projection.subscribe log store.Snapshots projection 3L stream after (fun item ->
                            eff {
                                seen.Add(string item)

                                return
                                    match item with
                                    | Feed.Next recorded -> recorded.Seq < last
                                    | Feed.Reset _ -> true
                            })
                    )

                $"stopped at {stopped}: " + String.concat "; " seen

            add 1 12

            let before =
                [
                    "inspect: " + inspect v1.Snapshot
                    "refresh: "
                    + string (run (Projection.refresh log store.Snapshots v1 10L stream))
                    "inspect: " + inspect v1.Snapshot
                ]

            add 13 15

            let after =
                [
                    "load: " + string (run (Projection.load log store.Snapshots v1 stream))
                    "older version: " + inspect v2.Snapshot
                    "rebuilt: " + string (run (Projection.refresh log store.Snapshots v2 1L stream))
                    "inspect: " + inspect v2.Snapshot
                ]

            add 16 17

            before
            @ after
            @ [
                "far behind: " + feed v2 1L 17L
                "close by: " + feed v2 14L 17L
                "older snapshot: " + feed v1 1L 3L
            ]))

let blobs =
    Scenario.lines "store-blobs" (fun () ->
        inTempDir (fun dir ->
            let root = Path.Combine(dir, "blobs")

            let store =
                match
                    FileBlobStore.openAt root SystemClock.clock (SystemIds.create SystemClock.clock) Durability.Full
                with
                | Ok store -> store
                | Error error -> failwith (string error)

            let blobs = store.Blobs

            let show (result: Result<byte array, BlobError>) =
                either (fun (b: byte array) -> escape (Encoding.UTF8.GetString b)) string result

            let abc = wait (blobs.Put (Encoding.UTF8.GetBytes "abc") none)
            let empty = wait (blobs.Put [||] none)
            let writer = wait (blobs.Create none)
            let large = Array.init (3 <<< 20) (fun i -> byte (i % 251))

            for chunk in Array.chunkBySize 100_000 large do
                wait (writer.Write chunk none)

            let chunked = wait (writer.Commit none)
            let missing = BlobHash.ofBytes (Encoding.UTF8.GetBytes "missing")

            let file =
                Path.Combine(root, "sha256", abc.Hash.Hex.Substring(0, 2), abc.Hash.Hex.Substring 2)

            let lines =
                [
                    "abc " + string abc
                    "empty " + string empty
                    "chunked " + string chunked
                    "same as put: " + string (wait (blobs.Put large none) = chunked)
                    "json " + Codec.encodeText Codecs.blobRef abc
                    "get " + show (wait (blobs.Get abc.Hash none))
                    "read " + show (wait (blobs.Read abc.Hash 1L 5 none))
                    "range of chunked: "
                    + either
                        (fun (b: byte array) -> $"{b.Length} bytes match {b = large[3_000_000..3_000_099]}")
                        string
                        (wait (blobs.Read chunked.Hash 3_000_000L 100 none))
                    "stat "
                    + (wait (blobs.Stat abc.Hash none)
                       |> Option.map string
                       |> Option.defaultValue "none")
                    "missing " + show (wait (blobs.Get missing none))
                    "missing stat "
                    + (wait (blobs.Stat missing none)
                       |> Option.map string
                       |> Option.defaultValue "none")
                    "index "
                    + (store.Index() |> List.map (fun b -> string b.Size) |> String.concat ",")
                ]

            File.WriteAllBytes(file, Encoding.UTF8.GetBytes "ABC")
            let damaged = "damaged " + show (wait (blobs.Get abc.Hash none))
            store.Dispose()
            lines @ [ damaged ]))

let scenarios = [ events; subscribe; snapshots; blobs ]
