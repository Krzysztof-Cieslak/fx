namespace Fx.Sqlite

#nowarn "9" // native pointers
#nowarn "51" // address-of for out parameters

open System
open System.Collections.Generic
open System.Runtime.InteropServices
open System.Threading
open Microsoft.FSharp.NativeInterop

/// How a database file is opened.
[<RequireQualifiedAccess>]
type Access =
    | ReadOnly
    | ReadWrite
    /// Read-write, creating the file if it does not exist.
    | Create

    override this.ToString() =
        match this with
        | ReadOnly -> "read-only"
        | ReadWrite -> "read-write"
        | Create -> "create"

type OpenOptions =
    {
        Access: Access
        /// Read the name as a `file:` URI (https://sqlite.org/uri.html), e.g. `file:data.db?mode=ro`.
        Uri: bool
        /// Refuse a path whose last component is a symbolic link.
        NoFollow: bool
        /// How long a statement waits for another connection's lock before it fails with `Busy`.
        BusyTimeout: TimeSpan
    }

    override this.ToString() =
        $"{this.Access}, uri {this.Uri}, no-follow {this.NoFollow}, busy timeout {this.BusyTimeout.TotalMilliseconds} ms"

module OpenOptions =
    /// Read-write, created if missing, a plain path, waiting up to 5 s for locks.
    let defaults =
        {
            Access = Access.Create
            Uri = false
            NoFollow = false
            BusyTimeout = TimeSpan.FromSeconds 5.0
        }

/// How a transaction takes its locks (https://sqlite.org/lang_transaction.html).
[<RequireQualifiedAccess>]
type TransactionKind =
    /// Locks on first read or write. A read transaction that later writes can fail with `Busy`.
    | Deferred
    /// Takes the write lock at once: the kind for read-modify-write.
    | Immediate
    /// Like `Immediate`; outside WAL mode it also keeps readers out.
    | Exclusive

    override this.ToString() =
        match this with
        | Deferred -> "DEFERRED"
        | Immediate -> "IMMEDIATE"
        | Exclusive -> "EXCLUSIVE"

/// How far a WAL checkpoint goes (https://sqlite.org/c3ref/wal_checkpoint_v2.html).
[<RequireQualifiedAccess>]
type CheckpointMode =
    /// As much as possible without waiting for readers or writers.
    | Passive
    /// Waits for writers, then copies every frame.
    | Full
    /// Like `Full`, then waits for readers so the next writer starts the log over.
    | Restart
    /// Like `Restart`, and truncates the log file to zero bytes.
    | Truncate

    override this.ToString() =
        match this with
        | Passive -> "passive"
        | Full -> "full"
        | Restart -> "restart"
        | Truncate -> "truncate"

/// What a checkpoint did: frames in the log, and how many of them are now in the database file.
/// Both are -1 when the database is not in WAL mode.
type Checkpoint =
    {
        LogFrames: int
        CheckpointedFrames: int
    }

/// A connection to a database. Operations lock the connection, so it may be shared between threads;
/// they run one at a time, and a transaction keeps the connection to itself until it ends. For
/// parallel reads, open more connections (WAL mode lets readers run beside the writer).
///
/// Statements run by SQL text (`Database.execute`, `query`, ...) are prepared once and cached.
[<Sealed>]
type Database internal (connection: Connection) =
    let cache = Dictionary<string, Statement>(StringComparer.Ordinal)

    /// The name it was opened with.
    member _.Name = connection.Name

    member internal _.Connection = connection
    member internal _.Cache = cache

    /// Finalizes the cached statements and closes the connection. Statements from `Database.prepare`
    /// keep it open until they are disposed too.
    member _.Dispose() =
        lock connection.Sync (fun () ->
            for statement in cache.Values do
                statement.Dispose()

            cache.Clear()
            connection.Handle.Dispose())

    interface IDisposable with
        member this.Dispose() = this.Dispose()

    override _.ToString() = "database " + connection.Name

