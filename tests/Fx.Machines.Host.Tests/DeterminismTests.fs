/// Replay and determinism: the same inputs on a fake clock give the same log, outcomes and trace on
/// every run, whatever the inputs (spec §15; AGENTS.md "replay tests").
module Fx.Machines.Host.Tests.DeterminismTests

open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Machines.Host.Tests.Chat
open Fx.Machines.Host.Tests.Support
open Fx.Machines.Host.Tests.TestEnv

/// One step of a script: what clients do, and what happens to the host.
type Step =
    | Send of client: int * text: int
    | Resend of index: int
    | Interrupting of client: int
    | Closing of client: int
    | Advance of seconds: int
    | Restart

    override this.ToString() =
        match this with
        | Send(client, text) -> $"Send {client} {text}"
        | Resend index -> $"Resend {index}"
        | Interrupting client -> $"Interrupt {client}"
        | Closing client -> $"Close {client}"
        | Advance seconds -> $"Advance {seconds}"
        | Restart -> "Restart"

/// Everything observable about a run.
type Run =
    {
        Log: string list
        Outcomes: string list
        Trace: string list
    }

    override this.ToString() =
        String.concat "\n" (this.Log @ this.Outcomes)

let private texts = [| ""; "a"; "bb"; "ccc" |]

let private clients = [| alice; bob; Attribution.create "user:carol" "ide" |]

