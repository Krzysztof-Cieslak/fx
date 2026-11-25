namespace Fx.Sessions.Host

open System
open System.Collections.Concurrent
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Sessions

/// How a session host runs its sessions. The host server (T129) reads these from variant slots.
type SessionSettings =
    {
        /// The request kinds sessions accept: the core's and those extensions contribute.
        Kinds: RequestKinds
        /// A session's snapshot is saved once this many events were appended after the last one.
        SnapshotEvery: int64
        /// A subscriber more than this many events behind gets the latest snapshot first.
        MaxGap: int64
        /// How often an override is resolved again when another client changed the profile meanwhile.
        ProfileAttempts: int
    }

    override this.ToString() =
        "SessionSettings(snapshot every "
        + string this.SnapshotEvery
        + ", max gap "
        + string this.MaxGap
        + ")"

module SessionSettings =
    let defaults: SessionSettings =
        {
            Kinds = RequestKinds.builtIn
            SnapshotEvery = 256L
            MaxGap = 1024L
            ProfileAttempts = 3
        }

/// Runs sessions (spec §3, §5): one actor per session stream, the single writer of its log, started
/// from the latest snapshot. It interprets the session's intents: turns go to the turn runner, request
/// timers sleep on the clock, forks start their child sessions. Use the functions of `SessionHost`.
[<Sealed>]
type SessionHost<'R when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
    internal
    (env: 'R, log: EventLog<Event>, snapshots: SnapshotStore, runner: TurnRunner<'R>, settings: SessionSettings) =
    let machine = Session.machine settings.Kinds
    // The sequence number of each session's latest snapshot that this host knows of.
    let saved = ConcurrentDictionary<StreamId, int64>()

    let mutable registry: ActorRegistry<SessionCommand, Event, SessionState> option =
        None

    let actors () =
        match registry with
        | Some registry -> registry
        | None -> invalidOp "The session host is not ready."

    /// The sender of the commands of interpreters, as the actor attributes them.
    let interpreter = Attribution.system "interpreter"
    /// The sender of the commands a turn runner sends during its turn.
    let turnRunner = Attribution.system "turn"
    /// The sender of a fork's start.
    let forker = Attribution.system "fork"

    let stamp (by: Attribution) (command: Command) : Eff<'R, SessionCommand> =
        Clock.now
        |> Eff.map (fun now ->
            {
                Command = command
                By = by
                At = now.UnixMicroseconds
            })

    let result (command: Command) : Eff<'R, SessionCommand list> =
        stamp interpreter command |> Eff.map List.singleton

    /// Saves the actor's snapshot if enough events were appended since the last one.
    let checkpoint (actor: Actor<SessionCommand, Event, SessionState>) : Eff<'R, unit> =
        eff {
            let current = Actor.snapshot actor
            let last = saved.GetOrAdd(actor.Stream, 0L)

            if current.Seq - last >= settings.SnapshotEvery then
                do! Snapshots.save snapshots SessionLog.snapshot actor.Stream current
                saved[actor.Stream] <- current.Seq
        }

    let sendTo
        (urgent: bool)
        (stream: StreamId)
        (key: IdempotencyKey)
        (by: Attribution)
        (command: Command)
        (cause: EventRef option)
        : Eff<'R, Outcome> =
        eff {
            let! sent = stamp by command

            let envelope =
                {
                    Command = sent
                    Key = key
                    By = by
                    Cause = cause
                }

            match! ActorRegistry.get (actors ()) stream with
            | None -> return Stopped
            | Some actor ->
                let! outcome =
                    if urgent then
                        Actor.interrupt actor envelope
                    else
                        Actor.send actor envelope

                do! checkpoint actor
                return outcome
        }

    let stopped =
        Rejection.create "session.stopped" "The session's actor stopped; send the command again."

    /// The session's side of a turn, for its runner.
    let context (turn: TurnStart) : TurnContext<'R> =
        let stream = Session.stream turn.Session
        let started = EventRef.create stream turn.Seq

        let send (what: string) (command: Command) =
            sendTo false stream (IdempotencyKey(string started + "/" + what)) turnRunner command (Some started)

        {
            Turn = turn
            Boundary =
                fun step ->
                    eff {
                        match! send ("step/" + string step) (Command.TakeInputs(turn.Turn, step)) with
                        | Accepted appended when appended.Count > 0 ->
                            let! events = EventLog.read log stream appended.After appended.Count

                            let ids =
                                events
                                |> List.collect (fun recorded ->
                                    match recorded.Event with
                                    | Event.InputsDelivered(_, _, ids) -> ids
                                    | _ -> [])

                            let! inputs = Eff.traverse (SessionLog.input log stream) ids
                            return List.choose (fun input -> input) inputs
                        | _ -> return []
                    }
            Open =
                fun request ->
                    eff {
                        let request =
                            { request with
                                Origin = RequestOrigin.Turn turn.Turn
                            }

                        match! send ("request/" + request.Id) (Command.OpenRequest request) with
                        | Accepted appended -> return Ok appended.First
                        | Rejected rejection -> return Error rejection
                        | Stopped -> return Error stopped
                    }
            Await = fun request after -> SessionLog.resolution log stream after request
        }

    let runTurn (turn: TurnStart) : Eff<'R, SessionCommand list> =
        eff {
            let! cancellation = Eff.cancellation
            let! outcome = Eff.catch (runner.Run(context turn))

            match outcome with
            | Ok(Ok finished) -> return! result (Command.EndTurn(turn.Turn, finished))
            | Ok(Error reason) -> return! result (Command.FailTurn(turn.Turn, reason))
            | Error(:? OperationCanceledException as e) when cancellation.IsCancellationRequested ->
                // Interrupted or stopped: the actor knows, and nothing is reported.
                ExceptionDispatchInfo.Throw e
                return []
            | Error e -> return! result (Command.FailTurn(turn.Turn, "the turn runner failed: " + e.GetType().Name))
        }

    let expire (request: string) (at: int64) : Eff<'R, SessionCommand list> =
        eff {
            let! started = Clock.now
            let mutable now = started.UnixMicroseconds

            // The wall clock decides: a delay runs on the monotonic clock, which may drift from it.
            while now < at do
                do! Clock.sleep (TimeSpan.FromTicks((at - now) * 10L))
                let! later = Clock.now
                now <- later.UnixMicroseconds

            return
                [
                    {
                        Command = Command.ExpireRequest request
                        By = interpreter
                        At = now
                    }
                ]
        }

    let startFork (fork: ForkStart) : Eff<'R, SessionCommand list> =
        eff {
            let cause = EventRef.create fork.Parent.Stream fork.Seq
            let key = IdempotencyKey(string cause + "/fork")

            match! sendTo false (Session.stream fork.Start.Id) key forker (Command.Start fork.Start) (Some cause) with
            | Accepted _ -> return! result (Command.ChildStarted fork.Start.Id)
            | Rejected rejection -> return! result (Command.ChildFailed(fork.Start.Id, rejection.Message))
            // The child's actor stopped: the fork stays `Starting`, and the next start of this actor
            // (or an interrupt) tries again with the same key.
            | Stopped -> return []
        }

    let interpret (intent: Intent) : Eff<'R, SessionCommand list> =
        match intent with
        | Intent.RunTurn turn -> runTurn turn
        | Intent.Expire(request, at) -> expire request at
        | Intent.StartFork fork -> startFork fork
        | Intent.Abandon turn -> result (Command.AbandonTurn(turn, "the host stopped during the turn"))

    let start (stream: StreamId) : Eff<'R, Actor<SessionCommand, Event, SessionState>> =
        eff {
            let spec =
                { ActorSpec.create stream machine log interpret with
                    Recover = Session.recover
                }

            let! from = ActorSnapshots.latest snapshots SessionLog.snapshot spec
            let! actor = Actor.startFrom from spec
            saved[stream] <- from.Seq
            do! checkpoint actor
            return actor
        }

    do registry <- Some((Eff.run env (ActorRegistry.createWith start)).GetAwaiter().GetResult())

    member _.Log = log
    member _.Snapshots = snapshots
    member _.Settings = settings

    /// The actors of the sessions this host runs.
    member _.Registry = actors ()

    member internal _.Send(urgent, stream, key, by, command) =
        sendTo urgent stream key by command None

    override _.ToString() = "SessionHost"

