module Fx.Machines.Host.Tests.ProcessManagerTests

open System
open Expecto
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Machines.Host.Tests.Support
open Fx.Machines.Host.Tests.TestEnv

/// Runs finish steps in order.
module Runs =
    type Command =
        | Finish of step: int

        override this.ToString() =
            match this with
            | Finish step -> $"Finish {step}"

    type Event =
        | StepFinished of step: int

        override this.ToString() =
            match this with
            | StepFinished step -> $"StepFinished {step}"

    let machine: Machine<Command, Event, int, unit> =
        {
            Initial = 0
            Evolve = fun _ (StepFinished step) -> step
            Handle =
                fun last (Finish step) ->
                    if step = last + 1 then
                        Ok [ StepFinished step ]
                    else
                        Error(Rejection.create "run.order" $"Step {step} after {last}.")
            React = fun _ _ -> []
        }

/// A session continues after each step of its runs, up to a budget of three steps per run.
module Session =
    type Command =
        | Continue of run: string * step: int

        override this.ToString() =
            match this with
            | Continue(run, step) -> $"Continue {run} {step}"

    type Event =
        | Continued of run: string * step: int

        override this.ToString() =
            match this with
            | Continued(run, step) -> $"Continued {run} {step}"

    let machine: Machine<Command, Event, (string * int) list, unit> =
        {
            Initial = []
            Evolve = fun steps (Continued(run, step)) -> steps @ [ run, step ]
            Handle =
                fun _ (Continue(run, step)) ->
                    if step > 3 then
                        Error(Rejection.create "session.budget" $"No budget for step {step} of {run}.")
                    else
                        Ok [ Continued(run, step) ]
            React = fun _ _ -> []
        }

    let stream = StreamId "session/1"

/// Steps finished per run; every finished step continues the session.
let private manager: ProcessManager<string * int, Map<string, int>, string * int> =
    ProcessManager.create
        Map.empty
        (fun counts (run, _) -> counts |> Map.change run (fun n -> Some(1 + Option.defaultValue 0 n)))
        (fun _ input -> [ input ])

let private pmStream = StreamId "pm/continue"
let private runA = StreamId "run/a"
let private runB = StreamId "run/b"

let private by = Attribution.create "user:alice" "tui"

/// The pieces of a process-manager test: run actors, a session actor and the process manager.
type private World =
    {
        Fakes: Fakes
        RunLog: InMemoryEventLog<Runs.Event>
        Runs: ActorRegistry<Runs.Command, Runs.Event, int>
        SessionLog: InMemoryEventLog<Session.Event>
        Session: Actor<Session.Command, Session.Event, (string * int) list> ref
        PmLog: InMemoryEventLog<ProcessEvent<string * int>>
    }

    member world.StartSession() =
        world.Session.Value <-
            runIn
                world.Fakes.Env
                (Actor.start (
                    ActorSpec.create Session.stream Session.machine world.SessionLog.Log (fun () -> Eff.ret [])
                ))

    member world.Finish (run: StreamId) (step: int) =
        let envelope =
            Envelope.create (IdempotencyKey $"{run}-{step}") by (Runs.Finish step)

        completed (Eff.run world.Fakes.Env (ActorRegistry.send world.Runs run envelope))

    member world.Spec
        (deliver: Delivery<string * int> -> Eff<Env, Outcome>)
        : ProcessManagerSpec<Env, string * int, Map<string, int>, string * int> =
        let select (recorded: Recorded<Runs.Event>) =
            match recorded.Event with
            | Runs.StepFinished step -> Some(recorded.Stream.Value, step)

        {
            Stream = pmStream
            Log = world.PmLog.Log
            Manager = manager
            Sources =
                [
                    Source.ofLog world.RunLog.Log runA select
                    Source.ofLog world.RunLog.Log runB select
                ]
            Deliver = deliver
            RetryDelay = ProcessManager.defaultRetryDelay
        }

    /// Delivers to whatever session actor runs now.
    member world.Deliver(delivery: Delivery<string * int>) : Eff<Env, Outcome> =
        Actor.send world.Session.Value (Delivery.envelope pmStream delivery (Session.Continue delivery.Output))

    member world.Start(deliver: Delivery<string * int> -> Eff<Env, Outcome>) =
        runIn world.Fakes.Env (ProcessManager.start (world.Spec deliver))

