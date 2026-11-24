module Fx.Sqlite.Tests.ErrorTests

open System
open System.IO
open Expecto
open Fx.Sqlite
open Fx.Sqlite.Tests.Support

[<Tests>]
let tests =
    testList
        "Errors"
        [
            test "a unique violation is an expected failure with its extended code" {
                use db = Database.memory ()
                exec db "CREATE TABLE commands (key TEXT PRIMARY KEY) STRICT"

                Database.execute db "INSERT INTO commands VALUES (?)" [ Value.Text "k1" ]
                |> ok "first"
                |> ignore

                let error =
                    Database.execute db "INSERT INTO commands VALUES (?)" [ Value.Text "k1" ]
                    |> failed "duplicate"

                Expect.equal error.Code ErrorCode.Constraint "constraint"
                Expect.isTrue (SqliteError.isUniqueViolation error) "unique"
                Expect.stringContains error.Message "UNIQUE constraint failed: commands.key" "message"
            }

            test "foreign keys are enforced by default" {
                use db = Database.memory ()
                exec db "CREATE TABLE a (id INTEGER PRIMARY KEY); CREATE TABLE b (a INTEGER REFERENCES a(id))"

                let error =
                    Database.execute db "INSERT INTO b VALUES (1)" [] |> failed "dangling reference"

                Expect.equal error.ExtendedCode 787 "SQLITE_CONSTRAINT_FOREIGNKEY"
            }

            test "SQL errors are results" {
                use db = Database.memory ()
                let syntax = Database.execute db "SELEC 1" [] |> failed "syntax"
                Expect.equal syntax.Code ErrorCode.Error "error"
                Expect.stringContains syntax.Message "syntax error" "message"

                let missing =
                    Database.query db "SELECT * FROM nowhere" [] ignore |> failed "no table"

                Expect.stringContains missing.Message "no such table: nowhere" "message"

                for sql in [ "SELECT 1; SELECT 2"; "SELECT 1; nonsense" ] do
                    let several = Database.execute db sql [] |> failed sql
                    Expect.stringContains several.Message "more than one statement" sql

                Expect.equal
                    (scalar db "SELECT 1; -- a comment\n ; /* another */ " [])
                    1L
                    "trailing semicolons and comments are fine"

                let comments = Database.execute db "-- only a comment" [] |> failed "comments"
                Expect.stringContains comments.Message "only comments" "only comments"

                let empty = Database.execute db "  " [] |> failed "empty"
                Expect.equal empty.Code ErrorCode.Error "empty"
            }

            test "opening a missing file read-only fails" {
                use dir = new TempDir()

                let error =
                    Database.connect
                        (dir.File "missing.db")
                        { OpenOptions.defaults with
                            Access = Access.ReadOnly
                        }
                    |> failed "missing"

                Expect.equal error.Code ErrorCode.CantOpen "cannot open"
            }

            test "opening a file that is not a database fails" {
                use dir = new TempDir()
                let path = dir.File "text.db"
                File.WriteAllText(path, String('x', 4096))
                let error = Database.connect path OpenOptions.defaults |> failed "not a database"
                Expect.equal error.Code ErrorCode.NotADatabase "not a database"
            }

            test "writing through a read-only connection fails" {
                use dir = new TempDir()
                let path = dir.File "ro.db"

                do
                    use db = openFile path
                    exec db "CREATE TABLE t (x)"

                use db =
                    Database.connect
                        path
                        { OpenOptions.defaults with
                            Access = Access.ReadOnly
                        }
                    |> ok "read-only"

                let error = Database.execute db "INSERT INTO t VALUES (1)" [] |> failed "write"
                Expect.equal error.Code ErrorCode.ReadOnly "read-only"
            }

            test "misuse is a defect" {
                use db = Database.memory ()

                Expect.equal
                    (Throws.check (fun () -> Database.execute db "SELECT ?, ?" [ Value.Integer 1L ]))
                    "ArgumentException"
                    "argument count"

                use statement = Database.prepare db "SELECT ?" |> ok "prepare"

                let nested =
                    Throws.check (fun () ->
                        Statement.query statement [ Value.Integer 1L ] (fun _ ->
                            Statement.query statement [ Value.Integer 2L ] ignore))

                Expect.equal nested "InvalidOperationException" "a statement cannot run inside itself"

                // The statement was reset after the exception and runs again.
                Expect.equal
                    (Statement.query statement [ Value.Integer 3L ] (fun row -> row.Int64 0)
                     |> ok "again")
                    [ 3L ]
                    "reusable"

                statement.Dispose()

                Expect.equal
                    (Throws.check (fun () -> Statement.execute statement [ Value.Null ]))
                    "ObjectDisposedException"
                    "finalized"

                db.Dispose()

                Expect.equal
                    (Throws.check (fun () -> Database.execute db "SELECT 1" []))
                    "ObjectDisposedException"
                    "closed"
            }

            test "the same SQL can run inside its own row callback" {
                use db = Database.memory ()
                exec db "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1), (2)"

                let pairs =
                    Database.query db "SELECT x FROM t ORDER BY x" [] (fun outer ->
                        let x = outer.Int64 0

                        Database.query db "SELECT x FROM t ORDER BY x" [] (fun inner -> x, inner.Int64 0)
                        |> ok "inner")
                    |> ok "outer"
                    |> List.concat

                Expect.equal pairs [ 1L, 1L; 1L, 2L; 2L, 1L; 2L, 2L ] "pairs"
            }

            test "more distinct statements than the cache holds" {
                use db = Database.memory ()

                let sums =
                    [
                        for i in 1 .. Database.CacheCapacity + 10 -> scalar db $"SELECT {i} + ?" [ Value.Integer 1L ]
                    ]

                Expect.equal sums [ for i in 1 .. Database.CacheCapacity + 10 -> int64 i + 1L ] "all ran"
                Expect.equal (scalar db "SELECT 1 + ?" [ Value.Integer 1L ]) 2L "cached ones still run"
            }
        ]
