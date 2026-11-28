/// Recorded sessions with background jobs (T143) replay exactly: the hands' jobs are journaled at their
/// level (`Jobs.Start`, `Jobs.Wait`, …), so replay answers what a cell asked of them without a process, and
/// the jobs' ends reach turns only through the session's log.
module Fx.Replay.Tests.JobTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Sessions
open Fx.CodeAct
open Fx.Replay
open Fx.Replay.Tests.Support
open Fx.Replay.Tests.Harness

let private completed (ending: TurnEnd) =
    match ending with
    | TurnEnd.Completed _ -> ()
    | other -> failtest $"the turn did not complete: {other}"

/// The notices of jobs' ends the session got.
let private notified (f: Fixture) (job: string) : Task =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 30.0

        let found () =
            events f "s1"
            |> List.exists (fun r ->
                match r.Event with
                | Event.JobNotified input ->
                    match input.Content with
                    | InputContent.JobFinished(id, _) -> id = job
                    | _ -> false
                | _ -> false)

        while not (found ()) do
            if DateTime.UtcNow > deadline then
                failtest $"no notice of {job}"

            do! Task.Delay 10
    }

/// The operations a recording journaled, in order.
let private ops (f: Fixture) : string list =
    (SessionReplay.segments f.Recording "s1" CancellationToken.None).GetAwaiter().GetResult()
    |> List.collect (fun segment -> segment.Entries |> List.map (fun (entry, _) -> entry.Op))

/// A cell starts a job, waits for it and reads its output; its notice reaches the next step.
let private waited (f: Fixture) : Task<string> =
    task {
        let hold = f.Model.Hold 2

        f.Model.AddAll
            [
                Say.cell
                    "// intent: build in the background\ncall j = jobs.start [\"job make build\", {\"name\": \"build\"}]\ncall w = jobs.wait [\"j1\", {\"timeoutMs\": 30000}]\ncall o = jobs.output [\"j1\"]\no"
                Say.cell "// intent: look again\ncall s = jobs.status [\"j1\"]\ncall l = jobs.list\nl"
                Say.finish "Built"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Build it"
        do! f.Model.Asked(2).WaitAsync(TimeSpan.FromSeconds 30.0)
        do! notified f "j1"
        hold.SetResult()
        let! ending = ended f "s1" 1
        completed ending
        return "s1"
    }

/// A turn ends waiting for a job; the job's end starts the next turn.
let private resumed (f: Fixture) : Task<string> =
    task {
        f.Model.AddAll
            [
                Say.cell
                    "// intent: watch the build\ncall j = jobs.start [\"job held build\"]\nfinish {\"kind\":\"waiting\",\"jobs\":[\"j1\"],\"note\":\"the build\"}"
                Say.finish "The build is done"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Build it"
        let! first = ended f "s1" 1
        completed first
        f.JobProcs.Release "job held build"
        let! second = ended f "s1" 2
        completed second
        return "s1"
    }

/// A job runs when the host stops: the next host's turn is told it was lost.
let private lost (f: Fixture) : Task<string> =
    task {
        f.Model.AddAll
            [
                Say.cell "// intent: serve\ncall j = jobs.start [\"job held server\"]\ncall k = jobs.status [\"j1\"]\nk"
                Say.finish "Serving"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Serve"
        let! first = ended f "s1" 1
        completed first
        do! restart f
        f.Model.AddAll [ Say.finish "Noted" ]
        do! prompt f "s1" "p2" "Is it up?"
        let! second = ended f "s1" 2
        completed second
        return "s1"
    }

[<Tests>]
let tests =
    testList
        "recorded jobs replay"
        [
            recordsAndReplays "a job started, waited for and read" (fun () -> create []) waited (fun f report ->
                Expect.equal report.Requests f.Model.Requests.Length "every request"
                let ops = ops f

                for op in [ Ops.JobStart; Ops.JobWait; Ops.JobRead; Ops.JobInfo; Ops.JobList ] do
                    Expect.contains ops op "journaled at the jobs' level"

                let journal =
                    f.Store.Events(StepJournal.stream "s1")
                    |> List.map (Fx.Store.EventLog.decode StepJournal.schema)
                    |> List.map (fun r -> r.Event)

                Expect.exists
                    journal
                    (fun e ->
                        match e with
                        | StepEvent.JobEnded ended -> ended.How = JobEnd.Exited 0
                        | _ -> false)
                    "its end, as the wait saw it")

            recordsAndReplays
                "a turn waiting for a job resumes when it ends"
                (fun () -> create [])
                resumed
                (fun _ report -> Expect.equal report.Turns 2 "two turns")

            recordsAndReplays
                "a job lost with the host: the next turn is told"
                (fun () -> create [])
                lost
                (fun f report ->
                    Expect.equal report.Segments 2 "two host processes"

                    Expect.stringContains
                        (Json.serialize (Fx.Models.ChatRequest.codec.Encode(List.last f.Model.Requests)))
                        "were lost when the host stopped"
                        "the note")
        ]
