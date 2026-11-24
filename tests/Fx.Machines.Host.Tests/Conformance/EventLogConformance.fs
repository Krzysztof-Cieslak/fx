/// The event-log conformance suite: what every `EventLog` interpreter must do. The in-memory log
/// passes it here; the durable store (T105) runs the same suite.
module Fx.Machines.Host.Tests.Conformance.EventLogConformance

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host

let private stream = StreamId "conformance/a"
let private other = StreamId "conformance/b"

let private meta (key: string) : EventMeta =
    {
        At = Instant.ofUnixMicroseconds 1_764_000_000_000_000L
        By = Attribution.create "user:alice" "test"
        Key = IdempotencyKey key
        Cause = Some(EventRef.create other 2L)
    }

let private wait (task: Task<'T>) = task.GetAwaiter().GetResult()

/// Tests that the logs made by `create` conform. `create` is called once per test and returns an
/// empty log.
let tests (name: string) (create: unit -> EventLog<string>) : Test =
    testList
        $"EventLog conformance ({name})"
        [
            testCase "an empty stream has head 0 and no events" (fun () ->
                let log = create ()
                Expect.equal (wait (log.Head stream CancellationToken.None)) 0L "head"
                Expect.isEmpty (wait (log.Read stream 0L 10 CancellationToken.None)) "events")

            testCase "a commit appends its events in order, numbered from the head, with its metadata" (fun () ->
                let log = create ()

                let appended =
                    wait (log.Append stream 0L (meta "k1") [ "a"; "b" ] CancellationToken.None)

                Expect.equal appended (Ok 2L) "new head"
                let next = wait (log.Append stream 2L (meta "k2") [ "c" ] CancellationToken.None)
                Expect.equal next (Ok 3L) "next head"
                let events = wait (log.Read stream 0L 10 CancellationToken.None)
                Expect.equal (events |> List.map (fun e -> e.Seq, e.Event)) [ 1L, "a"; 2L, "b"; 3L, "c" ] "events"
                Expect.allEqual (events |> List.map (fun e -> e.Stream)) stream "stream"
                Expect.equal (events |> List.map (fun e -> e.Meta.Key.Value)) [ "k1"; "k1"; "k2" ] "keys"
                Expect.equal events[0].Meta (meta "k1") "metadata round-trips"
                Expect.equal (wait (log.Head stream CancellationToken.None)) 3L "head")

            testCase "an append with the wrong expected head conflicts and appends nothing" (fun () ->
                let log = create ()
                wait (log.Append stream 0L (meta "k1") [ "a" ] CancellationToken.None) |> ignore

                Expect.equal
                    (wait (log.Append stream 0L (meta "k2") [ "b" ] CancellationToken.None))
                    (Error(Conflict(0L, 1L)))
                    "behind"

                Expect.equal
                    (wait (log.Append stream 5L (meta "k2") [ "b" ] CancellationToken.None))
                    (Error(Conflict(5L, 1L)))
                    "ahead"

                Expect.equal (wait (log.Head stream CancellationToken.None)) 1L "unchanged")

            testCase "a key the stream already has appends nothing and says where its commit went" (fun () ->
                let log = create ()

                wait (log.Append stream 0L (meta "k1") [ "a"; "b" ] CancellationToken.None)
                |> ignore

                let first = { After = 0L; Count = 2 }

                Expect.equal
                    (wait (log.Append stream 2L (meta "k1") [ "c" ] CancellationToken.None))
                    (Error(Duplicate(IdempotencyKey "k1", first)))
                    "at the head"

                Expect.equal
                    (wait (log.Append stream 0L (meta "k1") [ "c" ] CancellationToken.None))
                    (Error(Duplicate(IdempotencyKey "k1", first)))
                    "the key is checked before the head"

                Expect.equal (wait (log.Head stream CancellationToken.None)) 2L "unchanged"

                Expect.equal
                    (wait (log.Append stream 2L (meta "k2") [ "c" ] CancellationToken.None))
                    (Ok 3L)
                    "another key"

                Expect.equal
                    (wait (log.Append other 0L (meta "k1") [ "x" ] CancellationToken.None))
                    (Ok 1L)
                    "keys are per stream")

            testCase "an empty commit is a defect" (fun () ->
                let log = create ()

                Expect.throwsT<ArgumentException>
                    (fun () -> wait (log.Append stream 0L (meta "k") [] CancellationToken.None) |> ignore)
                    "empty")

            testCase "reads page through a stream" (fun () ->
                let log = create ()

                for i in 0..9 do
                    wait (log.Append stream (int64 i) (meta $"k{i}") [ string i ] CancellationToken.None)
                    |> ignore

                let page (after: int64) (max: int) =
                    wait (log.Read stream after max CancellationToken.None)
                    |> List.map (fun e -> e.Event)

                Expect.equal (page 0L 3) [ "0"; "1"; "2" ] "first page"
                Expect.equal (page 3L 3) [ "3"; "4"; "5" ] "second page"
                Expect.equal (page 8L 5) [ "8"; "9" ] "last page"
                Expect.isEmpty (page 10L 5) "at the head"
                Expect.isEmpty (page 20L 5) "past the head")

            testCase "streams are independent" (fun () ->
                let log = create ()
                wait (log.Append stream 0L (meta "k1") [ "a" ] CancellationToken.None) |> ignore

                Expect.equal
                    (wait (log.Append other 0L (meta "k1") [ "x"; "y" ] CancellationToken.None))
                    (Ok 2L)
                    "own head"

                Expect.equal (wait (log.Head stream CancellationToken.None)) 1L "first"

                Expect.equal
                    (wait (log.Read other 0L 10 CancellationToken.None) |> List.map (fun e -> e.Ref))
                    [ EventRef.create other 1L; EventRef.create other 2L ]
                    "second")

            testCase "waiting past a sequence number completes at once when the head is past it" (fun () ->
                let log = create ()

                wait (log.Append stream 0L (meta "k1") [ "a"; "b" ] CancellationToken.None)
                |> ignore

                let waiting = log.WaitPast stream 1L CancellationToken.None
                Expect.isTrue waiting.IsCompleted "at once"
                Expect.equal (wait waiting) 2L "head")

            testCase "waiting completes when a commit to the stream passes it, and not before" (fun () ->
                let log = create ()
                let waiting = log.WaitPast stream 1L CancellationToken.None

                wait (log.Append other 0L (meta "k1") [ "x"; "y" ] CancellationToken.None)
                |> ignore

                wait (log.Append stream 0L (meta "k1") [ "a" ] CancellationToken.None) |> ignore
                Expect.isFalse (waiting.Wait(TimeSpan.FromMilliseconds 50.0)) "not yet"

                wait (log.Append stream 1L (meta "k2") [ "b"; "c" ] CancellationToken.None)
                |> ignore

                Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 10.0)) "woken"
                Expect.equal waiting.Result 3L "head")

            testCase "cancelling a wait fails it with a cancellation" (fun () ->
                let log = create ()
                use cancellation = new CancellationTokenSource()
                let waiting = log.WaitPast stream 0L cancellation.Token
                cancellation.Cancel()

                let cancelled =
                    try
                        waiting.GetAwaiter().GetResult() |> ignore
                        false
                    with :? OperationCanceledException ->
                        true

                Expect.isTrue cancelled "cancelled"

                wait (log.Append stream 0L (meta "k1") [ "a" ] CancellationToken.None) |> ignore)

            testCase "of concurrent appends with the same expected head, exactly one wins" (fun () ->
                let log = create ()

                let outcomes =
                    Array.init 8 (fun i ->
                        Task.Run(fun () ->
                            wait (log.Append stream 0L (meta $"k{i}") [ string i ] CancellationToken.None)))
                    |> Task.WhenAll
                    |> wait

                Expect.equal (outcomes |> Array.filter Result.isOk |> Array.length) 1 "one winner"
                Expect.equal (outcomes |> Array.filter Result.isError |> Array.length) 7 "conflicts"
                Expect.equal (wait (log.Head stream CancellationToken.None)) 1L "one event")
        ]
