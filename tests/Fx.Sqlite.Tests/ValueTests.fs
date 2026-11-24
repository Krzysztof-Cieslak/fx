module Fx.Sqlite.Tests.ValueTests

open System
open Expecto
open FsCheck
open Fx.Sqlite
open Fx.Sqlite.Tests.Support

/// What `SELECT ?` gives back for a bound value.
let private echo (db: Database) (value: Value) : Value =
    match Database.tryFirst db "SELECT ?" [ value ] (fun row -> row.Value 0) |> ok "echo" with
    | Some echoed -> echoed
    | None -> failtest "no row"

/// Whether a string survives UTF-8: no unpaired surrogates.
let private wellFormed (s: string) =
    let mutable i = 0
    let mutable ok = true

    while ok && i < s.Length do
        if Char.IsHighSurrogate s[i] then
            ok <- i + 1 < s.Length && Char.IsLowSurrogate s[i + 1]
            i <- i + 2
        else
            ok <- not (Char.IsLowSurrogate s[i])
            i <- i + 1

    ok

[<Tests>]
let tests =
    testList
        "Values"
        [
            testProperty "integers round-trip" (fun (n: int64) ->
                use db = Database.memory ()
                echo db (Value.Integer n) = Value.Integer n)

            testProperty "reals round-trip" (fun (r: NormalFloat) ->
                use db = Database.memory ()
                echo db (Value.Real r.Get) = Value.Real r.Get)

            testProperty "text round-trips, NUL characters included" (fun (s: string) ->
                // FsCheck also generates null and unpaired surrogates, which UTF-8 cannot carry.
                isNull (box s)
                || not (wellFormed s)
                || (use db = Database.memory ()
                    echo db (Value.Text s) = Value.Text s))

            testProperty "blobs round-trip" (fun (b: byte array) ->
                isNull (box b)
                || (use db = Database.memory ()

                    match echo db (Value.Blob b) with
                    | Value.Blob echoed -> echoed = b
                    | _ -> false))

            test "edge values" {
                use db = Database.memory ()

                for value in
                    [
                        Value.Null
                        Value.Integer Int64.MinValue
                        Value.Integer Int64.MaxValue
                        Value.Real Double.MaxValue
                        Value.Real Double.Epsilon
                        Value.Real Double.PositiveInfinity
                        Value.Real Double.NegativeInfinity
                        Value.Text ""
                        Value.Text "naïve façade, 日本語, emoji 🎉"
                        Value.Blob [||]
                        Value.Blob [| 0uy; 255uy |]
                    ] do
                    Expect.equal (echo db value) value (string value)
            }

            test "NaN is stored as NULL" {
                use db = Database.memory ()
                Expect.equal (echo db (Value.Real Double.NaN)) Value.Null "NaN"
            }

            test "typed readers convert like SQLite and reject NULL" {
                use db = Database.memory ()

                let row =
                    Database.tryFirst db "SELECT 42, 2.5, 'text', x'0102', NULL, '7'" [] (fun row ->
                        [ for i in 0 .. row.Count - 1 -> string (row.Type i) ],
                        row.Int64 0,
                        row.Double 1,
                        row.Text 2,
                        row.Blob 3,
                        row.IsNull 4,
                        row.Int64 5,
                        row.Text 0,
                        Throws.check (fun () -> row.Int64 4))
                    |> ok "select"

                match row with
                | Some(types, i, d, t, b, isNull, converted, asText, nullRead) ->
                    Expect.equal types [ "INTEGER"; "REAL"; "TEXT"; "BLOB"; "NULL"; "TEXT" ] "types"
                    Expect.equal (i, d, t, b, isNull) (42L, 2.5, "text", [| 1uy; 2uy |], true) "values"
                    Expect.equal (converted, asText) (7L, "42") "conversions"
                    Expect.equal nullRead "InvalidOperationException" "NULL rejected"
                | None -> failtest "no row"
            }

            test "column names and range checks" {
                use db = Database.memory ()

                let names =
                    Database.tryFirst db "SELECT 1 AS one, 2 AS two" [] (fun row ->
                        [ row.Name 0; row.Name 1 ], Throws.check (fun () -> row.Int64 2))
                    |> ok "select"

                Expect.equal names (Some([ "one"; "two" ], "ArgumentOutOfRangeException")) "names"
            }

            test "a STRICT table stores what it is given" {
                use db = Database.memory ()
                exec db "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL, data BLOB, score REAL) STRICT"
                let payload = Array.init 4_000_000 byte

                for i in 1L .. 3L do
                    Database.execute
                        db
                        "INSERT INTO t (name, data, score) VALUES (?, ?, ?)"
                        [ Value.Text $"row {i}"; Value.Blob payload; Value.Real(float i / 2.0) ]
                    |> ok "insert"
                    |> fun changes -> Expect.equal changes 1L "one row"

                Expect.equal (Database.lastInsertRowId db) 3L "last rowid"

                Expect.equal
                    (Database.execute db "CREATE INDEX t_name ON t (name)" [] |> ok "index")
                    0L
                    "statements other than INSERT, UPDATE and DELETE change no rows"

                Expect.equal (Database.execute db "UPDATE t SET score = score * 2" [] |> ok "update") 3L "updated"

                let rows =
                    Database.query db "SELECT id, name, length(data), score FROM t ORDER BY id" [] (fun row ->
                        row.Int64 0, row.Text 1, row.Int64 2, row.Double 3)
                    |> ok "select"

                Expect.equal
                    rows
                    [
                        1L, "row 1", 4_000_000L, 1.0
                        2L, "row 2", 4_000_000L, 2.0
                        3L, "row 3", 4_000_000L, 3.0
                    ]
                    "rows"

                let data =
                    Database.tryFirst db "SELECT data FROM t WHERE id = ?" [ Value.Integer 2L ] (fun row -> row.Blob 0)
                    |> ok "blob"

                Expect.equal data (Some payload) "4 MB blob"

                let mismatch =
                    Database.execute
                        db
                        "INSERT INTO t (name, score) VALUES (?, ?)"
                        [ Value.Text "x"; Value.Text "high" ]
                    |> failed "text into REAL"

                Expect.equal
                    (mismatch.Code, mismatch.ExtendedCode)
                    (ErrorCode.Constraint, 3091)
                    "SQLITE_CONSTRAINT_DATATYPE"
            }
        ]
