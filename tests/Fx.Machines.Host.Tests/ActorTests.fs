module Fx.Machines.Host.Tests.ActorTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Machines.Host.Tests.Chat
open Fx.Machines.Host.Tests.Support
open Fx.Machines.Host.Tests.TestEnv

let private start (fakes: Fakes) (spec: ActorSpec<Env, Command, Event, State, Intent>) =
    runIn fakes.Env (Actor.start spec)

let private send (fakes: Fakes) actor (envelope: Envelope<Command>) =
    Eff.run fakes.Env (Actor.send actor envelope)

let private interrupt (fakes: Fakes) actor (envelope: Envelope<Command>) =
    Eff.run fakes.Env (Actor.interrupt actor envelope)

let private accepted (after: int64) (count: int) =
    Accepted { After = after; Count = count }

/// Adds numbers per client; used where the order between clients is up to the scheduler.
module Tally =
    type Command =
        | Add of client: int * value: int

        override this.ToString() =
            match this with
            | Add(client, value) -> $"Add {client} {value}"

    type Event =
        | Added of client: int * value: int

        override this.ToString() =
            match this with
            | Added(client, value) -> $"Added {client} {value}"

    let machine: Machine<Command, Event, Map<int, int list>, unit> =
        {
            Initial = Map.empty
            Evolve =
                fun state (Added(client, value)) ->
                    state
                    |> Map.change client (fun values -> Some(value :: Option.defaultValue [] values))
            Handle = fun _ (Add(client, value)) -> Ok [ Added(client, value); Added(client, -value) ]
            React = fun _ _ -> []
        }

