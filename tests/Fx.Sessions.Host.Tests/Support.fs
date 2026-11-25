/// A session host on fakes: an in-memory store of payloads (so every event goes through its JSON), in-
/// memory snapshots and blobs, a fake clock and a scripted turn runner. Everything runs inline, in an
/// order fixed by the test's inputs.
module Fx.Sessions.Host.Tests.Support

open System
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Store.Fakes
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

    override env.ToString() = $"Env({env.Scope})"

let alice = Attribution.create "user:alice" "tui"
let bob = Attribution.create "user:bob" "web"
let system = Attribution.system "test"

/// The base profile sessions start under in these tests.
let baseProfile = "sha256:base"

/// When the fake clock starts, in the machine's microseconds.
let epoch = 1_767_225_600_000_000L

let seconds (n: float) = TimeSpan.FromSeconds n

type Fixture =
    {
        Env: Env
        Clock: FakeClock
        Trace: TraceCollector
        Store: InMemoryEventLog<Payload>
        Snapshots: InMemorySnapshots
        Blobs: InMemoryBlobs
        Runner: ScriptedTurnRunner<Env>
        Settings: SessionSettings
        Host: SessionHost<Env>
    }

    override _.ToString() = "Fixture"

/// Waits for a task that should already have completed (everything here runs inline).
let completed (task: Task<'T>) : 'T =
    if not task.IsCompleted then
        failtest "expected the task to have completed inline"

    task.GetAwaiter().GetResult()

let runIn (env: 'R) (effect: Eff<'R, 'T>) : 'T = completed (Eff.run env effect)

let private host (fixture: Fixture) : SessionHost<Env> =
    runIn
        fixture.Env
        (SessionHost.create
            (SessionLog.typed fixture.Store.Log)
            fixture.Snapshots.Store
            fixture.Runner.Runner
            fixture.Settings)

/// A fixture whose turns play the steps `script` gives for them.
let createScripted (settings: SessionSettings) (script: TurnStart -> TurnStep list) : Fixture =
    let clock = FakeClock.Create()
    let trace = TraceCollector clock.Clock
    let blobs = InMemoryBlobs()

    let fixture =
        {
            Env =
                {
                    Clock = clock.Clock
                    Trace = trace.Trace
                    Blobs = blobs.Blobs
                    Scope = Scope.root
                }
            Clock = clock
            Trace = trace
            Store = InMemoryEventLog<Payload>()
            Snapshots = InMemorySnapshots()
            Blobs = blobs
            Runner = ScriptedTurnRunner<Env>(script)
            Settings = settings
            Host = Unchecked.defaultof<SessionHost<Env>>
        }

    { fixture with Host = host fixture }

/// A fixture whose turns play `turns` (by turn number; others end `Done` at once).
let createWith (settings: SessionSettings) (turns: (int * TurnStep list) list) : Fixture =
    createScripted settings (ScriptedTurnRunner.byTurn<Env> turns).Script

let create (turns: (int * TurnStep list) list) : Fixture =
    createWith SessionSettings.defaults turns

/// Stops the host and starts another on the same stores, as after a crash or an upgrade.
let restart (fixture: Fixture) : Fixture =
    runIn fixture.Env (SessionHost.stop fixture.Host)
    { fixture with Host = host fixture }

let send (fixture: Fixture) (session: string) (key: string) (by: Attribution) (command: Command) : Outcome =
    runIn fixture.Env (SessionHost.send fixture.Host session (IdempotencyKey key) by command)

let start (fixture: Fixture) (session: string) : Outcome =
    runIn
        fixture.Env
        (SessionHost.start fixture.Host session (IdempotencyKey(session + "/start")) alice baseProfile (Some session))

let steer fixture session key by text =
    send fixture session key by (Command.Prompt(text, PromptMode.Steer))

let queue fixture session key by text =
    send fixture session key by (Command.Prompt(text, PromptMode.Queue))

let state (fixture: Fixture) (session: string) : SessionState =
    (runIn fixture.Env (SessionHost.state fixture.Host session)).State

/// A session's events as stored, decoded: `#seq event`.
let events (fixture: Fixture) (session: string) : Recorded<Event> list =
    fixture.Store.Events(Session.stream session)
    |> List.map (EventLog.decode SessionLog.schema)

let lines (fixture: Fixture) (session: string) : string list =
    events fixture session
    |> List.map (fun recorded -> "#" + string recorded.Seq + " " + string recorded.Event)

/// The lines appended since a sequence number.
let linesAfter (fixture: Fixture) (session: string) (after: int64) : string list =
    lines fixture session |> List.skip (int after)

let accepted (after: int64) (count: int) =
    Accepted { After = after; Count = count }

let isAccepted (outcome: Outcome) =
    match outcome with
    | Accepted _ -> true
    | _ -> false

let rejectedWith (outcome: Outcome) : string =
    match outcome with
    | Rejected rejection -> rejection.Code
    | other -> failtest ("expected a rejection, got " + string other)

/// A request of the built-in `confirm` kind.
let confirm (id: string) (origin: RequestOrigin) (reply: Reply) (lifetime: Lifetime) : RequestSpec =
    RequestKind.request RequestKinds.confirm id ("confirm " + id) origin reply lifetime { Message = "Go on?" }

/// A request of the built-in `choice` kind.
let choice (id: string) (origin: RequestOrigin) (reply: Reply) (lifetime: Lifetime) : RequestSpec =
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

let expiring (after: TimeSpan) (policy: ExpiryPolicy) (request: RequestSpec) : RequestSpec =
    { request with
        Expiry =
            Some
                {
                    At = epoch + after.Ticks / 10L
                    Then = policy
                }
    }

let yes = Json.Bool true
let pick (option: string) = Json.String option
