module Fx.Machines.Host.Tests.EventLogTests

open System.Threading
open Expecto
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Machines.Host.Tests.Conformance
open Fx.Machines.Host.Tests.TestEnv

let private stream = StreamId "log/a"

let private meta (key: string) : EventMeta =
    {
        At = Instant.unixEpoch
        By = Attribution.system "test"
        Key = IdempotencyKey key
        Cause = None
    }

let private append (log: InMemoryEventLog<string>) (events: string list) =
    let head = (log.Events stream).Length

    match
        log.Log.Append stream (int64 head) (meta $"k{head}") events CancellationToken.None
        |> _.GetAwaiter().GetResult()
    with
    | Ok _ -> ()
    | Error e -> failtest (string e)

[<Tests>]
let tests =
    testList
        "EventLog"
        [
            EventLogConformance.tests "in memory" (fun () -> InMemoryEventLog<string>().Log)

            test "records print their position, event and metadata" {
                let log = InMemoryEventLog<string>()
                append log [ "hello" ]

                Expect.equal
                    (string (log.Events stream).Head)
                    "log/a#1 hello (1970-01-01T00:00:00.000000Z k0 by system via test)"
                    "text"

                Expect.equal log.Streams [ stream ] "streams"
            }

            test "next returns what is there, or waits for the next commit" {
                let fakes = create ()
                let log = InMemoryEventLog<string>()
                append log [ "a"; "b" ]
                let ready = runIn fakes.Env (EventLog.next log.Log stream 0L 10)
                Expect.equal (ready |> List.map _.Event) [ "a"; "b" ] "at once"
                let waiting = Eff.run fakes.Env (EventLog.next log.Log stream 2L 10)
                Expect.isFalse waiting.IsCompleted "waits"
                Expect.equal log.Waiting 1 "one waiter"
                append log [ "c" ]
                Expect.isTrue waiting.IsCompleted "woken by the append, inline"
                Expect.equal (waiting.Result |> List.map _.Seq) [ 3L ] "the new event"
                Expect.equal log.Waiting 0 "no waiter left"
            }

            test "readAll reads a stream longer than a page" {
                let fakes = create ()
                let log = InMemoryEventLog<string>()

                for i in 0..1199 do
                    append log [ string i ]

                let all = runIn fakes.Env (EventLog.readAll log.Log stream 0L)
                Expect.equal all.Length 1200 "every event"
                Expect.equal (all |> List.map _.Seq) [ 1L .. 1200L ] "in order"
                Expect.equal (runIn fakes.Env (EventLog.readAll log.Log stream 1190L)).Length 10 "a tail"
            }

            test "subscribe replays from a position, then follows live appends, without gaps or repeats" {
                let fakes = create ()
                let log = InMemoryEventLog<string>()
                append log [ "a"; "b"; "c" ]
                let seen = ResizeArray<string>()

                let following =
                    Eff.run
                        fakes.Env
                        (EventLog.subscribe log.Log stream 1L (fun recorded ->
                            eff {
                                seen.Add $"{recorded.Seq}:{recorded.Event}"
                                return recorded.Event <> "stop"
                            }))

                Expect.equal (List.ofSeq seen) [ "2:b"; "3:c" ] "replayed after 1"
                append log [ "d"; "e" ]
                append log [ "f" ]
                Expect.equal (List.ofSeq seen) [ "2:b"; "3:c"; "4:d"; "5:e"; "6:f" ] "live"
                Expect.isFalse following.IsCompleted "still following"
                append log [ "stop"; "never" ]
                Expect.equal following.Result 7L "stopped at the event it was told to"
                Expect.equal (Seq.last seen) "7:stop" "last"
            }

            test "a subscription ends with its scope" {
                let fakes = create ()
                let log = InMemoryEventLog<string>()
                use cancellation = new CancellationTokenSource()

                let env =
                    { fakes.Env with
                        Scope = Scope.ofCancellation cancellation.Token
                    }

                let following =
                    Eff.run env (EventLog.subscribe log.Log stream 0L (fun _ -> eff { return true }))

                cancellation.Cancel()
                Expect.isTrue following.IsCanceled "cancelled"
                Expect.equal log.Waiting 0 "no waiter left"
            }
        ]
