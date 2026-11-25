/// Crash recovery end to end (spec §5): a CodeAct session on the real session host, whose host process
/// is stopped as a crash would stop it (the turn stops where it is, kernels die) and started again on
/// the same stores. A call in flight becomes a decision with evidence; nothing runs until someone
/// answers; each answer goes on as it says; recovering again opens nothing new; kept values come back
/// after kernel restarts; and the recorded logs still replay into the requests sent.
module Fx.CodeAct.Tests.CrashTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Envd
open Fx.Store
open Fx.Sessions
open Fx.Prompt
open Fx.CodeAct
open Fx.CodeAct.Tests.Support

/// A command (`deploy`) that hangs the first time it runs, until its call is cancelled, and succeeds
/// after: the call in flight when the host stops.
[<Sealed>]
type private Gate() =
    let gate = obj ()
    let mutable runs = 0
    member val Started = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    member _.Runs = lock gate (fun () -> runs)

    member this.Wrap(shell: Shell) : Shell =
        { shell with
            Run =
                fun command token ->
                    match command.Spec.Program with
                    | Program.Script "deploy" ->
                        task {
                            let first =
                                lock gate (fun () ->
                                    runs <- runs + 1
                                    runs = 1)

                            if first then
                                this.Started.TrySetResult() |> ignore
                                do! Task.Delay(Timeout.Infinite, token)

                            return Ok(Fx.Effects.Fakes.ScriptedShell.output 0 "deployed\n" "")
                        }
                    | _ -> shell.Run command token
        }

let private snapshotsOn = [ "codeact.journal.snapshots", Json.Bool true ]

/// A fixture whose `deploy` command is gated.
let private gated (name: string) (selections: (string * Json) list) : Fixture * Gate =
    let gate = Gate()

    let hands root workspace cleanup =
        let hands = localHands (fun _ -> scriptKernel kernelOptions) root workspace cleanup

        { hands with
            Env =
                { hands.Env with
                    Shell = gate.Wrap hands.Env.Shell
                }
        }

    createOn name (snapshotsOn @ selections) hands None (fun _ -> None), gate

/// Runs a test on a gated fixture, and disposes it.
let private withGate (name: string) (body: Fixture -> Gate -> Task<unit>) : Task =
    task {
        let f, gate = gated name []
        use f = f
        do! (body f gate).WaitAsync(TimeSpan.FromSeconds 180.0)
    }

/// Runs a test on a fixture, and disposes it.
let private withFixture (name: string) (selections: (string * Json) list) (body: Fixture -> Task<unit>) : Task =
    task {
        use f = create name selections
        do! (body f).WaitAsync(TimeSpan.FromSeconds 180.0)
    }

/// The cell that is in flight when the host stops.
let private deploying =
    "// intent: deploy the site\nprint deploying\ncall w = fs.write [\"site.txt\", \"v2\"]\ncall r = shell.run [\"deploy\"]\nr"

let private journal (f: Fixture) (session: string) : Recorded<StepEvent> list =
    f.Store.Events(StepJournal.stream session)
    |> List.map (EventLog.decode StepJournal.schema)

/// Waits until a condition holds.
let private until (what: string) (condition: unit -> bool) : Task =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 60.0

        while not (condition ()) do
            if DateTime.UtcNow > deadline then
                failtest $"timed out waiting: {what}"

            do! Task.Delay 10
    }

/// Waits until the session has the open request.
let private opened (f: Fixture) (session: string) (id: string) : Task<PendingRequest> =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 60.0
        let mutable found = None

        while found.IsNone do
            let! state = state f session
            found <- state.Requests |> List.tryFind (fun r -> r.Request.Id = id)

            if found.IsNone then
                if DateTime.UtcNow > deadline then
                    failtest $"no request {id}: {state}"

                do! Task.Delay 10

        return found.Value
    }

let private cardOf (request: PendingRequest) : OutcomeUnknown =
    match Recovery.payload.Decode request.Request.Payload with
    | Ok card -> card
    | Error issues -> failtest (Issue.render issues)

let private answer (option: string) (note: string option) : Json =
    RequestKind.answer Recovery.kind { Option = option; Note = note }

let private requestsOpened (f: Fixture) (session: string) =
    events f session
    |> List.filter (fun r ->
        match r.Event with
        | Event.RequestOpened _ -> true
        | _ -> false)
    |> List.length

