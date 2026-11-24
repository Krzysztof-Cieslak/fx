namespace Fx.Store.Live

open System
open System.Collections.Generic
open System.Threading
open Fx.Sqlite

/// Opening and migrating the stores' SQLite databases.
module internal Connections =
    /// The value of a result that only a defect (a full disk, a damaged file, a bug) can fail.
    let orThrow (operation: string) (result: Result<'T, SqliteError>) : 'T =
        match result with
        | Ok value -> value
        | Error error -> raise (SqliteException(error, operation))

    let private options (busyTimeout: TimeSpan) =
        { OpenOptions.defaults with
            BusyTimeout = busyTimeout
        }

    let private closeOnError (db: Database) (result: Result<'T, SqliteError>) =
        match result with
        | Ok _ -> Ok db
        | Error error ->
            db.Dispose()
            Error error

    /// The connection that writes: creates the file if needed, switches it to write-ahead logging and
    /// sets how commits are synced.
    let openWriter (path: string) (busyTimeout: TimeSpan) (durability: Durability) : Result<Database, SqliteError> =
        let synchronous =
            match durability with
            | Durability.Full -> "FULL"
            | Durability.Normal -> "NORMAL"

        Database.connect path (options busyTimeout)
        |> Result.bind (fun db ->
            Database.useWal db
            |> Result.bind (fun () -> Database.exec db ("PRAGMA synchronous = " + synchronous))
            |> closeOnError db)

    /// A connection that only reads (`query_only`), for parallel readers beside the writer.
    let openReader (path: string) (busyTimeout: TimeSpan) : Result<Database, SqliteError> =
        { options busyTimeout with
            Access = Access.ReadWrite
        }
        |> Database.connect path
        |> Result.bind (fun db -> Database.exec db "PRAGMA query_only = ON" |> closeOnError db)

    let userVersion (db: Database) : Result<int, SqliteError> =
        Database.tryFirst db "PRAGMA user_version" [] (fun row -> int (row.Int64 0))
        |> Result.map (Option.defaultValue 0)

    /// Brings the database to `version` by running `create` on an empty one (schema version 0), in
    /// one transaction. A database of a newer version is an error: it may hold what this code would
    /// misread.
    let migrate (db: Database) (version: int) (create: string) : Result<unit, OpenError> =
        let check (found: int) =
            if found > version then
                Error(OpenError.NewerSchema(found, version))
            else
                Ok found

        match userVersion db |> Result.mapError OpenError.Database |> Result.bind check with
        | Error error -> Error error
        | Ok found when found = version -> Ok()
        | Ok _ ->
            // Another process may migrate at the same time: check again with the write lock held.
            Database.transaction db TransactionKind.Immediate OpenError.Database (fun () ->
                match userVersion db |> Result.mapError OpenError.Database |> Result.bind check with
                | Error error -> Error error
                | Ok found when found = version -> Ok()
                | Ok _ ->
                    Database.exec db create
                    |> Result.bind (fun () -> Database.exec db ("PRAGMA user_version = " + string version))
                    |> Result.mapError OpenError.Database)

    /// Runs a query on a connection, turning an interrupt by a cancelled token into a cancellation and
    /// any other failure into a defect.
    let read
        (db: Database)
        (cancellation: CancellationToken)
        (operation: string)
        (query: unit -> Result<'T, SqliteError>)
        : 'T =
        match Database.cancellable db cancellation query with
        | Ok value -> value
        | Error error when error.Code = ErrorCode.Interrupt && cancellation.IsCancellationRequested ->
            raise (OperationCanceledException(cancellation))
        | Error error -> raise (SqliteException(error, operation))

/// Reader connections, opened on demand and kept for reuse: each read takes one, so reads run in
/// parallel beside the writer (write-ahead logging) and never wait for each other.
[<Sealed>]
type internal ReaderPool(openReader: unit -> Database, keep: int) =
    let idle = Stack<Database>()
    let mutable disposed = false

    member _.Use(read: Database -> 'T) : 'T =
        let reused =
            lock idle (fun () ->
                if disposed then
                    raise (ObjectDisposedException "the store is closed")

                if idle.Count > 0 then Some(idle.Pop()) else None)

        let db =
            match reused with
            | Some db -> db
            | None -> openReader ()

        try
            read db
        finally
            let kept =
                lock idle (fun () ->
                    if not disposed && idle.Count < keep then
                        idle.Push db
                        true
                    else
                        false)

            if not kept then
                db.Dispose()

    member _.Dispose() =
        let closing =
            lock idle (fun () ->
                disposed <- true
                let all = List.ofSeq idle
                idle.Clear()
                all)

        for db in closing do
            db.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Dispose()
