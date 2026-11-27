module Fx.Store.Live.Tests.EventStoreTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open FsCheck
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Machines.Host.Tests.Conformance
open Fx.Sqlite
open Fx.Store
open Fx.Store.Fakes
open Fx.Store.Live
open Fx.Store.Live.Tests.Support
open Fx.Store.Tests.Conformance
open Fx.Store.Tests.Notes

let private stream = StreamId "chat/1"
let private notesStream = Fx.Store.Tests.Notes.stream

/// Rows of the events table as `version data`, read with a connection of its own.
let private rawEvents (path: string) : string list =
    use db =
        match Database.connect path OpenOptions.defaults with
        | Ok db -> db
        | Error error -> failtest (string error)

    match
        Database.query db "SELECT version, data FROM events ORDER BY position" [] (fun row ->
            $"{row.Int64 0} {row.Text 1}")
    with
    | Ok rows -> rows
    | Error error -> failtest (string error)

let private pragma (path: string) (name: string) : string =
    use db =
        match Database.connect path OpenOptions.defaults with
        | Ok db -> db
        | Error error -> failtest (string error)

    match Database.tryFirst db ("PRAGMA " + name) [] (fun row -> row.Text 0) with
    | Ok(Some value) -> value
    | other -> failtest $"PRAGMA {name}: {other.IsOk}"

let private newPath () =
    Path.Combine(Scratch.directory (), "events.db")

/// Checks a stream written by concurrent writers: sequence numbers count from 1, each commit's
/// events are together, and no key has two commits.
let private checkCommits (events: Recorded<string> list) (eventsPerCommit: string -> int) =
    Expect.equal (events |> List.map _.Seq) [ 1L .. int64 events.Length ] "sequence numbers count from 1 without gaps"

    let commits =
        events
        |> List.fold
            (fun (commits: (string * string list) list) recorded ->
                match commits with
                | (key, values) :: rest when key = recorded.Meta.Key.Value ->
                    (key, values @ [ recorded.Event ]) :: rest
                | _ -> (recorded.Meta.Key.Value, [ recorded.Event ]) :: commits)
            []
        |> List.rev

    let keys = commits |> List.map fst
    Expect.equal (List.distinct keys).Length keys.Length "no key has two commits"

    for key, values in commits do
        Expect.equal values.Length (eventsPerCommit key) $"commit {key} is whole"

        Expect.isTrue
            (values
             |> List.forall (fun value -> value.StartsWith(key + ".", StringComparison.Ordinal)))
            $"commit {key}"

    commits

let private subscribeConfig =
    { FsCheckConfig.defaultConfig with
        maxTest = 30
    }

