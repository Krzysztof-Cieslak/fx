/// Background jobs (T143) end to end on the session host, with the script kernel in process, a scripted
/// model and the local shell: a cell starts a job, reads its output, waits for it and kills one; a turn
/// that ends waiting for a job resumes when it ends; a running turn hears of a job's end between steps;
/// a wait shares the cell's timeout; a host restart reports jobs lost.
module Fx.CodeAct.Tests.JobTests

open System
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Machines
open Fx.Machines.Host
open Fx.Sessions
open Fx.Prompt
open Fx.CodeAct
open Fx.CodeAct.Tests.Support

/// The session `s1` started and prompted once.
let private kickoff (f: Fixture) (text: string) : Task =
    task {
        do! start f "s1"
        do! prompt f "s1" "p1" text
    }

/// The notices of jobs' ends the session got, in order.
let private notices (f: Fixture) : (string * string) list =
    events f "s1"
    |> List.choose (fun r ->
        match r.Event with
        | Event.JobNotified input ->
            match input.Content with
            | InputContent.JobFinished(job, note) -> Some(job, note)
            | _ -> None
        | _ -> None)

/// Waits until the session was told that the job ended.
let private notified (f: Fixture) (job: string) : Task =
    until $"the notice of {job}" (fun () -> notices f |> List.exists (fun (j, _) -> j = job))

/// The jobs' starts and ends in the step journal.
let private jobEvents (f: Fixture) : StepEvent list =
    journal f "s1"
    |> List.map (fun r -> r.Event)
    |> List.filter (fun event ->
        match event with
        | StepEvent.JobStarted _
        | StepEvent.JobEnded _ -> true
        | _ -> false)

let private endOf (f: Fixture) (job: string) : JobEnd option =
    jobEvents f
    |> List.tryPick (fun event ->
        match event with
        | StepEvent.JobEnded ended when ended.Job = job -> Some ended.How
        | _ -> None)

/// A cell's line that starts a job (`j`) of this command, with these options.
let private startJob (command: string) (options: string) : string =
    let args =
        if options = "" then
            Json.quote command
        else
            Json.quote command + ", " + options

    "call j = jobs.start [" + args + "]\n"

/// A process that writes these chunks to standard output, then exits with 0.
let private writes (chunks: byte array list) : Fx.Effects.Proc =
    let gate = obj ()
    let mutable left = chunks

    {
        Pid = 1
        Write = fun _ _ -> Task.FromResult()
        CloseStdin = fun () -> Task.FromResult()
        Read =
            fun _ ->
                lock gate (fun () ->
                    match left with
                    | chunk :: rest ->
                        left <- rest
                        Task.FromResult(Some(Fx.Effects.Stdout chunk))
                    | [] -> Task.FromResult None)
        Wait = fun _ -> Task.FromResult 0
        Kill = fun () -> Task.FromResult()
    }

/// Live jobs over hands whose shell starts `writes chunks` for any command; what they told sessions.
let private liveOver (chunks: byte array list) =
    let told = Collections.Concurrent.ConcurrentQueue<JobNotice>()

    let jobs =
        LiveJobs<HandsEnv>(fun notice ->
            told.Enqueue notice
            Task.FromResult())

    let env: HandsEnv =
        {
            Clock = Fx.Effects.Live.SystemClock.clock
            FsRead = Fx.Effects.Live.LocalFs.read
            Fs = Fx.Effects.Live.LocalFs.write
            FsOps = Unchecked.defaultof<_>
            Shell =
                {
                    Run = fun _ _ -> failwith "no commands"
                    Spawn = fun _ _ -> Task.FromResult(Ok(writes chunks))
                }
            Scope = Fx.Effects.Scope.root
        }

    jobs, env, told

let private launch (id: string) : JobLaunch =
    {
        Session = "s1"
        Id = id
        Command = "make"
        Cwd = "/w"
        Env = Fx.Effects.Inherit []
        Stdin = None
    }

let private jobRef (id: string) : JobRef = { Session = "s1"; Id = id }