/// Runs the gated cell, crashes while its `deploy` runs, and restarts: gives the decision opened.
let private crash (f: Fixture) (gate: Gate) : Task<PendingRequest> =
    task {
        do! start f "s1"
        do! prompt f "s1" "p1" "Deploy the site"
        do! gate.Started.Task.WaitAsync(TimeSpan.FromSeconds 60.0)

        do!
            until "the cell's output is journaled" (fun () ->
                journal f "s1"
                |> List.exists (fun r ->
                    match r.Event with
                    | StepEvent.Printed _ -> true
                    | _ -> false))

        do! restart f
        // The session's actor starts on first use: recovery runs then.
        let! _ = state f "s1"
        return! opened f "s1" "unknown-t1-s1-1"
    }

/// The receipt of the call in doubt (call-1).
let private receiptOfCall (f: Fixture) (id: string) : Receipt =
    receipts f "s1" |> List.find (fun r -> r.Call = Some id)

let private lastToolResult (f: Fixture) =
    f.Model.Requests
    |> List.last
    |> fun r -> r.Messages |> List.collect (fun m -> m.Parts)
    |> List.choose (fun part ->
        match part with
        | Fx.Models.Part.ToolResult result -> Some result
        | _ -> None)
    |> List.last

[<Tests>]
let tests =
    testList
        "CodeAct crash recovery"
        [
            testTask "a cell in flight becomes a decision with evidence; nothing runs until someone answers" {
                do!
                    withGate "outcome-unknown" (fun f gate ->
                        task {

                            f.Model.AddAll [ Say.cell deploying; Say.finish "Deployed" ]
                            let! (request: PendingRequest) = crash f gate

                            Expect.equal request.Request.Kind Recovery.KindId "a decision about the call"
                            let card = cardOf request
                            Expect.equal card.Call.Cell (Some "t1-s1-1") "the cell"
                            Expect.equal card.Call.Intent (Some "deploy the site") "its intent"
                            Expect.stringContains card.Call.Action "shell.run [\"deploy\"]" "its code"
                            Expect.stringContains card.Evidence.Output "deploying" "what it printed"

                            Expect.equal
                                (card.Evidence.HostCalls |> List.map (fun h -> h.Binding, h.Ended.IsSome))
                                [ "fs.write", true; "shell.run", false ]
                                "its host calls: one ended, one was running"

                            Expect.equal
                                (card.Evidence.Changes |> Option.map (List.map (fun c -> string c.Kind, c.Path)))
                                (Some [ "added", "site.txt" ])
                                "what changed in the workspace since the step began"

                            let! paused = state f "s1"
                            Expect.isSome paused.Running "the turn pauses"
                            Expect.equal f.Model.Requests.Length 1 "no model call since"
                            Expect.equal gate.Runs 1 "nothing ran again"

                            // Recovering again opens nothing new.
                            do! restart f
                            let! _ = state f "s1"
                            let! (again: PendingRequest) = opened f "s1" "unknown-t1-s1-1"
                            Expect.equal again.Seq request.Seq "the same request"
                            Expect.equal (requestsOpened f "s1") 1 "one decision however often the host restarts"

                            let! outcome =
                                send
                                    f
                                    "s1"
                                    "a1"
                                    (Command.Answer("unknown-t1-s1-1", answer "completed" (Some "it is live")))

                            Expect.isTrue
                                (match outcome with
                                 | Accepted _ -> true
                                 | _ -> false)
                                "answered"

                            let! ending = ended f "s1" 1

                            Expect.equal
                                (completed ending)
                                (TurnResult.Done("Deployed", [], None))
                                "the turn went on and finished"

                            Expect.equal gate.Runs 1 "deploy ran once"

                            let decided = receiptOfCall f "call-1"
                            Expect.isFalse decided.IsError "completed"

                            Expect.stringContains
                                (text decided)
                                "The user decided that it completed. Their note: it is live"
                                "the receipt"

                            Expect.stringContains (text decided) "added site.txt" "with the evidence"

                            let result = lastToolResult f
                            Expect.equal result.CallId "call-1" "the model sees it as the call's result"

                            // The model saw the decision after its call, then finished.
                            Expect.equal f.Model.Requests.Length 2 "one more call"
                            do! expectReplays f "s1"
                        })
            }

            testTask "each answer goes on as it says" {
                for option, expected, isError in
                    [
                        "failed", "decided that it failed", true
                        "inspect", "inspect what it did", true
                    ] do
                    do!
                        withGate $"answer-{option}" (fun f gate ->
                            task {
                                f.Model.AddAll [ Say.cell deploying; Say.finish "Done" ]
                                let! _ = crash f gate
                                let! _ = send f "s1" "a1" (Command.Answer("unknown-t1-s1-1", answer option None))
                                let! ending = ended f "s1" 1
                                completed ending |> ignore
                                let decided = receiptOfCall f "call-1"
                                Expect.equal decided.IsError isError $"{option}: error"
                                Expect.stringContains (text decided) expected option
                                Expect.equal gate.Runs 1 $"{option}: nothing ran again"
                                do! expectReplays f "s1"
                            })
            }

            testTask "a withdrawn decision says nobody decided" {
                do!
                    withGate "withdrawn" (fun f gate ->
                        task {
                            f.Model.AddAll [ Say.cell deploying; Say.finish "Done" ]
                            let! _ = crash f gate
                            let! _ = send f "s1" "c1" (Command.CancelRequest("unknown-t1-s1-1", "not now"))
                            let! ending = ended f "s1" 1
                            completed ending |> ignore
                            let decided = receiptOfCall f "call-1"
                            Expect.isTrue decided.IsError "an error"
                            Expect.stringContains (text decided) "nobody decided what happened (not now)" "why"
                        })
            }

            testTask "run it again: the call runs anew, and its new result is its receipt" {
                do!
                    withGate "rerun" (fun f gate ->
                        task {
                            f.Model.AddAll [ Say.cell deploying; Say.finish "Deployed again" ]
                            let! _ = crash f gate
                            let! _ = send f "s1" "a1" (Command.Answer("unknown-t1-s1-1", answer "rerun" None))
                            let! ending = ended f "s1" 1
                            Expect.equal (completed ending) (TurnResult.Done("Deployed again", [], None)) "finished"
                            Expect.equal gate.Runs 2 "deploy ran again"

                            let decided = receiptOfCall f "call-1"
                            Expect.isFalse decided.IsError "it worked this time"
                            Expect.stringContains (text decided) "chose to run it again" "said so"
                            Expect.stringContains (text decided) "deployed" "its new result"
                            Expect.stringContains (text decided) "Out[\"t1-s1-1-2\"]" "a cell of its own"

                            let attempts =
                                journal f "s1"
                                |> List.choose (fun r ->
                                    match r.Event with
                                    | StepEvent.CallStarted s -> Some(s.Call.Attempt, s.Cell)
                                    | _ -> None)

                            Expect.equal
                                attempts
                                [ 1, Some "t1-s1-1"; 2, Some "t1-s1-1-2"; 1, Some "t1-s2-1" ]
                                "both attempts journaled before they ran"

                            do! expectReplays f "s1"
                        })
            }

            testTask "calls after the call in doubt did not run, and say so" {
                do!
                    withGate "not-run" (fun f gate ->
                        task {

                            f.Model.AddAll
                                [
                                    Reply.Parts(
                                        [
                                            Say.call "cell" (Json.Object [ "code", Json.String deploying ])
                                            Say.call
                                                "cell"
                                                (Json.Object [ "code", Json.String "// intent: after\nprint after" ])
                                        ],
                                        Fx.Models.StopReason.ToolUse
                                    )
                                    Say.finish "Done"
                                ]

                            let! _ = crash f gate
                            let! _ = send f "s1" "a1" (Command.Answer("unknown-t1-s1-1", answer "failed" None))
                            let! _ = ended f "s1" 1
                            let later = receiptOfCall f "call-2"
                            Expect.stringContains (text later) "the host stopped before this call started" "not run"
                            Expect.isTrue later.IsError "an error"
                            do! expectReplays f "s1"
                        })
            }

            testTask "a host stopped during a model call abandons the turn: nothing is in doubt" {
                do!
                    withFixture "abandoned" [] (fun f ->
                        task {
                            let hold = f.Model.Hold 1
                            f.Model.AddAll [ Say.finish "never" ]
                            do! start f "s1"
                            do! prompt f "s1" "p1" "Think"
                            do! f.Model.Asked(1).WaitAsync(TimeSpan.FromSeconds 30.0)
                            do! restart f
                            hold.SetResult()
                            let! _ = state f "s1"
                            let! ending = ended f "s1" 1

                            match ending with
                            | TurnEnd.Abandoned _ -> ()
                            | other -> failtest $"{other}"

                            let! state = state f "s1"
                            Expect.isTrue state.Held "held: nothing auto-resumes"
                            Expect.isEmpty state.Requests "no decision"
                        })
            }

            testTask
                "after the host restarted, a new kernel gets earlier cells' values back and fx.recall gives any kept one" {
                do!
                    withFixture "recall" [ "values.inlineMaxBytes", Json.int 64 ] (fun f ->
                        task {

                            f.Model.AddAll
                                [
                                    Say.cell "// intent: count\nvalue {\"rows\":3}"
                                    Say.cell
                                        "// intent: a big one\nvalue [\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"]"
                                    Say.cell "// intent: keep\nlet k = 1"
                                    Say.finish "kept"
                                ]

                            do! start f "s1"
                            do! prompt f "s1" "p1" "Count"
                            let! _ = ended f "s1" 1

                            let kept =
                                journal f "s1"
                                |> List.choose (fun r ->
                                    match r.Event with
                                    | StepEvent.ValueKept k -> Some(k.Cell, k.Value)
                                    | _ -> None)

                            match kept with
                            | [ "t1-s1-1", KeptJson.Inline _; "t1-s2-1", KeptJson.Stored _ ] -> ()
                            | other -> failtest $"{other}"

                            do! restart f

                            f.Model.AddAll
                                [
                                    Say.cell "// intent: use them\nout a = t1-s1-1\nout b = t1-s2-1\nb"
                                    Say.cell "// intent: by call\ncall r = recall [\"call-1\"]\nr"
                                    Say.cell "// intent: unknown\ncall r = recall [\"t9-s9-9\"]\nr"
                                    Say.cell "// intent: gone\nk"
                                    Say.finish "used"
                                ]

                            do! prompt f "s1" "p2" "Use them"
                            let! _ = ended f "s1" 2
                            let turn2 = receipts f "s1" |> List.filter (fun r -> r.Turn = 2)
                            let first = text turn2[0]
                            Expect.stringContains first "the host restarted" "a new kernel, and why"
                            Expect.stringContains first "back as Out[\"t1-s1-1\"], Out[\"t1-s2-1\"]" "values restored"
                            Expect.stringContains first "bbbb" "the stored one, too"
                            Expect.stringContains (text turn2[1]) "rows" "recall by the tool call's id"
                            Expect.stringContains (text turn2[2]) "notKept" "an unknown id"
                            Expect.stringContains (text turn2[3]) "ReferenceError" "names are gone (Jupyter semantics)"

                            let launches =
                                journal f "s1"
                                |> List.choose (fun r ->
                                    match r.Event with
                                    | StepEvent.KernelLaunched n -> Some n
                                    | _ -> None)

                            Expect.equal launches [ 1; 2 ] "launch numbers go on after the restart"
                        })
            }

            testTask "a killed kernel's successor gets the kept values back; values without JSON say why" {
                do!
                    withFixture "recall-kill" [ "codeact.recall.maxBytes", Json.int 16 ] (fun f ->
                        task {

                            f.Model.AddAll
                                [
                                    Say.cell "// intent: small\nvalue 7"
                                    Say.cell "// intent: too big\nvalue \"0123456789abcdefghij\""
                                    Say.cell "// intent: spin\ncall w = fs.write [\"started.txt\", \"\"]\nspin"
                                ]

                            do! start f "s1"
                            do! prompt f "s1" "p1" "Spin"
                            do! f.Model.Asked(3).WaitAsync(TimeSpan.FromSeconds 30.0)
                            do! until "the cell started" (fun () -> IO.File.Exists(f.File "started.txt"))
                            let! _ = send f "s1" "stop" Command.Interrupt
                            let! _ = ended f "s1" 1

                            f.Model.AddAll
                                [
                                    Say.cell "// intent: check\nout s = t1-s1-1\ns"
                                    Say.cell "// intent: big\ncall r = recall [\"t1-s2-1\"]\nr"
                                    Say.finish "checked"
                                ]

                            do! prompt f "s1" "p2" "Check"
                            let! _ = ended f "s1" 2
                            let turn2 = receipts f "s1" |> List.filter (fun r -> r.Turn = 2)
                            Expect.stringContains (text turn2[0]) "the previous kernel stopped" "why"
                            Expect.stringContains (text turn2[0]) "(number): 7" "the value is back"
                            Expect.stringContains (text turn2[1]) "was not kept" "a value too large"
                            Expect.stringContains (text turn2[1]) "codeact.recall.maxBytes" "and why"
                        })
            }
        ]
