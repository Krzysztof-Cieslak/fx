/// Fx.Sessions and Fx.Sessions.Host (T125, docs/modules/sessions.md): the session machine and the JSON
/// of every event and its state; sessions run by a host on the fake clock (steering, queueing,
/// requests answered and expired, an interrupt, a restart mid-turn, a fork, subscribers, overrides
/// resolved into stored profiles); and a session on SQLite that restarts from a snapshot. Stores live in
/// a fresh temporary directory whose path is never printed.
module Fx.Aot.Scenarios.Sessions

open System
open System.IO
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Store.Fakes
open Fx.Store.Live
open Fx.Variants
open Fx.Sessions
open Fx.Sessions.Host
open Fx.Sessions.Host.Fakes

type Env =
    {
        Clock: Clock
        Trace: Trace
        Blobs: Blobs
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface ITrace with
        member env.Trace = env.Trace

    interface IBlobs with
        member env.Blobs = env.Blobs

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "Env"

let private alice = Attribution.create "user:alice" "tui"
let private bob = Attribution.create "user:bob" "web"
let private system = Attribution.system "interpreter"
let private epoch = 1_767_225_600_000_000L
let private second = 1_000_000L

let private confirm (id: string) (origin: RequestOrigin) (reply: Reply) (lifetime: Lifetime) =
    RequestKind.request RequestKinds.confirm id ("confirm " + id) origin reply lifetime { Message = "Go on?" }

let private choice (id: string) (origin: RequestOrigin) (reply: Reply) (lifetime: Lifetime) =
    RequestKind.request
        RequestKinds.choice
        id
        ("choose " + id)
        origin
        reply
        lifetime
        {
            Question = "Which?"
            Options = [ "a"; "b" ]
        }

let private expiring (seconds: int64) (policy: ExpiryPolicy) (request: RequestSpec) =
    { request with
        Expiry =
            Some
                {
                    At = epoch + seconds * second
                    Then = policy
                }
    }

/// The pure machine: commands through Handle, Evolve and React, every event through its JSON.
let private machine =
    Scenario.lines "sessions-machine" (fun () ->
        let kinds = RequestKinds.builtIn
        let session = Session.machine kinds
        let lines = ResizeArray<string>()
        let mutable state = session.Initial
        let mutable now = epoch

        let send (by: Attribution) (command: Command) =
            now <- now + second

            match Machine.step session state { Command = command; By = by; At = now } with
            | Error rejection -> lines.Add $"{command} -> {rejection.Code}"
            | Ok step ->
                state <- step.State

                for event in step.Events do
                    let json = Json.serializeCanonical (Codecs.event.Encode event)

                    match Codec.decodeText Codecs.event json with
                    | Ok read when read = event -> lines.Add json
                    | Ok read -> lines.Add $"MISMATCH {read}"
                    | Error issues -> lines.Add(Issue.render issues)

                for intent in step.Intents do
                    lines.Add("  -> " + Json.serializeCanonical (Codecs.intent.Encode intent))

        send
            alice
            (Command.Start
                {
                    Id = "s1"
                    Profile = "sha256:base"
                    Overrides = [ "edit.guard", Json.String "warn" ]
                    Parent = None
                    Title = Some "aot"
                })

        send alice (Command.Prompt("one", PromptMode.Steer))
        send bob (Command.Prompt("two", PromptMode.Steer))
        send alice (Command.Prompt("three", PromptMode.Queue))
        send system (Command.TakeInputs(1, 1))
        send system (Command.OpenRequest(choice "d1" (RequestOrigin.Turn 1) Reply.AsInput Lifetime.Session))
        send system (Command.OpenRequest(confirm "g1" (RequestOrigin.Turn 1) Reply.ToOpener Lifetime.Turn))
        send bob (Command.Answer("d1", Json.String "z"))
        send system (Command.EndTurn(1, TurnResult.NeedInput [ "d1" ]))
        send alice (Command.Answer("d1", Json.String "b"))
        send bob (Command.Answer("d1", Json.String "a"))

        send
            system
            (Command.OpenRequest(
                confirm "g2" RequestOrigin.Session Reply.AsInput Lifetime.Session
                |> expiring 60L (ExpiryPolicy.Answer(Json.Bool false))
            ))

        send bob Command.Interrupt
        now <- now + 60L * second
        send system (Command.ExpireRequest "g2")
        send alice (Command.Fork("c1", Some 3L))
        send system (Command.ChildStarted "c1")

        send
            bob
            (Command.SelectProfile
                {
                    Profile = "sha256:edited"
                    Overrides = [ "edit.guard", Json.String "require" ]
                    Changed = [ "edit.guard" ]
                    BasedOn = "sha256:base"
                })

        send alice (Command.Prompt("go on", PromptMode.Steer))

        lines.Add("recover: " + (Session.recover state |> List.map string |> String.concat "; "))

        send system (Command.EndTurn(3, TurnResult.Waiting([ "j1" ], "building")))
        send system (Command.NotifyJob("j1", "built"))

        send
            system
            (Command.EndTurn(
                4,
                TurnResult.Done(
                    "done",
                    [
                        {
                            Kind = "command"
                            Summary = "tests pass"
                            Detail = Json.Object [ "exitCode", Json.int 0 ]
                        }
                    ],
                    Some "dotnet test"
                )
            ))

        send alice Command.Close
        send alice (Command.Prompt("late", PromptMode.Steer))
        send alice Command.Archive
        lines.Add(Json.serializeCanonical (Codecs.state.Encode state))
        List.ofSeq lines)

/// Sessions run by a host on the fake clock. Runs on a thread-pool thread: FakeClock.Advance needs
/// inline continuations, which the main thread of a musl Native AOT binary never gets.
let private host =
    Scenario.task "sessions-host" (fun () ->
        Task.Run<string list>(
            Func<string list>(fun () ->
                let clock = FakeClock.Create()
                let trace = TraceCollector clock.Clock
                let blobs = InMemoryBlobs()

                let env =
                    {
                        Clock = clock.Clock
                        Trace = trace.Trace
                        Blobs = blobs.Blobs
                        Scope = Scope.root
                    }

                let run (effect: Eff<Env, 'T>) : 'T =
                    let task = Eff.run env effect

                    if not task.IsCompleted then
                        failwith "expected the effect to complete inline"

                    task.GetAwaiter().GetResult()

                let store = InMemoryEventLog<Payload>()
                let snapshots = InMemorySnapshots()

                let settings =
                    { SessionSettings.defaults with
                        SnapshotEvery = 6L
                        MaxGap = 4L
                    }

                let runner =
                    ScriptedTurnRunner.byTurn<Env>
                        [
                            1,
                            [
                                TurnStep.Think(TimeSpan.FromSeconds 2.0)
                                TurnStep.Boundary
                                TurnStep.Ask(confirm "g1" RequestOrigin.Session Reply.ToOpener Lifetime.Turn)
                                TurnStep.Finish(TurnResult.Done("one", [], None))
                            ]
                            2,
                            [
                                TurnStep.Post(
                                    choice "d2" RequestOrigin.Session Reply.AsInput Lifetime.Session
                                    |> expiring 30L (ExpiryPolicy.Answer(Json.String "a"))
                                )
                                TurnStep.Finish(TurnResult.NeedInput [ "d2" ])
                            ]
                            3, [ TurnStep.Think(TimeSpan.FromSeconds 60.0) ]
                            4, [ TurnStep.Think(TimeSpan.FromSeconds 60.0) ]
                            5, [ TurnStep.Throw ]
                        ]

                let openHost () =
                    run (SessionHost.create (SessionLog.typed store.Log) snapshots.Store runner.Runner settings)

                let mutable sessions = openHost ()
                let out = ResizeArray<string>()

                let send (key: string) (by: Attribution) (command: Command) =
                    let outcome = run (SessionHost.send sessions "s1" (IdempotencyKey key) by command)
                    out.Add $"{key} {command} -> {outcome}"

                let binder =
                    ProfileBinder.ofVariants (SlotRegistry.ofSlots Slots.all) [] [] (Profile.ofResolution "aot")

                let baseProfile =
                    match run (binder.Bind []) with
                    | Ok bound -> bound.Id
                    | Error issues -> String.concat "; " issues

                out.Add $"base profile {baseProfile}"

                out.Add(
                    string (
                        run (SessionHost.start sessions "s1" (IdempotencyKey "start") alice baseProfile (Some "aot"))
                    )
                )

                let following = ResizeArray<string>()

                let follower =
                    Eff.run
                        env
                        (SessionHost.subscribe sessions "s1" 0L (fun item ->
                            eff {
                                following.Add(string item)

                                return
                                    match item with
                                    | Feed.Next { Event = Event.Archived _ } -> false
                                    | _ -> true
                            }))

                send "p1" alice (Command.Prompt("hello", PromptMode.Steer))
                send "p2" bob (Command.Prompt("also", PromptMode.Steer))
                send "p3" alice (Command.Prompt("then", PromptMode.Queue))
                clock.Advance(TimeSpan.FromSeconds 2.0)

                out.Add(
                    "offered: "
                    + (run (SessionHost.offers sessions "s1" [ "confirm" ])
                       |> List.map string
                       |> String.concat "; ")
                )

                send "a1" bob (Command.Answer("g1", Json.Bool true))
                send "a2" alice (Command.Answer("g1", Json.Bool false))
                send "a1" bob (Command.Answer("g1", Json.Bool true))
                clock.Advance(TimeSpan.FromSeconds 30.0)
                send "p4" alice (Command.Prompt("more", PromptMode.Steer))
                out.Add $"pending delays {clock.PendingDelays}"
                send "i1" bob Command.Interrupt
                out.Add $"pending delays {clock.PendingDelays}"

                match
                    run (
                        SessionHost.setOverrides
                            sessions
                            binder
                            "s1"
                            (IdempotencyKey "o1")
                            alice
                            [ "edit.guard", Some(Json.String "require") ]
                    )
                with
                | Ok outcome -> out.Add $"override -> {outcome}"
                | Error issues -> out.Add("override refused: " + String.concat "; " issues)

                match
                    run (
                        SessionHost.setOverrides
                            sessions
                            binder
                            "s1"
                            (IdempotencyKey "o2")
                            alice
                            [ "edit.gaurd", Some(Json.String "require") ]
                    )
                with
                | Ok outcome -> out.Add $"override -> {outcome}"
                | Error issues -> out.Add $"override refused: {issues.Length} issues"

                send "p5" bob (Command.Prompt("restart soon", PromptMode.Steer))
                run (SessionHost.stop sessions)
                sessions <- openHost ()
                send "p5" bob (Command.Prompt("restart soon", PromptMode.Steer))
                send "f1" alice (Command.Fork("c1", None))
                send "p6" alice (Command.Prompt("fail", PromptMode.Steer))
                send "x" alice Command.Archive

                if not (follower.Wait(TimeSpan.FromSeconds 10.0)) then
                    out.Add "the follower did not finish"

                out.Add "-- s1"

                for recorded in store.Events(Session.stream "s1") do
                    let event = EventLog.decode SessionLog.schema recorded
                    out.Add $"#{event.Seq} {event.Event} [{event.Meta.Key} {event.Meta.By}]"

                out.Add "-- c1"

                for recorded in store.Events(Session.stream "c1") do
                    let event = EventLog.decode SessionLog.schema recorded
                    out.Add $"#{event.Seq} {event.Event} [{event.Meta.Key}]"

                out.Add "-- turns"
                out.AddRange runner.Transcript
                out.Add "-- follower"
                out.AddRange following
                let final = run (SessionHost.state sessions "s1")
                out.Add(Json.serializeCanonical (Codecs.state.Encode final.State))
                run (SessionHost.stop sessions)
                List.ofSeq out)
        ))

/// A session on SQLite: its actor restarts from a snapshot and looks older keys up in the database.
let private sqlite =
    Scenario.task "sessions-sqlite" (fun () ->
        Task.Run<string list>(
            Func<string list>(fun () ->
                let directory =
                    Path.Combine(Path.GetTempPath(), "sessions-scenario-" + Guid.NewGuid().ToString("N"))

                Directory.CreateDirectory directory |> ignore
                let path = Path.Combine(directory, "events.db")
                let clock = FakeClock.Create()

                let env =
                    {
                        Clock = clock.Clock
                        Trace = Trace.none
                        Blobs = InMemoryBlobs().Blobs
                        Scope = Scope.root
                    }

                let run (effect: Eff<Env, 'T>) : 'T =
                    (Eff.run env effect).WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

                let openStore () =
                    match SqliteEventStore.openFile path EventStoreOptions.defaults with
                    | Ok store -> store
                    | Error error -> failwith (string error)

                let settings =
                    { SessionSettings.defaults with
                        SnapshotEvery = 3L
                    }

                let runner = ScriptedTurnRunner.byTurn<Env> []
                let out = ResizeArray<string>()

                let idle (sessions: SessionHost<Env>) (turns: int) =
                    run (
                        SessionHost.subscribe sessions "s1" 0L (fun item ->
                            eff {
                                return
                                    match item with
                                    | Feed.Next { Event = Event.TurnEnded(turn, _, _) } -> turn < turns
                                    | _ -> true
                            })
                    )
                    |> ignore

                try
                    let store = openStore ()

                    let sessions =
                        run (SessionHost.create (SessionLog.typed store.Log) store.Snapshots runner.Runner settings)

                    let send key command =
                        string (run (SessionHost.send sessions "s1" (IdempotencyKey key) alice command))

                    out.Add(
                        string (
                            run (SessionHost.start sessions "s1" (IdempotencyKey "start") alice "sha256:base" None)
                        )
                    )

                    out.Add(send "p1" (Command.Prompt("one", PromptMode.Steer)))
                    idle sessions 1
                    out.Add(send "p2" (Command.Prompt("two", PromptMode.Steer)))
                    idle sessions 2
                    run (SessionHost.stop sessions)
                    store.Dispose()

                    let store = openStore ()

                    let sessions =
                        run (SessionHost.create (SessionLog.typed store.Log) store.Snapshots runner.Runner settings)

                    let send key command =
                        string (run (SessionHost.send sessions "s1" (IdempotencyKey key) alice command))

                    out.Add(send "p1" (Command.Prompt("one", PromptMode.Steer)))

                    out.Add(
                        string (
                            run (SessionHost.start sessions "s1" (IdempotencyKey "start") alice "sha256:base" None)
                        )
                    )

                    let state = run (SessionHost.state sessions "s1")
                    out.Add $"seq {state.Seq}, {state.State.Turns} turns, running {state.State.Running.IsSome}"
                    run (SessionHost.stop sessions)
                    store.Dispose()
                finally
                    try
                        Directory.Delete(directory, true)
                    with _ ->
                        ()

                List.ofSeq out)
        ))

let scenarios = [ machine; host; sqlite ]
