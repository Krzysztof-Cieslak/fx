namespace Fx.Store.Live

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Sqlite
open Fx.Store

/// How a `SqliteEventStore` runs.
type EventStoreOptions =
    {
        Durability: Durability
        /// The largest event, in bytes of JSON text. Large content belongs in the blob store, and an
        /// event refers to it by `BlobRef`.
        MaxEventBytes: int
        /// The most requests the writer commits in one transaction (group commit).
        MaxBatch: int
        /// How many idle reader connections are kept open.
        Readers: int
        /// How long a connection waits for a lock held by another process before it fails.
        BusyTimeout: TimeSpan
    }

    override this.ToString() =
        $"durability {this.Durability}, max event {this.MaxEventBytes} bytes, batches of {this.MaxBatch}, {this.Readers} readers"

module EventStoreOptions =
    /// Full durability, events up to 1 MiB, batches of up to 256 requests, 4 idle readers, 5 s.
    let defaults =
        {
            Durability = Durability.Full
            MaxEventBytes = 1 <<< 20
            MaxBatch = 256
            Readers = 4
            BusyTimeout = TimeSpan.FromSeconds 5.0
        }

/// One commit waiting for the writer, its events already serialized.
type internal AppendRequest =
    {
        Stream: string
        Expected: int64
        Meta: EventMeta
        /// Each event's schema version and canonical JSON text.
        Events: struct (int * string) list
        Reply: TaskCompletionSource<Result<int64, AppendError>>
        Cancellation: CancellationToken
    }

/// What the writer thread does, in arrival order.
type internal WriteRequest =
    | Append of AppendRequest
    | Save of
        stream: string *
        name: string *
        snapshot: StoredSnapshot *
        text: string *
        reply: TaskCompletionSource<unit>
    | Checkpoint of mode: CheckpointMode * reply: TaskCompletionSource<Result<Checkpoint, SqliteError>>

/// The schema of the event database, version 1.
module internal EventDatabase =
    [<Literal>]
    let Version = 1

    /// - `streams` names streams and keeps their heads (the expected-head check reads one row).
    /// - `commits` holds what the events of one command share, and the unique idempotency key.
    /// - `events` holds each event's schema version and canonical JSON; its rowid is the order in
    ///   which events were appended across streams.
    /// - `snapshots` keeps the latest snapshot per stream and name.
    let create =
        """
        CREATE TABLE streams (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE,
            head INTEGER NOT NULL
        ) STRICT;

        CREATE TABLE commits (
            stream INTEGER NOT NULL,
            first_seq INTEGER NOT NULL,
            count INTEGER NOT NULL,
            key TEXT NOT NULL,
            at INTEGER NOT NULL,
            principal TEXT NOT NULL,
            via TEXT NOT NULL,
            cause_stream TEXT,
            cause_seq INTEGER,
            PRIMARY KEY (stream, first_seq),
            UNIQUE (stream, key)
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE events (
            position INTEGER PRIMARY KEY,
            stream INTEGER NOT NULL,
            seq INTEGER NOT NULL,
            commit_seq INTEGER NOT NULL,
            version INTEGER NOT NULL,
            data TEXT NOT NULL,
            UNIQUE (stream, seq)
        ) STRICT;

        CREATE TABLE snapshots (
            stream TEXT NOT NULL,
            name TEXT NOT NULL,
            seq INTEGER NOT NULL,
            version INTEGER NOT NULL,
            state TEXT NOT NULL,
            PRIMARY KEY (stream, name)
        ) STRICT, WITHOUT ROWID;
        """

    let selectStream = "SELECT id, head FROM streams WHERE name = ?"
    let insertStream = "INSERT INTO streams (name, head) VALUES (?, 0)"
    let updateHead = "UPDATE streams SET head = ? WHERE id = ?"

    let selectCommit =
        "SELECT first_seq, count FROM commits WHERE stream = ? AND key = ?"

    let insertCommit =
        "INSERT INTO commits (stream, first_seq, count, key, at, principal, via, cause_stream, cause_seq) "
        + "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)"

    let insertEvent =
        "INSERT INTO events (stream, seq, commit_seq, version, data) VALUES (?, ?, ?, ?, ?)"

    let selectHead = "SELECT head FROM streams WHERE name = ?"

    let selectCommitByName =
        "SELECT c.first_seq, c.count FROM streams s JOIN commits c ON c.stream = s.id "
        + "WHERE s.name = ? AND c.key = ?"

    let selectEvents =
        "SELECT e.seq, e.version, e.data, c.key, c.at, c.principal, c.via, c.cause_stream, c.cause_seq "
        + "FROM streams s JOIN events e ON e.stream = s.id "
        + "JOIN commits c ON c.stream = e.stream AND c.first_seq = e.commit_seq "
        + "WHERE s.name = ? AND e.seq > ? ORDER BY e.seq LIMIT ?"

    let upsertSnapshot =
        "INSERT INTO snapshots (stream, name, seq, version, state) VALUES (?, ?, ?, ?, ?) "
        + "ON CONFLICT (stream, name) DO UPDATE SET seq = excluded.seq, version = excluded.version, state = excluded.state "
        + "WHERE excluded.version <> snapshots.version OR excluded.seq >= snapshots.seq"

    let selectSnapshot =
        "SELECT seq, version, state FROM snapshots WHERE stream = ? AND name = ?"

