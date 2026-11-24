namespace Fx.Machines.Host

open System
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Machines

/// What a process manager decides: it follows events of other streams (as inputs), folds them into a
/// state, and sends commands to actors in reaction, e.g. "a run's step finished, so the session
/// continues". Pure, like a machine: `React` gets the state after the input.
type ProcessManager<'In, 'S, 'Out> =
    {
        Initial: 'S
        Evolve: 'S -> 'In -> 'S
        React: 'S -> 'In -> 'Out list
    }

    override _.ToString() = "ProcessManager"

/// A command a process manager sends, with a key derived from the event that caused it, so that
/// delivering it again is harmless.
type Delivery<'Out> =
    {
        Key: IdempotencyKey
        Output: 'Out
        /// The source event the process manager reacted to.
        Cause: EventRef
    }

    override this.ToString() =
        string this.Output + " [" + this.Key.Value + "]"

/// Where a process manager's inputs come from: one stream, its events selected and mapped to inputs.
type Source<'R, 'In> =
    {
        Stream: StreamId
        /// The next inputs after a sequence number, waiting until there are some. `Last` is the last
        /// sequence number read: events that map to no input are skipped.
        Next: int64 -> Eff<'R, SourceBatch<'In>>
    }

    override this.ToString() = "Source(" + this.Stream.Value + ")"