let private pick (items: 'T array) (n: int) =
    items[(n % items.Length + items.Length) % items.Length]

/// What was sent, by whom, to which incarnation of the actor, and what came of it.
type private Sent =
    {
        Envelope: Envelope<Command>
        Incarnation: int
        Outcome: Task<Outcome>
    }

let private show (task: Task<Outcome>) =
    if not task.IsCompleted then
        "pending"
    else
        try
            string (task.GetAwaiter().GetResult())
        with e ->
            $"failed {e.GetType().Name}"

/// Runs a script against a fresh actor, then lets every question finish and stops the actor. With
/// `slowAppends`, every append takes a second, so inputs queue up behind it.
let run (slowAppends: bool) (steps: Step list) : Run =
    let fakes = create ()
    let store = InMemoryEventLog<Event>()

    let log =
        if slowAppends then
            slow fakes.Clock.Clock (seconds 1.0) store.Log
        else
            store.Log

    let actor = ref (runIn fakes.Env (Actor.start (spec log)))
    let incarnation = ref 0
    let sent = ResizeArray<Sent>()

    let record (envelope: Envelope<Command>) (outcome: Task<Outcome>) =
        sent.Add
            {
                Envelope = envelope
                Incarnation = incarnation.Value
                Outcome = outcome
            }

    let stop () =
        let stopping = actor.Value.Stop()
        advanceUntil fakes.Clock stopping

    for index, step in List.indexed steps do
        let key = $"s{index}"

        match step with
        | Send(client, text) ->
            let message = envelope key (pick clients client) (Prompt(pick texts text))
            record message (Eff.run fakes.Env (Actor.send actor.Value message))
        | Resend n when sent.Count > 0 ->
            let message = sent[(n % sent.Count + sent.Count) % sent.Count].Envelope
            record message (Eff.run fakes.Env (Actor.send actor.Value message))
        | Resend _ -> ()
        | Interrupting client ->
            let message = envelope key (pick clients client) Interrupt
            record message (Eff.run fakes.Env (Actor.interrupt actor.Value message))
        | Closing client ->
            let message = envelope key (pick clients client) Close
            record message (Eff.run fakes.Env (Actor.send actor.Value message))
        | Advance seconds -> fakes.Clock.Advance(TestEnv.seconds (float (abs seconds % 5)))
        | Restart ->
            stop ()
            incarnation.Value <- incarnation.Value + 1
            actor.Value <- runIn fakes.Env (Actor.start (spec log))

    while fakes.Clock.AdvanceToNextDelay() do
        ()

    let final = Actor.snapshot actor.Value
    stop ()
    checkLog machine store stream final
    let events = store.Events stream

    for message in sent do
        match message.Outcome.Status with
        | TaskStatus.RanToCompletion ->
            match message.Outcome.Result with
            | Accepted appended when appended.Count > 0 ->
                let commit =
                    events
                    |> List.filter (fun e -> e.Seq >= appended.First && e.Seq <= appended.Last)

                Expect.equal commit.Length appended.Count "the outcome's events are in the log"
                Expect.allEqual (commit |> List.map _.Meta.Key) message.Envelope.Key "with the command's key"
                Expect.allEqual (commit |> List.map _.Meta.By) message.Envelope.By "attributed to its sender"
            | _ -> ()
        | status -> failtest $"{message.Envelope} ended {status}"

    // A key answers with its first outcome: always within one actor, and across restarts once its
    // events are in the log.
    for _, copies in sent |> Seq.groupBy _.Envelope.Key do
        let handled =
            copies |> Seq.filter (fun copy -> copy.Outcome.Result <> Stopped) |> List.ofSeq

        match handled with
        | first :: rest ->
            for copy in rest do
                match first.Outcome.Result with
                | Accepted appended when appended.Count > 0 ->
                    Expect.equal copy.Outcome.Result first.Outcome.Result "durable outcome"
                | _ when copy.Incarnation = first.Incarnation ->
                    Expect.equal copy.Outcome.Result first.Outcome.Result "same actor, same outcome"
                | _ -> ()
        | [] -> ()

    {
        Log = lines store stream
        Outcomes =
            [
                for message in sent ->
                    $"{message.Envelope.Key} {message.Envelope.Command} by {message.Envelope.By} -> {show message.Outcome}"
            ]
        Trace = fakes.Trace.Records |> List.map string
    }

let private script =
    [
        Send(0, 3)
        Send(1, 2)
        Send(2, 0)
        Resend 0
        Interrupting 1
        Send(0, 1)
        Advance 2
        Send(2, 3)
        Restart
        Resend 1
        Advance 4
        Interrupting 2
        Interrupting 2
        Send(1, 1)
        Closing 0
        Send(2, 2)
    ]

let private config =
    { FsCheckConfig.defaultConfig with
        maxTest = 200
    }

[<Tests>]
let tests =
    testList
        "Determinism"
        [
            test "a script gives the same log, outcomes and trace on every run" {
                for slowAppends in [ false; true ] do
                    let first = run slowAppends script
                    Expect.isNonEmpty first.Log "something happened"
                    Expect.equal (run slowAppends script) first "identical"
            }

            test "the script's log and outcomes" {
                let result = run true script

                Expect.equal
                    result.Log
                    [
                        "chat/1#1 Prompted 1 ccc (2026-01-01T00:00:00.000000Z s0 by user:alice via tui)"
                        "chat/1#2 Interrupted 1 (2026-01-01T00:00:01.000000Z s4 by user:bob via web)"
                        "chat/1#3 Prompted 2 bb (2026-01-01T00:00:02.000000Z s1 by user:bob via web)"
                        "chat/1#4 Interrupted 2 (2026-01-01T00:00:07.000000Z s11 by user:carol via ide)"
                        "chat/1#5 Prompted 3 a (2026-01-01T00:00:08.000000Z s13 by user:bob via web)"
                        "chat/1#6 Closed (2026-01-01T00:00:09.000000Z s14 by user:alice via tui)"
                    ]
                    "the interrupt jumped the queue; the restart stopped what was queued"

                Expect.equal
                    result.Outcomes
                    [
                        "s0 Prompt ccc by user:alice via tui -> accepted, appended 1..1"
                        "s1 Prompt bb by user:bob via web -> accepted, appended 3..3"
                        "s2 Prompt  by user:carol via ide -> stopped"
                        "s0 Prompt ccc by user:alice via tui -> stopped"
                        "s4 Interrupt by user:bob via web -> accepted, appended 2..2"
                        "s5 Prompt a by user:alice via tui -> stopped"
                        "s7 Prompt ccc by user:carol via ide -> stopped"
                        "s1 Prompt bb by user:bob via web -> accepted, appended 3..3"
                        "s11 Interrupt by user:carol via ide -> accepted, appended 4..4"
                        "s12 Interrupt by user:carol via ide -> rejected, chat.idle: Nothing to interrupt."
                        "s13 Prompt a by user:bob via web -> accepted, appended 5..5"
                        "s14 Close by user:alice via tui -> accepted, appended 6..6"
                        "s15 Prompt bb by user:carol via ide -> rejected, chat.closed: The chat is closed."
                    ]
                    "outcomes"
            }

            testPropertyWithConfig
                config
                "any script gives the same run twice, with instant appends"
                (fun (steps: Step list) -> run false steps = run false steps)

            testPropertyWithConfig
                config
                "any script gives the same run twice, with slow appends"
                (fun (steps: Step list) -> run true steps = run true steps)
        ]
