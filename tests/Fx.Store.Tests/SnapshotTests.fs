module Fx.Store.Tests.SnapshotTests

open System.Threading
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Store.Fakes
open Fx.Store.Tests.Conformance
open Fx.Store.Tests.Notes
open Fx.Store.Tests.TestEnv

let private meta (key: string) : EventMeta =
    {
        At = Instant.unixEpoch
        By = Attribution.system "test"
        Key = IdempotencyKey key
        Cause = None
    }

/// A typed log over an in-memory payload log, with `count` notes "n1", "n2", … appended one per commit.
let private logWith (count: int) =
    let store = InMemoryEventLog<Payload>()
    let log = EventLog.typed schema store.Log

    for i in 1..count do
        log.Append stream (int64 i - 1L) (meta $"k{i}") [ Added $"n{i}" ] CancellationToken.None
        |> _.GetAwaiter().GetResult()
        |> ignore

    log

let private notes (count: int) =
    {
        Items = [ for i in 1..count -> $"n{i}" ]
        Folded = count
    }

[<Tests>]
let tests =
    testList
        "Snapshots"
        [
            SnapshotsConformance.tests "in memory" (fun () -> InMemorySnapshots().Store)

            test "a snapshot loads only with the version that wrote it, and only if its codec reads it" {
                let env = create ()
                let store = InMemorySnapshots().Store

                let inspect schema =
                    runIn env (Snapshots.inspect store schema stream)

                Expect.equal (inspect snapshotSchema) SnapshotLoad.Missing "missing"

                runIn env (Snapshots.save store snapshotSchema stream { Seq = 3L; State = notes 3 })

                Expect.equal (inspect snapshotSchema) (SnapshotLoad.Found { Seq = 3L; State = notes 3 }) "found"

                let version2 = { snapshotSchema with Version = 2 }
                Expect.equal (inspect version2) (SnapshotLoad.OtherVersion 1) "another version is ignored"
                Expect.equal (runIn env (Snapshots.load store version2 stream)) None "so nothing loads"

                let wrongCodec =
                    { snapshotSchema with
                        Codec =
                            Codec.list Codec.string
                            |> Codec.map (fun items -> { Items = items; Folded = 0 }) _.Items
                    }

                match inspect wrongCodec with
                | SnapshotLoad.Unreadable reason -> Expect.stringContains reason "expected array" "why"
                | other -> failtest $"unexpected {other}"
            }

            test "a projection loads from its snapshot and folds only the tail" {
                let env = create ()
                let log = logWith 10
                let snapshots = InMemorySnapshots().Store

                let full = runIn env (Projection.load log snapshots projection stream)
                Expect.equal full { Seq = 10L; State = notes 10 } "from the start"

                // A snapshot that says it folded nothing: what the fold adds to it is the tail.
                runIn
                    env
                    (Snapshots.save
                        snapshots
                        snapshotSchema
                        stream
                        {
                            Seq = 7L
                            State = { notes 7 with Folded = 0 }
                        })

                let loaded = runIn env (Projection.load log snapshots projection stream)
                Expect.equal loaded.Seq 10L "at the head"
                Expect.equal loaded.State.Items (notes 10).Items "same notes"
                Expect.equal loaded.State.Folded 3 "folded the tail only"
            }

            test "a snapshot of an older version, or past the head, is ignored and the state rebuilt from the log" {
                let env = create ()
                let log = logWith 5
                let snapshots = InMemorySnapshots().Store
                let old = snapshotSchema

                let current =
                    { projection with
                        Snapshot = { snapshotSchema with Version = 2 }
                    }

                runIn
                    env
                    (Snapshots.save
                        snapshots
                        old
                        stream
                        {
                            Seq = 4L
                            State = { notes 4 with Folded = 0 }
                        })

                Expect.equal (runIn env (Projection.load log snapshots current stream)).State.Folded 5 "rebuilt"

                runIn env (Snapshots.save snapshots current.Snapshot stream { Seq = 9L; State = notes 9 })

                Expect.equal
                    (runIn env (Projection.load log snapshots current stream))
                    { Seq = 5L; State = notes 5 }
                    "ahead"
            }

            test "refresh saves a snapshot once enough events were folded past the last one" {
                let env = create ()
                let log = logWith 6
                let snapshots = InMemorySnapshots()

                let refreshed =
                    runIn env (Projection.refresh log snapshots.Store projection 4L stream)

                Expect.equal refreshed { Seq = 6L; State = notes 6 } "state"
                Expect.equal (runIn env (Snapshots.load snapshots.Store snapshotSchema stream)) (Some refreshed) "saved"

                log.Append stream 6L (meta "k7") [ Added "n7" ] CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore

                runIn env (Projection.refresh log snapshots.Store projection 4L stream)
                |> ignore

                Expect.equal
                    (runIn env (Snapshots.load snapshots.Store snapshotSchema stream)
                     |> Option.map _.Seq)
                    (Some 6L)
                    "one event is not enough"

                Expect.throwsT<System.ArgumentException>
                    (fun () -> Projection.refresh log snapshots.Store projection 0L stream |> ignore)
                    "every must be positive"
            }

            test "a subscriber far behind gets the snapshot, then the tail; one close by gets the events" {
                let env = create ()
                let log = logWith 20
                let snapshots = InMemorySnapshots().Store
                runIn env (Snapshots.save snapshots snapshotSchema stream { Seq = 15L; State = notes 15 })

                let feed (after: int64) (maxGap: int64) =
                    let seen = ResizeArray<string>()

                    let last =
                        runIn
                            env
                            (Projection.subscribe log snapshots projection maxGap stream after (fun item ->
                                eff {
                                    seen.Add(string item)

                                    return
                                        match item with
                                        | Feed.Next recorded -> recorded.Seq < 20L
                                        | Feed.Reset _ -> true
                                }))

                    last, List.ofSeq seen

                Expect.equal
                    (feed 2L 10L)
                    (20L,
                     [
                         "reset to 15"
                         "next notes/1#16"
                         "next notes/1#17"
                         "next notes/1#18"
                         "next notes/1#19"
                         "next notes/1#20"
                     ])
                    "snapshot + tail"

                Expect.equal
                    (feed 17L 10L)
                    (20L, [ "next notes/1#18"; "next notes/1#19"; "next notes/1#20" ])
                    "small gap"

                Expect.equal (fst (feed 16L 1L)) 20L "a snapshot behind the subscriber is not sent"
                Expect.equal (snd (feed 16L 1L)).Head "next notes/1#17" "so it gets events"
            }
        ]