module Database =
    /// How many statements a connection keeps prepared. Beyond that, statements are prepared per use.
    [<Literal>]
    let CacheCapacity = 128

    let private pinned = lazy (Library.ensurePinned ())

    let private interrupted = SqliteError.create 9 "interrupted"

    let private check (connection: Connection) (operation: string) (code: int) =
        if code <> Native.SQLITE_OK then
            raise (SqliteException(connection.Error code, operation))

    let private configure (connection: Connection) (options: OpenOptions) =
        let pointer = connection.Pointer
        let timeout = int (min options.BusyTimeout.TotalMilliseconds (float Int32.MaxValue))
        check connection "busy timeout" (Native.sqlite3_busy_timeout (pointer, max 0 timeout))

        let flag (op: int) (value: int) =
            let mutable current = -1
            check connection $"db_config {op}" (Native.sqlite3shim_db_config_flag (pointer, op, value, &&current))

            if current <> value then
                raise (SqliteException(SqliteError.create 21 $"db_config {op} stayed {current}", "configure"))

        // Defensive: no writable_schema, no shadow-table writes, no journal_mode=OFF, ...
        flag Native.SQLITE_DBCONFIG_DEFENSIVE 1
        // The compile-time defaults, set again so that they hold whatever the build.
        flag Native.SQLITE_DBCONFIG_TRUSTED_SCHEMA 0
        flag Native.SQLITE_DBCONFIG_DQS_DML 0
        flag Native.SQLITE_DBCONFIG_DQS_DDL 0

    /// Runs SQL that may hold several statements, without arguments or results (schema, pragmas).
    let exec (db: Database) (sql: string) : Result<unit, SqliteError> =
        let connection = db.Connection
        let text = Native.utf8z sql

        lock connection.Sync (fun () ->
            match
                Native.pinned text (fun sql ->
                    Native.sqlite3_exec (connection.Pointer, sql, 0n, 0n, NativePtr.nullPtr))
            with
            | Native.SQLITE_OK -> Ok()
            | code -> Error(connection.Error code))

    /// Opens a connection. Expected failures (a missing file, no permission, not a database) are
    /// errors. The connection is hardened (defensive mode, untrusted schema, no double-quoted strings)
    /// and its first read checks that the file is a database.
    let connect (name: string) (options: OpenOptions) : Result<Database, SqliteError> =
        pinned.Force()
        let path = Native.utf8z name

        let flags =
            Native.SQLITE_OPEN_FULLMUTEX
            ||| Native.SQLITE_OPEN_EXRESCODE
            ||| (match options.Access with
                 | Access.ReadOnly -> Native.SQLITE_OPEN_READONLY
                 | Access.ReadWrite -> Native.SQLITE_OPEN_READWRITE
                 | Access.Create -> Native.SQLITE_OPEN_READWRITE ||| Native.SQLITE_OPEN_CREATE)
            ||| (if options.Uri then Native.SQLITE_OPEN_URI else 0)
            ||| (if options.NoFollow then Native.SQLITE_OPEN_NOFOLLOW else 0)

        let struct (code, pointer) =
            Native.pinned path (fun text ->
                let mutable pointer = 0n
                let code = Native.sqlite3_open_v2 (text, &&pointer, flags, 0n)
                struct (code, pointer))

        if pointer = 0n then
            raise (OutOfMemoryException "SQLite could not allocate a connection")

        let connection = Connection(new ConnectionHandle(pointer), name)

        let opened =
            if code <> Native.SQLITE_OK then
                Error(connection.Error code)
            else
                try
                    configure connection options
                with _ ->
                    connection.Handle.Dispose()
                    reraise ()

                let db = new Database(connection)

                exec db "SELECT count(*) FROM sqlite_schema" |> Result.map (fun () -> db)

        match opened with
        | Ok db -> Ok db
        | Error error ->
            connection.Handle.Dispose()
            Error error

    /// A new, empty in-memory database.
    let memory () : Database =
        match connect ":memory:" OpenOptions.defaults with
        | Ok db -> db
        | Error error -> raise (SqliteException(error, "opening an in-memory database"))

    /// Prepares one statement for repeated use with `Statement.execute`, `query`, ... Dispose it.
    let prepare (db: Database) (sql: string) : Result<Statement, SqliteError> =
        Statement.prepare db.Connection sql true

    /// Runs `run` with the cached statement for the SQL, preparing it on first use.
    let private cached
        (db: Database)
        (sql: string)
        (run: Statement -> Result<'T, SqliteError>)
        : Result<'T, SqliteError> =
        let connection = db.Connection

        lock connection.Sync (fun () ->
            match db.Cache.TryGetValue sql with
            | true, statement when not statement.Running -> run statement
            | found, _ ->
                match Statement.prepare connection sql true with
                | Error error -> Error error
                | Ok statement when not found && db.Cache.Count < CacheCapacity ->
                    db.Cache.Add(sql, statement)
                    run statement
                | Ok statement ->
                    // Already running (a nested use of the same SQL), or the cache is full.
                    use statement = statement
                    run statement)

    /// Runs one statement and returns how many rows it inserted, updated or deleted.
    let execute (db: Database) (sql: string) (args: Value list) : Result<int64, SqliteError> =
        cached db sql (fun statement -> Statement.execute statement args)

    /// Runs one statement and reads every row.
    let query (db: Database) (sql: string) (args: Value list) (read: Row -> 'T) : Result<'T list, SqliteError> =
        cached db sql (fun statement -> Statement.query statement args read)

    /// Runs one statement and reads its first row, if any.
    let tryFirst (db: Database) (sql: string) (args: Value list) (read: Row -> 'T) : Result<'T option, SqliteError> =
        cached db sql (fun statement -> Statement.tryFirst statement args read)

    /// Runs one statement and folds over its rows.
    let fold
        (db: Database)
        (sql: string)
        (args: Value list)
        (state: 'S)
        (folder: 'S -> Row -> 'S)
        : Result<'S, SqliteError> =
        cached db sql (fun statement -> Statement.fold statement args state folder)

    /// Whether a transaction is open.
    let isInTransaction (db: Database) : bool =
        lock db.Connection.Sync (fun () -> Native.sqlite3_get_autocommit db.Connection.Pointer = 0)

    /// The rowid of the last row inserted on this connection.
    let lastInsertRowId (db: Database) : int64 =
        lock db.Connection.Sync (fun () -> Native.sqlite3_last_insert_rowid db.Connection.Pointer)

    /// How many rows this connection has inserted, updated or deleted since it opened.
    let totalChanges (db: Database) : int64 =
        lock db.Connection.Sync (fun () -> Native.sqlite3_total_changes64 db.Connection.Pointer)

    let private runOrDefect (db: Database) (sql: string) =
        match execute db sql [] with
        | Ok _ -> ()
        | Error error -> raise (SqliteException(error, sql))

    /// Runs `body` in a transaction: commits if it returns `Ok`, rolls back if it returns `Error` or
    /// throws. Inside another transaction it runs in a savepoint, so it nests. `ofError` turns the
    /// database's own failures (BEGIN or COMMIT, e.g. `Busy`) into the body's error type.
    ///
    /// The transaction holds the connection: other threads wait until it ends. Keep `body` short and
    /// synchronous.
    let transaction
        (db: Database)
        (kind: TransactionKind)
        (ofError: SqliteError -> 'E)
        (body: unit -> Result<'T, 'E>)
        : Result<'T, 'E> =
        let connection = db.Connection

        lock connection.Sync (fun () ->
            let nested = Native.sqlite3_get_autocommit connection.Pointer = 0
            let savepoint = "s" + string (connection.SavepointDepth + 1)

            let start, commit, rollback =
                if nested then
                    "SAVEPOINT " + savepoint,
                    "RELEASE " + savepoint,
                    [ "ROLLBACK TO " + savepoint; "RELEASE " + savepoint ]
                else
                    "BEGIN " + string kind, "COMMIT", [ "ROLLBACK" ]

            let undo () =
                // SQLite may already have rolled back after some errors (e.g. a full disk).
                if Native.sqlite3_get_autocommit connection.Pointer = 0 then
                    for sql in rollback do
                        runOrDefect db sql

            match execute db start [] with
            | Error error -> Error(ofError error)
            | Ok _ ->
                if nested then
                    connection.SavepointDepth <- connection.SavepointDepth + 1

                let outcome =
                    try
                        try
                            body ()
                        with _ ->
                            undo ()
                            reraise ()
                    finally
                        if nested then
                            connection.SavepointDepth <- connection.SavepointDepth - 1

                match outcome with
                | Error error ->
                    undo ()
                    Error error
                | Ok value ->
                    match execute db commit [] with
                    | Ok _ -> Ok value
                    | Error error ->
                        undo ()
                        Error(ofError error))

    /// Switches the database to write-ahead logging, which persists in the file: readers no longer block
    /// the writer, and the writer does not block readers. Fails for in-memory databases.
    let useWal (db: Database) : Result<unit, SqliteError> =
        match tryFirst db "PRAGMA journal_mode=WAL" [] (fun row -> row.Text 0) with
        | Error error -> Error error
        | Ok(Some "wal") -> Ok()
        | Ok mode ->
            let actual = Option.defaultValue "unknown" mode
            Error(SqliteError.create 1 $"the journal mode stayed {actual}")

    /// Copies WAL frames into the database file (https://sqlite.org/wal.html#checkpointing). `Busy`
    /// when other connections keep it from finishing.
    let checkpoint (db: Database) (mode: CheckpointMode) : Result<Checkpoint, SqliteError> =
        let connection = db.Connection

        let code =
            match mode with
            | CheckpointMode.Passive -> 0
            | CheckpointMode.Full -> 1
            | CheckpointMode.Restart -> 2
            | CheckpointMode.Truncate -> 3

        lock connection.Sync (fun () ->
            let mutable log = 0
            let mutable checkpointed = 0

            match
                Native.sqlite3_wal_checkpoint_v2 (connection.Pointer, NativePtr.nullPtr, code, &&log, &&checkpointed)
            with
            | Native.SQLITE_OK ->
                Ok
                    {
                        LogFrames = log
                        CheckpointedFrames = checkpointed
                    }
            | failure -> Error(connection.Error failure))

    /// Stops the statement running on the connection, which then fails with `Interrupt`. Safe to call
    /// from any thread, and a no-op when nothing runs.
    let interrupt (db: Database) : unit =
        let handle = db.Connection.Handle
        let mutable added = false

        try
            if not handle.IsClosed then
                handle.DangerousAddRef(&added)
                Native.sqlite3_interrupt (handle.DangerousGetHandle())
        finally
            if added then
                handle.DangerousRelease()

    /// Runs `run` with a statement left running on the connection (stepped once, reset after it).
    /// SQLite forgets an interrupt that comes while no statement runs: a statement that starts then
    /// clears it. With one running, an interrupt stays in effect for every statement `run` starts.
    let private holdingInterrupts (db: Database) (run: unit -> Result<'T, SqliteError>) : Result<'T, SqliteError> =
        cached db "SELECT 1" (fun holder ->
            let pointer = holder.Pointer
            holder.Running <- true

            try
                match Native.sqlite3_step pointer with
                | Native.SQLITE_ROW -> run ()
                | code -> Error(db.Connection.Error code)
            finally
                Native.sqlite3_reset pointer |> ignore
                holder.Running <- false)

    /// Runs `operation`, interrupting it if the token is cancelled meanwhile: a cancelled operation
    /// fails with `Interrupt`, and so does one whose token was cancelled before it started. A
    /// statement runs on the connection throughout, so a cancellation just before one of the
    /// operation's statements starts still stops it: what needs the connection idle (`VACUUM`)
    /// fails inside.
    let cancellable
        (db: Database)
        (cancellation: CancellationToken)
        (operation: unit -> Result<'T, SqliteError>)
        : Result<'T, SqliteError> =
        if cancellation.IsCancellationRequested then
            Error interrupted
        elif not cancellation.CanBeCanceled then
            operation ()
        else
            holdingInterrupts db (fun () ->
                use _ = cancellation.Register(fun () -> interrupt db)

                if cancellation.IsCancellationRequested then
                    Error interrupted
                else
                    operation ())

    /// The main database as bytes: the file format (https://sqlite.org/fileformat.html), so
    /// `deserialize` or writing the bytes to a file gives the same database.
    let serialize (db: Database) : Result<byte array, SqliteError> =
        let connection = db.Connection
        let schema = Native.utf8z "main"

        lock connection.Sync (fun () ->
            let struct (data, size) =
                Native.pinned schema (fun name ->
                    let mutable size = 0L
                    let data = Native.sqlite3_serialize (connection.Pointer, name, &&size, 0u)
                    struct (data, size))

            if data = 0n then
                Error(SqliteError.create 7 "the database could not be serialized")
            elif size > int64 Array.MaxLength then
                Native.sqlite3_free data
                Error(SqliteError.create 18 $"the database is too big for an array ({size} bytes)")
            else
                let bytes: byte array = Array.zeroCreate (int size)
                Marshal.Copy(data, bytes, 0, int size)
                Native.sqlite3_free data
                Ok bytes)

    /// Replaces the main database with an in-memory copy of serialized bytes. Fails with `Busy` when a
    /// statement or transaction is open.
    let deserialize (db: Database) (bytes: byte array) : Result<unit, SqliteError> =
        let connection = db.Connection
        let schema = Native.utf8z "main"

        lock connection.Sync (fun () ->
            // SQLite owns the buffer from here on, and frees it even when this fails.
            let buffer = Native.sqlite3_malloc64 (uint64 (max bytes.Length 1))

            if buffer = 0n then
                raise (OutOfMemoryException "SQLite could not allocate the database")

            Marshal.Copy(bytes, 0, buffer, bytes.Length)

            let flags =
                Native.SQLITE_DESERIALIZE_FREEONCLOSE ||| Native.SQLITE_DESERIALIZE_RESIZEABLE

            let code =
                Native.pinned schema (fun name ->
                    Native.sqlite3_deserialize (
                        connection.Pointer,
                        name,
                        buffer,
                        bytes.LongLength,
                        bytes.LongLength,
                        flags
                    ))

            match code with
            | Native.SQLITE_OK -> Ok()
            | code -> Error(connection.Error code))

    /// Copies the main database of `source` over the main database of `destination` with the online
    /// backup API (https://sqlite.org/backup.html), page by page and consistent. To write a copy to a
    /// file, `VACUUM INTO 'path'` also works.
    let backup (source: Database) (destination: Database) : Result<unit, SqliteError> =
        let main = Native.utf8z "main"

        lock destination.Connection.Sync (fun () ->
            lock source.Connection.Sync (fun () ->
                let backup =
                    Native.pinned main (fun name ->
                        Native.sqlite3_backup_init (
                            destination.Connection.Pointer,
                            name,
                            source.Connection.Pointer,
                            name
                        ))

                if backup = 0n then
                    Error(
                        destination.Connection.Error(Native.sqlite3_extended_errcode destination.Connection.Pointer)
                    )
                else
                    let step = Native.sqlite3_backup_step (backup, -1)
                    let finish = Native.sqlite3_backup_finish backup

                    if step <> Native.SQLITE_DONE && step <> Native.SQLITE_OK then
                        Error(destination.Connection.Error step)
                    elif finish <> Native.SQLITE_OK then
                        Error(destination.Connection.Error finish)
                    else
                        Ok()))
