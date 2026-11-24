module Fx.Sqlite.Tests.FeatureTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Sqlite
open Fx.Sqlite.Tests.Support

[<Tests>]
let tests =
    testList
        "Features"
        [
            test "FTS5: ranked matches, phrases, prefixes and highlights" {
                use db = Database.memory ()

                exec
                    db
                    "CREATE VIRTUAL TABLE notes USING fts5(title, body);
                     INSERT INTO notes VALUES
                       ('Use SQLite', 'Store events in SQLite with WAL'),
                       ('Kestrel', 'Serve WebSocket and SSE from Kestrel'),
                       ('Ledger', 'Decisions are searched with full-text search over SQLite')"

                Expect.equal
                    (texts db "SELECT title FROM notes WHERE notes MATCH ? ORDER BY rank" [ Value.Text "sqlite" ])
                    [ "Use SQLite"; "Ledger" ]
                    "ranked by bm25"

                Expect.equal
                    (texts db "SELECT title FROM notes WHERE notes MATCH ?" [ Value.Text "\"full-text search\"" ])
                    [ "Ledger" ]
                    "phrase"

                Expect.equal
                    (texts db "SELECT title FROM notes WHERE notes MATCH ?" [ Value.Text "web*" ])
                    [ "Kestrel" ]
                    "prefix"

                Expect.equal
                    (texts
                        db
                        "SELECT highlight(notes, 1, '[', ']') FROM notes WHERE notes MATCH ?"
                        [ Value.Text "body:wal" ])
                    [ "Store events in SQLite with [WAL]" ]
                    "highlight"

                let error =
                    Database.query db "SELECT * FROM notes WHERE notes MATCH ?" [ Value.Text "\"unbalanced" ] ignore
                    |> failed "bad query"

                Expect.stringContains error.Message "unterminated string" "query syntax errors are results"
            }

            test "FTS5 external content kept in sync by triggers, with an untrusted schema" {
                use db = Database.memory ()

                exec
                    db
                    "CREATE TABLE decisions (id INTEGER PRIMARY KEY, question TEXT NOT NULL) STRICT;
                     CREATE VIRTUAL TABLE decisions_fts USING fts5(question, content='decisions', content_rowid='id');
                     CREATE TRIGGER decisions_ai AFTER INSERT ON decisions BEGIN
                       INSERT INTO decisions_fts(rowid, question) VALUES (new.id, new.question);
                     END;
                     CREATE TRIGGER decisions_ad AFTER DELETE ON decisions BEGIN
                       INSERT INTO decisions_fts(decisions_fts, rowid, question) VALUES ('delete', old.id, old.question);
                     END;"

                for question in [ "Which database for events?"; "Which shell on Windows?" ] do
                    Database.execute db "INSERT INTO decisions (question) VALUES (?)" [ Value.Text question ]
                    |> ok "insert"
                    |> ignore

                let search term =
                    texts
                        db
                        "SELECT d.question FROM decisions_fts f JOIN decisions d ON d.id = f.rowid WHERE decisions_fts MATCH ?"
                        [ Value.Text term ]

                Expect.equal (search "windows") [ "Which shell on Windows?" ] "found"

                Database.execute db "DELETE FROM decisions WHERE id = 2" []
                |> ok "delete"
                |> ignore

                Expect.equal (search "windows") [] "removed from the index"
            }

            test "JSON functions and operators" {
                use db = Database.memory ()
                let doc = """{"kind":"Opened","by":"alice","tags":["db","perf"],"n":3}"""

                Expect.equal
                    (Database.tryFirst
                        db
                        "SELECT ? ->> '$.kind', json_extract(?, '$.n'), json_array_length(?, '$.tags'), json_valid(?)"
                        [ Value.Text doc; Value.Text doc; Value.Text doc; Value.Text doc ]
                        (fun row -> row.Text 0, row.Int64 1, row.Int64 2, row.Int64 3)
                     |> ok "extract")
                    (Some("Opened", 3L, 2L, 1L))
                    "scalars"

                Expect.equal
                    (texts db "SELECT value FROM json_each(?, '$.tags') ORDER BY key" [ Value.Text doc ])
                    [ "db"; "perf" ]
                    "json_each"

                Expect.equal (texts db "SELECT json(jsonb(?))" [ Value.Text doc ]) [ doc ] "jsonb round trip"

                Expect.equal
                    (texts db "SELECT json_group_array(value) FROM json_each('[3,1,2]')" [])
                    [ "[3,1,2]" ]
                    "aggregate"

                let error =
                    Database.query db "SELECT json(?)" [ Value.Text "{bad" ] ignore
                    |> failed "malformed"

                Expect.stringContains error.Message "malformed JSON" "malformed JSON is a result"
            }

            test "hardening: no extension loading, no double-quoted strings, defensive mode" {
                use db = Database.memory ()

                let noLoader =
                    Database.query db "SELECT load_extension('x')" [] ignore
                    |> failed "load_extension"

                Expect.stringContains noLoader.Message "no such function: load_extension" "extensions"

                let dqs = Database.query db "SELECT \"not a string\"" [] ignore |> failed "DQS"
                Expect.stringContains dqs.Message "no such column" "double quotes name identifiers"

                exec db "CREATE TABLE t (x)"
                exec db "PRAGMA writable_schema=ON"

                let schema =
                    Database.execute db "UPDATE sqlite_schema SET sql = 'CREATE TABLE t (y)' WHERE name = 't'" []
                    |> failed "writable schema"

                Expect.stringContains schema.Message "may not be modified" "defensive"
                Expect.equal (texts db "PRAGMA trusted_schema" []) [ "0" ] "untrusted schema"
            }

            test "serialize, deserialize and backup copy whole databases" {
                use dir = new TempDir()
                use source = openFile (dir.File "source.db")
                exec source "CREATE TABLE t (x TEXT); INSERT INTO t VALUES ('a'), ('b')"

                let bytes = Database.serialize source |> ok "serialize"
                Expect.equal (Text.Encoding.ASCII.GetString(bytes, 0, 15)) "SQLite format 3" "file format"

                use copy = Database.memory ()
                Database.deserialize copy bytes |> ok "deserialize"
                Expect.equal (texts copy "SELECT x FROM t ORDER BY x" []) [ "a"; "b" ] "deserialized"
                exec copy "INSERT INTO t VALUES ('c')"
                Expect.equal (scalar copy "SELECT count(*) FROM t" []) 3L "writable copy"

                use target = openFile (dir.File "target.db")
                Database.backup copy target |> ok "backup"
                Expect.equal (texts target "SELECT x FROM t ORDER BY x" []) [ "a"; "b"; "c" ] "backed up"

                Database.execute source "VACUUM INTO ?" [ Value.Text(dir.File "vacuumed.db") ]
                |> ok "VACUUM INTO"
                |> ignore

                use vacuumed = openFile (dir.File "vacuumed.db")
                Expect.equal (scalar vacuumed "SELECT count(*) FROM t" []) 2L "VACUUM INTO"
            }

            test "a cancelled token interrupts a running statement" {
                use db = Database.memory ()
                use cancellation = new CancellationTokenSource()

                let forever =
                    "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT count(*) FROM c"

                let running =
                    Task.Run(fun () ->
                        Database.cancellable db cancellation.Token (fun () -> Database.query db forever [] ignore))

                Thread.Sleep 100
                cancellation.Cancel()
                let result = running.WaitAsync(TimeSpan.FromSeconds 30.0).Result
                Expect.equal (failed "cancelled" result).Code ErrorCode.Interrupt "interrupted"
                Expect.equal (scalar db "SELECT 1" []) 1L "the connection still works"

                let before =
                    Database.cancellable db cancellation.Token (fun () -> Database.query db "SELECT 1" [] ignore)

                Expect.equal (failed "cancelled before" before).Code ErrorCode.Interrupt "cancelled before it started"

                let uncancellable =
                    Database.cancellable db CancellationToken.None (fun () -> Database.query db "SELECT 1" [] ignore)

                Expect.equal uncancellable (Ok [ () ]) "runs"
            }

            test "an interrupt with nothing running changes nothing" {
                use db = Database.memory ()
                Database.interrupt db
                Expect.equal (scalar db "SELECT 2" []) 2L "runs"
                db.Dispose()
                Database.interrupt db
            }

            test "files are created with the database" {
                use dir = new TempDir()
                let path = dir.File "created.db"
                use db = openFile path
                Expect.isTrue (File.Exists path) "file"
                Expect.equal db.Name path "name"
            }
        ]
