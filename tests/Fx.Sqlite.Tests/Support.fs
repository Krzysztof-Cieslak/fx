module Fx.Sqlite.Tests.Support

open System
open System.IO
open Expecto
open Fx.Sqlite

/// The value of a result the test expects to succeed.
let ok (what: string) (result: Result<'T, SqliteError>) : 'T =
    match result with
    | Ok value -> value
    | Error error -> failtest $"{what}: {error}"

/// The error of a result the test expects to fail.
let failed (what: string) (result: Result<'T, SqliteError>) : SqliteError =
    match result with
    | Ok value -> failtest $"{what}: expected an error, got {value}"
    | Error error -> error

/// A new, empty temporary directory, deleted on dispose.
type TempDir() =
    let path =
        Path.Combine(Path.GetTempPath(), "sqlite-tests-" + Guid.NewGuid().ToString("N"))

    do Directory.CreateDirectory path |> ignore

    member _.Path = path
    member _.File(name: string) = Path.Combine(path, name)

    interface IDisposable with
        member _.Dispose() =
            try
                Directory.Delete(path, true)
            with :? IOException ->
                ()

/// Opens (creating) a file database.
let openFile (path: string) : Database =
    Database.connect path OpenOptions.defaults |> ok $"open {path}"

/// Runs SQL that must succeed.
let exec (db: Database) (sql: string) = Database.exec db sql |> ok sql

/// The single integer a query returns.
let scalar (db: Database) (sql: string) (args: Value list) : int64 =
    match Database.tryFirst db sql args (fun row -> row.Int64 0) |> ok sql with
    | Some value -> value
    | None -> failtest $"{sql}: no row"

/// Every row's first column as text.
let texts (db: Database) (sql: string) (args: Value list) : string list =
    Database.query db sql args (fun row -> row.Text 0) |> ok sql

module Throws =
    /// The name of the exception type `f` throws, or "nothing".
    let check (f: unit -> 'T) : string =
        try
            f () |> ignore
            "nothing"
        with e ->
            e.GetType().Name
