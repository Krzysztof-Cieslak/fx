/// Writers in child processes (tests/support/Fx.Store.Writer): one killed in the middle of appending,
/// and two that append to one stream at the same time.
module Fx.Store.Live.Tests.CrashTests

open System
open System.Diagnostics
open System.IO
open System.Text
open Expecto
open Fx.Machines
open Fx.Machines.Host
open Fx.Sqlite
open Fx.Store
open Fx.Store.Live.Tests.Support

/// The writer's apphost, copied next to the tests by the project reference.
let private writerPath =
    Path.Combine(AppContext.BaseDirectory, "Fx.Store.Writer" + (if OperatingSystem.IsWindows() then ".exe" else ""))

let private start (args: string list) : Process =
    let info =
        ProcessStartInfo(
            writerPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = UTF8Encoding(false)
        )

    for arg in args do
        info.ArgumentList.Add arg

    Process.Start info |> nonNull

/// The next line the child writes, within a bound; fails with its error output if it ended.
let private nextLine (child: Process) : string =
    let reading = child.StandardOutput.ReadLineAsync()

    if not (reading.Wait(TimeSpan.FromSeconds 60.0)) then
        child.Kill true
        failtest "the writer said nothing for 60 s"

    match reading.Result with
    | null ->
        child.WaitForExit()
        failtest $"the writer ended ({child.ExitCode}): {child.StandardError.ReadToEnd()}"
    | line -> line

let private finish (child: Process) =
    if not (child.WaitForExit(TimeSpan.FromSeconds 60.0)) then
        child.Kill true
        failtest "the writer did not exit"

    Expect.equal child.ExitCode 0 $"the writer failed: {child.StandardError.ReadToEnd()}"

/// The events of commit `c<n>`, as the writer makes them.
let private eventsOf (n: int64) =
    [ for j in 1L .. 1L + n % 4L -> $"{n}.{j}" ]

let private integrity (path: string) =
    use db =
        match Database.connect path OpenOptions.defaults with
        | Ok db -> db
        | Error error -> failtest (string error)

    match Database.query db "PRAGMA integrity_check" [] (fun row -> row.Text 0) with
    | Ok rows -> rows
    | Error error -> failtest (string error)

/// Checks the writer's stream after a kill: sequence numbers count from 1, commits `c1`…`cM` follow
/// each other, each whole. Returns M.
let private checkWriterLog (path: string) (stream: StreamId) : int64 =
    use store = openStore path
    let events = readAll (EventLog.typed strings store.Log) stream
    Expect.equal (events |> List.map _.Seq) [ 1L .. int64 events.Length ] "sequence numbers count from 1 without gaps"

    let commits =
        events
        |> List.fold
            (fun (commits: (string * string list) list) recorded ->
                match commits with
                | (key, values) :: rest when key = recorded.Meta.Key.Value ->
                    (key, values @ [ recorded.Event ]) :: rest
                | _ -> (recorded.Meta.Key.Value, [ recorded.Event ]) :: commits)
            []
        |> List.rev

    commits
    |> List.iteri (fun i (key, values) ->
        let n = int64 i + 1L
        Expect.equal key $"c{n}" "commits follow each other"
        Expect.equal values (eventsOf n) $"commit {key} is whole")

    Expect.equal (wait (store.Log.Head stream none)) (int64 events.Length) "the head is the last event"
    int64 commits.Length

[<Tests>]
let tests =
    testSequenced
    <| testList
        "Crashes"
        [
            test "a writer killed mid-append leaves a consistent log, and every reported commit" {
                let path = Path.Combine(Scratch.directory (), "events.db")
                let stream = "crash/1"
                let mutable kept = 0L

                for round, durability in [ 1, "full"; 2, "normal"; 3, "full" ] do
                    let child = start [ "append"; path; stream; durability; "forever" ]
                    let mutable reported = 0L

                    try
                        // Let it write a while past where it started, then kill it wherever it is.
                        while reported < kept + 40L do
                            match (nextLine child).Split ' ' with
                            | [| "committed"; n; _ |] -> reported <- Int64.Parse n
                            | other -> failtest $"""unexpected output: {String.Join(" ", other)}"""
                    finally
                        child.Kill true
                        child.WaitForExit()

                    Expect.equal (integrity path) [ "ok" ] $"round {round}: the database is intact"
                    kept <- checkWriterLog path (StreamId stream)
                    Expect.isGreaterThanOrEqual kept reported $"round {round}: every reported commit is kept"

                // The writer's keys are still known: its last commit cannot be appended again.
                use store = openStore path
                let log = EventLog.typed strings store.Log
                let head = wait (log.Head (StreamId stream) none)

                match wait (log.Append (StreamId stream) head (meta $"c{kept}") (eventsOf kept) none) with
                | Error(Duplicate(key, appended)) ->
                    Expect.equal key (IdempotencyKey $"c{kept}") "key"
                    Expect.equal appended.Last head "the last commit"
                | other -> failtest $"expected a duplicate, got {other}"
            }

            test "writers in two processes meet at the head check: every commit lands once, whole" {
                let path = Path.Combine(Scratch.directory (), "events.db")

                // Create the database first, so the writers do not race to create it.
                (openStore path).Dispose()

                let writers =
                    [ "a"; "b" ]
                    |> List.map (fun name -> start [ "contend"; path; "shared/1"; name; "150" ])

                try
                    for writer in writers do
                        Expect.equal (nextLine writer) "ready" "ready"

                    for writer in writers do
                        writer.StandardInput.WriteLine "go"
                        writer.StandardInput.Flush()

                    for writer in writers do
                        Expect.stringStarts (nextLine writer) "done " "done"
                        finish writer
                finally
                    for writer in writers do
                        if not writer.HasExited then
                            writer.Kill true

                use store = openStore path
                let events = readAll (EventLog.typed strings store.Log) (StreamId "shared/1")
                Expect.equal events.Length 600 "every event"
                Expect.equal (events |> List.map _.Seq) [ 1L .. 600L ] "sequence numbers count from 1 without gaps"

                let commits =
                    events
                    |> List.chunkBySize 2
                    |> List.map (fun pair ->
                        let key = pair.Head.Meta.Key.Value
                        Expect.equal (pair |> List.map _.Event) [ key + ".1"; key + ".2" ] $"commit {key} is whole"
                        key)

                for name in [ "a"; "b" ] do
                    Expect.equal
                        (commits
                         |> List.filter (fun key -> key.StartsWith(name + "-", StringComparison.Ordinal)))
                        [ for i in 1..150 -> $"{name}-{i}" ]
                        $"writer {name}: each commit once, in order"
            }
        ]
