namespace Fx.Machines.Host

open System
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Machines

/// How an actor runs the intents `React` returns.
[<RequireQualifiedAccess>]
type IntentOrder =
    /// Each intent starts as soon as `React` returns it, and they run concurrently.
    | Concurrent
    /// One at a time, in the order `React` returned them: each starts when the one before finishes.
    | Sequential

    override this.ToString() =
        match this with
        | Concurrent -> "concurrent"
        | Sequential -> "sequential"

/// What an actor runs: a machine on one stream of a log, and the interpreter of its intents.
type ActorSpec<'R, 'Cmd, 'Evt, 'S, 'Intent> =
    {
        /// The actor's stream. The actor is its single writer.
        Stream: StreamId
        Machine: Machine<'Cmd, 'Evt, 'S, 'Intent>
        Log: EventLog<'Evt>
        /// Runs an intent. The commands it returns go back to the actor, attributed to the system and
        /// caused by the intent's event. Expected failures are commands too; an exception is a defect
        /// and stops the actor. Cancelled when the actor is interrupted or stopped.
        Interpret: 'Intent -> Eff<'R, 'Cmd list>
        /// What to do when the actor starts on a log that already has events. `React` runs only when
        /// an event is appended, never when a log is replayed, so work that was in flight when the
        /// last actor stopped is lost unless the state records it and `Recover` picks it up (spec §5:
        /// nothing auto-resumes; a machine turns it into, e.g., an `OutcomeUnknown` command).
        Recover: 'S -> 'Intent list
        Intents: IntentOrder
    }

    override this.ToString() = "ActorSpec(" + this.Stream.Value + ")"

