/// Measures the event store and the blob store (docs/modules/store.md, "Performance"): append
/// throughput and latency with one writer and with many (group commit), read and wake-up latency,
/// and blob throughput, with full and normal durability. Prints Markdown tables. Numbers vary by
/// machine and disk; the ratios are what matter.
module Fx.Store.Bench.Program

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Store.Live

let private none = CancellationToken.None
let private wait (task: Task<'T>) = task.GetAwaiter().GetResult()

/// A typical small event: about 200 bytes of JSON.
let private event (i: int) =
    $"tool call {i} finished: exit 0, 12 lines of output, 3 files changed under src/Fx.Store.Live, took 1.2 s"

let private meta (key: string) : EventMeta =
    {
        At = Instant.ofUnixMicroseconds 1_764_000_000_000_000L
        By = Attribution.create "user:bench" "bench"
        Key = IdempotencyKey key
        Cause = None
    }

let private inTemp (run: string -> 'T) : 'T =
    let dir =
        Path.Combine(Path.GetTempPath(), "store-bench-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    try
        run dir
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

let private openStore (dir: string) (durability: Durability) =
    match
        SqliteEventStore.openFile
            (Path.Combine(dir, "events.db"))
            { EventStoreOptions.defaults with
                Durability = durability
            }
    with
    | Ok store -> store
    | Error error -> failwith (string error)

let private percentile (p: float) (samples: TimeSpan array) =
    let sorted = Array.sort samples
    sorted[min (sorted.Length - 1) (int (float sorted.Length * p))]

let private us (t: TimeSpan) = t.TotalMicroseconds.ToString("0")

let private perSecond (count: int) (elapsed: TimeSpan) =
    (float count / elapsed.TotalSeconds).ToString("N0")

let private row (cells: string list) = "| " + String.concat " | " cells + " |"

/// One writer appends commits of `size` events, waiting for each.
let private sequential (durability: Durability) (commits: int) (size: int) =
    inTemp (fun dir ->
        use store = openStore dir durability
        let log = EventLog.typed (EventSchema.create Codec.string) store.Log
        let stream = StreamId "bench/1"
        let latencies = Array.zeroCreate commits
        let total = Stopwatch.StartNew()

        for i in 0 .. commits - 1 do
            let started = Stopwatch.GetTimestamp()

            match wait (log.Append stream (int64 (i * size)) (meta $"k{i}") [ for j in 1..size -> event j ] none) with
            | Ok _ -> ()
            | Error error -> failwith (string error)

            latencies[i] <- Stopwatch.GetElapsedTime started

        total.Stop()

        row
            [
                $"one writer, {size} event(s) per commit"
                string durability
                perSecond commits total.Elapsed
                perSecond (commits * size) total.Elapsed
                us (percentile 0.5 latencies)
                us (percentile 0.99 latencies)
            ])

/// `writers` writers, each on its own stream, append `commits` commits of one event at once.
let private concurrent (durability: Durability) (writers: int) (commits: int) =
    inTemp (fun dir ->
        use store = openStore dir durability
        let log = EventLog.typed (EventSchema.create Codec.string) store.Log
        let latencies = Array.zeroCreate (writers * commits)
        let total = Stopwatch.StartNew()

        let writer (w: int) =
            Task.Run(fun () ->
                let stream = StreamId $"bench/{w}"

                for i in 0 .. commits - 1 do
                    let started = Stopwatch.GetTimestamp()

                    match wait (log.Append stream (int64 i) (meta $"k{i}") [ event i ] none) with
                    | Ok _ -> ()
                    | Error error -> failwith (string error)

                    latencies[w * commits + i] <- Stopwatch.GetElapsedTime started)

        Task.WaitAll [| for w in 0 .. writers - 1 -> writer w |]
        total.Stop()

        row
            [
                $"{writers} writers, 1 event per commit"
                string durability
                perSecond (writers * commits) total.Elapsed
                perSecond (writers * commits) total.Elapsed
                us (percentile 0.5 latencies)
                us (percentile 0.99 latencies)
            ])

let private time (count: int) (operation: int -> unit) =
    let latencies = Array.zeroCreate count

    for i in 0 .. count - 1 do
        let started = Stopwatch.GetTimestamp()
        operation i
        latencies[i] <- Stopwatch.GetElapsedTime started

    latencies

/// Reads from a stream of 10,000 events, and the time from an append to a subscriber seeing it.
let private reads () =
    inTemp (fun dir ->
        use store = openStore dir Durability.Normal
        let log = EventLog.typed (EventSchema.create Codec.string) store.Log
        let stream = StreamId "bench/read"

        for i in 0..99 do
            match wait (log.Append stream (int64 (i * 100)) (meta $"k{i}") [ for j in 1..100 -> event j ] none) with
            | Ok _ -> ()
            | Error error -> failwith (string error)

        let random = Random 42
        let head = time 2000 (fun _ -> wait (log.Head stream none) |> ignore)

        let one =
            time 2000 (fun _ -> wait (log.Read stream (int64 (random.Next 9999)) 1 none) |> ignore)

        let page =
            time 500 (fun _ -> wait (log.Read stream (int64 (random.Next 9900)) 100 none) |> ignore)

        let full = time 20 (fun _ -> wait (log.Read stream 0L 10_000 none) |> ignore)

        // A waiter on the head, then an append: from the append until the waiter has woken.
        let woken =
            time 500 (fun i ->
                let waiting = log.WaitPast stream (10_000L + int64 i) none

                wait (log.Append stream (10_000L + int64 i) (meta $"w{i}") [ event i ] none)
                |> ignore

                wait waiting |> ignore)

        let line (what: string) (samples: TimeSpan array) =
            row [ what; us (percentile 0.5 samples); us (percentile 0.99 samples) ]

        [
            line "head" head
            line "read 1 event" one
            line "read a page of 100 events" page
            line "read 10,000 events (pages of 10,000)" full
            line "append, then the waiter wakes (normal)" woken
        ])

let private blobs (durability: Durability) =
    inTemp (fun dir ->
        let ids = SystemIds.create SystemClock.clock

        use store =
            match FileBlobStore.openAt (Path.Combine(dir, "blobs")) SystemClock.clock ids durability with
            | Ok store -> store
            | Error error -> failwith (string error)

        let mib = 1 <<< 20

        let contents =
            [| for i in 0..63 -> Array.init mib (fun j -> byte ((i * 7 + j) % 256)) |]

        let put = Stopwatch.StartNew()

        let refs =
            contents |> Array.map (fun content -> wait (store.Blobs.Put content none))

        put.Stop()
        let get = Stopwatch.StartNew()

        for blob in refs do
            match wait (store.Blobs.Get blob.Hash none) with
            | Ok _ -> ()
            | Error error -> failwith (string error)

        get.Stop()

        let small =
            time 500 (fun i ->
                wait (store.Blobs.Put (BitConverter.GetBytes(int64 i + 1_000_000L)) none)
                |> ignore)

        let again = Stopwatch.StartNew()

        for content in contents do
            wait (store.Blobs.Put content none) |> ignore

        again.Stop()

        let mbps (elapsed: TimeSpan) =
            (float contents.Length / elapsed.TotalSeconds).ToString("N0")

        row
            [
                string durability
                mbps put.Elapsed
                mbps again.Elapsed
                mbps get.Elapsed
                us (percentile 0.5 small)
                us (percentile 0.99 small)
            ])

[<EntryPoint>]
let main _ =
    // Warm up the JIT and SQLite before measuring.
    sequential Durability.Normal 200 1 |> ignore
    concurrent Durability.Normal 4 50 |> ignore

    let lines =
        [
            "## Appends"
            ""
            row [ "Case"; "Durability"; "Commits/s"; "Events/s"; "p50 µs"; "p99 µs" ]
            row [ "---"; "---"; "---:"; "---:"; "---:"; "---:" ]
            sequential Durability.Full 300 1
            sequential Durability.Normal 3000 1
            sequential Durability.Full 300 10
            sequential Durability.Normal 1000 10
            concurrent Durability.Full 32 50
            concurrent Durability.Normal 32 200
            ""
            "## Reads (10,000 events of about 200 bytes)"
            ""
            row [ "Operation"; "p50 µs"; "p99 µs" ]
            row [ "---"; "---:"; "---:" ]
            yield! reads ()
            ""
            "## Blobs (64 blobs of 1 MiB, then 500 blobs of 8 bytes)"
            ""
            row
                [
                    "Durability"
                    "Put MiB/s"
                    "Put again MiB/s"
                    "Get MiB/s"
                    "Small put p50 µs"
                    "Small put p99 µs"
                ]
            row [ "---"; "---:"; "---:"; "---:"; "---:"; "---:" ]
            blobs Durability.Full
            blobs Durability.Normal
        ]

    for line in lines do
        stdout.Write(line + "\n")

    0