/// Checks on what is stored, so that everything written can be read back.
module internal Stored =
    /// How deeply arrays and objects nest: `[]` has depth 1, a scalar 0.
    let depth (json: Json) : int =
        let pending = Stack<struct (Json * int)>()
        pending.Push(struct (json, 0))
        let mutable deepest = 0

        while pending.Count > 0 do
            let struct (value, depth) = pending.Pop()

            match value with
            | Json.Array items ->
                deepest <- max deepest (depth + 1)

                for item in items do
                    pending.Push(struct (item, depth + 1))
            | Json.Object members ->
                deepest <- max deepest (depth + 1)

                for _, item in members do
                    pending.Push(struct (item, depth + 1))
            | _ -> ()

        deepest

    /// Canonical JSON text that the store's reader will accept (`ReadOptions.defaults`); a defect
    /// otherwise, raised before anything is written.
    let text (what: string) (json: Json) : string =
        let nesting = depth json

        if nesting > ReadOptions.defaults.MaxDepth then
            invalidArg
                what
                $"JSON nested {nesting} levels deep cannot be read back (at most {ReadOptions.defaults.MaxDepth})."

        Json.serializeCanonical json

/// A waiter of `WaitPast`.
[<ReferenceEquality>]
type private Waiter =
    {
        Stream: string
        After: int64
        Id: int64
        Completion: TaskCompletionSource<int64>
    }