[<Tests>]
let tests =
    testList
        "SqliteEventStore"
        [
            EventLogConformance.tests "SQLite" (fun () -> EventLog.typed strings (newStore ()).Log)

            SnapshotsConformance.tests "SQLite" (fun () -> (newStore ()).Snapshots)

            test "events, heads and metadata survive closing and reopening the store" {
                let path = newPath ()
                let first = openStore path
                let log = EventLog.typed strings first.Log

                let caused =
                    { meta "k2" with
                        Cause = Some(EventRef.create (StreamId "run/7") 3L)
                    }

                appendOk log stream 0L "k1" [ "a"; "b" ] |> ignore

                Expect.equal (wait (log.Append stream 2L caused [ "c" ] none)) (Ok 3L) "caused"
                appendOk log (StreamId "chat/2") 0L "k1" [ "x" ] |> ignore
                let before = readAll log stream
                first.Dispose()

                use second = openStore path
                let log = EventLog.typed strings second.Log
                Expect.equal (readAll log stream) before "the same events"
                Expect.equal (before |> List.map (fun e -> e.Event)) [ "a"; "b"; "c" ] "events"
                Expect.equal before[2].Meta caused "metadata"
                Expect.equal (wait (log.Head stream none)) 3L "head"
                Expect.equal (wait (log.Head (StreamId "chat/2") none)) 1L "other head"
                Expect.equal (wait (log.Head (StreamId "chat/3") none)) 0L "no stream"
            }

            test "the database uses write-ahead logging and records its schema version" {
                let path = newPath ()
                use _ = openStore path
                Expect.equal (pragma path "journal_mode") "wal" "WAL"
                Expect.equal (pragma path "user_version") "1" "schema version"
            }

            test "payloads are stored as canonical JSON with the version that wrote them, never rewritten" {
                let path = newPath ()
                let first = openStore path
                let old = EventLog.typed schemaV1 first.Log
                appendOk old stream 0L "k1" [ AddedV1 "milk"; RemovedV1 0 ] |> ignore

                first.Log.Append
                    stream
                    2L
                    (meta "k2")
                    [
                        {
                            Version = 2
                            Data = Json.Object [ "kind", Json.String "added"; "body", Json.String "eggs" ]
                        }
                    ]
                    none
                |> wait
                |> ignore

                first.Dispose()

                use second = openStore path
                let current = EventLog.typed schema second.Log

                Expect.equal
                    (readAll current stream |> List.map _.Event)
                    [ Added "milk"; Removed 0; Added "eggs" ]
                    "read with the current schema"

                Expect.equal
                    (rawEvents path)
                    [
                        """1 {"kind":"added","text":"milk"}"""
                        """1 {"index":0,"kind":"removed"}"""
                        """2 {"body":"eggs","kind":"added"}"""
                    ]
                    "stored as written, canonical"
            }

            test "an event no version of the schema reads fails the read" {
                use store = newStore ()

                store.Log.Append stream 0L (meta "k1") [ { Version = 3; Data = Json.Null } ] none
                |> wait
                |> ignore

                let thrown =
                    try
                        wait ((EventLog.typed schema store.Log).Read stream 0L 10 none) |> ignore
                        None
                    with :? UnreadableEventException as e ->
                        Some e.Message

                Expect.equal
                    thrown
                    (Some "chat/1#1: written by schema version 3; this reader knows versions up to 2")
                    "unreadable"
            }

            test "idempotency keys survive restarts" {
                let path = newPath ()
                let first = openStore path

                appendOk (EventLog.typed strings first.Log) stream 0L "k1" [ "a"; "b" ]
                |> ignore

                first.Dispose()

                use second = openStore path
                let log = EventLog.typed strings second.Log
                let duplicate = Error(Duplicate(IdempotencyKey "k1", { After = 0L; Count = 2 }))
                Expect.equal (wait (log.Append stream 2L (meta "k1") [ "c" ] none)) duplicate "at the head"
                Expect.equal (wait (log.Append stream 0L (meta "k1") [ "c" ] none)) duplicate "at a stale head"
                Expect.equal (wait (log.Head stream none)) 2L "nothing appended"
            }

            test "two stores on one file: the head check rejects the writer that is behind" {
                let path = newPath ()
                use a = openStore path
                use b = openStore path
                let logA = EventLog.typed strings a.Log
                let logB = EventLog.typed strings b.Log
                appendOk logA stream 0L "a1" [ "a1.1" ] |> ignore

                Expect.equal
                    (wait (logB.Append stream 0L (meta "b1") [ "b1.1" ] none))
                    (Error(Conflict(0L, 1L)))
                    "behind"

                Expect.equal (wait (logB.Append stream 1L (meta "b1") [ "b1.1" ] none)) (Ok 2L) "caught up"
                Expect.equal (wait (logA.Head stream none)) 2L "the other store sees it"
            }

            test "concurrent writers through two stores on one file: every commit lands once, whole" {
                let path = newPath ()
                use a = openStore path
                use b = openStore path
                let logs = [| EventLog.typed strings a.Log; EventLog.typed strings b.Log |]

                /// Appends the commit, with a fresh head after each conflict.
                let land (log: EventLog<string>) (key: string) =
                    let mutable written = false

                    while not written do
                        let head = wait (log.Head stream none)

                        match wait (log.Append stream head (meta key) [ key + ".1"; key + ".2" ] none) with
                        | Ok _ -> written <- true
                        | Error(Conflict _) -> ()
                        | Error error -> failtest (string error)

                // In rounds: eight writers, four per store, land one commit each and meet at the head
                // check. Free-running, one store's writer thread, kept busy by its own four, held the
                // write lock for all their commits, longer than the other's busy timeout: SQLite polls
                // for the lock, without a queue, so two stores on a file are not fair (store.md).
                for i in 1..25 do
                    let round = [| for w in 0..7 -> Task.Run(fun () -> land logs[w % 2] $"w{w}-{i}") |]
                    Expect.isTrue (Task.WaitAll(round, TimeSpan.FromMinutes 1.0)) $"round {i} finished"

                let commits = checkCommits (readAll logs[0] stream) (fun _ -> 2)
                Expect.equal commits.Length 200 "every commit"

                for w in 0..7 do
                    Expect.equal
                        (commits
                         |> List.map fst
                         |> List.filter (fun key -> key.StartsWith($"w{w}-", StringComparison.Ordinal)))
                        [ for i in 1..25 -> $"w{w}-{i}" ]
                        $"writer {w} in order"
            }

            test "concurrent appends to many streams are grouped into commits, and all land" {
                use store = newStore ()
                let log = EventLog.typed strings store.Log

                let writer (s: int) =
                    Task.Run(fun () ->
                        let stream = StreamId $"many/{s}"

                        for i in 1..50 do
                            appendOk log stream (int64 i - 1L) $"k{i}" [ $"{s}.{i}" ] |> ignore)

                Expect.isTrue (Task.WaitAll([| for s in 1..16 -> writer s |], TimeSpan.FromMinutes 2.0)) "finished"

                for s in 1..16 do
                    Expect.equal
                        (readAll log (StreamId $"many/{s}") |> List.map _.Event)
                        [ for i in 1..50 -> $"{s}.{i}" ]
                        $"stream {s}"
            }

            test "an event over the size limit is refused before it is queued" {
                use store =
                    SqliteEventStore.openFile
                        (newPath ())
                        { EventStoreOptions.defaults with
                            MaxEventBytes = 100
                        }
                    |> function
                        | Ok store -> store
                        | Error error -> failtest (string error)

                let log = EventLog.typed strings store.Log

                Expect.throwsT<ArgumentException>
                    (fun () -> log.Append stream 0L (meta "k1") [ String('x', 200) ] none |> ignore)
                    "too large"

                Expect.equal (appendOk log stream 0L "k2" [ String('x', 90) ]) 1L "small enough"
            }

            test "JSON nested too deeply to read back is refused before it is written" {
                use store = newStore ()

                let nested (depth: int) =
                    Seq.fold (fun json _ -> Json.Array [ json ]) Json.Null (seq { 1..depth })

                let payload depth = { Version = 1; Data = nested depth }

                Expect.throwsT<ArgumentException>
                    (fun () -> store.Log.Append stream 0L (meta "k1") [ payload 129 ] none |> ignore)
                    "an event"

                Expect.throwsT<ArgumentException>
                    (fun () ->
                        store.Snapshots.Save
                            stream
                            "view"
                            {
                                Seq = 1L
                                Version = 1
                                State = nested 200
                            }
                            none
                        |> ignore)
                    "a snapshot"

                Expect.equal (wait (store.Log.Append stream 0L (meta "k2") [ payload 128 ] none)) (Ok 1L) "at the limit"
                Expect.equal (wait (store.Log.Read stream 0L 1 none)).Head.Event (payload 128) "read back"
            }

            test "cancelled calls fail with a cancellation and write nothing" {
                use store = newStore ()
                use source = new CancellationTokenSource()
                source.Cancel()
                let log = EventLog.typed strings store.Log
                Expect.isTrue (log.Append stream 0L (meta "k1") [ "a" ] source.Token).IsCanceled "append"
                Expect.isTrue (log.Read stream 0L 10 source.Token).IsCanceled "read"
                Expect.isTrue (log.Head stream source.Token).IsCanceled "head"
                Expect.isTrue (log.WaitPast stream 0L source.Token).IsCanceled "wait"
                Expect.equal (wait (log.Head stream none)) 0L "nothing written"
            }

            test "closing the store finishes queued appends, fails waiters, and refuses later calls" {
                let store = openStore (newPath ())
                let log = EventLog.typed strings store.Log
                let waiting = log.WaitPast (StreamId "never") 0L none

                let appends =
                    [
                        for i in 1..50 -> log.Append (StreamId $"closing/{i}") 0L (meta "k") [ "x" ] none
                    ]

                store.Dispose()

                Expect.isTrue
                    (appends |> List.forall (fun append -> append.IsCompletedSuccessfully))
                    "queued appends finished"

                Expect.isTrue (appends |> List.forall (fun append -> append.Result = Ok 1L)) "and were written"
                Expect.throwsT<ObjectDisposedException> (fun () -> wait waiting |> ignore) "waiter"

                Expect.throwsT<ObjectDisposedException>
                    (fun () -> wait (log.Append stream 0L (meta "k") [ "x" ] none) |> ignore)
                    "append"

                Expect.throwsT<ObjectDisposedException> (fun () -> wait (log.Read stream 0L 1 none) |> ignore) "read"
                store.Dispose()
            }

            test "a database of a newer schema version, or a file that is no database, is refused" {
                let newer = newPath ()

                do
                    use db =
                        match Database.connect newer OpenOptions.defaults with
                        | Ok db -> db
                        | Error error -> failtest (string error)

                    Database.exec db "PRAGMA user_version = 7" |> ignore

                match SqliteEventStore.openFile newer EventStoreOptions.defaults with
                | Error(OpenError.NewerSchema(7, 1)) -> ()
                | Error other -> failtest (string other)
                | Ok store ->
                    store.Dispose()
                    failtest "opened"

                let text = newPath ()
                File.WriteAllText(text, String('x', 4096))

                match SqliteEventStore.openFile text EventStoreOptions.defaults with
                | Error(OpenError.Database error) -> Expect.equal error.Code ErrorCode.NotADatabase "not a database"
                | Error other -> failtest (string other)
                | Ok store ->
                    store.Dispose()
                    failtest "opened"
            }

            test "normal durability stores events too" {
                let path = newPath ()

                let options =
                    { EventStoreOptions.defaults with
                        Durability = Durability.Normal
                    }

                match SqliteEventStore.openFile path options with
                | Error error -> failtest (string error)
                | Ok store ->
                    appendOk (EventLog.typed strings store.Log) stream 0L "k1" [ "a" ] |> ignore
                    store.Dispose()

                use reopened = openStore path
                Expect.equal (readAll (EventLog.typed strings reopened.Log) stream |> List.map _.Event) [ "a" ] "events"
            }

            test "a projection over the store loads from its snapshot, and rebuilds when the version changed" {
                let path = newPath ()
                let first = openStore path
                let log = EventLog.typed schema first.Log

                for i in 1..10 do
                    appendOk log notesStream (int64 i - 1L) $"k{i}" [ Added $"n{i}" ] |> ignore

                let env = env (InMemoryBlobs().Blobs)
                // A snapshot that says it folded nothing, so a load shows how much it folded on top.
                let marked =
                    {
                        Seq = 7L
                        State =
                            {
                                Items = [ for i in 1..7 -> $"n{i}" ]
                                Folded = 0
                            }
                    }

                runIn env (Snapshots.save first.Snapshots snapshotSchema notesStream marked)
                first.Dispose()

                use second = openStore path
                let log = EventLog.typed schema second.Log
                let loaded = runIn env (Projection.load log second.Snapshots projection notesStream)
                Expect.equal loaded.State.Items [ for i in 1..10 -> $"n{i}" ] "notes"
                Expect.equal loaded.State.Folded 3 "the tail only"

                let changed =
                    { projection with
                        Snapshot = { snapshotSchema with Version = 2 }
                    }

                Expect.equal
                    (runIn env (Snapshots.inspect second.Snapshots changed.Snapshot notesStream))
                    (SnapshotLoad.OtherVersion 1)
                    "an older snapshot"

                let rebuilt =
                    runIn env (Projection.refresh log second.Snapshots changed 1L notesStream)

                Expect.equal rebuilt.State.Folded 10 "rebuilt from the log"

                Expect.equal
                    (runIn env (Snapshots.inspect second.Snapshots changed.Snapshot notesStream))
                    (SnapshotLoad.Found rebuilt)
                    "and saved in the new version"
            }

            // Durability is not what this tests: skip the syncs.
            let store =
                lazy
                    (newStoreWith
                        { EventStoreOptions.defaults with
                            Durability = Durability.Normal
                        })

            testPropertyWithConfig
                subscribeConfig
                "a subscriber replays, then follows appends from another thread, without gaps or repeats"
            <| fun (initial: PositiveInt list) (live: PositiveInt list) (start: NonNegativeInt) ->
                let log = EventLog.typed strings store.Value.Log
                let stream = StreamId("subscribe/" + Guid.NewGuid().ToString("N"))

                let sizes (counts: PositiveInt list) =
                    counts |> List.truncate 20 |> List.map (fun count -> 1 + count.Get % 4)

                let commits = sizes initial
                let later = sizes live @ [ 1 ]
                let head = ref 0L

                let commit (n: int) (size: int) =
                    head.Value <- appendOk log stream head.Value $"k{n}" [ for j in 1..size -> $"{n}.{j}" ]

                commits |> List.iteri commit
                let before = head.Value
                let after = int64 start.Get % (before + 1L)
                let total = before + int64 (List.sum later)
                let seen = ConcurrentQueue<int64>()

                let subscriber =
                    Eff.run
                        (env (InMemoryBlobs().Blobs))
                        (EventLog.subscribe log stream after (fun recorded ->
                            eff {
                                seen.Enqueue recorded.Seq
                                return recorded.Seq < total
                            }))

                let writer =
                    Task.Run(fun () -> later |> List.iteri (fun i size -> commit (commits.Length + i) size))

                Expect.isTrue (writer.Wait(TimeSpan.FromSeconds 30.0)) "writer finished"
                Expect.isTrue (subscriber.Wait(TimeSpan.FromSeconds 30.0)) "subscriber finished"
                Expect.equal subscriber.Result total "stopped at the last event"
                Expect.equal (List.ofSeq seen) [ after + 1L .. total ] "every event once, in order"
        ]
