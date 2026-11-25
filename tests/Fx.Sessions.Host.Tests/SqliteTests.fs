/// Sessions on the durable store: appends complete on the store's writer thread, so these tests wait
/// for what they expect instead of relying on inline execution.
module Fx.Sessions.Host.Tests.SqliteTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Machines
open Fx.Store
open Fx.Store.Fakes
open Fx.Store.Live
open Fx.Sessions
open Fx.Sessions.Host
open Fx.Sessions.Host.Fakes
open Fx.Sessions.Host.Tests.Support

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

let private openStore (path: string) : SqliteEventStore =
    match SqliteEventStore.openFile path EventStoreOptions.defaults with
    | Ok store -> store
    | Error error -> failtest (string error)

[<Tests>]
let tests =
    testList
        "Sessions on SQLite"
        [
            test "a session survives a restart: its actor starts from a snapshot, and older keys keep their outcome" {
                let directory =
                    Path.Combine(Path.GetTempPath(), "session-tests-" + Guid.NewGuid().ToString("N"))

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

                let run (effect: Eff<Env, 'T>) : 'T = wait (Eff.run env effect)

                let settings =
                    { SessionSettings.defaults with
                        SnapshotEvery = 3L
                        MaxGap = 2L
                    }

                let runner = ScriptedTurnRunner.byTurn<Env> []

                let openHost (store: SqliteEventStore) =
                    run (SessionHost.create (SessionLog.typed store.Log) store.Snapshots runner.Runner settings)

                /// Follows the session until its state satisfies `until`, and returns that state.
                let follow
                    (host: SessionHost<Env>)
                    (until: SessionState -> bool)
                    : Feed<SessionState, Event> list * SessionState =
                    let received = ResizeArray()
                    let folded = ref Session.initial

                    run (
                        SessionHost.subscribe host "s1" 0L (fun item ->
                            eff {
                                received.Add item

                                folded.Value <-
                                    match item with
                                    | Feed.Reset snapshot -> snapshot.State
                                    | Feed.Next recorded -> Session.evolve folded.Value recorded.Event

                                return not (until folded.Value)
                            })
                    )
                    |> ignore

                    List.ofSeq received, folded.Value

                let send (host: SessionHost<Env>) (key: string) (command: Command) =
                    run (SessionHost.send host "s1" (IdempotencyKey key) alice command)

                try
                    let store = openStore path
                    let host = openHost store

                    let started =
                        run (SessionHost.start host "s1" (IdempotencyKey "start") alice baseProfile None)

                    Expect.equal started (accepted 0L 1) "started"
                    let first = send host "p1" (Command.Prompt("one", PromptMode.Steer))
                    Expect.equal first (accepted 1L 2) "turn 1"
                    let _, afterOne = follow host (fun state -> state.Turns = 1 && state.Running.IsNone)
                    Expect.equal afterOne.Seq 4L "turn 1 ended"
                    send host "p2" (Command.Prompt("two", PromptMode.Queue)) |> ignore
                    let _, afterTwo = follow host (fun state -> state.Turns = 2 && state.Running.IsNone)
                    Expect.equal afterTwo.Seq 7L "turn 2 ended"

                    Expect.isSome
                        (wait (
                            store.Snapshots.Load
                                (Session.stream "s1")
                                SessionLog.snapshot.Name
                                Threading.CancellationToken.None
                        ))
                        "a snapshot was saved"

                    run (SessionHost.stop host)
                    store.Dispose()

                    let store = openStore path
                    let host = openHost store
                    Expect.equal (send host "p1" (Command.Prompt("one", PromptMode.Steer))) first "the first outcome"

                    Expect.equal
                        (run (SessionHost.start host "s1" (IdempotencyKey "start") alice baseProfile None))
                        started
                        "not refused"

                    let current = (run (SessionHost.state host "s1")).State

                    let log =
                        run (Fx.Machines.Host.EventLog.readAll (SessionLog.typed store.Log) (Session.stream "s1") 0L)

                    Expect.equal log.Length 7 "nothing appended"

                    Expect.equal
                        current
                        (Machine.replay (Session.machine RequestKinds.builtIn) (log |> List.map _.Event))
                        "the fold of the log"

                    let feed, followed = follow host (fun state -> state.Seq = 7L)

                    match feed with
                    | Feed.Reset snapshot :: _ ->
                        Expect.isGreaterThan snapshot.Seq 2L "a subscriber far behind starts from the snapshot"
                    | other -> failtest (string other)

                    Expect.equal followed current "and catches up with the session"
                    run (SessionHost.stop host)
                    store.Dispose()
                finally
                    try
                        Directory.Delete(directory, true)
                    with _ ->
                        ()
            }
        ]