let private units =
    testList
        "jobs' output"
        [
            testTask "read from offsets without cutting a character; the tail by default" {
                let utf8 = Text.Encoding.UTF8
                // "é" is two bytes, split across two writes; "✓" three.
                let bytes = utf8.GetBytes "abé✓\nline two\nline three is longer\n"
                let chunks = [ bytes[0..2]; bytes[3..] ]
                let jobs, env, told = liveOver chunks
                let! started = Fx.Effects.Eff.run env (jobs.Jobs.Start(launch "j1"))
                Expect.isOk started "started"

                let! (info: JobInfo option) =
                    Fx.Effects.Eff.run
                        env
                        (jobs.Jobs.Wait
                            {
                                Job = jobRef "j1"
                                TimeoutMs = Some 10000
                            })

                Expect.equal (info |> Option.map _.State) (Some(JobState.Exited 0)) "ended"
                Expect.equal (info |> Option.map _.Bytes) (Some(int64 bytes.Length)) "every byte"

                let read from maxChars : JobOutput =
                    (Fx.Effects.Eff.run
                        env
                        (jobs.Jobs.Read
                            {
                                Job = jobRef "j1"
                                From = from
                                MaxChars = maxChars
                            }))
                        .GetAwaiter()
                        .GetResult()
                    |> Option.get

                let whole = read (Some 0L) 1000
                Expect.equal whole.Text "abé✓\nline two\nline three is longer\n" "all of it"
                Expect.equal whole.Next (int64 bytes.Length) "read on from the end"
                Expect.isFalse whole.Running "ended"

                // From inside "é": the character is not cut, the text starts after it.
                let inside = read (Some 3L) 1000
                Expect.equal inside.From 4L "the next character"
                Expect.equal inside.Text "✓\nline two\nline three is longer\n" "whole characters"

                // At most 16 characters, and `next` goes on from them.
                let first = read (Some 0L) 16
                Expect.equal first.Text.Length 16 "at most"
                let rest = read (Some first.Next) 1000
                Expect.equal (first.Text + rest.Text) whole.Text "in parts"

                let tail = read None 16
                Expect.isTrue (whole.Text.EndsWith tail.Text) "the end"
                Expect.isLessThanOrEqual tail.Text.Length 16 "the last characters by default"

                let notice = told |> Seq.exactlyOne
                Expect.equal notice.Job "j1" "the session is told"
                Expect.stringContains notice.Note "`make` exited with code 0" "how"
                Expect.stringContains notice.Note "line three is longer" "its last lines"
                do! jobs.Close()
            }

            testTask "a long output keeps its last megabyte: older offsets read from what is kept" {
                let chunk = Array.create 65536 (byte 'x')
                let chunks = List.replicate 48 chunk
                let jobs, env, _ = liveOver chunks
                let! _ = Fx.Effects.Eff.run env (jobs.Jobs.Start(launch "j1"))

                let! _ =
                    Fx.Effects.Eff.run
                        env
                        (jobs.Jobs.Wait
                            {
                                Job = jobRef "j1"
                                TimeoutMs = Some 10000
                            })

                let! (output: JobOutput option) =
                    Fx.Effects.Eff.run
                        env
                        (jobs.Jobs.Read
                            {
                                Job = jobRef "j1"
                                From = Some 0L
                                MaxChars = 100
                            })

                let output = Option.get output
                Expect.equal output.Bytes (48L * 65536L) "every byte counted"
                Expect.isGreaterThan output.From 0L "the start is gone"

                Expect.isLessThanOrEqual
                    (output.Bytes - output.From)
                    (2L * int64 Jobs.RetainBytes)
                    "at most twice the kept bytes"

                Expect.equal output.Text.Length 100 "bounded"
                do! jobs.Close()
            }

            testTask "an unknown job is none; a start the environment refuses is an error" {
                let jobs, env, _ = liveOver []
                let! (info: JobInfo option) = Fx.Effects.Eff.run env (jobs.Jobs.Info(jobRef "j9"))
                Expect.isNone info "unknown"
                let! refused = Fx.Effects.Eff.run env (Jobs.none.Start(launch "j1"))
                Expect.isError refused "no jobs here"
                do! jobs.Close()
            }
        ]

