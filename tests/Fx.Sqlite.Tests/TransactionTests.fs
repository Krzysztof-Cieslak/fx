module Fx.Sqlite.Tests.TransactionTests

open System
open Expecto
open Fx.Sqlite
open Fx.Sqlite.Tests.Support

let private count (db: Database) = scalar db "SELECT count(*) FROM t" []

let private insert (db: Database) (x: int64) =
    Database.execute db "INSERT INTO t VALUES (?)" [ Value.Integer x ]
    |> Result.map ignore

let private withTable () =
    let db = Database.memory ()
    exec db "CREATE TABLE t (x INTEGER)"
    db

[<Tests>]
let tests =
    testList
        "Transactions"
        [
            test "Ok commits" {
                use db = withTable ()

                let result =
                    Database.transaction db TransactionKind.Immediate id (fun () ->
                        insert db 1L
                        |> Result.bind (fun () -> insert db 2L)
                        |> Result.map (fun () -> "done"))

                Expect.equal result (Ok "done") "result"
                Expect.equal (count db) 2L "committed"
                Expect.isFalse (Database.isInTransaction db) "closed"
            }

            test "Error rolls back" {
                use db = withTable ()

                let result =
                    Database.transaction db TransactionKind.Deferred string (fun () ->
                        insert db 1L
                        |> Result.mapError string
                        |> Result.bind (fun () -> Error "conflict"))

                Expect.equal result (Error "conflict") "result"
                Expect.equal (count db) 0L "rolled back"
                Expect.isFalse (Database.isInTransaction db) "closed"
            }

            test "an exception rolls back and propagates" {
                use db = withTable ()

                let thrown =
                    Throws.check (fun () ->
                        Database.transaction db TransactionKind.Immediate id (fun () ->
                            insert db 1L |> ignore
                            invalidOp "boom"))

                Expect.equal thrown "InvalidOperationException" "propagates"
                Expect.equal (count db) 0L "rolled back"
                Expect.isFalse (Database.isInTransaction db) "closed"
            }

            test "transactions nest as savepoints" {
                use db = withTable ()

                let result =
                    Database.transaction db TransactionKind.Immediate id (fun () ->
                        insert db 1L |> ok "outer insert"

                        let inner =
                            Database.transaction db TransactionKind.Immediate string (fun () ->
                                insert db 2L |> ok "inner insert"
                                Error "inner fails")

                        Expect.equal inner (Error "inner fails") "inner"
                        Expect.isTrue (Database.isInTransaction db) "outer still open"

                        Database.transaction db TransactionKind.Immediate id (fun () -> insert db 3L))

                Expect.equal result (Ok()) "outer commits"

                Expect.equal
                    (Database.query db "SELECT x FROM t ORDER BY x" [] (fun row -> row.Int64 0)
                     |> ok "rows")
                    [ 1L; 3L ]
                    "only the failed savepoint rolled back"
            }

            test "a second writer gets Busy, then succeeds once the first commits" {
                use dir = new TempDir()
                let path = dir.File "busy.db"

                let options =
                    { OpenOptions.defaults with
                        BusyTimeout = TimeSpan.Zero
                    }

                use first = Database.connect path options |> ok "first"
                use second = Database.connect path options |> ok "second"
                exec first "CREATE TABLE t (x INTEGER)"

                let outcome =
                    Database.transaction first TransactionKind.Immediate id (fun () ->
                        insert first 1L |> ok "insert"

                        let blocked =
                            Database.transaction second TransactionKind.Immediate id (fun () -> insert second 2L)
                            |> failed "second writer"

                        Ok blocked)
                    |> ok "first writer"

                Expect.equal outcome.Code ErrorCode.Busy "busy"

                Database.transaction second TransactionKind.Immediate id (fun () -> insert second 2L)
                |> ok "later"

                Expect.equal (count first) 2L "both rows"
            }
        ]
