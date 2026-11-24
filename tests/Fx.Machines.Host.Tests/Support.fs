/// Helpers for the actor tests: wrapped logs that are slow or crash, and checks of log invariants.
module Fx.Machines.Host.Tests.Support

open System
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Machines
open Fx.Machines.Host

/// A log whose appends take time on the clock: while an actor waits for one, other inputs queue up.
let slow (clock: Clock) (latency: TimeSpan) (log: EventLog<'Evt>) : EventLog<'Evt> =
    { log with
        Append =
            fun stream expected meta events cancellation ->
                task {
                    do! clock.Delay latency cancellation
                    return! log.Append stream expected meta events cancellation
                }
    }

/// The process crashed after the store made the commit durable, before the actor heard back.
exception Crashed of string

/// A log that appends a commit with this key, then throws: the outcome is unknown to the actor.
let crashAfterAppending (key: string) (log: EventLog<'Evt>) : EventLog<'Evt> =
    { log with
        Append =
            fun stream expected meta events cancellation ->
                task {
                    let! result = log.Append stream expected meta events cancellation

                    if meta.Key.Value = key then
                        return raise (Crashed key)
                    else
                        return result
                }
    }

/// Every event of a stream, printed.
let lines (log: InMemoryEventLog<'Evt>) (stream: StreamId) : string list = log.Events stream |> List.map string

/// Waits for a task that should already have completed (actors on in-memory logs run inline).
let completed (task: Task<'T>) : 'T =
    if not task.IsCompleted then
        failtest "expected the task to have completed inline"

    task.GetAwaiter().GetResult()

/// Advances the clock to each pending delay until the task completes.
let advanceUntil (clock: FakeClock) (task: Task) =
    while not task.IsCompleted do
        if not (clock.AdvanceToNextDelay()) then
            failtest "the task waits for something other than the clock"

/// Checks what must hold for any actor's log: sequence numbers count from 1 without gaps, each
/// command's events are contiguous and no key has two commits, and the actor's state is the fold of
/// its log.
let checkLog
    (machine: Machine<'Cmd, 'Evt, 'S, 'Intent>)
    (log: InMemoryEventLog<'Evt>)
    (stream: StreamId)
    (state: Snapshot<'S>)
    =
    let events = log.Events stream
    Expect.equal (events |> List.map _.Seq) [ 1L .. int64 events.Length ] "sequence numbers count from 1 without gaps"

    let commits =
        events
        |> List.fold
            (fun (commits: (IdempotencyKey * int) list) recorded ->
                match commits with
                | (key, count) :: rest when key = recorded.Meta.Key -> (key, count + 1) :: rest
                | _ -> (recorded.Meta.Key, 1) :: commits)
            []

    let keys = commits |> List.map fst
    Expect.equal (List.distinct keys).Length keys.Length "no key has two commits"
    Expect.equal state.Seq (int64 events.Length) "the snapshot is at the head"
    Expect.equal state.State (Machine.replay machine (events |> List.map _.Event)) "the state is the fold of the log"
