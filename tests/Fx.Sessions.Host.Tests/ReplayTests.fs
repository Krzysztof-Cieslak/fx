/// Replay and determinism (AGENTS.md: changes to machines need replay tests): the same inputs on a fake
/// clock give the same log, snapshots, turns and trace on every run; replaying a recorded log gives the
/// state the host had; and a recorded conversation, kept as golden files, replays to its recorded state.
module Fx.Sessions.Host.Tests.ReplayTests

open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Store
open Fx.Sessions
open Fx.Sessions.Host
open Fx.Sessions.Host.Fakes
open Fx.Sessions.Host.Tests.Support

/// What clients and the host do in a random script.
type Step =
    | Steering of client: int
    | Queueing of client: int
    | Interrupting of client: int
    | Answering of client: int * yes: bool
    | Cancelling
    | Resending of index: int
    | Advancing of seconds: int
    | Restarting
    | Forking
    | Overriding of value: int

    override this.ToString() =
        match this with
        | Steering client -> $"steer {client}"
        | Queueing client -> $"queue {client}"
        | Interrupting client -> $"interrupt {client}"
        | Answering(client, yes) -> $"answer {client} {yes}"
        | Cancelling -> "cancel"
        | Resending index -> $"resend {index}"
        | Advancing seconds -> $"advance {seconds}"
        | Restarting -> "restart"
        | Forking -> "fork"
        | Overriding value -> $"override {value}"

let private clients = [| alice; bob; Attribution.create "user:carol" "ide" |]
let private client (n: int) = clients[(n % 3 + 3) % 3]

/// Turns by number: some think and steer, some ask a person and wait, some post a decision and need
/// its answer.
let private script (turn: TurnStart) : TurnStep list =
    match turn.Turn % 3 with
    | 1 ->
        [
            TurnStep.Think(seconds 2.0)
            TurnStep.Boundary
            TurnStep.Think(seconds 1.0)
            TurnStep.Finish(TurnResult.Done($"turn {turn.Turn}", [], None))
        ]
    | 2 ->
        [
            TurnStep.Ask(confirm $"g{turn.Turn}" RequestOrigin.Session Reply.ToOpener Lifetime.Turn)
            TurnStep.Boundary
            TurnStep.Finish(TurnResult.Done($"turn {turn.Turn}", [], None))
        ]
    | _ ->
        [
            TurnStep.Post(
                choice $"d{turn.Turn}" RequestOrigin.Session Reply.AsInput Lifetime.Session
                |> expiring (seconds 600.0) (ExpiryPolicy.Answer(pick "a"))
            )
            TurnStep.Finish(TurnResult.NeedInput [ $"d{turn.Turn}" ])
        ]

/// Everything observable about a run.
type Run =
    {
        Logs: string list
        Outcomes: string list
        Transcript: string list
        Trace: string list
        Snapshots: string list
    }

    override this.ToString() =
        String.concat "\n" (this.Logs @ this.Outcomes @ this.Transcript)

/// The stored form of a stream: each event with its envelope, as `fx` would export it.
let private stored (fixture: Fixture) (stream: StreamId) : string list =
    fixture.Store.Events stream
    |> List.map (fun recorded ->
        Json.serializeCanonical ((Fx.Store.Codecs.recorded Fx.Store.Codecs.payload).Encode recorded))

let private binder: ProfileBinder<Env> =
    ProfileBinder.constant (fun overrides ->
        "sha256:"
        + (overrides
           |> List.map (fun (slot, value) -> slot + "=" + Json.serialize value)
           |> String.concat ","))