/// The durable event store: append-only streams in one SQLite database (spec §5), with the
/// snapshots of their states. See docs/modules/store.md.
///
/// - **One writer.** A dedicated thread owns the only writing connection. Appends and snapshot saves
///   queue for it, and it commits whatever is queued in one `BEGIN IMMEDIATE` transaction (group
///   commit), each append with its own key and head checks. Callers continue on the thread pool.
/// - **Many readers.** Reads run on the caller's thread with a pooled read-only connection; with
///   write-ahead logging they never wait for the writer.
/// - **Waiting.** The writer wakes `WaitPast` waiters after each commit. Commits by another process
///   are seen by reads and by the head check, but wake no waiters: one process owns a store.
[<Sealed>]
type SqliteEventStore internal (path: string, options: EventStoreOptions, writer: Database, readers: ReaderPool) =
    let queue = new BlockingCollection<WriteRequest>()
    let gate = obj ()
    let waiters = ResizeArray<Waiter>()
    let mutable lastWaiter = 0L
    let mutable closed = false

    let value (operation: string) (result: Result<'T, SqliteError>) = Connections.orThrow operation result

    // ---- Writing (the writer thread only) ---------------------------------------------------------

    /// Checks and inserts one commit; `Error` is a conflict or a duplicate, and nothing was written.
    let appendOne (request: AppendRequest) : Result<int64, AppendError> =
        let stream =
            Database.tryFirst writer EventDatabase.selectStream [ Value.Text request.Stream ] (fun row ->
                struct (row.Int64 0, row.Int64 1))
            |> value "reading a stream's head"

        let duplicate =
            match stream with
            | Some(struct (id, _)) ->
                Database.tryFirst
                    writer
                    EventDatabase.selectCommit
                    [ Value.Integer id; Value.Text request.Meta.Key.Value ]
                    (fun row ->
                        {
                            After = row.Int64 0 - 1L
                            Count = int (row.Int64 1)
                        })
                |> value "looking up an idempotency key"
            | None -> None

        let head =
            match stream with
            | Some(struct (_, head)) -> head
            | None -> 0L

        match duplicate with
        | Some first -> Error(Duplicate(request.Meta.Key, first))
        | None when head <> request.Expected -> Error(Conflict(request.Expected, head))
        | None ->
            let id =
                match stream with
                | Some(struct (id, _)) -> id
                | None ->
                    Database.execute writer EventDatabase.insertStream [ Value.Text request.Stream ]
                    |> value "adding a stream"
                    |> ignore

                    Database.lastInsertRowId writer

            let meta = request.Meta
            let first = head + 1L
            let count = List.length request.Events

            let cause =
                match meta.Cause with
                | Some cause -> [ Value.Text cause.Stream.Value; Value.Integer cause.Seq ]
                | None -> [ Value.Null; Value.Null ]

            Database.execute
                writer
                EventDatabase.insertCommit
                ([
                    Value.Integer id
                    Value.Integer first
                    Value.Integer(int64 count)
                    Value.Text meta.Key.Value
                    Value.Integer meta.At.UnixMicroseconds
                    Value.Text meta.By.Principal
                    Value.Text meta.By.Via
                 ]
                 @ cause)
            |> value "adding a commit"
            |> ignore

            request.Events
            |> List.iteri (fun i (struct (version, data)) ->
                Database.execute
                    writer
                    EventDatabase.insertEvent
                    [
                        Value.Integer id
                        Value.Integer(first + int64 i)
                        Value.Integer first
                        Value.Integer(int64 version)
                        Value.Text data
                    ]
                |> value "adding an event"
                |> ignore)

            let next = head + int64 count

            Database.execute writer EventDatabase.updateHead [ Value.Integer next; Value.Integer id ]
            |> value "moving a stream's head"
            |> ignore

            Ok next

    let saveOne (stream: string) (name: string) (snapshot: StoredSnapshot) (text: string) =
        Database.execute
            writer
            EventDatabase.upsertSnapshot
            [
                Value.Text stream
                Value.Text name
                Value.Integer snapshot.Seq
                Value.Integer(int64 snapshot.Version)
                Value.Text text
            ]
        |> value "saving a snapshot"
        |> ignore

    /// Wakes the waiters of streams whose heads moved. Their continuations run on the thread pool.
    let wake (heads: (string * int64) list) =
        if not heads.IsEmpty then
            let woken =
                lock gate (fun () ->
                    let woken =
                        [
                            for waiter in waiters do
                                for stream, head in heads do
                                    if waiter.Stream = stream && waiter.After < head then
                                        yield waiter, head
                        ]

                    for waiter, _ in woken do
                        waiters.Remove waiter |> ignore

                    woken)

            for waiter, head in woken do
                waiter.Completion.TrySetResult head |> ignore

    /// Runs appends and saves in one transaction. If it fails as a whole and holds more than one
    /// request, each request runs again on its own, so that one bad request fails only itself.
    let rec commit (batch: WriteRequest list) =
        let outcome =
            try
                Database.transaction writer TransactionKind.Immediate id (fun () ->
                    batch
                    |> List.map (fun request ->
                        match request with
                        | Append append -> Choice1Of2(append, appendOne append)
                        | Save(stream, name, snapshot, text, reply) ->
                            saveOne stream name snapshot text
                            Choice2Of2 reply
                        | Checkpoint _ -> invalidOp "checkpoints run outside transactions")
                    |> Ok)
                |> Result.mapError (fun error -> SqliteException(error, "committing") :> exn)
            with e ->
                Error e

        match outcome with
        | Ok results ->
            let heads =
                [
                    for result in results do
                        match result with
                        | Choice1Of2(append, Ok head) ->
                            append.Reply.TrySetResult(Ok head) |> ignore
                            yield append.Stream, head
                        | Choice1Of2(append, Error error) -> append.Reply.TrySetResult(Error error) |> ignore
                        | Choice2Of2 reply -> reply.TrySetResult() |> ignore
                ]

            wake heads
        | Error _ when batch.Length > 1 ->
            for request in batch do
                commit [ request ]
        | Error e ->
            for request in batch do
                match request with
                | Append append -> append.Reply.TrySetException e |> ignore
                | Save(reply = reply) -> reply.TrySetException e |> ignore
                | Checkpoint(reply = reply) -> reply.TrySetException e |> ignore

    let cancelled (request: WriteRequest) =
        match request with
        | Append append when append.Cancellation.IsCancellationRequested ->
            append.Reply.TrySetCanceled append.Cancellation |> ignore
            true
        | _ -> false

    let run (batch: WriteRequest list) =
        let writes, checkpoints =
            batch
            |> List.filter (cancelled >> not)
            |> List.partition (function
                | Checkpoint _ -> false
                | _ -> true)

        if not writes.IsEmpty then
            commit writes

        for request in checkpoints do
            match request with
            | Checkpoint(mode, reply) -> reply.TrySetResult(Database.checkpoint writer mode) |> ignore
            | _ -> ()

    let writerLoop () =
        for first in queue.GetConsumingEnumerable() do
            let batch = ResizeArray [ first ]
            let mutable next = Unchecked.defaultof<WriteRequest>

            while batch.Count < options.MaxBatch && queue.TryTake(&next) do
                batch.Add next

            try
                run (List.ofSeq batch)
            with e ->
                // A defect outside the transactions (they handle their own): fail what is left, keep going.
                for request in batch do
                    match request with
                    | Append append -> append.Reply.TrySetException e |> ignore
                    | Save(reply = reply) -> reply.TrySetException e |> ignore
                    | Checkpoint(reply = reply) -> reply.TrySetException e |> ignore

    let thread =
        Thread(ThreadStart writerLoop, IsBackground = true, Name = "event store writer")

    do thread.Start()

    let enqueue (request: WriteRequest) =
        try
            queue.Add request
        with :? InvalidOperationException ->
            raise (ObjectDisposedException("SqliteEventStore", "The store is closed."))

    // ---- The log ----------------------------------------------------------------------------------

    let append
        (stream: StreamId)
        (expected: int64)
        (meta: EventMeta)
        (events: Payload list)
        (cancellation: CancellationToken)
        =
        if events.IsEmpty then
            invalidArg (nameof events) "A commit has at least one event."

        if cancellation.IsCancellationRequested then
            Task.FromCanceled<Result<int64, AppendError>> cancellation
        else
            let serialized =
                events
                |> List.map (fun payload ->
                    let text = Stored.text (nameof events) payload.Data

                    if Encoding.UTF8.GetByteCount text > options.MaxEventBytes then
                        invalidArg
                            (nameof events)
                            $"An event of {Encoding.UTF8.GetByteCount text} bytes is over the limit of {options.MaxEventBytes}; store large content as a blob and refer to it."

                    struct (payload.Version, text))

            let reply =
                TaskCompletionSource<Result<int64, AppendError>>(TaskCreationOptions.RunContinuationsAsynchronously)

            enqueue (
                Append
                    {
                        Stream = stream.Value
                        Expected = expected
                        Meta = meta
                        Events = serialized
                        Reply = reply
                        Cancellation = cancellation
                    }
            )

            reply.Task

    let parse (event: string) (text: string) : Json =
        match Json.parse text with
        | Ok json -> json
        | Error error ->
            raise (SqliteException(SqliteError.create 11 $"{event}: stored JSON is invalid: {error}", "reading"))

    let read (stream: StreamId) (after: int64) (max: int) (cancellation: CancellationToken) =
        if max < 1 then
            invalidArg (nameof max) "Read at least one event."

        if cancellation.IsCancellationRequested then
            Task.FromCanceled<Recorded<Payload> list> cancellation
        else
            readers.Use(fun db ->
                Connections.read db cancellation "reading events" (fun () ->
                    Database.query
                        db
                        EventDatabase.selectEvents
                        [ Value.Text stream.Value; Value.Integer after; Value.Integer(int64 max) ]
                        (fun row ->
                            let seq = row.Int64 0

                            {
                                Stream = stream
                                Seq = seq
                                Event =
                                    {
                                        Version = int (row.Int64 1)
                                        Data = parse $"{stream}#{seq}" (row.Text 2)
                                    }
                                Meta =
                                    {
                                        Key = IdempotencyKey(row.Text 3)
                                        At = Instant.ofUnixMicroseconds (row.Int64 4)
                                        By = Attribution.create (row.Text 5) (row.Text 6)
                                        Cause =
                                            if row.IsNull 7 then
                                                None
                                            else
                                                Some(EventRef.create (StreamId(row.Text 7)) (row.Int64 8))
                                    }
                            }
                            : Recorded<Payload>)))
            |> Task.FromResult

    let headOf (stream: StreamId) (cancellation: CancellationToken) : int64 =
        readers.Use(fun db ->
            Connections.read db cancellation "reading a head" (fun () ->
                Database.tryFirst db EventDatabase.selectHead [ Value.Text stream.Value ] (fun row -> row.Int64 0)))
        |> Option.defaultValue 0L

    let head (stream: StreamId) (cancellation: CancellationToken) =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<int64> cancellation
        else
            Task.FromResult(headOf stream cancellation)

    let waitPast (stream: StreamId) (after: int64) (cancellation: CancellationToken) : Task<int64> =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<int64> cancellation
        else
            let completion =
                TaskCompletionSource<int64>(TaskCreationOptions.RunContinuationsAsynchronously)

            let waiter =
                lock gate (fun () ->
                    if closed then
                        raise (ObjectDisposedException("SqliteEventStore", "The store is closed."))

                    lastWaiter <- lastWaiter + 1L

                    let waiter =
                        {
                            Stream = stream.Value
                            After = after
                            Id = lastWaiter
                            Completion = completion
                        }

                    waiters.Add waiter
                    waiter)

            let remove () =
                lock gate (fun () -> waiters.Remove waiter)

            // Registered before reading the head: a commit after the read wakes it, one before shows.
            let current =
                try
                    headOf stream cancellation
                with e ->
                    remove () |> ignore
                    reraise ()

            if current > after then
                remove () |> ignore
                completion.TrySetResult current |> ignore
            elif cancellation.CanBeCanceled then
                // Registered outside the lock: a token cancelled meanwhile runs the callback at once.
                let registration =
                    cancellation.Register(fun () ->
                        if remove () then
                            completion.TrySetCanceled cancellation |> ignore)

                completion.Task.ContinueWith(
                    (fun (_: Task<int64>) -> registration.Dispose()),
                    TaskContinuationOptions.ExecuteSynchronously
                )
                |> ignore

            completion.Task

    let findCommit (stream: StreamId) (key: IdempotencyKey) (cancellation: CancellationToken) =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<Appended option> cancellation
        else
            readers.Use(fun db ->
                Connections.read db cancellation "looking up an idempotency key" (fun () ->
                    Database.tryFirst
                        db
                        EventDatabase.selectCommitByName
                        [ Value.Text stream.Value; Value.Text key.Value ]
                        (fun row ->
                            {
                                After = row.Int64 0 - 1L
                                Count = int (row.Int64 1)
                            })))
            |> Task.FromResult

    // ---- Snapshots --------------------------------------------------------------------------------

    let saveSnapshot (stream: StreamId) (name: string) (snapshot: StoredSnapshot) (cancellation: CancellationToken) =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<unit> cancellation
        else
            let reply =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let text = Stored.text (nameof snapshot) snapshot.State
            enqueue (Save(stream.Value, name, snapshot, text, reply))
            reply.Task

    let loadSnapshot (stream: StreamId) (name: string) (cancellation: CancellationToken) =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<StoredSnapshot option> cancellation
        else
            readers.Use(fun db ->
                Connections.read db cancellation "reading a snapshot" (fun () ->
                    Database.tryFirst
                        db
                        EventDatabase.selectSnapshot
                        [ Value.Text stream.Value; Value.Text name ]
                        (fun row ->
                            {
                                Seq = row.Int64 0
                                Version = int (row.Int64 1)
                                State = parse $"snapshot {name} of {stream}" (row.Text 2)
                            })))
            |> Task.FromResult

    /// The database file.
    member _.Path = path

    member _.Options = options

    /// The log of stored events. Type it with `EventLog.typed`.
    member val Log: EventLog<Payload> =
        {
            Append = append
            Read = read
            Head = head
            WaitPast = waitPast
            FindCommit = findCommit
        }

    /// The snapshots of the streams' states, kept in the same database.
    member val Snapshots: SnapshotStore =
        {
            Save = saveSnapshot
            Load = loadSnapshot
        }

    /// Copies the write-ahead log into the database file (after the writes queued before it). SQLite
    /// does this on its own every 1000 pages; `Truncate` when idle also shrinks the log file.
    member _.Checkpoint(mode: CheckpointMode) : Task<Result<Checkpoint, SqliteError>> =
        let reply =
            TaskCompletionSource<Result<Checkpoint, SqliteError>>(TaskCreationOptions.RunContinuationsAsynchronously)

        enqueue (Checkpoint(mode, reply))
        reply.Task

    /// Finishes the queued writes, then closes the store: waiters fail with `ObjectDisposedException`,
    /// and the write-ahead log is checkpointed and truncated.
    member _.Dispose() =
        let first =
            lock gate (fun () ->
                let first = not closed
                closed <- true
                first)

        if first then
            queue.CompleteAdding()
            thread.Join()

            let left =
                lock gate (fun () ->
                    let left = List.ofSeq waiters
                    waiters.Clear()
                    left)

            for waiter in left do
                waiter.Completion.TrySetException(ObjectDisposedException("SqliteEventStore", "The store is closed."))
                |> ignore

            readers.Dispose()
            Database.checkpoint writer CheckpointMode.Truncate |> ignore
            writer.Dispose()
            queue.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Dispose()

    override _.ToString() = "SqliteEventStore(" + path + ")"

module SqliteEventStore =
    /// Opens the store in a database file, creating it if needed. The path comes from the composition
    /// root (the host decides where its data lives); its directory must exist.
    let openFile (path: string) (options: EventStoreOptions) : Result<SqliteEventStore, OpenError> =
        if options.MaxBatch < 1 || options.MaxEventBytes < 1 || options.Readers < 0 then
            invalidArg (nameof options) $"Invalid options: {options}"

        match Connections.openWriter path options.BusyTimeout options.Durability with
        | Error error -> Error(OpenError.Database error)
        | Ok writer ->
            match Connections.migrate writer EventDatabase.Version EventDatabase.create with
            | Error error ->
                writer.Dispose()
                Error error
            | Ok() ->
                let openReader () =
                    Connections.openReader path options.BusyTimeout
                    |> Connections.orThrow "opening a reader"

                Ok(new SqliteEventStore(path, options, writer, new ReaderPool(openReader, options.Readers)))
