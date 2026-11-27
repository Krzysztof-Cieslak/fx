/// Fx.Sqlite and the pinned SQLite (T006, docs/spikes/sqlite.md): linked statically into the native
/// binary, loaded as a shared library on the JIT. Every scenario prints what SQLite returned, so the
/// diff compares both builds of the library as well as the binding. File databases live in a fresh
/// temporary directory whose path is never printed.
module Fx.Aot.Scenarios.Sqlite

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Fx.Aot.Scenarios.Show
open Fx.Sqlite

let private show (result: Result<'T, SqliteError>) (format: 'T -> string) : string =
    match result with
    | Ok value -> "ok " + format value
    | Error error -> $"error {error.Code} {error.ExtendedCode}: {escape error.Message}"

/// A result whose error is not a SqliteError. Never interpolate a Result itself: FSharp.Core prints
/// unions with reflection.
let private either (ok: 'T -> string) (error: 'E -> string) (result: Result<'T, 'E>) : string =
    match result with
    | Ok v -> "ok " + ok v
    | Error e -> "error " + error e

let private value (result: Result<'T, SqliteError>) : 'T =
    match result with
    | Ok value -> value
    | Error error -> failwith (string error)

let private exceptionType (f: unit -> 'T) : string =
    try
        f () |> ignore
        "no exception"
    with e ->
        e.GetType().Name

let private exec (db: Database) (sql: string) = Database.exec db sql |> value

let private texts (db: Database) (sql: string) (args: Value list) : string list =
    Database.query db sql args (fun row -> row.Text 0) |> value

let private scalar (db: Database) (sql: string) (args: Value list) : int64 =
    Database.tryFirst db sql args (fun row -> row.Int64 0)
    |> value
    |> Option.defaultValue Int64.MinValue

/// A fresh temporary directory, deleted afterwards. Its path is never printed.
let private inTempDir (run: string -> string list) : string list =
    let dir =
        Path.Combine(Path.GetTempPath(), "sqlite-scenario-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    try
        run dir
    finally
        try
            Directory.Delete(dir, true)
        with :? IOException ->
            ()

let private openFile (path: string) =
    Database.connect path OpenOptions.defaults |> value

let library =
    Scenario.lines "sqlite-library" (fun () ->
        [
            kv "version" (Library.version ())
            $"version number = {Library.versionNumber ()}"
            $"pinned = {Library.version () = Library.PinnedVersion
                        && Library.sourceId () = Library.PinnedSourceId}"
            kv "source id" (Library.sourceId ())
            $"threadsafe = {Library.isThreadsafe ()}"
            yield! Library.compileOptions () |> List.map (fun o -> "option " + o)
        ])

let values =
    Scenario.lines "sqlite-values" (fun () ->
        use db = Database.memory ()

        let echo (v: Value) =
            Database.tryFirst db "SELECT ?, typeof(?1)" [ v ] (fun row -> row.Value 0, row.Text 1)
            |> value
            |> Option.get

        let echoes =
            [
                Value.Null
                Value.Integer 0L
                Value.Integer Int64.MinValue
                Value.Integer Int64.MaxValue
                Value.Real 0.1
                Value.Real(-0.0)
                Value.Real Double.MaxValue
                Value.Real Double.Epsilon
                Value.Real Double.PositiveInfinity
                Value.Real Double.NaN
                Value.Text ""
                Value.Text "naïve façade, 日本語, emoji 🎉, nul \000 inside"
                Value.Blob [||]
                Value.Blob [| 0uy; 1uy; 254uy; 255uy |]
            ]
            |> List.map (fun v ->
                let back, kind = echo v
                $"{escape (string v)} -> {escape (string back)} ({kind})")

        exec db "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL, data BLOB, score REAL) STRICT"
        let payload = Array.init (1 <<< 20) (fun i -> byte (i * 31))

        let inserted =
            [ 1..3 ]
            |> List.map (fun i ->
                Database.execute
                    db
                    "INSERT INTO t (name, data, score) VALUES (?, ?, ?)"
                    [ Value.Text $"row {i}"; Value.Blob payload; Value.Real(float i / 4.0) ]
                |> value)

        let rows =
            Database.query db "SELECT id, name, length(data), score FROM t ORDER BY id DESC" [] (fun row ->
                $"{row.Int64 0} {row.Text 1} {row.Int64 2} {row.Double 3}")
            |> value

        let blob =
            Database.tryFirst db "SELECT data FROM t WHERE id = ?" [ Value.Integer 2L ] (fun row -> row.Blob 0)
            |> value
            |> Option.get

        let conversions =
            Database.tryFirst db "SELECT 42, '7', 2.5, x'41'" [] (fun row ->
                $"{row.Text 0} {row.Int64 1} {row.Int64 2} {row.Text 3} {row.Name 0}")
            |> value
            |> Option.get

        [
            yield! echoes
            "inserted = "
            + (inserted |> List.map string |> String.concat ",")
            + $", last rowid = {Database.lastInsertRowId db}, total changes = {Database.totalChanges db}"
            yield! rows
            $"blob = {blob.Length} bytes, sha256 {Convert.ToHexString(SHA256.HashData blob)}"
            $"conversions = {conversions}"
            "datatype mismatch = "
            + show (Database.execute db "INSERT INTO t (name, score) VALUES ('x', 'high')" []) string
        ])

let errors =
    Scenario.lines "sqlite-errors" (fun () ->
        use db = Database.memory ()

        exec
            db
            "CREATE TABLE commands (key TEXT PRIMARY KEY) STRICT;
             CREATE TABLE a (id INTEGER PRIMARY KEY);
             CREATE TABLE b (a INTEGER REFERENCES a(id));
             INSERT INTO commands VALUES ('k1')"

        let duplicate =
            Database.execute db "INSERT INTO commands VALUES (?)" [ Value.Text "k1" ]

        use statement = Database.prepare db "SELECT ?" |> value

        let nested =
            exceptionType (fun () ->
                Statement.query statement [ Value.Integer 1L ] (fun _ ->
                    Statement.query statement [ Value.Integer 2L ] ignore))

        let again = Statement.query statement [ Value.Integer 3L ] (fun row -> row.Int64 0)

        let fileErrors =
            inTempDir (fun dir ->
                let text = Path.Combine(dir, "text.db")
                File.WriteAllText(text, String('x', 4096))
                let readOnlyPath = Path.Combine(dir, "ro.db")

                do
                    use db = openFile readOnlyPath
                    exec db "CREATE TABLE t (x)"

                let readOnly =
                    Database.connect
                        readOnlyPath
                        { OpenOptions.defaults with
                            Access = Access.ReadOnly
                        }
                    |> value

                let lines =
                    [
                        "missing read-only = "
                        + show
                            (Database.connect
                                (Path.Combine(dir, "missing.db"))
                                { OpenOptions.defaults with
                                    Access = Access.ReadOnly
                                })
                            (fun _ -> "opened")
                        "not a database = "
                        + show (Database.connect text OpenOptions.defaults) (fun _ -> "opened")
                        "write read-only = "
                        + show (Database.execute readOnly "INSERT INTO t VALUES (1)" []) string
                    ]

                readOnly.Dispose()
                lines)

        [
            "duplicate = " + show duplicate string
            "unique violation = "
            + either string (SqliteError.isUniqueViolation >> string) duplicate
            "foreign key = "
            + show (Database.execute db "INSERT INTO b VALUES (1)" []) string
            "syntax = " + show (Database.execute db "SELEC 1" []) string
            "no table = "
            + show (Database.query db "SELECT * FROM nowhere" [] ignore) (fun _ -> "rows")
            "two statements = " + show (Database.execute db "SELECT 1; SELECT 2" []) string
            "empty = " + show (Database.execute db " " []) string
            yield! fileErrors
            "argument count = "
            + exceptionType (fun () -> Database.execute db "SELECT ?, ?" [ Value.Integer 1L ])
            "nested statement = " + nested
            "statement again = " + show again (List.map string >> String.concat ",")
            "read NULL as integer = "
            + exceptionType (fun () -> Database.query db "SELECT NULL" [] (fun row -> row.Int64 0))
            "column out of range = "
            + exceptionType (fun () -> Database.query db "SELECT 1" [] (fun row -> row.Int64 1))
            "finalized = "
            + exceptionType (fun () ->
                statement.Dispose()
                Statement.execute statement [ Value.Null ])
            "closed = "
            + exceptionType (fun () ->
                db.Dispose()
                Database.execute db "SELECT 1" [])
        ])

let transactions =
    Scenario.lines "sqlite-transactions" (fun () ->
        inTempDir (fun dir ->
            let path = Path.Combine(dir, "tx.db")

            let options =
                { OpenOptions.defaults with
                    BusyTimeout = TimeSpan.Zero
                }

            use db = Database.connect path options |> value
            use other = Database.connect path options |> value
            exec db "CREATE TABLE t (x INTEGER)"

            let insert (db: Database) (x: int64) =
                Database.execute db "INSERT INTO t VALUES (?)" [ Value.Integer x ]
                |> Result.map ignore

            let rows () =
                Database.query db "SELECT x FROM t ORDER BY x" [] (fun row -> string (row.Int64 0))
                |> value
                |> String.concat ","

            let committed =
                Database.transaction db TransactionKind.Immediate string (fun () ->
                    insert db 1L |> Result.mapError string |> Result.map (fun () -> "committed"))

            let rolledBack: Result<unit, string> =
                Database.transaction db TransactionKind.Deferred string (fun () ->
                    insert db 2L |> value
                    Error "conflict")

            let thrown =
                exceptionType (fun () ->
                    Database.transaction db TransactionKind.Exclusive id (fun () ->
                        insert db 3L |> value
                        invalidOp "boom"))

            let nested =
                Database.transaction db TransactionKind.Immediate string (fun () ->
                    insert db 4L |> value

                    let inner: Result<unit, string> =
                        Database.transaction db TransactionKind.Immediate string (fun () ->
                            insert db 5L |> value
                            Error "inner fails")

                    let kept =
                        Database.transaction db TransactionKind.Immediate string (fun () ->
                            insert db 6L |> Result.mapError string)

                    Ok(
                        "inner "
                        + either string id inner
                        + ", kept "
                        + either string id kept
                        + $", open {Database.isInTransaction db}"
                    ))

            let busy =
                Database.transaction db TransactionKind.Immediate string (fun () ->
                    insert db 7L |> value

                    Ok(
                        show
                            (Database.transaction other TransactionKind.Immediate id (fun () -> insert other 8L))
                            string
                    ))

            let later =
                Database.transaction other TransactionKind.Immediate id (fun () -> insert other 8L)

            [
                "commit = " + either id id committed
                "error = " + either string id rolledBack
                $"exception = {thrown}"
                "nested = " + either id id nested
                "second writer while the first writes = " + either id id busy
                "second writer afterwards = " + show later string
                $"rows = {rows ()}"
                $"in transaction = {Database.isInTransaction db}"
            ]))

let wal =
    Scenario.lines "sqlite-wal" (fun () ->
        inTempDir (fun dir ->
            let path = Path.Combine(dir, "wal.db")
            use writer = openFile path
            let enabled = Database.useWal writer
            exec writer "PRAGMA wal_autocheckpoint=0; CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1)"
            use reader = openFile path
            let count (db: Database) = scalar db "SELECT count(*) FROM t" []

            let snapshot =
                Database.transaction reader TransactionKind.Deferred id (fun () ->
                    let before = count reader
                    exec writer "INSERT INTO t VALUES (2)"
                    Ok $"before {before}, during {count reader}")

            let after = count reader

            for i in 3L .. 100L do
                Database.execute writer "INSERT INTO t VALUES (?)" [ Value.Integer i ]
                |> value
                |> ignore

            let frames (c: Checkpoint) =
                $"log {c.LogFrames}, checkpointed {c.CheckpointedFrames}"

            let passive = Database.checkpoint writer CheckpointMode.Passive
            let truncate = Database.checkpoint writer CheckpointMode.Truncate
            let walSize = FileInfo(path + "-wal").Length
            use memory = Database.memory ()

            [
                "enable = " + show enabled string
                "memory = " + show (Database.useWal memory) string
                "journal mode = " + String.concat "," (texts reader "PRAGMA journal_mode" [])
                "reader snapshot = " + show snapshot id
                $"after the read transaction = {after}"
                "passive checkpoint = " + show passive frames
                "truncate checkpoint = " + show truncate frames
                $"log file bytes = {walSize}"
                "memory checkpoint = "
                + show (Database.checkpoint memory CheckpointMode.Passive) frames
                $"rows = {count writer}"
            ]))

let concurrency =
    Scenario.task "sqlite-concurrency" (fun () ->
        task {
            let dir =
                Path.Combine(Path.GetTempPath(), "sqlite-scenario-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory dir |> ignore

            try
                let path = Path.Combine(dir, "parallel.db")
                use writer = openFile path
                Database.useWal writer |> value
                exec writer "CREATE TABLE t (x INTEGER)"
                let readers = [ for _ in 1..3 -> openFile path ]
                use stop = new CancellationTokenSource()
                use started = new CountdownEvent(readers.Length)

                let reading =
                    readers
                    |> List.map (fun reader ->
                        Task.Run(fun () ->
                            let mutable odd = 0
                            let mutable reads = 0

                            while not stop.IsCancellationRequested do
                                if scalar reader "SELECT count(*) FROM t" [] % 2L <> 0L then
                                    odd <- odd + 1

                                reads <- reads + 1

                                if reads = 1 then
                                    started.Signal() |> ignore

                            odd))

                let readersStarted = started.Wait(TimeSpan.FromSeconds 30.0)

                for i in 1L .. 200L do
                    Database.transaction writer TransactionKind.Immediate id (fun () ->
                        Database.execute writer "INSERT INTO t VALUES (?), (?)" [ Value.Integer i; Value.Integer -i ]
                        |> Result.map ignore)
                    |> value

                stop.Cancel()
                let! odd = Task.WhenAll(reading).WaitAsync(TimeSpan.FromSeconds 30.0)

                for reader in readers do
                    reader.Dispose()

                // One connection shared by four threads: each transaction keeps it to itself.
                let shared (n: int64) =
                    Task.Run(fun () ->
                        for i in 1L .. 25L do
                            Database.transaction writer TransactionKind.Immediate string (fun () ->
                                let before = scalar writer "SELECT count(*) FROM t" []

                                Database.execute writer "INSERT INTO t VALUES (?)" [ Value.Integer(n * 1000L + i) ]
                                |> value
                                |> ignore

                                if scalar writer "SELECT count(*) FROM t" [] = before + 1L then
                                    Ok()
                                else
                                    Error "another thread wrote inside the transaction")
                            |> Result.defaultWith failwith)

                do! Task.WhenAll([ for n in 1L .. 4L -> shared n ]).WaitAsync(TimeSpan.FromSeconds 60.0)

                return
                    [
                        $"readers started = {readersStarted}"
                        $"reads that saw half a transaction = {Array.sum odd}"
                        "rows = " + string (scalar writer "SELECT count(*) FROM t" [])
                    ]
            finally
                try
                    Directory.Delete(dir, true)
                with :? IOException ->
                    ()
        })

let fullText =
    Scenario.lines "sqlite-fts5" (fun () ->
        use db = Database.memory ()

        exec
            db
            "CREATE VIRTUAL TABLE notes USING fts5(title, body);
             INSERT INTO notes VALUES
               ('Use SQLite', 'Store events in SQLite with WAL'),
               ('Kestrel', 'Serve WebSocket and SSE from Kestrel'),
               ('Ledger', 'Decisions are searched with full-text search over SQLite');
             CREATE TABLE decisions (id INTEGER PRIMARY KEY, question TEXT NOT NULL) STRICT;
             CREATE VIRTUAL TABLE decisions_fts USING fts5(question, content='decisions', content_rowid='id');
             CREATE TRIGGER decisions_ai AFTER INSERT ON decisions BEGIN
               INSERT INTO decisions_fts(rowid, question) VALUES (new.id, new.question);
             END;
             CREATE TRIGGER decisions_ad AFTER DELETE ON decisions BEGIN
               INSERT INTO decisions_fts(decisions_fts, rowid, question) VALUES ('delete', old.id, old.question);
             END;
             INSERT INTO decisions (question) VALUES ('Which database for events?'), ('Which shell on Windows?');"

        let search (query: string) =
            Database.query
                db
                "SELECT title, round(bm25(notes), 6), snippet(notes, 1, '<', '>', '...', 4) FROM notes WHERE notes MATCH ? ORDER BY rank"
                [ Value.Text query ]
                (fun row -> $"{row.Text 0} | {row.Double 1} | {row.Text 2}")

        let decisions (query: string) =
            texts
                db
                "SELECT d.question FROM decisions_fts f JOIN decisions d ON d.id = f.rowid WHERE decisions_fts MATCH ?"
                [ Value.Text query ]
            |> String.concat "; "

        let found = decisions "windows"
        Database.execute db "DELETE FROM decisions WHERE id = 2" [] |> value |> ignore

        [
            for query in
                [
                    "sqlite"
                    "\"full-text search\""
                    "web*"
                    "body:wal"
                    "kestrel NOT sse"
                    "\"unbalanced"
                ] do
                $"{query} = " + show (search query) (String.concat " / ")
            $"external content = {found}"
            "after delete = [" + decisions "windows" + "]"
        ])

let json =
    Scenario.lines "sqlite-json" (fun () ->
        use db = Database.memory ()

        let doc =
            """{"kind":"Opened","by":"alice","tags":["db","perf"],"n":3,"nested":{"ok":true}}"""

        let one (sql: string) =
            sql
            + " = "
            + show (Database.query db sql [ Value.Text doc ] (fun row -> row.Text 0)) (String.concat ",")

        [
            one "SELECT ? ->> '$.kind'"
            one "SELECT ? -> '$.nested'"
            one "SELECT json_extract(?, '$.n') * 2"
            one "SELECT json_array_length(?, '$.tags')"
            one "SELECT value FROM json_each(?, '$.tags')"
            one "SELECT key || '=' || type FROM json_each(?)"
            one "SELECT json_set(?, '$.n', 4, '$.new', json('[1]'))"
            one "SELECT json(jsonb(?))"
            one "SELECT length(jsonb(?))"
            one "SELECT json_group_object(key, value) FROM json_each(?) WHERE type = 'text'"
            one "SELECT json_valid(?)"
            "malformed = "
            + show (Database.query db "SELECT json('{bad')" [] ignore) (fun _ -> "rows")
        ])

let hardening =
    Scenario.lines "sqlite-hardening" (fun () ->
        use db = Database.memory ()
        exec db "CREATE TABLE t (x)"

        let pragma (name: string) =
            name + " = " + String.concat "," (texts db ("PRAGMA " + name) [])

        [
            "load_extension = "
            + show (Database.query db "SELECT load_extension('x')" [] ignore) (fun _ -> "rows")
            "double-quoted string = "
            + show (Database.query db "SELECT \"text\"" [] ignore) (fun _ -> "rows")
            "writable schema = "
            + show
                (Database.exec db "PRAGMA writable_schema=ON"
                 |> Result.bind (fun () ->
                     Database.execute db "UPDATE sqlite_schema SET sql = 'CREATE TABLE t (y)' WHERE name = 't'" []))
                string
            pragma "trusted_schema"
            pragma "foreign_keys"
            pragma "recursive_triggers"
            pragma "journal_mode"
            pragma "synchronous"
            pragma "page_size"
        ])

let copies =
    Scenario.lines "sqlite-copies" (fun () ->
        inTempDir (fun dir ->
            use source = openFile (Path.Combine(dir, "source.db"))
            exec source "CREATE TABLE t (x TEXT); INSERT INTO t VALUES ('a'), ('b')"
            let bytes = Database.serialize source |> value
            use copy = Database.memory ()
            let restored = Database.deserialize copy bytes
            exec copy "INSERT INTO t VALUES ('c')"
            use target = openFile (Path.Combine(dir, "target.db"))
            let backedUp = Database.backup copy target
            let vacuumPath = Path.Combine(dir, "vacuumed.db")
            let vacuumed = Database.execute source "VACUUM INTO ?" [ Value.Text vacuumPath ]
            use vacuum = openFile vacuumPath

            [
                $"serialized = {bytes.Length} bytes, header {escape (Text.Encoding.ASCII.GetString(bytes, 0, 16))}"
                "deserialize = " + show restored string
                "copy rows = " + String.concat "," (texts copy "SELECT x FROM t ORDER BY x" [])
                "backup = " + show backedUp string
                "backup rows = "
                + String.concat "," (texts target "SELECT x FROM t ORDER BY x" [])
                "vacuum into = " + show vacuumed string
                "vacuumed rows = "
                + String.concat "," (texts vacuum "SELECT x FROM t ORDER BY x" [])
                "deserialize garbage = "
                + show
                    (Database.deserialize copy (Array.create 1024 7uy)
                     |> Result.bind (fun () -> Database.query copy "SELECT * FROM t" [] ignore))
                    (fun _ -> "rows")
            ]))

/// Waits at most 30 s for an operation running on the database. One still running then is
/// interrupted, and the scenario fails: disposing the database would wait for it.
let private within (db: Database) (running: Task<'T>) : 'T =
    if not (running.Wait(TimeSpan.FromSeconds 30.0)) then
        Database.interrupt db
        running.Wait(TimeSpan.FromSeconds 30.0) |> ignore
        failwith "the statement was still running after 30 s"

    running.Result

let interrupt =
    Scenario.lines "sqlite-interrupt" (fun () ->
        use db = Database.memory ()
        use cancellation = new CancellationTokenSource()

        let forever =
            "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT count(*) FROM c"

        let running =
            Task.Run(fun () ->
                Database.cancellable db cancellation.Token (fun () -> Database.query db forever [] ignore))

        cancellation.CancelAfter(TimeSpan.FromMilliseconds 100.0)
        let cancelled = within db running

        // Cancelled after the check, before the statement starts: SQLite would forget an interrupt
        // with nothing running.
        use late = new CancellationTokenSource()

        let lately =
            within
                db
                (Task.Run(fun () ->
                    Database.cancellable db late.Token (fun () ->
                        late.Cancel()
                        Database.query db forever [] ignore)))

        let before =
            Database.cancellable db cancellation.Token (fun () -> Database.query db "SELECT 1" [] ignore)

        Database.interrupt db

        [
            "cancelled while running = " + show cancelled (fun _ -> "rows")
            "cancelled before it starts = " + show lately (fun _ -> "rows")
            "cancelled before = " + show before (fun _ -> "rows")
            "after an idle interrupt = "
            + show (Database.query db "SELECT 2" [] (fun row -> row.Int64 0)) (List.map string >> String.concat ",")
            "uncancellable = "
            + show
                (Database.cancellable db CancellationToken.None (fun () ->
                    Database.query db "SELECT 3" [] (fun row -> row.Int64 0)))
                (List.map string >> String.concat ",")
        ])

let scenarios =
    [
        library
        values
        errors
        transactions
        wal
        concurrency
        fullText
        json
        hardening
        copies
        interrupt
    ]
