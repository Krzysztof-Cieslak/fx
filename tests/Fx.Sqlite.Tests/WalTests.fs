module Fx.Sqlite.Tests.WalTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Sqlite
open Fx.Sqlite.Tests.Support

let private count (db: Database) = scalar db "SELECT count(*) FROM t" []

[<Tests>]
let tests =
    testList
        "WAL"
        [
            test "WAL persists in the file; in-memory databases cannot use it" {
                use dir = new TempDir()
                let path = dir.File "wal.db"

                do
                    use db = openFile path
                    Database.useWal db |> ok "wal"

                use db = openFile path
                Expect.equal (texts db "PRAGMA journal_mode" []) [ "wal" ] "persisted"

                use memory = Database.memory ()
                let error = Database.useWal memory |> failed "memory"
                Expect.stringContains error.Message "memory" "stays memory"
            }

            test "a reader keeps its snapshot while the writer commits" {
                use dir = new TempDir()
                let path = dir.File "snapshot.db"
                use writer = openFile path
                Database.useWal writer |> ok "wal"
                exec writer "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1)"
                use reader = openFile path

                let seen =
                    Database.transaction reader TransactionKind.Deferred id (fun () ->
                        let before = count reader
                        exec writer "INSERT INTO t VALUES (2)"
                        let during = count reader
                        Ok(before, during))
                    |> ok "read transaction"

                Expect.equal seen (1L, 1L) "the snapshot"
                Expect.equal (count reader) 2L "after the read transaction"
            }

            test "checkpoints copy the log into the database" {
                use dir = new TempDir()
                let path = dir.File "checkpoint.db"
                use db = openFile path
                Database.useWal db |> ok "wal"
                exec db "PRAGMA wal_autocheckpoint=0; CREATE TABLE t (x INTEGER)"

                for i in 1L .. 100L do
                    Database.execute db "INSERT INTO t VALUES (?)" [ Value.Integer i ]
                    |> ok "insert"
                    |> ignore

                let passive = Database.checkpoint db CheckpointMode.Passive |> ok "passive"
                Expect.isGreaterThan passive.LogFrames 0 "frames in the log"
                Expect.equal passive.CheckpointedFrames passive.LogFrames "all copied"

                let truncate = Database.checkpoint db CheckpointMode.Truncate |> ok "truncate"
                Expect.equal (truncate.LogFrames, truncate.CheckpointedFrames) (0, 0) "log reset"
                Expect.equal (FileInfo(path + "-wal").Length) 0L "log truncated"

                use memory = Database.memory ()
                let none = Database.checkpoint memory CheckpointMode.Passive |> ok "not WAL"
                Expect.equal (none.LogFrames, none.CheckpointedFrames) (-1, -1) "nothing to do"
            }

            test "one writer and parallel readers never see half a transaction" {
                use dir = new TempDir()
                let path = dir.File "parallel.db"
                use writer = openFile path
                Database.useWal writer |> ok "wal"
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
                                if count reader % 2L <> 0L then
                                    odd <- odd + 1

                                reads <- reads + 1

                                if reads = 1 then
                                    started.Signal() |> ignore

                            odd, reads))

                Expect.isTrue (started.Wait(TimeSpan.FromSeconds 30.0)) "the readers started"

                for i in 1L .. 200L do
                    Database.transaction writer TransactionKind.Immediate id (fun () ->
                        Database.execute writer "INSERT INTO t VALUES (?), (?)" [ Value.Integer i; Value.Integer -i ]
                        |> Result.map ignore)
                    |> ok "write"

                stop.Cancel()
                let results = Task.WhenAll(reading).WaitAsync(TimeSpan.FromSeconds 30.0).Result

                for reader in readers do
                    reader.Dispose()

                Expect.all results (fun (odd, _) -> odd = 0) "every read saw whole transactions"
                Expect.equal (count writer) 400L "all rows"
            }

            test "a connection shared between threads serializes their transactions" {
                use dir = new TempDir()
                use db = openFile (dir.File "shared.db")
                Database.useWal db |> ok "wal"
                exec db "CREATE TABLE t (x INTEGER)"

                let work (n: int64) =
                    Task.Run(fun () ->
                        for i in 1L .. 50L do
                            Database.transaction db TransactionKind.Immediate string (fun () ->
                                let before = scalar db "SELECT count(*) FROM t" []

                                Database.execute db "INSERT INTO t VALUES (?)" [ Value.Integer(n * 1000L + i) ]
                                |> ok "insert"
                                |> ignore

                                let after = scalar db "SELECT count(*) FROM t" []

                                if after = before + 1L then
                                    Ok()
                                else
                                    Error "another thread wrote inside the transaction")
                            |> function
                                | Ok() -> ()
                                | Error e -> failtest e)

                Task.WhenAll([ for n in 1L .. 4L -> work n ]).WaitAsync(TimeSpan.FromSeconds 60.0).Wait()
                Expect.equal (scalar db "SELECT count(*) FROM t" []) 200L "all rows"
            }
        ]
