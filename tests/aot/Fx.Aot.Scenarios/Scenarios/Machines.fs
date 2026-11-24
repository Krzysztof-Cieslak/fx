/// Fx.Machines and Fx.Machines.Host: a machine run as an actor on the fake clock (serialized and
/// attributed inputs, duplicates, rejections, intents, the control path, a crash and a rebuild from
/// the log, a subscription) and a process manager across two streams.
module Fx.Aot.Scenarios.Machines

open System
open System.Threading.Tasks
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Machines
open Fx.Machines.Host

type Env =
    {
        Clock: Clock
        Trace: Trace
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface ITrace with
        member env.Trace = env.Trace

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "Env"

/// A kettle boils water for some seconds; a new order waits in line, and Stop ends the boil.
type Command =
    | Boil of seconds: int
    | Stop
    | Done of boil: int

    override this.ToString() =
        match this with
        | Boil seconds -> $"Boil {seconds}"
        | Stop -> "Stop"
        | Done boil -> $"Done {boil}"

type Event =
    | Started of boil: int * seconds: int * fromLine: bool
    | Waiting of seconds: int
    | Boiled of boil: int
    | Stopped of boil: int

    override this.ToString() =
        match this with
        | Started(boil, seconds, fromLine) ->
            $"""Started {boil} {seconds}s{if fromLine then " from the line" else ""}"""
        | Waiting seconds -> $"Waiting {seconds}s"
        | Boiled boil -> $"Boiled {boil}"
        | Stopped boil -> $"Stopped {boil}"

type State =
    {
        Boiling: int option
        Boils: int
        Line: int list
    }

    override this.ToString() =
        let boiling =
            match this.Boiling with
            | Some boil -> string boil
            | None -> "-"

        let line = this.Line |> List.map string |> String.concat ","
        $"boiling {boiling}, {this.Boils} boils, line [{line}]"

type Intent =
    | Heat of boil: int * seconds: int

    override this.ToString() =
        match this with
        | Heat(boil, seconds) -> $"Heat {boil} {seconds}s"

let kettle: Machine<Command, Event, State, Intent> =
    {
        Initial = { Boiling = None; Boils = 0; Line = [] }
        Evolve =
            fun state event ->
                match event with
                | Started(boil, _, fromLine) ->
                    { state with
                        Boiling = Some boil
                        Boils = boil
                        Line = if fromLine then List.tail state.Line else state.Line
                    }
                | Waiting seconds ->
                    { state with
                        Line = state.Line @ [ seconds ]
                    }
                | Boiled _
                | Stopped _ -> { state with Boiling = None }
        Handle =
            fun state command ->
                match state.Boiling, command with
                | _, Boil seconds when seconds <= 0 -> Error(Rejection.create "kettle.empty" "Boil for a while.")
                | None, Boil seconds -> Ok [ Started(state.Boils + 1, seconds, false) ]
                | Some _, Boil seconds -> Ok [ Waiting seconds ]
                | Some boil, Stop -> Ok [ Stopped boil ]
                | None, Stop -> Error(Rejection.create "kettle.cold" "Nothing is boiling.")
                | Some current, Done boil when boil = current ->
                    Ok(
                        match state.Line with
                        | next :: _ -> [ Boiled boil; Started(boil + 1, next, true) ]
                        | [] -> [ Boiled boil ]
                    )
                | _, Done boil -> Error(Rejection.create "kettle.stale" $"Boil {boil} is over.")
        React =
            fun _ event ->
                match event with
                | Started(boil, seconds, _) -> [ Heat(boil, seconds) ]
                | _ -> []
    }

let private heat (Heat(boil, seconds)) : Eff<Env, Command list> =
    eff {
        do! Clock.sleep (TimeSpan.FromSeconds(float seconds))
        return [ Done boil ]
    }

let private run (env: 'R) (effect: Eff<'R, 'T>) : 'T =
    (Eff.run env effect).GetAwaiter().GetResult()

let private show (task: Task<Outcome>) : string =
    try
        string (task.GetAwaiter().GetResult())
    with e ->
        $"failed {e.GetType().Name}"

/// The process crashed after the store made the commit durable.
exception Crash

/// Runs on a thread-pool thread: FakeClock.Advance needs inline continuations, which the main thread
/// of a musl Native AOT binary never gets.
let actor =
    Scenario.task "machines-actor" (fun () ->
        Task.Run<string list>(
            Func<string list>(fun () ->
                let clock = FakeClock.Create()
                let trace = TraceCollector clock.Clock

                let env =
                    {
                        Clock = clock.Clock
                        Trace = trace.Trace
                        Scope = Scope.root
                    }

                let store = InMemoryEventLog<Event>()
                let stream = StreamId.create "kettle" "1"

                let slow: EventLog<Event> =
                    { store.Log with
                        Append =
                            fun s expected meta events cancellation ->
                                task {
                                    do! clock.Clock.Delay (TimeSpan.FromSeconds 1.0) cancellation
                                    let! result = store.Log.Append s expected meta events cancellation

                                    if meta.Key.Value = "crash" then
                                        return raise Crash
                                    else
                                        return result
                                }
                    }

                let ann = Attribution.create "user:ann" "tui"
                let ben = Attribution.create "user:ben" "web"

                let order key by command =
                    Envelope.create (IdempotencyKey key) by command

                let first = run env (Actor.start (ActorSpec.create stream kettle slow heat))
                let seen = ResizeArray<string>()

                let following =
                    Eff.run
                        env
                        (Actor.subscribe first 0L (fun recorded ->
                            eff {
                                seen.Add $"seen {recorded.Seq}"
                                return recorded.Seq < 8L
                            }))

                let outcomes =
                    [
                        "boil 3", Eff.run env (Actor.send first (order "a1" ann (Boil 3)))
                        "boil 2", Eff.run env (Actor.send first (order "b1" ben (Boil 2)))
                        "again", Eff.run env (Actor.send first (order "a1" ann (Boil 3)))
                        "empty", Eff.run env (Actor.send first (order "b2" ben (Boil 0)))
                        "stop", Eff.run env (Actor.interrupt first (order "b3" ben Stop))
                        "crash", Eff.run env (Actor.send first (order "crash" ann (Boil 4)))
                        "queued", Eff.run env (Actor.send first (order "a2" ann (Boil 1)))
                    ]

                while clock.AdvanceToNextDelay() do
                    ()

                let crashed = first.Completion.IsFaulted

                let recovering =
                    { ActorSpec.create stream kettle store.Log heat with
                        // The boil in progress died with the first actor: heat for another second.
                        Recover =
                            fun state ->
                                match state.Boiling with
                                | Some boil -> [ Heat(boil, 1) ]
                                | None -> []
                    }

                let second = run env (Actor.start recovering)
                let retried = Eff.run env (Actor.send second (order "crash" ann (Boil 4)))
                let more = Eff.run env (Actor.send second (order "a2" ann (Boil 1)))

                while clock.AdvanceToNextDelay() do
                    ()

                let snapshot = Actor.snapshot second
                run env (Actor.stop second)

                [
                    yield! store.Events stream |> List.map string
                    for label, outcome in outcomes -> $"{label}: {show outcome}"
                    yield $"crashed: {crashed}"
                    yield $"retried: {show retried}"
                    yield $"more: {show more}"
                    yield $"snapshot: {snapshot}"
                    yield
                        $"replayed: {Machine.replay kettle (store.Events stream |> List.map _.Event) = snapshot.State}"
                    yield
                        if following.IsCompleted then
                            $"followed to {following.Result}"
                        else
                            "still following"
                    yield! seen
                    yield! trace.Records |> List.map string
                ])
        ))

/// Steps finished in two streams continue a third, through a process manager.
let processManager =
    Scenario.task "machines-process-manager" (fun () ->
        Task.Run<string list>(
            Func<string list>(fun () ->
                let clock = FakeClock.Create()
                let trace = TraceCollector clock.Clock

                let env =
                    {
                        Clock = clock.Clock
                        Trace = trace.Trace
                        Scope = Scope.root
                    }

                let kettles = InMemoryEventLog<Event>()
                let mugs = InMemoryEventLog<string>()
                let pmLog = InMemoryEventLog<ProcessEvent<string>>()
                let mugStream = StreamId "mug/1"

                let mug: Machine<string, string, string list, unit> =
                    {
                        Initial = []
                        Evolve = fun poured cup -> poured @ [ cup ]
                        Handle = fun _ cup -> Ok [ "poured " + cup ]
                        React = fun _ _ -> []
                    }

                let mugActor =
                    run env (Actor.start (ActorSpec.create mugStream mug mugs.Log (fun () -> Eff.ret [])))

                let kettleActors =
                    run env (ActorRegistry.create (fun stream -> ActorSpec.create stream kettle kettles.Log heat))

                let select (recorded: Recorded<Event>) =
                    match recorded.Event with
                    | Boiled boil -> Some $"{recorded.Stream} boil {boil}"
                    | _ -> None

                let pmStream = StreamId "pm/pour"

                let spec: ProcessManagerSpec<Env, string, int, string> =
                    {
                        Stream = pmStream
                        Log = pmLog.Log
                        Manager =
                            ProcessManager.create 0 (fun count _ -> count + 1) (fun count boiled ->
                                [ $"{boiled} (#{count})" ])
                        Sources =
                            [
                                Source.ofLog kettles.Log (StreamId "kettle/a") select
                                Source.ofLog kettles.Log (StreamId "kettle/b") select
                            ]
                        Deliver =
                            fun delivery -> Actor.send mugActor (Delivery.envelope pmStream delivery delivery.Output)
                        RetryDelay = ProcessManager.defaultRetryDelay
                    }

                let pm = run env (ProcessManager.start spec)
                let by = Attribution.create "user:ann" "tui"

                let boil (kettle: string) (key: string) (seconds: int) =
                    run
                        env
                        (ActorRegistry.send
                            kettleActors
                            (StreamId("kettle/" + kettle))
                            (Envelope.create (IdempotencyKey key) by (Boil seconds)))
                    |> ignore

                boil "a" "a1" 2
                boil "b" "b1" 1
                boil "a" "a2" 1

                while clock.AdvanceToNextDelay() do
                    ()

                pm.Stop().GetAwaiter().GetResult()
                boil "b" "b2" 1

                while clock.AdvanceToNextDelay() do
                    ()

                let again = run env (ProcessManager.start spec)
                let state = Actor.snapshot again.Actor
                again.Stop().GetAwaiter().GetResult()
                run env (ActorRegistry.stop kettleActors)
                run env (Actor.stop mugActor)

                [
                    yield! mugs.Events mugStream |> List.map string
                    yield! pmLog.Events pmStream |> List.map string
                    yield $"state: {state}"
                    yield
                        $"completed: {pm.Completion.IsCompletedSuccessfully} {again.Completion.IsCompletedSuccessfully}"
                ])
        ))

let scenarios = [ actor; processManager ]
