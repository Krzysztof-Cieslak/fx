/// The environment the grants service runs in during tests, and helpers.
module Fx.Grants.Host.Tests.Support

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Sdk
open Fx.Grants
open Fx.Grants.Host

type Env =
    {
        Clock: Clock
        Ids: Ids
        Trace: Trace
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IIds with
        member env.Ids = env.Ids

    interface ITrace with
        member env.Trace = env.Trace

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = "Env(" + string env.Scope + ")"

type Fakes =
    {
        Env: Env
        Clock: FakeClock
    }

    override _.ToString() = "Fakes"

let fakes () : Fakes =
    let clock = FakeClock.Create()

    {
        Env =
            {
                Clock = clock.Clock
                Ids = FakeIds(7UL, clock.Clock).Ids
                Trace = (TraceCollector clock.Clock).Trace
                Scope = Scope.root
            }
        Clock = clock
    }

/// Runs an effect and waits for it (tests only; in-memory logs complete inline).
/// Waits for a task, failing the test after 30 s; a failed task rethrows its exception.
let wait (task: Task<'T>) : 'T =
    try
        task.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()
    with :? TimeoutException ->
        failtest "the task did not complete in 30 s"

let runIn (env: 'R) (effect: Eff<'R, 'T>) : 'T = wait (Eff.run env effect)

/// Expects the action to throw an `OperationCanceledException` (or a subtype).
let cancelled (action: unit -> unit) (message: string) =
    try
        action ()
        failtest (message + ": nothing was thrown")
    with :? OperationCanceledException ->
        ()

let ok (result: Result<'T, GrantError>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failtest (string e)

let error (result: Result<'T, GrantError>) : GrantError =
    match result with
    | Ok value -> failtest ("expected an error, got " + string value)
    | Error e -> e

let shellExec = ActionName.create "shell.exec"
let fsWrite = ActionName.create "fs.write"
let alice = Attribution.create "user:alice" "tui"
let bob = Attribution.create "user:bob" "web"

let context =
    {
        Session = Some "s1"
        Run = None
        Repo = Some { Repo = "r1"; Path = "src" }
    }

let ask (key: string) (action: ActionName) (resource: string) : GrantAsk =
    {
        Key = key
        Action = action
        Resource = resource
        Context = context
        Requester =
            {
                Principal = "user:alice"
                Agent = Some "build"
                Extension = None
            }
        Summary = "Run " + resource
        Evidence = []
        Suggestions = []
        Scopes = []
        Expires = None
    }

let query (action: ActionName) (resource: string) : GrantQuery =
    {
        Action = action
        Resource = resource
        Context = context
    }

let once: GrantAnswer =
    {
        Effect = GrantEffect.Allow
        Scope = ScopeChoice.Once
        Resource = None
        Expires = None
        Reason = None
    }

let forSession: GrantAnswer =
    { once with
        Scope = ScopeChoice.Session
    }

/// A service on a fresh host-local store over an in-memory log.
type Harness =
    {
        Fakes: Fakes
        Log: InMemoryEventLog<Payload>
        Service: GrantsService<Env>
    }

    member this.Env = this.Fakes.Env
    member this.Run(effect: Eff<Env, 'T>) : 'T = runIn this.Env effect
    member this.Grants: Grants = GrantsService.grants this.Service

    override _.ToString() = "Harness"

let startOn (fakes: Fakes) (log: InMemoryEventLog<Payload>) (configure: GrantsSettings -> GrantsSettings) : Harness =
    let settings = configure (GrantsSettings.create (GrantStoreLog.hostLocal log.Log))

    {
        Fakes = fakes
        Log = log
        Service = runIn fakes.Env (GrantsService.start settings)
    }

let harness () : Harness =
    startOn (fakes ()) (InMemoryEventLog<Payload>()) id

/// Asks and expects a pending request.
let pendingOf (h: Harness) (a: GrantAsk) : RequestId =
    match h.Run(GrantsService.request h.Service a) with
    | Ok(AskOutcome.Pending id) -> id
    | other -> failtest ("expected a pending request, got " + string other)

/// The grants log's events, printed.
let events (h: Harness) : string list =
    h.Log.Events(GrantMachine.stream "host")
    |> List.map (fun recorded ->
        match EventSchema.decode GrantMachine.schema recorded.Event with
        | Ok event -> string event
        | Error reason -> failtest reason)

let none = CancellationToken.None

/// An environment with only the grants capability: what an agent gets.
type GrantsEnv =
    {
        Grants: Grants
        Scope: Scope
    }

    interface IGrants with
        member env.Grants = env.Grants

    interface IScopeWith<GrantsEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "GrantsEnv"

/// An extension's environment, for gates.
let extensionEnv (extension: string) (clock: Clock) (scope: Scope) : IExtensionEnv =
    {
        BasicExtensionEnv.Extension = extension
        Clock = clock
        Scope = scope
    }
    :> IExtensionEnv