/// Runs a script on a fresh host and checks, after every step, that the session's state is the fold of
/// its log.
let private run (steps: Step list) : Run =
    let mutable fixture =
        createScripted
            { SessionSettings.defaults with
                SnapshotEvery = 7L
                MaxGap = 5L
            }
            script

    start fixture "s1" |> ignore
    let sent = ResizeArray<string * Attribution * Command>()
    let outcomes = ResizeArray<string>()

    let deliver (key: string) (by: Attribution) (command: Command) =
        let outcome = send fixture "s1" key by command
        outcomes.Add $"{key} {command} by {by.Principal} -> {outcome}"

    let mutable forks = 0

    for index, step in List.indexed steps do
        let key = $"k{index}"
        let open' = (state fixture "s1").Requests

        match step with
        | Steering n ->
            let command = Command.Prompt($"steer {index}", PromptMode.Steer)
            sent.Add(key, client n, command)
            deliver key (client n) command
        | Queueing n ->
            let command = Command.Prompt($"queue {index}", PromptMode.Queue)
            sent.Add(key, client n, command)
            deliver key (client n) command
        | Interrupting n ->
            sent.Add(key, client n, Command.Interrupt)
            deliver key (client n) Command.Interrupt
        | Answering(n, yes) ->
            match open' with
            | pending :: _ ->
                let response =
                    match pending.Request.Kind, yes with
                    | "choice", true -> pick "b"
                    | "choice", false -> pick "z"
                    | _, answer -> Json.Bool answer

                let command = Command.Answer(pending.Request.Id, response)
                sent.Add(key, client n, command)
                deliver key (client n) command
            | [] -> ()
        | Cancelling ->
            match List.tryLast open' with
            | Some pending -> deliver key bob (Command.CancelRequest(pending.Request.Id, "no"))
            | None -> ()
        | Resending n when sent.Count > 0 ->
            let key, by, command = sent[(n % sent.Count + sent.Count) % sent.Count]
            deliver key by command
        | Resending _ -> ()
        | Advancing seconds -> fixture.Clock.Advance(Support.seconds (float ((seconds % 7 + 7) % 7)))
        | Restarting -> fixture <- restart fixture
        | Forking ->
            forks <- forks + 1
            deliver key alice (Command.Fork($"c{forks}", None))
        | Overriding value ->
            let changes = [ "edit.guard", Some(Json.String $"v{value % 3}") ]

            let outcome =
                runIn fixture.Env (SessionHost.setOverrides fixture.Host binder "s1" (IdempotencyKey key) alice changes)

            outcomes.Add $"{key} override {value % 3} -> {outcome}"

        // The host's state is the fold of the log, through the stored JSON. (Asking for the state
        // first starts the actor after a restart, which may append what recovery decides.)
        let current = state fixture "s1"
        let events = events fixture "s1" |> List.map _.Event
        Expect.equal current (Machine.replay (Session.machine RequestKinds.builtIn) events) "the fold of the log"

    let streams = fixture.Store.Streams

    for stream in streams do
        let recorded = fixture.Store.Events stream
        Expect.equal (recorded |> List.map _.Seq) [ 1L .. int64 recorded.Length ] "sequence numbers count from 1"

        let commits =
            recorded
            |> List.fold
                (fun (commits: IdempotencyKey list) event ->
                    match commits with
                    | key :: _ when key = event.Meta.Key -> commits
                    | _ -> event.Meta.Key :: commits)
                []

        Expect.equal (List.distinct commits).Length commits.Length "no key has two commits"

    let snapshots =
        streams
        |> List.map (fun stream ->
            match
                completed (
                    fixture.Snapshots.Store.Load
                        stream
                        SessionLog.snapshot.Name
                        System.Threading.CancellationToken.None
                )
            with
            | Some snapshot -> $"{stream} {snapshot.Seq} {Json.serializeCanonical snapshot.State}"
            | None -> $"{stream} none")

    runIn fixture.Env (SessionHost.stop fixture.Host)

    {
        Logs = streams |> List.collect (stored fixture)
        Outcomes = List.ofSeq outcomes
        Transcript = fixture.Runner.Transcript
        Trace = fixture.Trace.Records |> List.map string
        Snapshots = snapshots
    }

/// A conversation that goes through steering, queueing, requests, an interrupt, a restart and a fork.
let private conversation: Step list =
    [
        Steering 0
        Steering 1
        Queueing 2
        Advancing 2
        Advancing 1
        Advancing 3
        Answering(1, true)
        Queueing 0
        Advancing 3
        Answering(0, true)
        Answering(2, false)
        Steering 1
        Interrupting 2
        Resending 0
        Overriding 1
        Steering 0
        Advancing 6
        Restarting
        Advancing 6
        Forking
        Answering(1, true)
        Cancelling
        Steering 2
        Advancing 6
        Advancing 6
    ]

let private config =
    { FsCheckConfig.defaultConfig with
        maxTest = 60
        endSize = 40
    }

[<Tests>]
let tests =
    testList
        "Session replay"
        [
            test "a conversation gives the same log, outcomes, turns, snapshots and trace on every run" {
                let first = run conversation
                Expect.isGreaterThan first.Logs.Length 30 "something happened"
                Expect.equal (run conversation) first "identical"
            }

            test "the recorded conversation replays to the state the host had" {
                let first = run conversation
                // The log as `fx` would export it, kept as a fixture of this version's behaviour.
                Golden.expect
                    "conversation.jsonl"
                    (first.Logs |> List.map (fun line -> line + "\n") |> String.concat "")

                let read =
                    first.Logs
                    |> List.map (fun line ->
                        match Codec.decodeText (Fx.Store.Codecs.recorded Fx.Store.Codecs.payload) line with
                        | Ok recorded -> EventLog.decode SessionLog.schema recorded
                        | Error issues -> failtest (Issue.render issues))
                    |> List.filter (fun recorded -> recorded.Stream = Session.stream "s1")

                let replayed =
                    Machine.replay (Session.machine RequestKinds.builtIn) (read |> List.map _.Event)

                Golden.expect "conversation-state.json" (Json.serializeIndented (Codecs.state.Encode replayed) + "\n")
                Expect.equal replayed.Seq (int64 read.Length) "every event"
            }

            test "the golden conversation replays to its golden state" {
                // Independent of today's host: what was recorded must replay to what was recorded.
                let log =
                    (System.IO.File.ReadAllText(Golden.path "conversation.jsonl"))
                        .Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun line ->
                        match Codec.decodeText (Fx.Store.Codecs.recorded Fx.Store.Codecs.payload) line with
                        | Ok recorded -> EventLog.decode SessionLog.schema recorded
                        | Error issues -> failtest (Issue.render issues))
                    |> Array.filter (fun recorded -> recorded.Stream = Session.stream "s1")
                    |> Array.map _.Event
                    |> List.ofArray

                let expected =
                    match
                        Codec.decodeText
                            Codecs.state
                            (System.IO.File.ReadAllText(Golden.path "conversation-state.json"))
                    with
                    | Ok state -> state
                    | Error issues -> failtest (Issue.render issues)

                Expect.equal (Machine.replay (Session.machine RequestKinds.builtIn) log) expected "replayed"
            }

            testPropertyWithConfig config "any script gives the same run twice" (fun (steps: Step list) ->
                Expect.equal (run steps) (run steps) "identical")
        ]