[<Tests>]
let tests =
    testList
        "Actor"
        [
            test "a conversation: inputs are handled in order, attributed, deduplicated, and intents answer" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()
                let actor = start fakes (spec log.Log)
                let o1 = send fakes actor (envelope "a1" alice (Prompt "hello"))
                let o2 = send fakes actor (envelope "b1" bob (Prompt "hi"))
                let o3 = send fakes actor (envelope "a1" alice (Prompt "hello"))
                let o4 = send fakes actor (envelope "b2" bob (Prompt ""))
                fakes.Clock.Advance(seconds 5.0)
                fakes.Clock.Advance(seconds 2.0)
                let o5 = send fakes actor (envelope "a2" alice Close)
                let o6 = send fakes actor (envelope "b3" bob (Prompt "late"))

                Expect.equal
                    (lines log stream)
                    [
                        "chat/1#1 Prompted 1 hello (2026-01-01T00:00:00.000000Z a1 by user:alice via tui)"
                        "chat/1#2 Queued hi (2026-01-01T00:00:00.000000Z b1 by user:bob via web)"
                        "chat/1#3 Answered 1 re:hello (2026-01-01T00:00:05.000000Z chat/1#1/0.0 by system via interpreter after chat/1#1)"
                        "chat/1#4 Prompted 2 hi (queued) (2026-01-01T00:00:05.000000Z chat/1#1/0.0 by system via interpreter after chat/1#1)"
                        "chat/1#5 Answered 2 re:hi (2026-01-01T00:00:07.000000Z chat/1#4/0.0 by system via interpreter after chat/1#4)"
                        "chat/1#6 Closed (2026-01-01T00:00:07.000000Z a2 by user:alice via tui)"
                    ]
                    "log"

                Expect.equal
                    ([ o1; o2; o3; o4; o5; o6 ] |> List.map (completed >> string))
                    [
                        "accepted, appended 1..1"
                        "accepted, appended 2..2"
                        "accepted, appended 1..1"
                        "rejected, chat.empty: Say something."
                        "accepted, appended 6..6"
                        "rejected, chat.closed: The chat is closed."
                    ]
                    "outcomes"

                let snapshot = Actor.snapshot actor

                Expect.equal
                    (string snapshot)
                    "Snapshot(6, Closed, 2 turns, queue [], replies [re:hello|re:hi])"
                    "state"

                checkLog machine log stream snapshot
                runIn fakes.Env (Actor.stop actor)
                Expect.isTrue actor.Completion.IsCompletedSuccessfully "stopped cleanly"
            }

            test "a duplicate key returns the first outcome and appends nothing, also after a restart" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()
                let first = start fakes (spec log.Log)
                let prompt = envelope "p" alice (Prompt "abc")
                let empty = envelope "e" alice (Prompt "")
                Expect.equal (completed (send fakes first prompt)) (accepted 0L 1) "accepted"
                Expect.equal (completed (send fakes first prompt)) (accepted 0L 1) "duplicate"

                let rejection = Rejected(Rejection.create "chat.empty" "Say something.")
                Expect.equal (completed (send fakes first empty)) rejection "rejected"
                Expect.equal (completed (send fakes first empty)) rejection "duplicate rejection"
                Expect.equal (log.Events stream).Length 1 "one event"
                runIn fakes.Env (Actor.stop first)

                let second = start fakes (spec log.Log)
                Expect.equal (completed (send fakes second prompt)) (accepted 0L 1) "the key survives the restart"
                Expect.equal (log.Events stream).Length 1 "still one event"
                // A rejection appends nothing, so a new actor handles the command again.
                Expect.equal (completed (send fakes second empty)) rejection "handled again"
                runIn fakes.Env (Actor.stop second)
            }

            test "inputs from concurrent senders are serialized: each command's events stay together" {
                let fakes = create ()
                let log = InMemoryEventLog<Tally.Event>()
                let stream = StreamId "tally/1"

                let actor =
                    runIn fakes.Env (Actor.start (ActorSpec.create stream Tally.machine log.Log (fun () -> Eff.ret [])))

                let client (id: int) =
                    Task.Run(fun () ->
                        for value in 1..100 do
                            let key = IdempotencyKey $"c{id}-{value}"
                            let by = Attribution.create $"user:{id}" "test"

                            match
                                runIn fakes.Env (Actor.send actor (Envelope.create key by (Tally.Add(id, value))))
                            with
                            | Accepted appended -> Expect.equal appended.Count 2 "two events"
                            | other -> failtest (string other))

                Task.WaitAll [| for id in 1..4 -> client id |]
                let events = log.Events stream
                Expect.equal events.Length 800 "every command"

                for pair in List.chunkBySize 2 events do
                    match pair with
                    | [ a; b ] ->
                        Expect.equal a.Meta.Key b.Meta.Key "a command's events are contiguous"

                        match a.Event, b.Event with
                        | Tally.Added(c1, v1), Tally.Added(c2, v2) ->
                            Expect.equal (c2, v2) (c1, -v1) "in order"
                            Expect.equal a.Meta.By.Principal $"user:{c1}" "attributed to the sender"
                    | _ -> failtest "odd"

                let state = Actor.snapshot actor

                for id in 1..4 do
                    Expect.equal
                        (state.State[id] |> List.rev |> List.filter (fun v -> v > 0))
                        [ 1..100 ]
                        "each sender's commands in the order it sent them"

                checkLog Tally.machine log stream state
                runIn fakes.Env (Actor.stop actor)
            }

            test "an interrupt cancels the work in flight at once" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()
                let actor = start fakes (spec log.Log)
                completed (send fakes actor (envelope "p1" alice (Prompt "hello"))) |> ignore
                Expect.equal fakes.Clock.PendingDelays 1 "the model is thinking"
                let stopped = interrupt fakes actor (envelope "i1" bob Interrupt)
                Expect.equal fakes.Clock.PendingDelays 0 "cancelled before the interrupt was handled"
                Expect.equal (completed stopped) (accepted 1L 1) "interrupted"
                fakes.Clock.Advance(seconds 60.0)

                Expect.equal
                    (log.Events stream |> List.map (fun e -> string e.Event))
                    [ "Prompted 1 hello"; "Interrupted 1" ]
                    "no answer"

                runIn fakes.Env (Actor.stop actor)
            }

            test "an interrupt goes before queued commands, and cancels work started meanwhile" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()
                let actor = start fakes (spec (slow fakes.Clock.Clock (seconds 1.0) log.Log))
                let p1 = send fakes actor (envelope "p1" alice (Prompt "one"))
                let p2 = send fakes actor (envelope "p2" alice (Prompt "two"))
                let p3 = send fakes actor (envelope "p3" bob (Prompt "three"))
                // p1 is being appended: the interrupt cancels nothing yet, but jumps the queue.
                let i1 = interrupt fakes actor (envelope "i1" bob Interrupt)
                advanceUntil fakes.Clock (Task.WhenAll(p1, p2, p3, i1))

                Expect.equal
                    (log.Events stream |> List.map (fun e -> $"{e.Meta.At} {e.Event}"))
                    [
                        "2026-01-01T00:00:00.000000Z Prompted 1 one"
                        "2026-01-01T00:00:01.000000Z Interrupted 1"
                        "2026-01-01T00:00:02.000000Z Prompted 2 two"
                        "2026-01-01T00:00:03.000000Z Queued three"
                    ]
                    "the interrupt was handled right after the command in progress"

                // The question of turn 1 started after the interrupt was sent; handling it cancelled it.
                Expect.equal fakes.Clock.PendingDelays 1 "only turn 2 is thinking"
                runIn fakes.Env (Actor.stop actor)
            }

            test "a retried interrupt does not cancel work that started after the first" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()
                let actor = start fakes (spec log.Log)
                completed (send fakes actor (envelope "p1" alice (Prompt "one"))) |> ignore
                let first = completed (interrupt fakes actor (envelope "i1" bob Interrupt))
                completed (send fakes actor (envelope "p2" alice (Prompt "two"))) |> ignore
                Expect.equal (completed (interrupt fakes actor (envelope "i1" bob Interrupt))) first "the first outcome"
                Expect.equal fakes.Clock.PendingDelays 1 "turn 2 still thinking"
                fakes.Clock.Advance(seconds 3.0)
                Expect.equal (Actor.snapshot actor).State.Replies [ "re:two" ] "answered"
                runIn fakes.Env (Actor.stop actor)
            }

            test "a snapshot plus the tail of the log reproduces the actor's state" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()
                let actor = start fakes (spec log.Log)
                completed (send fakes actor (envelope "p1" alice (Prompt "ab"))) |> ignore
                completed (send fakes actor (envelope "p2" bob (Prompt "cd"))) |> ignore
                let snapshot = Actor.snapshot actor
                Expect.equal snapshot.Seq 2L "at the head"
                let client = ref snapshot.State

                let following =
                    Eff.run
                        fakes.Env
                        (Actor.subscribe actor snapshot.Seq (fun recorded ->
                            eff {
                                client.Value <- machine.Evolve client.Value recorded.Event
                                return recorded.Event <> ChatClosed
                            }))

                fakes.Clock.Advance(seconds 10.0)
                completed (send fakes actor (envelope "c" alice Close)) |> ignore
                Expect.equal (completed following) 6L "followed to the end"
                Expect.equal client.Value (Actor.snapshot actor).State "same state"
                runIn fakes.Env (Actor.stop actor)
            }

            test "after a crash a new actor rebuilds from the log, and the retry is not handled twice" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()

                let crashing =
                    start fakes (spec (crashAfterAppending "p2" (slow fakes.Clock.Clock (seconds 1.0) log.Log)))

                let p1 = send fakes crashing (envelope "p1" alice (Prompt "one"))
                let p2 = send fakes crashing (envelope "p2" alice (Prompt "two"))
                let p3 = send fakes crashing (envelope "p3" bob (Prompt "three"))
                advanceUntil fakes.Clock crashing.Completion
                Expect.equal (completed p1) (accepted 0L 1) "before the crash"
                Expect.throwsT<Crashed> (fun () -> p2.GetAwaiter().GetResult() |> ignore) "outcome unknown"
                Expect.equal (completed p3) Stopped "queued, never handled"

                Expect.throwsT<Crashed>
                    (fun () -> crashing.Completion.GetAwaiter().GetResult())
                    "the actor stopped with the defect"

                Expect.equal (completed (send fakes crashing (envelope "p4" bob (Prompt "four")))) Stopped "stopped"
                Expect.equal (lines log stream).Length 2 "p2 was appended"

                let restarted = start fakes (spec log.Log)
                checkLog machine log stream (Actor.snapshot restarted)

                Expect.equal
                    (completed (send fakes restarted (envelope "p2" alice (Prompt "two"))))
                    (accepted 1L 1)
                    "deduplicated"

                Expect.equal
                    (completed (send fakes restarted (envelope "p3" bob (Prompt "three"))))
                    (accepted 2L 1)
                    "handled now"

                Expect.equal
                    (log.Events stream |> List.map (fun e -> string e.Event))
                    [ "Prompted 1 one"; "Queued two"; "Queued three" ]
                    "each once"

                runIn fakes.Env (Actor.stop restarted)
            }

            test "a second writer on the stream conflicts, and its actor stops" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()
                let first = start fakes (spec log.Log)
                let second = start fakes (spec log.Log)
                completed (send fakes first (envelope "p1" alice (Prompt "one"))) |> ignore
                let lost = send fakes second (envelope "p2" bob (Prompt "two"))
                Expect.throwsT<ConcurrencyException> (fun () -> lost.GetAwaiter().GetResult() |> ignore) "conflict"

                try
                    second.Completion.GetAwaiter().GetResult()
                    failtest "the second actor should have stopped"
                with :? ConcurrencyException as e ->
                    Expect.equal (string e.Error) "conflict: expected head 0, found 1" "why"

                Expect.equal (lines log stream).Length 1 "only the first writer's event"
                runIn fakes.Env (Actor.stop first)
            }

            test "a defect in Handle fails that command only" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()

                let fragile =
                    { machine with
                        Handle =
                            fun state command ->
                                match command with
                                | Prompt "boom" -> invalidOp "boom"
                                | _ -> machine.Handle state command
                    }

                let actor = start fakes { spec log.Log with Machine = fragile }
                let failed = send fakes actor (envelope "b" alice (Prompt "boom"))
                Expect.throwsT<InvalidOperationException> (fun () -> failed.GetAwaiter().GetResult() |> ignore) "defect"

                Expect.equal
                    (completed (send fakes actor (envelope "p" alice (Prompt "fine"))))
                    (accepted 0L 1)
                    "still running"

                runIn fakes.Env (Actor.stop actor)
                Expect.isTrue actor.Completion.IsCompletedSuccessfully "not a fault of the actor"
            }

            test "a defect in an interpreter stops the actor" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()

                let actor =
                    start
                        fakes
                        { spec log.Log with
                            Interpret = fun _ -> eff { return invalidOp "model exploded" }
                        }

                Expect.equal (completed (send fakes actor (envelope "p" alice (Prompt "x")))) (accepted 0L 1) "accepted"
                Expect.isTrue actor.Completion.IsFaulted "stopped"
                Expect.equal (completed (send fakes actor (envelope "q" alice (Prompt "y")))) Stopped "no more inputs"

                Expect.throwsT<InvalidOperationException>
                    (fun () -> runIn fakes.Env (Actor.wait actor))
                    "wait reports the defect"
            }

            test "stopping finishes the command in progress, stops queued ones and cancels work" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()
                let actor = start fakes (spec (slow fakes.Clock.Clock (seconds 1.0) log.Log))
                let p1 = send fakes actor (envelope "p1" alice (Prompt "one"))
                let p2 = send fakes actor (envelope "p2" alice (Prompt "two"))
                let stopping = Eff.run fakes.Env (Actor.stop actor)
                Expect.equal (completed p2) Stopped "queued"
                Expect.equal (completed (send fakes actor (envelope "p3" bob (Prompt "three")))) Stopped "later"
                Expect.isFalse stopping.IsCompleted "waits for the append in progress"
                fakes.Clock.Advance(seconds 1.0)
                Expect.equal (completed p1) (accepted 0L 1) "finished"
                Expect.isTrue stopping.IsCompletedSuccessfully "stopped"
                Expect.equal fakes.Clock.PendingDelays 0 "its question was cancelled"
                Expect.equal (lines log stream).Length 1 "nothing else"
            }

            test "Recover picks up work in flight when an actor starts on an existing log" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()

                let recovering =
                    { spec log.Log with
                        Recover =
                            fun state ->
                                match state.Status with
                                | Answering turn -> [ Ask(turn, "again") ]
                                | _ -> []
                    }

                let first = start fakes recovering
                completed (send fakes first (envelope "p1" alice (Prompt "lost"))) |> ignore
                runIn fakes.Env (Actor.stop first)
                Expect.equal fakes.Clock.PendingDelays 0 "the question died with the actor"
                let second = start fakes recovering
                Expect.equal fakes.Clock.PendingDelays 1 "asked again"
                fakes.Clock.Advance(seconds 5.0)

                Expect.equal
                    (lines log stream |> List.last)
                    "chat/1#2 Answered 1 re:again (2026-01-01T00:00:05.000000Z chat/1#1/recover/0.0 by system via interpreter)"
                    "answered, keyed by the head it recovered at"

                runIn fakes.Env (Actor.stop second)
            }

            test "intents run concurrently, or one at a time in order" {
                let works: Machine<int list, int list, unit, int * int> =
                    {
                        Initial = ()
                        Evolve = fun () _ -> ()
                        Handle = fun () durations -> Ok [ durations ]
                        React = fun () durations -> List.indexed durations
                    }

                let schedule order =
                    let fakes = create ()
                    let log = ResizeArray<string>()

                    let work (index, duration) : Eff<Env, int list list> =
                        eff {
                            let! start = Clock.monotonic
                            do! Clock.sleep (seconds (float duration))
                            let! finish = Clock.monotonic
                            log.Add $"{index}: {start.SinceOrigin.TotalSeconds}..{finish.SinceOrigin.TotalSeconds}"
                            return []
                        }

                    let spec =
                        { ActorSpec.create (StreamId "works/1") works (InMemoryEventLog().Log) work with
                            Intents = order
                        }

                    let actor = runIn fakes.Env (Actor.start spec)

                    completed (
                        Eff.run fakes.Env (Actor.send actor (Envelope.create (IdempotencyKey "w") alice [ 3; 1; 2 ]))
                    )
                    |> ignore

                    while fakes.Clock.AdvanceToNextDelay() do
                        ()

                    runIn fakes.Env (Actor.stop actor)
                    List.ofSeq log

                Expect.equal (schedule IntentOrder.Concurrent) [ "1: 0..1"; "2: 0..2"; "0: 0..3" ] "all at once"
                Expect.equal (schedule IntentOrder.Sequential) [ "0: 0..3"; "1: 3..4"; "2: 4..6" ] "one after another"
            }

            test "sequential intents keep their order when recovery intents finish at once" {
                let chain: Machine<int, int, unit, int> =
                    {
                        Initial = ()
                        Evolve = fun () _ -> ()
                        Handle = fun () step -> Ok [ step ]
                        React = fun () step -> [ step ]
                    }

                let fakes = create ()
                let ran = ResizeArray<int>()

                let step (n: int) : Eff<Env, int list> =
                    eff {
                        ran.Add n
                        return if n % 10 < 2 then [ n + 1 ] else []
                    }

                let spec =
                    { ActorSpec.create (StreamId "chain/1") chain (InMemoryEventLog().Log) step with
                        Intents = IntentOrder.Sequential
                        Recover = fun () -> [ 0; 10 ]
                    }

                let actor = runIn fakes.Env (Actor.start spec)
                Expect.equal (List.ofSeq ran) [ 0; 10; 1; 11; 2; 12 ] "recovery first, then reactions, in order"
                runIn fakes.Env (Actor.stop actor)
            }

            test "the actor outlives the scope that started it; run stops it with its body" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()
                use cancellation = new CancellationTokenSource()

                let scoped =
                    { fakes.Env with
                        Scope = Scope.ofCancellation cancellation.Token
                    }

                let actor = runIn scoped (Actor.start (spec log.Log))
                cancellation.Cancel()
                Expect.equal (completed (send fakes actor (envelope "p" alice (Prompt "x")))) (accepted 0L 1) "running"
                runIn fakes.Env (Actor.stop actor)

                let inside = ref None

                let outcome =
                    runIn
                        fakes.Env
                        (Actor.run (spec log.Log) (fun running ->
                            eff {
                                inside.Value <- Some running
                                return! Actor.send running (envelope "q" alice Interrupt)
                            }))

                Expect.equal outcome (accepted 1L 1) "handled"
                Expect.isTrue inside.Value.Value.Completion.IsCompletedSuccessfully "stopped after the body"
            }

            test "the trace shows the actor's span and what it did" {
                let fakes = create ()
                let log = InMemoryEventLog<Event>()
                let actor = start fakes (spec log.Log)
                completed (send fakes actor (envelope "p1" alice (Prompt "x"))) |> ignore
                completed (send fakes actor (envelope "p1" alice (Prompt "x"))) |> ignore
                completed (interrupt fakes actor (envelope "i" alice Interrupt)) |> ignore
                completed (send fakes actor (envelope "i2" alice Interrupt)) |> ignore
                runIn fakes.Env (Actor.stop actor)

                Expect.equal
                    (fakes.Trace.Records |> List.map string)
                    [
                        "2026-01-01T00:00:00.000000Z start 0000000000000001 parent=- actor stream=chat/1"
                        "2026-01-01T00:00:00.000000Z info span=0000000000000001 actor.accepted events=appended 1..1 key=p1 by=user:alice via tui"
                        "2026-01-01T00:00:00.000000Z debug span=0000000000000001 actor.duplicate key=p1 by=user:alice via tui"
                        "2026-01-01T00:00:00.000000Z debug span=0000000000000001 actor.intent-cancelled intent=Ask 1 x"
                        "2026-01-01T00:00:00.000000Z info span=0000000000000001 actor.accepted events=appended 2..2 key=i by=user:alice via tui"
                        "2026-01-01T00:00:00.000000Z info span=0000000000000001 actor.rejected rejection=chat.idle: Nothing to interrupt. key=i2 by=user:alice via tui"
                        "2026-01-01T00:00:00.000000Z end 0000000000000001 ok"
                    ]
                    "trace"
            }
        ]
