/// Recorded CodeAct sessions replay exactly: both action surfaces, a kernel through the shell,
/// steering, interrupts (a cell stopped, a cell killed), questions answered into the next turn, model
/// retries, and a host stopped mid-cell whose turn people decide about after a restart.
module Fx.Replay.Tests.SessionTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Models
open Fx.Sessions
open Fx.Sessions.Host
open Fx.CodeAct
open Fx.Replay
open Fx.Replay.Tests.Support
open Fx.Replay.Tests.Harness

/// Fails unless a turn completed.
let completed (ending: TurnEnd) =
    match ending with
    | TurnEnd.Completed _ -> ()
    | other -> failtest $"the turn did not complete: {other}"

let hybrid = [ "agent.actionSurface", Json.String "hybrid" ]

/// Waits until a cell wrote `started.txt`.
let started (f: Fixture) : Task =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 30.0
        let mutable found = false

        while not found do
            match! f.Fs.FsRead.Stat (file "started.txt") CancellationToken.None with
            | Ok _ -> found <- true
            | Error _ ->
                if DateTime.UtcNow > deadline then
                    failtest "the cell did not start"

                do! Task.Delay 10
    }

/// A cells-only turn: cells write, read and run a command, and one finishes.
let cellsOnly (f: Fixture) : Task<string> =
    task {
        f.Model.AddAll
            [
                Say.cell
                    "// intent: write\ncall w = fs.write [\"notes.md\", \"# Notes\\nfirst\\n\"]\nprint wrote the notes\ncall r = shell.run [\"make test\"]\nr"
                Say.cell "// intent: read\ncall n = fs.read [\"notes.md\"]\nn"
                Say.finish "Wrote the notes"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Write the notes"
        let! ending = ended f "s1" 1
        completed ending
        return "s1"
    }

/// A hybrid turn: direct tools read, edit and run a command; a cell reads back and one finishes.
let hybridTurn (f: Fixture) : Task<string> =
    task {
        f.Fs.AddText(file "notes.md", "# Notes\nfirst\n")

        f.Model.AddAll
            [
                Say.tool "read" [ "path", Json.String "notes.md" ]
                Say.tool
                    "edit"
                    [
                        "path", Json.String "notes.md"
                        "old", Json.String "first"
                        "new", Json.String "first\nsecond"
                    ]
                Say.tool "bash" [ "command", Json.String "make test" ]
                Say.cell "// intent: read it back\ncall n = fs.read [\"notes.md\"]\nprint checked\nn"
                Say.finish "Added a line"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Add a line to the notes"
        let! ending = ended f "s1" 1
        completed ending
        return "s1"
    }

/// A steer sent while the model answers reaches it at the next step boundary.
let steered (f: Fixture) : Task<string> =
    task {
        let hold = f.Model.Hold 2

        f.Model.AddAll
            [
                Say.cell "// intent: one\nprint one"
                Say.cell "// intent: two\nprint two"
                Say.finish "Done with both"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Count"
        do! f.Model.Asked(2).WaitAsync(TimeSpan.FromSeconds 30.0)
        do! prompt f "s1" "p2" "Also say three"
        hold.SetResult()
        let! ending = ended f "s1" 1
        completed ending
        return "s1"
    }

/// An interrupt stops a running cell; the kernel and its names live on into the next turn.
let interrupted (f: Fixture) : Task<string> =
    task {
        f.Model.AddAll
            [
                Say.cell "// intent: wait\nlet x = 1\ncall w = fs.write [\"started.txt\", \"\"]\nsleep"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Wait"
        do! started f
        let! _ = send f "s1" "stop" Command.Interrupt
        let! ending = ended f "s1" 1
        Expect.equal ending TurnEnd.Interrupted "interrupted"

        f.Model.AddAll [ Say.cell "// intent: check\nx"; Say.finish "x is still there" ]
        do! prompt f "s1" "p2" "Go on"
        let! ending = ended f "s1" 2
        completed ending
        return "s1"
    }

/// A cell that cannot be interrupted is killed; the next cell starts a new kernel.
let killed (f: Fixture) : Task<string> =
    task {
        f.Model.AddAll
            [
                Say.cell "// intent: spin\nlet y = 2\ncall w = fs.write [\"started.txt\", \"\"]\nspin"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Spin"
        do! started f
        let! _ = send f "s1" "stop" Command.Interrupt
        let! ending = ended f "s1" 1
        Expect.equal ending TurnEnd.Interrupted "interrupted"

        f.Model.AddAll [ Say.cell "// intent: check\ny"; Say.finish "y is gone" ]
        do! prompt f "s1" "p2" "Go on"
        let! ending = ended f "s1" 2
        completed ending
        return "s1"
    }

/// Text replies: a nudge, then a question whose answer starts the next turn.
let questioned (f: Fixture) : Task<string> =
    task {
        f.Model.AddAll [ Say.text "Which file?"; Say.text "Which file should I change?" ]
        do! start f "s1"
        do! prompt f "s1" "p1" "Change the file"
        let! ending = ended f "s1" 1
        Expect.equal ending (TurnEnd.Completed(TurnResult.NeedInput [ "q-1-2" ])) "a question"
        f.Model.AddAll [ Say.finish "Changed it" ]
        let! _ = send f "s1" "answer" (Command.Answer("q-1-2", Json.String "greeting.txt"))
        let! ending = ended f "s1" 2
        completed ending
        return "s1"
    }

/// Retryable model errors are retried after a delay.
let retried (f: Fixture) : Task<string> =
    task {
        f.Model.AddAll
            [
                Say.failed (
                    ChatError.RateLimited
                        {
                            RetryAfter = None
                            Message = "slow down"
                        }
                )
                Say.failed (ChatError.Overloaded { RetryAfter = None; Message = "busy" })
                Say.finish "Done"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Do it"
        let! ending = ended f "s1" 1
        completed ending
        return "s1"
    }

/// Commands that hang until cancelled the first time `deploy` runs (a host that stops mid-command).
let hangsOnce () : (Fx.Effects.Command -> CancellationToken -> Task<Result<Output, ShellError>>) * Task =
    let deploying =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let deploys = ref 0

    let run (command: Fx.Effects.Command) (token: CancellationToken) =
        task {
            let first =
                lock deploying (fun () ->
                    deploys.Value <- deploys.Value + 1
                    deploys.Value = 1)

            if first then
                deploying.TrySetResult() |> ignore
                let never = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                use _ = token.Register(fun () -> never.TrySetCanceled token |> ignore)
                do! never.Task

            return! ran command token
        }

    run, deploying.Task

/// The host stops mid-cell (in a command); a new host recovers the turn, which pauses on a decision; the
/// answer (`rerun`) runs the cell again and the turn finishes; a later turn reads the value back.
let recovered (f: Fixture) (deploying: Task) : Task<string> =
    task {
        f.Model.AddAll
            [
                Say.cell
                    "// intent: deploy\ncall w = fs.write [\"site.txt\", \"v2\"]\ncall r = shell.run [\"deploy\"]\nr"
                Say.finish "Deployed"
                Say.cell "// intent: read it back\ncall v = recall [\"call-1\"]\nv"
                Say.finish "Read"
            ]

        do! start f "s1"
        do! prompt f "s1" "p1" "Deploy"
        do! deploying.WaitAsync(TimeSpan.FromSeconds 30.0)
        do! restart f
        let! decision = opened f "s1" "unknown-t1-s1-1"
        Expect.equal decision.Request.Kind "decision.outcomeUnknown" "a decision"

        let! _ =
            send
                f
                "s1"
                "a1"
                (Command.Answer(
                    "unknown-t1-s1-1",
                    RequestKind.answer
                        Recovery.kind
                        {
                            Option = "rerun"
                            Note = Some "try again"
                        }
                ))

        let! ending = ended f "s1" 1
        completed ending
        do! prompt f "s1" "p2" "Read it back"
        let! ending = ended f "s1" 2
        completed ending
        return "s1"
    }

[<Tests>]
let tests =
    testList
        "recorded sessions replay"
        [
            recordsAndReplays "a cells-only turn" (fun () -> create []) cellsOnly (fun f report ->
                Expect.equal report.Turns 1 "one turn"
                Expect.equal report.Requests f.Model.Requests.Length "every request")

            recordsAndReplays "a hybrid turn" (fun () -> create hybrid) hybridTurn (fun f report ->
                Expect.equal report.Requests f.Model.Requests.Length "every request")

            recordsAndReplays "a steer delivered at the next boundary" (fun () -> create []) steered ignore2

            recordsAndReplays
                "an interrupted cell; the kernel lives on"
                (fun () -> create [])
                interrupted
                (fun _ report -> Expect.equal report.Turns 2 "two turns")

            recordsAndReplays "a killed cell; a new kernel" (fun () -> create []) killed ignore2

            recordsAndReplays "a question answered into the next turn" (fun () -> create []) questioned ignore2

            recordsAndReplays "model errors retried after a delay" (fun () -> create []) retried ignore2

            testTask "a host stopped mid-cell, a decision, a rerun and recall" {
                let commands, deploying = hangsOnce ()
                let f = createWith [] commands (Inherit [])

                try
                    let! session = (recovered f deploying).WaitAsync(TimeSpan.FromSeconds 120.0)
                    do! close f
                    let! report = replay f.Recording session
                    dump f session report
                    expectMatched report
                    Expect.equal report.Segments 2 "two host processes"
                    Expect.equal report.Turns 3 "a turn, its recovery, another turn"
                finally
                    (f :> IDisposable).Dispose()
            }
        ]