[<Tests>]
let tests =
    testList
        "jobs"
        [
            units

            testTask "a cell starts a job, waits for it and reads its output; the step journal has both ends" {
                do!
                    within
                        "jobs-wait"
                        []
                        (fun f ->
                            task {
                                let hold = f.Model.Hold 2

                                f.Model.AddAll
                                    [
                                        Say.cell (
                                            "// intent: build in the background\n"
                                            + startJob "echo one; echo two" "{\"name\": \"build\"}"
                                            + "call w = jobs.wait [\"j1\", {\"timeoutMs\": 30000}]\n"
                                            + "call o = jobs.output [\"j1\"]\n"
                                            + "o"
                                        )
                                        Say.cell "// intent: look again\ncall l = jobs.list\nl"
                                        Say.finish "Built"
                                    ]

                                do! kickoff f "Build it"
                                do! f.Model.Asked(2).WaitAsync(TimeSpan.FromSeconds 30.0)
                                // The job's end reaches the session before the next step's boundary.
                                do! notified f "j1"
                                hold.SetResult()
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (TurnResult.Done("Built", [], None)) "done"

                                let first = (receipts f "s1").Head
                                Expect.isFalse first.IsError (text first)
                                Expect.stringContains (text first) "one" "the job's output"
                                Expect.stringContains (text first) "two" "all of it"

                                Expect.equal
                                    first.Jobs
                                    [
                                        {
                                            Id = "j1"
                                            Name = "build"
                                            Command = "echo one; echo two"
                                        }
                                    ]
                                    "the receipt names the job it started, for people"

                                match jobEvents f with
                                | [ StepEvent.JobStarted started; StepEvent.JobEnded ended ] ->
                                    Expect.equal started.Job "j1" "numbered"
                                    Expect.equal started.Name "build" "named"
                                    Expect.equal started.Command "echo one; echo two" "its command"
                                    Expect.isSome started.Call "started by the cell's call"
                                    Expect.equal ended.How (JobEnd.Exited 0) "the wait saw it exit"
                                    Expect.isSome ended.Bytes "with its output's size"
                                | other -> failtest $"{other}"

                                // The model hears of it between steps, as a steer.
                                let _, note = notices f |> List.exactlyOne
                                Expect.stringContains note "exited with code 0" "how it ended"
                                Expect.stringContains note "two" "its last lines"

                                let input =
                                    events f "s1"
                                    |> List.pick (fun r ->
                                        match r.Event with
                                        | Event.JobNotified _ -> Some r.Seq
                                        | _ -> None)

                                // Delivered at the boundary of step 2 or 3, whichever came after it.
                                let step =
                                    events f "s1"
                                    |> List.pick (fun r ->
                                        match r.Event with
                                        | Event.InputsDelivered(1, step, ids) when List.contains input ids ->
                                            Some step
                                        | _ -> None)

                                Expect.stringContains
                                    (requestText f.Model.Requests[step - 1])
                                    "Job j1 finished: `echo one; echo two` exited with code 0"
                                    "the model read it"

                                do! expectReplays f "s1"
                            })
                        ()
            }

            testTask "a turn that ends waiting for a job resumes with its notice when the job ends" {
                do!
                    within
                        "jobs-waiting"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell (
                                            "// intent: watch the build, then wait for it outside the turn\n"
                                            + startJob "sleep 1; echo built" ""
                                            + "finish {\"kind\":\"waiting\",\"jobs\":[\"j1\"],\"note\":\"the build\"}"
                                        )
                                        Say.finish "The build is done"
                                    ]

                                do! kickoff f "Build it"
                                let! first = ended f "s1" 1

                                Expect.equal
                                    (completed first)
                                    (TurnResult.Waiting([ "j1" ], "the build"))
                                    "waiting for the job"

                                let! second = ended f "s1" 2

                                Expect.equal
                                    (completed second)
                                    (TurnResult.Done("The build is done", [], None))
                                    "resumed"

                                let started =
                                    events f "s1"
                                    |> List.pick (fun r ->
                                        match r.Event with
                                        | Event.TurnStarted(2, ids, _, _) -> Some ids
                                        | _ -> None)

                                Expect.equal started.Length 1 "the notice is the turn's input"

                                let request = requestText (List.last f.Model.Requests)

                                Expect.stringContains
                                    request
                                    "Job j1 finished: `sleep 1; echo built` exited with code 0"
                                    "how"

                                Expect.stringContains request "built" "its last lines"

                                match endOf f "j1" with
                                | Some(JobEnd.Reported note) ->
                                    Expect.stringContains note "exited with code 0" "the notice"
                                | other -> failtest $"{other}"

                                do! expectReplays f "s1"
                            })
                        ()
            }

            testTask "a job killed by a cell: its status says so, and the session gets no notice" {
                do!
                    within
                        "jobs-kill"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell (
                                            "// intent: start and stop a server\n"
                                            + startJob "sleep 30" ""
                                            + "call s = jobs.status [\"j1\"]\n"
                                            + "call k = jobs.kill [\"j1\"]\n"
                                            + "k"
                                        )
                                        Say.cell "// intent: an unknown job\ncall s = jobs.status [\"j9\"]"
                                        Say.finish "Stopped it"
                                    ]

                                do! kickoff f "Run and stop"
                                let! ending = ended f "s1" 1
                                completed ending |> ignore
                                let rs = receipts f "s1"
                                Expect.stringContains (text rs[0]) "killed" (text rs[0])
                                Expect.equal (endOf f "j1") (Some JobEnd.Killed) "journaled"
                                Expect.isTrue rs[1].IsError "an unknown job fails"
                                Expect.stringContains (text rs[1]) "no background job \"j9\"" (text rs[1])
                                do! Task.Delay 200
                                Expect.isEmpty (notices f) "a job the agent killed is not news"
                            })
                        ()
            }

            testTask "a wait shares the cell's timeout: cut at what is left, the next refused" {
                do!
                    within
                        "jobs-timeout"
                        [ "codeact.cell.timeoutMs", Json.int 1500 ]
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell (
                                            "// intent: wait for a long job\n"
                                            + startJob "sleep 30" ""
                                            + "call w = jobs.wait [\"j1\"]\n"
                                            // A timer may fire a little early: what is left of the
                                            // timeout then bounds the next wait, and the one after is
                                            // refused.
                                            + "call x = jobs.wait [\"j1\", {\"timeoutMs\": 100}]\n"
                                            + "call y = jobs.wait [\"j1\", {\"timeoutMs\": 100}]"
                                        )
                                        Say.cell "// intent: stop it\ncall k = jobs.kill [\"j1\"]\nk"
                                        Say.finish "Stopped"
                                    ]

                                do! kickoff f "Wait for it"
                                let! ending = ended f "s1" 1
                                completed ending |> ignore
                                let shown = text (receipts f "s1").Head
                                Expect.stringContains shown "already ran for its whole timeout (1500 ms)" "refused"
                                Expect.stringContains shown "Declared: j, w" "the first wait returned"

                                let unassigned =
                                    shown.Split('\n')
                                    |> Array.find (fun line ->
                                        line.StartsWith "Not assigned (the cell stopped first):")

                                Expect.isTrue
                                    (unassigned.EndsWith " y" || unassigned.EndsWith ", y")
                                    "the last refused"
                            })
                        ()
            }

            testTask "finish waits only for the session's running jobs" {
                do!
                    within
                        "jobs-ensure"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell
                                            "// intent: wait for nothing\nfinish {\"kind\":\"waiting\",\"jobs\":[\"j7\"],\"note\":\"x\"}"
                                        Say.cell (
                                            "// intent: a job that ended\n"
                                            + startJob "echo quick" ""
                                            + "call w = jobs.wait [\"j1\", {\"timeoutMs\": 30000}]\n"
                                            + "finish {\"kind\":\"waiting\",\"jobs\":[\"j1\"],\"note\":\"x\"}"
                                        )
                                        Say.finish "Nothing to wait for"
                                        // The job's notice may come after the turn: it starts one.
                                        Say.finish "Noted"
                                    ]

                                do! kickoff f "Wait"
                                let! ending = ended f "s1" 1
                                completed ending |> ignore
                                let rs = receipts f "s1"
                                Expect.stringContains (text rs[0]) "no background job \"j7\"" (text rs[0])
                                Expect.stringContains (text rs[1]) "job j1 ended already" (text rs[1])
                            })
                        ()
            }

            testTask "a host restart: a running job is lost, and the next turn is told" {
                do!
                    within
                        "jobs-lost"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell ("// intent: serve\n" + startJob "sleep 30" "")
                                        Say.finish "Serving"
                                    ]

                                do! kickoff f "Serve"
                                let! _ = ended f "s1" 1
                                do! restart f

                                f.Model.AddAll [ Say.finish "Noted" ]
                                do! prompt f "s1" "p2" "Is it still up?"
                                let! second = ended f "s1" 2
                                completed second |> ignore

                                Expect.equal (endOf f "j1") (Some JobEnd.Lost) "journaled lost"

                                Expect.stringContains
                                    (requestText (List.last f.Model.Requests))
                                    "background jobs were lost when the host stopped, with their output: j1 (`sleep 30`)"
                                    "the model is told"

                                Expect.isEmpty (notices f) "the job's end was never news: it was lost"
                            })
                        ()
            }

            testTask "a host restart while a turn waits for a job: the job is reported lost and the session goes on" {
                do!
                    within
                        "jobs-lost-waiting"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell (
                                            "// intent: build, and wait for it\n"
                                            + startJob "sleep 30" ""
                                            + "finish {\"kind\":\"waiting\",\"jobs\":[\"j1\"],\"note\":\"the build\"}"
                                        )
                                        Say.finish "Will build again"
                                    ]

                                do! kickoff f "Build"
                                let! _ = ended f "s1" 1
                                do! restart f

                                // What a host that starts does for sessions that wait for jobs.
                                let! state = state f "s1"
                                Expect.equal state.AwaitingJobs [ "j1" ] "waiting"

                                for notice in JobNotices.lostAll state do
                                    let! _ =
                                        run
                                            f
                                            (Fx.Sessions.Host.SessionHost.send
                                                f.Host
                                                notice.Session
                                                (JobNotices.key notice.Job)
                                                JobNotices.by
                                                (JobNotices.command notice))

                                    ()

                                let! second = ended f "s1" 2
                                completed second |> ignore
                                let _, note = notices f |> List.exactlyOne
                                Expect.stringContains note "lost" "the notice"

                                match endOf f "j1" with
                                | Some(JobEnd.Reported note) ->
                                    Expect.stringContains note "lost" "journaled with its notice"
                                | other -> failtest $"{other}"

                                let request = requestText (List.last f.Model.Requests)
                                Expect.stringContains request "Job j1 finished: lost" "the model reads it"

                                Expect.isFalse (request.Contains "background jobs were lost") "no second word of it"
                            })
                        ()
            }
        ]