module ActorSpec =
    /// A spec that recovers nothing and runs intents concurrently.
    let create
        (stream: StreamId)
        (machine: Machine<'Cmd, 'Evt, 'S, 'Intent>)
        (log: EventLog<'Evt>)
        (interpret: 'Intent -> Eff<'R, 'Cmd list>)
        : ActorSpec<'R, 'Cmd, 'Evt, 'S, 'Intent> =
        {
            Stream = stream
            Machine = machine
            Log = log
            Interpret = interpret
            Recover = fun _ -> []
            Intents = IntentOrder.Concurrent
        }

/// A machine's state and the sequence number of the last event folded into it. A client that starts
/// from a snapshot and then follows the log after `Seq` sees every event exactly once (spec §5:
/// snapshot + tail).
type Snapshot<'S> =
    {
        Seq: int64
        State: 'S
    }

    override this.ToString() =
        "Snapshot(" + string this.Seq + ", " + string this.State + ")"

/// A running actor: the single writer of one stream (spec §3, §5). Inputs are serialized through
/// its mailbox in arrival order, and every event records who sent the command behind it. Use the
/// functions of the `Actor` module.
type Actor<'Cmd, 'Evt, 'S> =
    {
        Stream: StreamId
        Log: EventLog<'Evt>
        /// Queues a command and waits for its outcome. Cancelling the wait does not withdraw the command.
        Send: Envelope<'Cmd> -> CancellationToken -> Task<Outcome>
        /// The control path: cancels the actor's work in flight, puts the command before every queued
        /// one, and cancels the work started meanwhile when the actor handles it.
        Interrupt: Envelope<'Cmd> -> CancellationToken -> Task<Outcome>
        Snapshot: unit -> Snapshot<'S>
        /// Stops the actor and waits until it has stopped.
        Stop: unit -> Task<unit>
        /// Completes when the actor has stopped; faults with the defect that stopped it, if one did.
        Completion: Task<unit>
    }

    override this.ToString() = "Actor(" + this.Stream.Value + ")"

/// A command in the mailbox, with the sender's reply if anyone waits for it.
type internal Pending<'Cmd> =
    {
        Envelope: Envelope<'Cmd>
        Reply: TaskCompletionSource<Outcome> option
        /// Sent on the control path.
        Urgent: bool
    }

/// The state an actor starts from: the fold of its log.
type internal Rebuilt<'S> =
    {
        State: 'S
        Head: int64
        /// Every command in the log, by key: deduplication survives restarts.
        Keys: Map<IdempotencyKey, Outcome>
    }

module internal Rebuilt =
    let initial (state: 'S) : Rebuilt<'S> =
        {
            State = state
            Head = 0L
            Keys = Map.empty
        }

    /// Folds one event. Events of a commit share its key, so a key's events are contiguous.
    let add (evolve: 'S -> 'Evt -> 'S) (rebuilt: Rebuilt<'S>) (recorded: Recorded<'Evt>) : Rebuilt<'S> =
        if recorded.Seq <> rebuilt.Head + 1L then
            invalidOp $"{recorded.Stream}: expected event {rebuilt.Head + 1L}, read {recorded.Seq}."

        let key = recorded.Meta.Key

        let keys =
            match Map.tryFind key rebuilt.Keys with
            | None -> Map.add key (Accepted { After = recorded.Seq - 1L; Count = 1 }) rebuilt.Keys
            | Some(Accepted appended) when appended.Last = rebuilt.Head ->
                Map.add
                    key
                    (Accepted
                        { appended with
                            Count = appended.Count + 1
                        })
                    rebuilt.Keys
            | Some _ -> rebuilt.Keys

        {
            State = evolve rebuilt.State recorded.Event
            Head = recorded.Seq
            Keys = keys
        }

/// The actor itself. Its loop is the only code that changes the state, the head and the outcomes, and
/// it publishes them under the gate for the other threads.
[<Sealed>]
type internal ActorCore<'R, 'Cmd, 'Evt, 'S, 'Intent when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
    (spec: ActorSpec<'R, 'Cmd, 'Evt, 'S, 'Intent>, env: 'R, start: Rebuilt<'S>, recover: 'Intent list) =
    let stream = spec.Stream
    let machine = spec.Machine
    let trace = env.Trace

    let span =
        trace.StartSpan env.Scope.Span "actor" [ Attr.text "stream" stream.Value ]

    // The actor outlives the scope that started it: only Stop ends it. An append in progress is not
    // cancelled, so a stopping actor never leaves its outcome unknown.
    let loopEnv =
        env.WithScope
            {
                Cancellation = CancellationToken.None
                Span = Some span
            }

    let gate = obj ()
    let mailbox = Mailbox<Pending<'Cmd>>()
    let background = TaskGroup()
    let mutable state = start.State
    let mutable head = start.Head
    let mutable outcomes = start.Keys

    let mutable published: Snapshot<'S> =
        {
            Seq = start.Head
            State = start.State
        }
    // Cancelled by an interrupt (and replaced) and by stopping: the scope of the work in flight.
    let mutable work = new CancellationTokenSource()
    let mutable sequentialTail: Task = Task.CompletedTask
    let mutable fault: exn option = None

    let event (level: Level) (message: string) (attrs: Attr list) =
        trace.Event (Some span) level message attrs

    let describe (envelope: Envelope<'Cmd>) =
        [ Attr.text "key" envelope.Key.Value; Attr.text "by" (string envelope.By) ]

    let reply (pending: Pending<'Cmd>) (outcome: Outcome) =
        match pending.Reply with
        | Some waiting -> waiting.TrySetResult outcome |> ignore
        | None -> ()

    let replyFault (pending: Pending<'Cmd>) (e: exn) =
        match pending.Reply with
        | Some waiting -> waiting.TrySetException e |> ignore
        | None -> ()

    /// Closes the mailbox; whatever was still queued was not handled.
    let close () =
        for pending in mailbox.Close() do
            reply pending Stopped

    let reportFault (e: exn) =
        lock gate (fun () ->
            if fault.IsNone then
                fault <- Some e)

        close ()

    let cancelWork () =
        let cancelled =
            lock gate (fun () ->
                let current = work
                work <- new CancellationTokenSource()
                current)

        cancelled.Cancel()

    let post (envelope: Envelope<'Cmd>) (urgent: bool) (cancellation: CancellationToken) : Task<Outcome> =
        let waiting = TaskCompletionSource<Outcome>()

        if
            mailbox.Post(
                {
                    Envelope = envelope
                    Reply = Some waiting
                    Urgent = urgent
                },
                urgent
            )
        then
            if cancellation.CanBeCanceled then
                waiting.Task.WaitAsync cancellation
            else
                waiting.Task
        else
            Task.FromResult Stopped

    let postResult (envelope: Envelope<'Cmd>) =
        if
            not (
                mailbox.Post(
                    {
                        Envelope = envelope
                        Reply = None
                        Urgent = false
                    },
                    false
                )
            )
        then
            event Level.Debug "actor.result-dropped" (describe envelope)

    let interpret (token: CancellationToken) (key: int -> IdempotencyKey) (cause: EventRef option) (intent: 'Intent) =
        eff {
            try
                let! commands = spec.Interpret intent

                commands
                |> List.iteri (fun index command ->
                    postResult
                        {
                            Command = command
                            Key = key index
                            By = Attribution.system "interpreter"
                            Cause = cause
                        })
            with
            | :? OperationCanceledException when token.IsCancellationRequested ->
                event Level.Debug "actor.intent-cancelled" [ Attr.text "intent" (string intent) ]
            | e ->
                event
                    Level.Error
                    "actor.intent-failed"
                    [ Attr.text "intent" (string intent); Attr.text "error" (e.GetType().Name) ]

                reportFault e
        }

    /// Starts intents in the scope of the current work. `prefix` makes the keys of their result
    /// commands: `<prefix><intent index>.<command index>`, the same in every run.
    let startIntents (prefix: string) (cause: EventRef option) (intents: 'Intent list) =
        let token = lock gate (fun () -> work.Token)

        let workEnv =
            loopEnv.WithScope
                { loopEnv.Scope with
                    Cancellation = token
                }

        intents
        |> List.iteri (fun index intent ->
            let key command =
                IdempotencyKey(prefix + string index + "." + string command)

            let run () : Task =
                if token.IsCancellationRequested then
                    Task.CompletedTask
                else
                    Eff.run workEnv (interpret token key cause intent) :> Task

            match spec.Intents with
            | IntentOrder.Concurrent -> background.Track(run ())
            | IntentOrder.Sequential ->
                let previous = sequentialTail

                let next: Task =
                    task {
                        do! previous
                        do! run ()
                    }

                sequentialTail <- next
                background.Track next)

    let accept (pending: Pending<'Cmd>) (appended: Appended) (next: 'S) (reactions: (int64 * 'Intent list) list) =
        let outcome = Accepted appended
        state <- next
        head <- appended.Last

        lock gate (fun () ->
            outcomes <- Map.add pending.Envelope.Key outcome outcomes
            published <- { Seq = head; State = state })

        if appended.Count > 0 then
            event Level.Info "actor.accepted" (Attr.text "events" (string appended) :: describe pending.Envelope)

        for seq, intents in reactions do
            let cause = EventRef.create stream seq
            startIntents (string cause + "/") (Some cause) intents

        reply pending outcome

    let reject (pending: Pending<'Cmd>) (rejection: Rejection) =
        let outcome = Rejected rejection
        lock gate (fun () -> outcomes <- Map.add pending.Envelope.Key outcome outcomes)
        event Level.Info "actor.rejected" (Attr.text "rejection" (string rejection) :: describe pending.Envelope)
        reply pending outcome

    /// Evolves and reacts to appended events: the new state and each event's intents.
    let applyAppended (events: 'Evt list) =
        let next, reactions, _ =
            List.fold
                (fun (current, reactions, seq) event ->
                    let next = machine.Evolve current event
                    next, (seq, machine.React next event) :: reactions, seq + 1L)
                (state, [], head + 1L)
                events

        next, List.rev reactions

    /// Handles one command: command → Handle → append → Evolve → React → intents (spec §4.4).
    let handle (pending: Pending<'Cmd>) : Eff<'R, unit> =
        eff {
            let envelope = pending.Envelope
            let known = lock gate (fun () -> Map.tryFind envelope.Key outcomes)

            match known with
            | Some outcome ->
                event Level.Debug "actor.duplicate" (describe envelope)
                reply pending outcome
            | None ->
                // The command in progress when the interrupt was sent may have started work since.
                if pending.Urgent then
                    cancelWork ()

                let decision =
                    try
                        Ok(machine.Handle state envelope.Command)
                    with e ->
                        Error e

                match decision with
                | Error e ->
                    event Level.Error "actor.handle-failed" (Attr.text "error" (e.GetType().Name) :: describe envelope)

                    match pending.Reply with
                    | Some _ -> replyFault pending e
                    // A result command: no sender to tell, and the machine would wait for it forever.
                    | None -> reportFault e
                | Ok(Error rejection) -> reject pending rejection
                | Ok(Ok []) -> accept pending { After = head; Count = 0 } state []
                | Ok(Ok events) ->
                    let! now = Clock.now

                    let meta =
                        {
                            At = now
                            By = envelope.By
                            Key = envelope.Key
                            Cause = envelope.Cause
                        }

                    let! appended = Eff.catch (EventLog.append spec.Log stream head meta events)
                    let count = List.length events

                    // After a failed append the commit may or may not be in the log: stop, so that a new
                    // actor rebuilds from the log; the sender retries with the same key.
                    let applied =
                        match appended with
                        | Error e -> Error e
                        | Ok(Error conflict) -> Error(ConcurrencyException(stream, conflict) :> exn)
                        | Ok(Ok newHead) when newHead <> head + int64 count ->
                            Error(
                                InvalidOperationException
                                    $"{stream}: appended {count} events after {head}, the log says the head is {newHead}."
                            )
                        | Ok(Ok _) ->
                            try
                                Ok(applyAppended events)
                            with e ->
                                Error e

                    match applied with
                    | Ok(next, reactions) -> accept pending { After = head; Count = count } next reactions
                    | Error e ->
                        replyFault pending e
                        return Exn.rethrow e
        }

    let loop: Eff<'R, unit> =
        eff {
            let mutable running = true

            while running do
                let! next = Eff.ofTask mailbox.Take

                match next with
                | ValueSome pending -> do! handle pending
                | ValueNone -> running <- false
        }

    // Before the loop starts, so that results of recovery intents that finish at once are queued
    // rather than handled inside this call, while later recovery intents are still being started.
    do startIntents (string (EventRef.create stream head) + "/recover/") None recover

    let completion: Task<unit> =
        task {
            match! Eff.run loopEnv (Eff.catch loop) with
            | Error e -> reportFault e
            | Ok() -> ()

            close ()
            cancelWork ()
            do! background.WaitAll()
            let stoppedBy = lock gate (fun () -> fault)

            match stoppedBy with
            | Some e ->
                trace.EndSpan span (SpanStatus.Failed(e.GetType().Name, e.Message))
                return Exn.rethrow e
            | None -> trace.EndSpan span SpanStatus.Ok
        }

    member val Handle: Actor<'Cmd, 'Evt, 'S> =
        {
            Stream = stream
            Log = spec.Log
            Send = fun envelope cancellation -> post envelope false cancellation
            Interrupt =
                fun envelope cancellation ->
                    match lock gate (fun () -> Map.tryFind envelope.Key outcomes) with
                    // A retried interrupt must not cancel work that started after the first one.
                    | Some outcome -> Task.FromResult outcome
                    | None ->
                        cancelWork ()
                        post envelope true cancellation
            Snapshot = fun () -> lock gate (fun () -> published)
            Stop =
                fun () ->
                    close ()

                    task {
                        try
                            do! completion
                        with _ ->
                            ()
                    }
            Completion = completion
        }

/// Runs machines as actors (spec §4.4, §5). See docs/modules/machines.md.
module Actor =
    let private rebuild
        (machine: Machine<'Cmd, 'Evt, 'S, 'Intent>)
        (log: EventLog<'Evt>)
        (stream: StreamId)
        : Eff<'R, Rebuilt<'S>> =
        eff {
            let mutable rebuilt = Rebuilt.initial machine.Initial
            let mutable reading = true

            while reading do
                let! page = EventLog.read log stream rebuilt.Head EventLog.PageSize

                if page.IsEmpty then
                    reading <- false
                else
                    rebuilt <- List.fold (Rebuilt.add machine.Evolve) rebuilt page

            return rebuilt
        }

    /// Starts an actor: rebuilds the machine's state from the log, runs `Recover`, then handles
    /// commands until stopped. The actor outlives the caller's scope; stop it with `stop`, or use `run`.
    let start<'R, 'Cmd, 'Evt, 'S, 'Intent when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (spec: ActorSpec<'R, 'Cmd, 'Evt, 'S, 'Intent>)
        : Eff<'R, Actor<'Cmd, 'Evt, 'S>> =
        eff {
            let! rebuilt = rebuild spec.Machine spec.Log spec.Stream
            let recover = spec.Recover rebuilt.State
            let! env = Eff.ask
            return ActorCore(spec, env, rebuilt, recover).Handle
        }

    /// Sends a command and waits for its outcome. Sending a key again returns the first outcome.
    let send<'R, 'Cmd, 'Evt, 'S when 'R :> IScope>
        (actor: Actor<'Cmd, 'Evt, 'S>)
        (envelope: Envelope<'Cmd>)
        : Eff<'R, Outcome> =
        fun env -> actor.Send envelope env.Scope.Cancellation

    /// Sends a command on the control path (spec §5): it cancels the work in flight (intents, which
    /// may shield clean-up with `Eff.shield`) at once, and the actor handles it before every queued
    /// command. A key the actor has handled cancels nothing.
    let interrupt<'R, 'Cmd, 'Evt, 'S when 'R :> IScope>
        (actor: Actor<'Cmd, 'Evt, 'S>)
        (envelope: Envelope<'Cmd>)
        : Eff<'R, Outcome> =
        fun env -> actor.Interrupt envelope env.Scope.Cancellation

    /// The state now, and the last sequence number in it.
    let snapshot (actor: Actor<'Cmd, 'Evt, 'S>) : Snapshot<'S> = actor.Snapshot()

    /// Stops the actor: the command in progress finishes, queued and later commands are `Stopped`,
    /// work in flight is cancelled and awaited. Never fails; `wait` reports a defect.
    let stop<'R, 'Cmd, 'Evt, 'S> (actor: Actor<'Cmd, 'Evt, 'S>) : Eff<'R, unit> = fun _ -> actor.Stop()

    /// Waits until the actor has stopped; rethrows the defect that stopped it, if one did.
    let wait<'R, 'Cmd, 'Evt, 'S when 'R :> IScope> (actor: Actor<'Cmd, 'Evt, 'S>) : Eff<'R, unit> =
        fun env ->
            if env.Scope.Cancellation.CanBeCanceled then
                actor.Completion.WaitAsync env.Scope.Cancellation
            else
                actor.Completion

    /// Runs an actor for the duration of an effect, and stops it afterwards, also on failure or
    /// cancellation.
    let run<'R, 'Cmd, 'Evt, 'S, 'Intent, 'T when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (spec: ActorSpec<'R, 'Cmd, 'Evt, 'S, 'Intent>)
        (body: Actor<'Cmd, 'Evt, 'S> -> Eff<'R, 'T>)
        : Eff<'R, 'T> =
        Eff.bracket (start spec) stop body

    /// Follows the actor's stream after a sequence number: replays, then goes live (`EventLog.subscribe`).
    let subscribe<'R, 'Cmd, 'Evt, 'S when 'R :> IScope>
        (actor: Actor<'Cmd, 'Evt, 'S>)
        (after: int64)
        (handle: Recorded<'Evt> -> Eff<'R, bool>)
        : Eff<'R, int64> =
        EventLog.subscribe actor.Log actor.Stream after handle