/// Sessions as clients and the host server (T129) use them (spec §5, §17: `session/*`). Every command
/// carries an idempotency key and the attribution of its sender; commands from any number of clients
/// are handled one at a time by the session's actor, in arrival order.
module SessionHost =
    /// A host for the sessions in a log (typed with `SessionLog.typed`), with their snapshots, running
    /// turns with `runner`. Actors outlive the scopes of the calls that start them: stop the host.
    let create<'R when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (log: EventLog<Event>)
        (snapshots: SnapshotStore)
        (runner: TurnRunner<'R>)
        (settings: SessionSettings)
        : Eff<'R, SessionHost<'R>> =
        fun env -> Task.FromResult(SessionHost(env, log, snapshots, runner, settings))

    /// Sends a command to a session. `Interrupt` goes on the control path (`Actor.interrupt`): it
    /// cancels the running turn at once and goes before queued commands.
    let send<'R when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (host: SessionHost<'R>)
        (session: string)
        (key: IdempotencyKey)
        (by: Attribution)
        (command: Command)
        : Eff<'R, Outcome> =
        let urgent =
            match command with
            | Command.Interrupt -> true
            | _ -> false

        host.Send(urgent, Session.stream session, key, by, command)

    /// Interrupts a session's running turn (the control path).
    let interrupt<'R when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (host: SessionHost<'R>)
        (session: string)
        (key: IdempotencyKey)
        (by: Attribution)
        : Eff<'R, Outcome> =
        send host session key by Command.Interrupt

    /// Starts a session under a profile (`ProfileBinder.Bind []` gives the base one).
    let start<'R when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (host: SessionHost<'R>)
        (session: string)
        (key: IdempotencyKey)
        (by: Attribution)
        (profile: string)
        (title: string option)
        : Eff<'R, Outcome> =
        send
            host
            session
            key
            by
            (Command.Start
                {
                    Id = session
                    Profile = profile
                    Overrides = []
                    Parent = None
                    Title = title
                })

    /// A session's state and the sequence number of its last event (starting its actor if needed).
    let state<'R when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (host: SessionHost<'R>)
        (session: string)
        : Eff<'R, Snapshot<SessionState>> =
        eff {
            match! ActorRegistry.get host.Registry (Session.stream session) with
            | Some actor -> return Actor.snapshot actor
            | None -> return invalidOp "The session host is stopped."
        }

    /// The open requests of a session that a client with these capabilities can answer (spec §5:
    /// re-offered to capable clients); a client that attaches gets them.
    let offers<'R when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (host: SessionHost<'R>)
        (session: string)
        (capabilities: string list)
        : Eff<'R, PendingRequest list> =
        state host session
        |> Eff.map (fun snapshot -> Session.offers capabilities snapshot.State)

    /// Changes a session's variant overrides (a value sets a slot, `None` removes its override): the
    /// session's profile is resolved again and stored, and the session binds it (docs/modules/variants.md).
    /// Turns that start afterwards run under it. If another client changes the profile meanwhile, it is
    /// resolved again (`ProfileAttempts`). An `Error` lists why the overrides do not resolve.
    let setOverrides<'R when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (host: SessionHost<'R>)
        (binder: ProfileBinder<'R>)
        (session: string)
        (key: IdempotencyKey)
        (by: Attribution)
        (changes: (string * Json option) list)
        : Eff<'R, Result<Outcome, string list>> =
        eff {
            let mutable attempt = 1
            let mutable finished = None

            while finished.IsNone do
                let! current = state host session
                let before = current.State.Overrides

                let after =
                    changes
                    |> List.fold
                        (fun overrides (slot, value) ->
                            let others = overrides |> List.filter (fun (other, _) -> other <> slot)

                            match value with
                            | Some value -> (slot, value) :: others
                            | None -> others)
                        before
                    |> List.sortBy fst

                let! old = binder.Bind before
                let! bound = binder.Bind after

                match bound with
                | Error issues -> finished <- Some(Error issues)
                | Ok bound ->
                    let changed =
                        match old with
                        | Ok old -> ProfileBinder.changed old bound
                        | Error _ -> after |> List.map fst

                    let key =
                        if attempt = 1 then
                            key
                        else
                            IdempotencyKey(key.Value + "/" + string attempt)

                    let! outcome =
                        send
                            host
                            session
                            key
                            by
                            (Command.SelectProfile
                                {
                                    Profile = bound.Id
                                    Overrides = after
                                    Changed = changed
                                    BasedOn = current.State.Profile
                                })

                    match outcome with
                    | Rejected rejection when
                        rejection.Code = Rejections.StaleProfile
                        && attempt < host.Settings.ProfileAttempts
                        ->
                        attempt <- attempt + 1
                    | outcome -> finished <- Some(Ok outcome)

            return finished.Value
        }

    /// Follows a session's log (spec §5 `subscribe(afterSeq)`): replays the events after a sequence
    /// number, then goes live. A subscriber more than `MaxGap` events behind gets the state from the
    /// latest snapshot first (`Feed.Reset`). Stops when `handle` returns false, and returns the last
    /// sequence number handled; runs until the scope is cancelled otherwise. The host server (T129)
    /// serves it as `session/subscribe`, encoding each item with `SessionLog.feed`.
    let subscribe<'R when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>>
        (host: SessionHost<'R>)
        (session: string)
        (after: int64)
        (handle: Feed<SessionState, Event> -> Eff<'R, bool>)
        : Eff<'R, int64> =
        Projection.subscribe
            host.Log
            host.Snapshots
            SessionLog.projection
            host.Settings.MaxGap
            (Session.stream session)
            after
            handle

    /// Stops every session's actor: commands in progress finish, turns are cancelled. A new host
    /// picks up from the log (turns that were running are abandoned).
    let stop<'R when 'R :> IClock and 'R :> ITrace and 'R :> IScopeWith<'R>> (host: SessionHost<'R>) : Eff<'R, unit> =
        ActorRegistry.stop host.Registry
