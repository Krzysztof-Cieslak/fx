/// A writer for the crash and multi-process tests of the event store (tests/Fx.Store.Live.Tests).
///
/// - `append <db> <stream> <full|normal> <last|forever>`: appends commits `c<n>` after the last one in
///   the stream, until commit `last` (or until killed), and prints `committed <n> <head>` after each.
/// - `contend <db> <stream> <name> <count>`: prints `ready`; then, `count` times, waits for a line on
///   standard input, appends commit `<name>-<i>` (retrying after each conflict with a fresh head) and
///   prints `committed <i>`; then prints `done <conflicts>`. The test starts each round in both writers
///   at once, so they meet at the head check, and neither holds the write lock for more than one
///   commit while the other waits: SQLite's busy timeout polls, without a queue, and a writer that
///   commits back to back can keep the lock longer than the timeout (Windows, synced commits).
module Fx.Store.Writer.Program

open System
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Store.Live

let private none = CancellationToken.None
let private wait (task: Task<'T>) = task.GetAwaiter().GetResult()

let private say (line: string) =
    stdout.Write(line + "\n")
    stdout.Flush()

/// The events of commit `n`: one to four of them, `n.1`, `n.2`, …
let eventsOf (n: int64) : string list =
    [ for j in 1L .. 1L + n % 4L -> $"{n}.{j}" ]

let private meta (key: string) : EventMeta =
    {
        At = Instant.ofUnixMicroseconds 1_764_000_000_000_000L
        By = Attribution.create "writer" "test"
        Key = IdempotencyKey key
        Cause = None
    }

let private openLog (path: string) (durability: Durability) =
    match
        SqliteEventStore.openFile
            path
            { EventStoreOptions.defaults with
                Durability = durability
            }
    with
    | Ok store -> store, EventLog.typed (EventSchema.create Codec.string) store.Log
    | Error error -> failwith (string error)

/// The number of the last commit in the stream, from its key `c<n>`; 0 for an empty stream.
let private lastCommit (log: EventLog<string>) (stream: StreamId) : int64 =
    let head = wait (log.Head stream none)

    if head = 0L then
        0L
    else
        match wait (log.Read stream (head - 1L) 1 none) with
        | [ last ] -> Int64.Parse(last.Meta.Key.Value.Substring 1)
        | _ -> failwith "the last event is missing"

let private append (path: string) (stream: StreamId) (durability: Durability) (last: int64 option) =
    let store, log = openLog path durability
    use _ = store
    let mutable n = lastCommit log stream + 1L
    let mutable head = wait (log.Head stream none)

    while last |> Option.forall (fun last -> n <= last) do
        match wait (log.Append stream head (meta $"c{n}") (eventsOf n) none) with
        | Ok next ->
            head <- next
            say $"committed {n} {head}"
            n <- n + 1L
        | Error error -> failwith (string error)

    0

let private contend (path: string) (stream: StreamId) (name: string) (count: int) =
    let store, log = openLog path Durability.Full
    use _ = store
    say "ready"
    let mutable conflicts = 0

    for i in 1..count do
        stdin.ReadLine() |> ignore
        let mutable written = false

        while not written do
            let head = wait (log.Head stream none)

            match wait (log.Append stream head (meta $"{name}-{i}") [ $"{name}-{i}.1"; $"{name}-{i}.2" ] none) with
            | Ok _ -> written <- true
            | Error(Conflict _) -> conflicts <- conflicts + 1
            | Error(Duplicate _ as error) -> failwith (string error)

        say $"committed {i}"

    say $"done {conflicts}"
    0

[<EntryPoint>]
let main argv =
    let durability =
        function
        | "full" -> Durability.Full
        | "normal" -> Durability.Normal
        | other -> failwith $"unknown durability {other}"

    match List.ofArray argv with
    | [ "append"; path; stream; level; "forever" ] -> append path (StreamId stream) (durability level) None
    | [ "append"; path; stream; level; last ] ->
        append path (StreamId stream) (durability level) (Some(Int64.Parse last))
    | [ "contend"; path; stream; name; count ] -> contend path (StreamId stream) name (Int32.Parse count)
    | _ ->
        stderr.Write "usage: append <db> <stream> <full|normal> <last|forever> | contend <db> <stream> <name> <count>\n"
        2
