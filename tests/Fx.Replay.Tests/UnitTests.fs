/// The journal and the interpreters on their own: version 1 of the journal's JSON (kept), concurrent
/// effects and timeouts recorded and replayed with the same results, values in the blob store, and a
/// replay that asks something else reported as a divergence naming the call.
module Fx.Replay.Tests.UnitTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Store.Fakes
open Fx.Replay
open Fx.Replay.Tests.Support

/// An environment with a clock and files.
type Env =
    {
        Clock: Clock
        FsRead: FsRead
        Fs: Fs
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IFs with
        member env.FsRead = env.FsRead
        member env.Fs = env.Fs

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "Env"

/// A journal in memory.
type private Stores() =
    let log = InMemoryEventLog<Payload>()
    let blobs = InMemoryBlobs()

    member val Journal =
        SessionJournal("s1", JournalLog.typed log.Log, blobs.Blobs, SystemClock.clock, RecorderSettings.defaults)

    member _.Recording: RecordedSession = { Log = log.Log; Blobs = blobs.Blobs }

    member _.Log = log
    member _.Blobs = blobs

/// Records `program` in an environment whose clock and files are journaled; gives its result.
let private record (stores: Stores) (fs: InMemoryFs) (program: Eff<Env, 'T>) : Task<'T> =
    task {
        let env: Env =
            {
                Clock = Record.clock stores.Journal "env" SystemClock.clock
                FsRead = Record.fsRead stores.Journal "env" fs.FsRead
                Fs = Record.fs stores.Journal "env" fs.Fs
                Scope = Scope.root
            }

        let! result = Eff.run env program
        do! stores.Journal.Close()
        return result
    }

/// Replays `program` from the journal; gives its result and the segment.
let private replayed (stores: Stores) (program: Eff<Env, 'T>) : Task<Result<'T, exn> * ReplaySegment> =
    task {
        let! segments = SessionReplay.segments stores.Recording "s1" CancellationToken.None
        let journal = List.exactlyOne segments
        let segment = ReplaySegment(journal.Number, journal.Entries, journal.Ended)

        let env: Env =
            {
                Clock = Replay.clock segment "env"
                FsRead = Replay.fsRead segment "env"
                Fs = Replay.fs segment "env"
                Scope = Scope.ofCancellation segment.Stop
            }

        let! result =
            task {
                try
                    let! value = (Eff.run env program).WaitAsync(TimeSpan.FromSeconds 30.0)
                    return Ok value
                with e ->
                    return Error e
            }

        return result, segment
    }

let private sample: JournalEvent list =
    [
        JournalEvent.Began 1
        JournalEvent.Recorded(
            1,
            [
                {
                    Seq = 1L
                    Lane = "turn/1"
                    Index = 0L
                    Op = "Clock.Now"
                    Input = ""
                    Summary = ""
                    Outcome = Outcome.Returned(Stored.Inline(Json.int64 1_700_000_000_000_000L))
                }
                {
                    Seq = 2L
                    Lane = "hands"
                    Index = 0L
                    Op = "Fs.ReadFile"
                    Input = Digest.json (Json.String "/work/a.txt")
                    Summary = "/work/a.txt"
                    Outcome =
                        Outcome.Returned(
                            Stored.Blob
                                {
                                    Hash = BlobHash.ofBytes [| 1uy |]
                                    Size = 1L
                                }
                        )
                }
                {
                    Seq = 3L
                    Lane = "hands"
                    Index = 1L
                    Op = "Clock.Delay"
                    Input = Digest.json (Json.int64 10_000L)
                    Summary = "1 ms"
                    Outcome = Outcome.Cancelled
                }
                {
                    Seq = 4L
                    Lane = "hands/proc/0"
                    Index = 0L
                    Op = "Proc.Read"
                    Input = ""
                    Summary = ""
                    Outcome = Outcome.Pending
                }
                {
                    Seq = 5L
                    Lane = "turn/1"
                    Index = 1L
                    Op = "Llm.Stream"
                    Input = Digest.json Json.Null
                    Summary = "test/model (2 messages)"
                    Outcome = Outcome.Threw "HttpRequestException"
                }
            ]
        )
        JournalEvent.Ended 1
    ]

let private lines (events: JournalEvent list) =
    events
    |> List.map (fun e -> Json.serializeCanonical (JournalCodecs.event.Encode e))
    |> String.concat "\n"

/// A program whose children read the clock and sleep concurrently, waking in another order than they
/// started, some at the same time (equal delays): each child's second reading depends on when it woke.
let private concurrent: Eff<Env, (Instant * Instant) list> =
    let child (delay: int) : Eff<Env, Instant * Instant> =
        eff {
            let! first = Clock.now
            do! Clock.sleep (TimeSpan.FromMilliseconds(float delay))
            let! second = Clock.now
            return first, second
        }

    Eff.all [ for i in 1..8 -> child (1 + (i * 7) % 5) ]

/// A stream the replayed store writes again (as a session's step journal).
let private steps = StreamId.create "steps" "s1"

let private stepMeta (key: string) : EventMeta =
    {
        At = Instant.ofUnixMicroseconds 1_700_000_000_000_000L
        By = Attribution.system "test"
        Key = IdempotencyKey key
        Cause = None
    }

let private step (n: int) : Payload =
    {
        Version = 1
        Data = Json.Object [ "n", Json.int n ]
    }

/// Records an append to `steps` and a read that saw it, through the recording store.
let private recordAppendAndRead (stores: Stores) : Task<unit> =
    task {
        let log = Record.log stores.Journal Markers.Store id stores.Log.Log
        let! appended = log.Append steps 0L (stepMeta "k1") [ step 1 ] CancellationToken.None
        Expect.equal appended (Ok 1L) "recorded append"
        let! page = log.Read steps 0L 100 CancellationToken.None
        Expect.equal page.Length 1 "recorded read"
        do! stores.Journal.Close()
    }

/// The segment of a recording, and the store replay writes again into `written` (wrapped by `wrap`).
let private replayedStore
    (stores: Stores)
    (written: InMemoryEventLog<Payload>)
    (wrap: ReplaySegment -> EventLog<Payload> -> EventLog<Payload>)
    : Task<ReplaySegment * EventLog<Payload>> =
    task {
        let! segments = SessionReplay.segments stores.Recording "s1" CancellationToken.None
        let journal = List.exactlyOne segments
        let segment = ReplaySegment(journal.Number, journal.Entries, journal.Ended)

        let store =
            Replay.store segment Markers.Store stores.Recording.Log (wrap segment written.Log) [ steps ] (fun _ _ _ ->
                "")

        return segment, store
    }

let private entryOf (segment: ReplaySegment) (op: string) : ReplayEntry =
    segment.Entries |> List.find (fun e -> e.Entry.Op = op)

/// Runs `work` while busy threads keep every core loaded, so continuations are scheduled late and in
/// any order.
let private underLoad (work: unit -> Task<'T>) : Task<'T> =
    task {
        use stop = new CancellationTokenSource()

        let spinners =
            [
                for _ in 1 .. Environment.ProcessorCount * 2 ->
                    Thread(
                        (fun () ->
                            while not stop.IsCancellationRequested do
                                Thread.SpinWait 1000),
                        IsBackground = true
                    )
            ]

        for spinner in spinners do
            spinner.Start()

        try
            return! work ()
        finally
            stop.Cancel()

            for spinner in spinners do
                spinner.Join()
    }

[<Tests>]
let tests =
    testList
        "journal and interpreters"
        [
            test "version 1 of the journal's JSON is kept, and read back" {
                let written = lines sample
                let kept = Golden.kept "journal-events-v1.jsonl" (written + "\n")

                let read =
                    kept.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun line ->
                        match
                            Json.parse line
                            |> Result.mapError string
                            |> Result.bind (fun j ->
                                JournalCodecs.event.Decode j |> Result.mapError (Issue.renderAtMost 3))
                        with
                        | Ok event -> event
                        | Error e -> failtest e)
                    |> List.ofArray

                Expect.equal read sample "the events"
                Expect.equal (lines read) written "the same JSON"
            }

            testTask "concurrent children replay with the values and wake-ups they had, under load" {
                // Replay lets flows that waited go one at a time, in the order they woke when recorded,
                // each until it waits again: so each reads the clock in its recorded turn.
                for _ in 1..30 do
                    let stores = Stores()
                    let fs = InMemoryFs(SystemClock.clock, false)
                    let! recorded = underLoad (fun () -> record stores fs concurrent)

                    let! (replayed: Result<_, exn>), (segment: ReplaySegment) =
                        underLoad (fun () -> replayed stores concurrent)

                    match replayed with
                    | Ok values -> Expect.equal values recorded "the same times"
                    | Error e -> failtest $"{e}"

                    Expect.isTrue segment.Finished "every call replayed"
                    Expect.isNone segment.Divergence "no divergence"
            }

            testTask
                "an append the recording made is written again before its entry is done, whatever its caller's cancellation says then" {
                // What was recorded after an append may happen in replay as soon as its entry is done:
                // a turn's cancellation, which ends the host call that appends (the step journal's
                // hostCallStarted of a cell's command, when the host stopped mid-cell). The caller here
                // is cancelled the moment replay writes, as that one was on a slow runner.
                let stores = Stores()
                do! recordAppendAndRead stores
                let written = InMemoryEventLog<Payload>()
                let caller = new CancellationTokenSource()
                let mutable doneWhenWritten = None

                let! (segment: ReplaySegment), (store: EventLog<Payload>) =
                    replayedStore stores written (fun segment (log: EventLog<Payload>) ->
                        { log with
                            Append =
                                fun stream expected meta events cancellation ->
                                    caller.Cancel()
                                    doneWhenWritten <- Some (entryOf segment Ops.LogAppend).Done
                                    log.Append stream expected meta events cancellation
                        })

                let! appended = store.Append steps 0L (stepMeta "k1") [ step 1 ] caller.Token
                Expect.equal appended (Ok 1L) "as recorded"
                Expect.equal (written.Events steps |> List.map _.Event) [ step 1 ] "the event is written again"
                Expect.equal doneWhenWritten (Some false) "written before the entry was done"
                let! page = store.Read steps 0L 100 CancellationToken.None
                Expect.equal (page |> List.map _.Event) [ step 1 ] "the read sees it"
                Expect.isTrue segment.Finished "every call replayed"
                Expect.isNone segment.Divergence "no divergence"
            }

            testTask "a read shows what the recorded one saw, also when its caller is cancelled while it waits for it" {
                // Replay may read before it has written what the recorded read saw: the read waits, and
                // its entry is done only once it has read it. A cancellation meanwhile came after the
                // recorded read completed, so it changes nothing.
                let stores = Stores()
                do! recordAppendAndRead stores
                let written = InMemoryEventLog<Payload>()

                let! (segment: ReplaySegment), (store: EventLog<Payload>) =
                    replayedStore stores written (fun _ log -> log)

                let caller = new CancellationTokenSource()
                let reading = store.Read steps 0L 100 caller.Token
                caller.Cancel()
                Expect.isFalse reading.IsCompleted "it waits for replay's append"
                Expect.isFalse (entryOf segment Ops.LogRead).Done "its entry waits too"

                let! appended =
                    store.Append steps 0L (stepMeta "k1") [ step 1 ] CancellationToken.None

                Expect.equal appended (Ok 1L) "as recorded"
                let! page = reading.WaitAsync(TimeSpan.FromSeconds 30.0)
                Expect.equal (page |> List.map _.Event) [ step 1 ] "what the recorded read saw"
                Expect.isTrue segment.Finished "every call replayed"
                Expect.isNone segment.Divergence "no divergence"
            }

            testTask "a timeout decides as it did" {
                let program: Eff<Env, int option * int option> =
                    eff {
                        let! late =
                            Eff.timeout
                                (TimeSpan.FromMilliseconds 20.0)
                                (eff {
                                    do! Clock.sleep (TimeSpan.FromSeconds 5.0)
                                    return 1
                                })

                        let! early =
                            Eff.timeout
                                (TimeSpan.FromSeconds 5.0)
                                (eff {
                                    do! Clock.sleep (TimeSpan.FromMilliseconds 5.0)
                                    return 2
                                })

                        return late, early
                    }

                let stores = Stores()
                let fs = InMemoryFs(SystemClock.clock, false)
                let! recorded = record stores fs program
                Expect.equal recorded (None, Some 2) "recorded"
                let! (replayed: Result<_, exn>), (segment: ReplaySegment) = replayed stores program

                match replayed with
                | Ok values -> Expect.equal values recorded "the same outcome"
                | Error e -> failtest $"{e}"

                Expect.isNone segment.Divergence "no divergence"
            }

            testTask "large values are kept in the blob store, once" {
                let stores = Stores()
                let fs = InMemoryFs(SystemClock.clock, false)

                let path =
                    if OperatingSystem.IsWindows() then
                        @"C:\big.txt"
                    else
                        "/big.txt"

                fs.AddText(path, String('x', 10_000))

                let program: Eff<Env, int> =
                    eff {
                        let! first = Fs.readText path
                        let! second = Fs.readText path

                        match first, second with
                        | Ok a, Ok b -> return a.Length + b.Length
                        | _ -> return 0
                    }

                let! recorded = record stores fs program
                Expect.equal recorded 20_000 "read twice"
                Expect.equal stores.Blobs.Count 1 "one blob for two equal values"

                let events =
                    stores.Log.Events(JournalLog.stream "s1")
                    |> List.sumBy (fun e -> (Json.serialize e.Event.Data).Length)

                Expect.isLessThan events 4_000 "the journal holds references"
                let! (replayed: Result<_, exn>), (_: ReplaySegment) = replayed stores program
                Expect.equal replayed (Ok recorded) "replayed from the blob"
            }

            testTask "a replay that asks something else diverges, naming the call" {
                let stores = Stores()
                let fs = InMemoryFs(SystemClock.clock, false)

                let a =
                    if OperatingSystem.IsWindows() then
                        @"C:\a.txt"
                    else
                        "/a.txt"

                let b =
                    if OperatingSystem.IsWindows() then
                        @"C:\b.txt"
                    else
                        "/b.txt"

                fs.AddText(a, "a")
                fs.AddText(b, "b")
                let! _ = record stores fs (Fs.readText a)

                let! (replayed: Result<_, exn>), (segment: ReplaySegment) =
                    replayed stores (Fs.readText b)

                match replayed with
                | Error(:? ReplayDivergedException) -> ()
                | other -> failtest $"{other}"

                match segment.Divergence with
                | Some divergence ->
                    Expect.equal divergence.Lane "env" "the lane"
                    Expect.stringContains divergence.Expected "Fs.ReadFile" "what was recorded"
                    Expect.stringContains divergence.Expected a "the recorded path"
                    Expect.stringContains divergence.Actual b "what replay asked"
                | None -> failtest "no divergence"
            }

            test "a kernel launch's journaled description reads back as it was" {
                let info: LaunchInfo =
                    {
                        Provider = "bun-ts"
                        Installed =
                            [
                                {
                                    Id = "bun"
                                    Version = Fx.Sdk.SemVer.create 1 4 5
                                    Dir = "/data/runtimes/bun/1.4.5"
                                    Bin = [ "bun", "/data/runtimes/bun/1.4.5/bin/bun" ]
                                    Env = [ "BUN_INSTALL", "/data/runtimes/bun/1.4.5" ]
                                }
                            ]
                        AssetsRoot = "/env/assets"
                        Target =
                            {
                                Os = Fx.Sdk.Os.Linux
                                Arch = Fx.Sdk.Arch.X64
                                Libc = Some Fx.Sdk.Libc.Glibc
                            }
                        Owned = Fx.Sdk.OwnedDirs.under "/env/owned/kernel-1"
                        Workspace = Some "/work"
                        ProjectTrusted = false
                        HostEnvironment = [ "HOME"; "PATH" ]
                    }

                let json = LaunchInfo.codec.Encode info
                Expect.equal (LaunchInfo.codec.Decode json) (Ok info) "the same launch"
            }

            testTask "terminals are refused in recorded hands" {
                let terminal =
                    Record.terminal
                        {
                            Spawn = fun _ _ -> failwith "spawned"
                        }

                let! spawned =
                    terminal.Spawn
                        {
                            Program = Exec("sh", [])
                            Cwd = None
                            Env = Inherit []
                            Size = { Cols = 80; Rows = 24 }
                        }
                        CancellationToken.None

                match spawned with
                | Error(Fx.Pty.NotStarted(_, reason)) -> Expect.stringContains reason "not recorded" "why"
                | other -> failtest $"{other}"
            }
        ]