and SourceBatch<'In> =
    {
        Last: int64
        Inputs: (EventRef * 'In) list
    }

    override this.ToString() =
        "SourceBatch("
        + string this.Last
        + ", "
        + string (List.length this.Inputs)
        + " inputs)"

/// A process manager's own events: what it observed, in the order it did, and what was delivered.
type ProcessEvent<'In> =
    | Observed of source: EventRef * input: 'In
    | Delivered of key: IdempotencyKey

    override this.ToString() =
        match this with
        | Observed(source, input) -> "Observed " + string source + " " + string input
        | Delivered key -> "Delivered " + key.Value

type ProcessCommand<'In> =
    | Observe of source: EventRef * input: 'In
    | Acknowledge of key: IdempotencyKey

    override this.ToString() =
        match this with
        | Observe(source, input) -> "Observe " + string source + " " + string input
        | Acknowledge key -> "Acknowledge " + key.Value

/// A process manager's state: the manager's own state, how far it has read each source, and the
/// deliveries not yet acknowledged (its outbox).
type ProcessState<'S, 'Out> =
    {
        State: 'S
        Checkpoints: Map<StreamId, int64>
        Outbox: Delivery<'Out> list
    }

    override this.ToString() =
        let checkpoints =
            this.Checkpoints
            |> Map.toList
            |> List.map (fun (stream, seq) -> stream.Value + "#" + string seq)
            |> String.concat ", "

        "ProcessState("
        + string this.State
        + "; read "
        + checkpoints
        + "; "
        + string (List.length this.Outbox)
        + " to deliver)"

/// How a process manager runs.
type ProcessManagerSpec<'R, 'In, 'S, 'Out> =
    {
        /// The process manager's own stream: it records its inputs there, so its state is a fold of
        /// one log even when it reads several streams.
        Stream: StreamId
        Log: EventLog<ProcessEvent<'In>>
        Manager: ProcessManager<'In, 'S, 'Out>
        Sources: Source<'R, 'In> list
        /// Sends a delivery to its actor, with the delivery's key (`Delivery.envelope`). `Accepted`
        /// and `Rejected` acknowledge it; `Stopped` is retried after `RetryDelay`.
        Deliver: Delivery<'Out> -> Eff<'R, Outcome>
        /// How long to wait before the nth retry of a delivery whose actor was stopped.
        RetryDelay: int -> TimeSpan
    }

    override this.ToString() =
        "ProcessManagerSpec(" + this.Stream.Value + ")"

/// A running process manager.
type RunningProcess<'In, 'S, 'Out> =
    {
        /// The actor of the process manager's own stream.
        Actor: Actor<ProcessCommand<'In>, ProcessEvent<'In>, ProcessState<'S, 'Out>>
        /// Stops reading the sources and stops the actor; waits until both have stopped.
        Stop: unit -> Task<unit>
        /// Completes when the process manager has stopped; faults with the defect that stopped it.
        Completion: Task<unit>
    }

    override this.ToString() =
        "RunningProcess(" + this.Actor.Stream.Value + ")"

module Source =
    /// Reads a stream of a log, selecting and mapping its events.
    let ofLog<'R, 'Evt, 'In when 'R :> IScope>
        (log: EventLog<'Evt>)
        (stream: StreamId)
        (select: Recorded<'Evt> -> 'In option)
        : Source<'R, 'In> =
        {
            Stream = stream
            Next =
                fun after ->
                    eff {
                        let! events = EventLog.next log stream after EventLog.PageSize

                        return
                            {
                                Last = (List.last events).Seq
                                Inputs =
                                    events
                                    |> List.choose (fun recorded ->
                                        select recorded |> Option.map (fun input -> recorded.Ref, input))
                            }
                    }
        }

module Delivery =
    /// The envelope that sends a delivery's command: its key, the process manager as sender, and the
    /// source event as cause.
    let envelope (from: StreamId) (delivery: Delivery<'Out>) (command: 'Cmd) : Envelope<'Cmd> =
        {
            Command = command
            Key = delivery.Key
            By = Attribution.system from.Value
            Cause = Some delivery.Cause
        }

module ProcessManager =
    let create (initial: 'S) (evolve: 'S -> 'In -> 'S) (react: 'S -> 'In -> 'Out list) : ProcessManager<'In, 'S, 'Out> =
        {
            Initial = initial
            Evolve = evolve
            React = react
        }

    /// Retry after 100 ms, doubling up to 30 s.
    let defaultRetryDelay (attempt: int) : TimeSpan =
        let doublings = Math.Min(Math.Max(attempt, 1) - 1, 10)
        TimeSpan.FromMilliseconds(Math.Min(100.0 * Math.Pow(2.0, float doublings), 30000.0))

    /// The key of the nth delivery caused by a source event: `<process stream>:<source event>/<n>`.
    let deliveryKey (self: StreamId) (source: EventRef) (index: int) : IdempotencyKey =
        IdempotencyKey(self.Value + ":" + string source + "/" + string index)

    /// The process manager as a machine on its own stream. `Observe` records an input once per source
    /// position (checkpoints make a source event observed twice a no-op), and its evolution computes
    /// the deliveries and puts them in the outbox; `React` hands them to the host to deliver.
    /// `Acknowledge` takes a delivery out of the outbox.
    let machine
        (self: StreamId)
        (manager: ProcessManager<'In, 'S, 'Out>)
        : Machine<ProcessCommand<'In>, ProcessEvent<'In>, ProcessState<'S, 'Out>, Delivery<'Out>> =
        let evolve (current: ProcessState<'S, 'Out>) (event: ProcessEvent<'In>) : ProcessState<'S, 'Out> =
            match event with
            | Observed(source, input) ->
                let state = manager.Evolve current.State input

                let deliveries =
                    manager.React state input
                    |> List.mapi (fun index output ->
                        {
                            Key = deliveryKey self source index
                            Output = output
                            Cause = source
                        }
                        : Delivery<'Out>)

                {
                    State = state
                    Checkpoints = Map.add source.Stream source.Seq current.Checkpoints
                    Outbox = current.Outbox @ deliveries
                }
            | Delivered key ->
                { current with
                    Outbox = current.Outbox |> List.filter (fun delivery -> delivery.Key <> key)
                }

        let handle (current: ProcessState<'S, 'Out>) (command: ProcessCommand<'In>) =
            match command with
            | Observe(source, input) ->
                let seen =
                    current.Checkpoints |> Map.tryFind source.Stream |> Option.defaultValue 0L

                if source.Seq <= seen then
                    Ok []
                else
                    Ok [ Observed(source, input) ]
            | Acknowledge key ->
                if current.Outbox |> List.exists (fun delivery -> delivery.Key = key) then
                    Ok [ Delivered key ]
                else
                    Ok []

        let react (current: ProcessState<'S, 'Out>) (event: ProcessEvent<'In>) =
            match event with
            | Observed(source, _) -> current.Outbox |> List.filter (fun delivery -> delivery.Cause = source)
            | Delivered _ -> []

        {
            Initial =
                {
                    State = manager.Initial
                    Checkpoints = Map.empty
                    Outbox = []
                }
            Evolve = evolve
            Handle = handle
            React = react
        }

    /// Reads a source from a position and sends each input to the process manager's actor. Ends when
    /// the actor has stopped.
    let private pump
        (actor: Actor<ProcessCommand<'In>, ProcessEvent<'In>, ProcessState<'S, 'Out>>)
        (source: Source<'R, 'In>)
        (from: int64)
        : Eff<'R, unit> =
        eff {
            let mutable after = from
            let mutable running = true

            while running do
                let! batch = source.Next after
                let mutable inputs = batch.Inputs

                while running && not inputs.IsEmpty do
                    let cause, input = List.head inputs
                    inputs <- List.tail inputs

                    let envelope: Envelope<ProcessCommand<'In>> =
                        {
                            Command = Observe(cause, input)
                            Key = IdempotencyKey(actor.Stream.Value + ":" + string cause)
                            By = Attribution.system actor.Stream.Value
                            Cause = Some cause
                        }

                    match! Actor.send actor envelope with
                    | Stopped -> running <- false
                    | Accepted _
                    | Rejected _ -> ()

                if running then
                    after <- batch.Last
        }

    /// Starts a process manager (see docs/modules/machines.md for its delivery guarantees):
    ///
    /// 1. its actor rebuilds its state from its own stream and redelivers the outbox;
    /// 2. one pump per source reads the source after its checkpoint and sends each input to the actor,
    ///    which records it once, in arrival order, and puts the commands it reacts with in the outbox;
    /// 3. deliveries run one at a time, in order, and each is acknowledged once its actor handled it.
    let start<'R, 'In, 'S, 'Out when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (spec: ProcessManagerSpec<'R, 'In, 'S, 'Out>)
        : Eff<'R, RunningProcess<'In, 'S, 'Out>> =
        let deliver (delivery: Delivery<'Out>) : Eff<'R, ProcessCommand<'In> list> =
            eff {
                let mutable attempt = 0
                let mutable delivered = false

                while not delivered do
                    match! spec.Deliver delivery with
                    | Stopped ->
                        attempt <- attempt + 1
                        do! Clock.sleep (spec.RetryDelay attempt)
                    | Accepted _
                    | Rejected _ -> delivered <- true

                return [ Acknowledge delivery.Key ]
            }

        let actorSpec =
            { ActorSpec.create spec.Stream (machine spec.Stream spec.Manager) spec.Log deliver with
                Recover = fun state -> state.Outbox
                Intents = IntentOrder.Sequential
            }

        eff {
            let! actor = Actor.start actorSpec
            let! env = Eff.ask
            let checkpoints = (Actor.snapshot actor).State.Checkpoints
            let pumping = new CancellationTokenSource()

            let pumpEnv =
                env.WithScope
                    { env.Scope with
                        Cancellation = pumping.Token
                    }

            let gate = obj ()
            let mutable failure: exn option = None

            let fail (e: exn) =
                lock gate (fun () ->
                    if failure.IsNone then
                        failure <- Some e)

            let stopAll () =
                pumping.Cancel()
                actor.Stop() |> ignore

            // When the actor stops, for whatever reason, the pumps stop too.
            let watchActor: Task =
                task {
                    try
                        do! actor.Completion
                    with e ->
                        fail e

                    pumping.Cancel()
                }

            let watchPump (source: Source<'R, 'In>) : Task =
                let from = checkpoints |> Map.tryFind source.Stream |> Option.defaultValue 0L

                task {
                    match! Eff.run pumpEnv (Eff.catch (pump actor source from)) with
                    | Error(:? OperationCanceledException) when pumping.IsCancellationRequested -> ()
                    | Error e ->
                        fail e
                        stopAll ()
                    | Ok() -> ()
                }

            let watchers = spec.Sources |> List.map watchPump

            let completion: Task<unit> =
                task {
                    for watcher in watchers do
                        do! watcher

                    do! watchActor

                    match lock gate (fun () -> failure) with
                    | Some e -> return Exn.rethrow e
                    | None -> ()
                }

            return
                {
                    Actor = actor
                    Stop =
                        fun () ->
                            stopAll ()

                            task {
                                try
                                    do! completion
                                with _ ->
                                    ()
                            }
                    Completion = completion
                }
        }

    /// Runs a process manager for the duration of an effect, and stops it afterwards.
    let run<'R, 'In, 'S, 'Out, 'T when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (spec: ProcessManagerSpec<'R, 'In, 'S, 'Out>)
        (body: RunningProcess<'In, 'S, 'Out> -> Eff<'R, 'T>)
        : Eff<'R, 'T> =
        Eff.bracket (start spec) (fun running -> fun _ -> running.Stop()) body