let private world () : World =
    let fakes = create ()
    let runLog = InMemoryEventLog<Runs.Event>()

    let world =
        {
            Fakes = fakes
            RunLog = runLog
            Runs =
                runIn
                    fakes.Env
                    (ActorRegistry.create (fun stream ->
                        ActorSpec.create stream Runs.machine runLog.Log (fun () -> Eff.ret [])))
            SessionLog = InMemoryEventLog()
            Session = ref Unchecked.defaultof<_>
            PmLog = InMemoryEventLog()
        }

    world.StartSession()
    world

let private continued (world: World) =
    world.SessionLog.Events Session.stream |> List.map (fun e -> string e.Event)

[<Tests>]
let tests =
    testList
        "Process managers"
        [
            test "carry events from several streams to an actor, once each, in the order observed" {
                let world = world ()
                let pm = world.Start world.Deliver
                world.Finish runA 1 |> ignore
                world.Finish runB 1 |> ignore
                world.Finish runA 2 |> ignore

                Expect.equal
                    (lines world.SessionLog Session.stream)
                    [
                        "session/1#1 Continued run/a 1 (2026-01-01T00:00:00.000000Z pm/continue:run/a#1/0 by system via pm/continue after run/a#1)"
                        "session/1#2 Continued run/b 1 (2026-01-01T00:00:00.000000Z pm/continue:run/b#1/0 by system via pm/continue after run/b#1)"
                        "session/1#3 Continued run/a 2 (2026-01-01T00:00:00.000000Z pm/continue:run/a#2/0 by system via pm/continue after run/a#2)"
                    ]
                    "the session continued after each step"

                Expect.equal
                    (world.PmLog.Events pmStream |> List.map (fun e -> $"{e.Event} [{e.Meta.Key}]"))
                    [
                        "Observed run/a#1 (run/a, 1) [pm/continue:run/a#1]"
                        "Delivered pm/continue:run/a#1/0 [pm/continue#1/0.0]"
                        "Observed run/b#1 (run/b, 1) [pm/continue:run/b#1]"
                        "Delivered pm/continue:run/b#1/0 [pm/continue#3/0.0]"
                        "Observed run/a#2 (run/a, 2) [pm/continue:run/a#2]"
                        "Delivered pm/continue:run/a#2/0 [pm/continue#5/0.0]"
                    ]
                    "the process manager's own log"

                let state = Actor.snapshot pm.Actor

                Expect.equal
                    (string state.State)
                    "ProcessState(map [(run/a, 2); (run/b, 1)]; read run/a#2, run/b#1; 0 to deliver)"
                    "state"

                checkLog (ProcessManager.machine pmStream manager) world.PmLog pmStream state
                completed (pm.Stop())
                Expect.isTrue pm.Completion.IsCompletedSuccessfully "stopped cleanly"
            }

            test "a restarted process manager resumes after its checkpoints" {
                let world = world ()
                let first = world.Start world.Deliver
                world.Finish runA 1 |> ignore
                completed (first.Stop())
                world.Finish runA 2 |> ignore
                world.Finish runB 1 |> ignore
                Expect.equal (continued world) [ "Continued run/a 1" ] "nothing while stopped"
                let second = world.Start world.Deliver

                Expect.equal
                    (continued world)
                    [ "Continued run/a 1"; "Continued run/a 2"; "Continued run/b 1" ]
                    "caught up, without repeats"

                world.Finish runB 2 |> ignore
                Expect.equal (List.last (continued world)) "Continued run/b 2" "live again"
                completed (second.Stop())
            }

            test "a delivery to a stopped actor is retried until it lands" {
                let world = world ()
                let pm = world.Start world.Deliver
                completed (world.Session.Value.Stop())
                world.Finish runA 1 |> ignore
                Expect.isEmpty (continued world) "not delivered"
                world.Fakes.Clock.Advance(TimeSpan.FromMilliseconds 300.0)
                Expect.equal world.Fakes.Clock.PendingDelays 1 "retrying, after 100 ms, then 200 ms"
                world.StartSession()
                world.Fakes.Clock.Advance(TimeSpan.FromMilliseconds 400.0)
                Expect.equal (continued world) [ "Continued run/a 1" ] "delivered"
                Expect.isEmpty (Actor.snapshot pm.Actor).State.Outbox "acknowledged"
                completed (pm.Stop())
            }

            test "a delivery made but not acknowledged is made again after a restart, and the actor deduplicates it" {
                let world = world ()
                let crashes = ref 1

                let crashing (delivery: Delivery<string * int>) =
                    eff {
                        let! outcome = world.Deliver delivery

                        if crashes.Value > 0 then
                            crashes.Value <- crashes.Value - 1
                            return raise (Crashed "after delivering")
                        else
                            return outcome
                    }

                let first = world.Start crashing
                world.Finish runA 1 |> ignore
                Expect.isTrue first.Completion.IsFaulted "the process manager stopped"
                Expect.equal (continued world) [ "Continued run/a 1" ] "delivered once"
                Expect.equal (Actor.snapshot first.Actor).State.Outbox.Length 1 "but not acknowledged"
                let second = world.Start world.Deliver
                Expect.equal (continued world) [ "Continued run/a 1" ] "the redelivery was deduplicated"
                Expect.isEmpty (Actor.snapshot second.Actor).State.Outbox "acknowledged"

                Expect.equal
                    (Seq.last (world.PmLog.Events pmStream)).Meta.Key.Value
                    "pm/continue#1/recover/0.0"
                    "by the recovery"

                completed (second.Stop())
            }

            test "a restarted process manager delivers its whole outbox, in order" {
                let world = world ()
                completed (world.Session.Value.Stop())
                let first = world.Start world.Deliver
                world.Finish runA 1 |> ignore
                world.Finish runB 1 |> ignore
                world.Finish runA 2 |> ignore
                Expect.equal (Actor.snapshot first.Actor).State.Outbox.Length 3 "waiting for the session"
                completed (first.Stop())
                world.StartSession()
                let second = world.Start world.Deliver

                Expect.equal
                    (continued world)
                    [ "Continued run/a 1"; "Continued run/b 1"; "Continued run/a 2" ]
                    "in the order observed"

                Expect.isEmpty (Actor.snapshot second.Actor).State.Outbox "all acknowledged"
                completed (second.Stop())
            }

            test "a rejected delivery is acknowledged too" {
                let world = world ()
                let pm = world.Start world.Deliver

                for step in 1..4 do
                    world.Finish runA step |> ignore

                Expect.equal (continued world).Length 3 "the fourth step was rejected"
                Expect.isEmpty (Actor.snapshot pm.Actor).State.Outbox "and acknowledged"
                completed (pm.Stop())
            }

            test "the same inputs give the same logs on every run" {
                let scenario () =
                    let world = world ()
                    let pm = world.Start world.Deliver
                    world.Finish runA 1 |> ignore
                    world.Finish runB 1 |> ignore
                    completed (pm.Stop())
                    world.Finish runB 2 |> ignore
                    world.Fakes.Clock.Advance(TimeSpan.FromSeconds 1.0)
                    let again = world.Start world.Deliver
                    world.Finish runA 2 |> ignore
                    completed (again.Stop())

                    lines world.RunLog runA
                    @ lines world.RunLog runB
                    @ lines world.PmLog pmStream
                    @ lines world.SessionLog Session.stream
                    @ (world.Fakes.Trace.Records |> List.map string)

                Expect.equal (scenario ()) (scenario ()) "identical"
            }

            test "observing a source event twice records it once; unknown acknowledgements change nothing" {
                let machine = ProcessManager.machine pmStream manager
                let source = EventRef.create runA 1L

                let state, events =
                    Machine.run machine machine.Initial [ Observe(source, ("run/a", 1)); Observe(source, ("run/a", 1)) ]

                Expect.equal events [ Observed(source, ("run/a", 1)) ] "once"
                Expect.equal state.Outbox.Length 1 "one delivery"

                Expect.equal (machine.Handle state (Acknowledge(IdempotencyKey "unknown"))) (Ok []) "unknown key"

                let key = state.Outbox.Head.Key
                Expect.equal key.Value "pm/continue:run/a#1/0" "derived from the source event"
                let state, _ = Machine.run machine state [ Acknowledge key; Acknowledge key ]
                Expect.isEmpty state.Outbox "delivered"
            }
        ]
