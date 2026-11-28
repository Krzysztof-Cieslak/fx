/// CodeAct turns end to end on the session host, with the script kernel in process and a scripted
/// model: both action surfaces doing a small coding task, finishing validated, steering, interrupts,
/// kernel restarts, the loop guard, the policies and budgets, and content exclusions.
module Fx.CodeAct.Tests.TurnTests

open System
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Kernel
open Fx.Rpc
open Fx.Sessions
open Fx.Prompt
open Fx.CodeAct
open Fx.CodeAct.Tests.Support

let private q (text: string) = Json.quote text

/// The session `s1` started and prompted once.
let private kickoff (f: Fixture) (text: string) : Task =
    task {
        do! start f "s1"
        do! prompt f "s1" "p1" text
    }

/// Waits until the running cell wrote `started.txt`: it is past its first statements.
let private started (f: Fixture) : Task =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 30.0

        while not (IO.File.Exists(f.File "started.txt")) do
            if DateTime.UtcNow > deadline then
                failtest "the cell did not start"

            do! Task.Delay 10
    }

/// The kernel's connection as the host sees it: the kernel's answer to `cell/cancel` arrives `delay`
/// after the cancelled cell's answer (whichever the kernel wrote first), and `sent` keeps every message
/// the host sent.
let private cancelAnsweredLate
    (delay: TimeSpan)
    (sent: Collections.Concurrent.ConcurrentQueue<string>)
    (transport: Transport)
    : Transport =
    let held = ref None
    let answered = ref false

    let isCancelAnswer (text: string) = text.Contains "\"wasRunning\""
    let isCellAnswer (text: string) = text.Contains "\"outcome\""

    { transport with
        Send =
            fun frame cancellation ->
                match frame with
                | Frame.Text text -> sent.Enqueue text
                | Frame.Binary _ -> ()

                transport.Send frame cancellation
        Receive =
            fun cancellation ->
                task {
                    match held.Value with
                    | Some later when answered.Value ->
                        held.Value <- None
                        do! Task.Delay(delay, cancellation)
                        return later
                    | _ ->
                        let mutable next = None

                        while next.IsNone do
                            match! transport.Receive cancellation with
                            | Received.Frame(Frame.Text text) as received when isCancelAnswer text ->
                                if answered.Value then
                                    do! Task.Delay(delay, cancellation)
                                    next <- Some received
                                else
                                    held.Value <- Some received
                            | Received.Frame(Frame.Text text) as received when isCellAnswer text ->
                                answered.Value <- true
                                next <- Some received
                            | received -> next <- Some received

                        return next.Value
                }
    }

let private toolNames (request: ChatRequest) =
    request.Tools |> List.map (fun t -> t.Name)

let private toolResults (request: ChatRequest) =
    request.Messages
    |> List.collect (fun m -> m.Parts)
    |> List.choose (fun part ->
        match part with
        | Part.ToolResult r -> Some r
        | _ -> None)

let private openQuestion (state: SessionState) (id: string) =
    match state.Requests |> List.tryFind (fun r -> r.Request.Id = id) with
    | Some request ->
        Expect.equal request.Request.Kind Questions.KindId "a question"
        request
    | None -> failtest $"no open request {id}: {state.Requests}"

let private reasonOf (request: PendingRequest) =
    match Questions.payload.Decode request.Request.Payload with
    | Ok question -> question
    | Error issues -> failtest $"{issues}"

[<Tests>]
let tests =
    testList
        "CodeAct turns"
        [
            testTask "cells-only: a cell reads, another edits and runs a command, a third finishes with evidence" {
                do!
                    within
                        "cells-only"
                        []
                        (fun f ->
                            task {
                                f.Write("greeting.txt", "hello\n")

                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: read the greeting\ncall f = fs.read [\"greeting.txt\"]\nf"
                                        Say.cell
                                            "// intent: change it and check\ncall w = fs.write [\"greeting.txt\", \"hello, world\\n\"]\ncall run = shell.run [\"cat greeting.txt\"]\nrun"
                                        Say.cell
                                            "// intent: finish\nfinish {\"kind\":\"done\",\"summary\":\"The greeting says hello, world\",\"evidence\":[{\"kind\":\"command\",\"summary\":\"cat greeting.txt shows hello, world\"}],\"verify\":\"cat greeting.txt\"}"
                                    ]

                                do! kickoff f "Make the greeting say hello, world"
                                let! ending = ended f "s1" 1

                                match completed ending with
                                | TurnResult.Done(summary, [ evidence ], verify) ->
                                    Expect.equal summary "The greeting says hello, world" "summary"
                                    Expect.equal evidence.Kind "command" "evidence"
                                    Expect.equal verify (Some "cat greeting.txt") "verify"
                                | other -> failtest $"{other}"

                                Expect.equal (f.Read "greeting.txt") "hello, world\n" "the file changed"

                                let requests = f.Model.Requests
                                Expect.equal requests.Length 3 "three calls"

                                for request in requests do
                                    Expect.equal (toolNames request) [ "cell" ] "one cell tool"

                                let first = receipts f "s1" |> List.head
                                Expect.stringContains (text first) "hello" "the file's content"

                                // The cell ends with what fs.read returned: its lines show as read
                                // shows them, a file piece that counts as seen (T203a).
                                match first.Pieces with
                                | Piece.Quote quote :: Piece.File view :: _ ->
                                    Expect.equal
                                        (quote.Sources |> List.map (fun s -> s.Path))
                                        [ "greeting.txt" ]
                                        "provenance"

                                    Expect.equal view.Path "greeting.txt" "the file"
                                    Expect.equal view.Text "     1\thello" "numbered lines"
                                | other -> failtest $"{other}"

                                let result = toolResults requests[1] |> List.head
                                Expect.equal result.CallId "call-1" "answers the call"

                                // The quoted text and the file piece both name it.
                                Expect.equal
                                    (result.Sources |> List.map (fun s -> s.Path) |> List.distinct)
                                    [ "greeting.txt" ]
                                    "the tool result quotes the file"

                                Expect.stringContains
                                    (text (receipts f "s1").[1])
                                    "(RunResult):\n$ cat greeting.txt\nexit code 0\nhello, world"
                                    "the command's result, as bash shows it"

                                // Every request is a prefix of the next.
                                for a, b in List.pairwise requests do
                                    Expect.equal
                                        (b.Messages |> List.truncate a.Messages.Length)
                                        a.Messages
                                        "append-only"

                                    Expect.equal b.System a.System "a stable prefix"

                                let kinds =
                                    contextEvents f "s1"
                                    |> List.map (fun r ->
                                        match r.Event with
                                        | ContextEvent.Framed _ -> "framed"
                                        | ContextEvent.Replied _ -> "replied"
                                        | ContextEvent.Returned _ -> "returned"
                                        | other -> string other)

                                Expect.equal
                                    kinds
                                    [
                                        "framed"
                                        "replied"
                                        "returned"
                                        "replied"
                                        "returned"
                                        "replied"
                                        "returned"
                                    ]
                                    "the context log"
                            })
                        ()
            }

            testTask "hybrid: read, edit and bash act through the same bindings, and a cell finishes" {
                do!
                    within
                        "hybrid"
                        [ "agent.actionSurface", Json.String "hybrid" ]
                        (fun f ->
                            task {
                                f.Write("greeting.txt", "hello\nbye\n")

                                f.Model.AddAll
                                    [
                                        Say.tool "read" [ "path", Json.String "greeting.txt" ]
                                        Say.tool
                                            "edit"
                                            [
                                                "path", Json.String "greeting.txt"
                                                "old", Json.String "hello"
                                                "new", Json.String "hello, world"
                                            ]
                                        Say.tool "bash" [ "command", Json.String "cat greeting.txt" ]
                                        Say.finish "The greeting says hello, world"
                                    ]

                                do! kickoff f "Make the greeting say hello, world"
                                let! ending = ended f "s1" 1

                                match completed ending with
                                | TurnResult.Done(summary, _, _) ->
                                    Expect.equal summary "The greeting says hello, world" "summary"
                                | other -> failtest $"{other}"

                                Expect.equal (f.Read "greeting.txt") "hello, world\nbye\n" "edited"

                                for request in f.Model.Requests do
                                    Expect.equal
                                        (toolNames request)
                                        [ "cell"; "read"; "edit"; "bash" ]
                                        "the hybrid tools"

                                match receipts f "s1" with
                                | read :: edit :: bash :: _ ->
                                    match read.Pieces with
                                    | [ Piece.File view ] ->
                                        Expect.equal view.Path "greeting.txt" "path"
                                        Expect.equal view.Text "     1\thello\n     2\tbye" "numbered lines"
                                        Expect.isEmpty view.Lines "the whole file"
                                    | other -> failtest $"{other}"

                                    Expect.stringContains (text edit) "Edited greeting.txt (1 replacement)." "edit"
                                    Expect.stringContains (text edit) "1\thello, world" "the changed lines"
                                    Expect.isFalse edit.IsError "fine"
                                    Expect.stringContains (text bash) "exit code 0" "bash"
                                    Expect.stringContains (text bash) "hello, world" "its output"
                                | other -> failtest $"{other}"

                                let readResult = toolResults f.Model.Requests[1] |> List.head

                                Expect.equal
                                    (readResult.Sources |> List.map (fun s -> s.Path))
                                    [ "greeting.txt" ]
                                    "a read quotes its file"
                            })
                        ()
            }

            testTask "a rejected finish comes back to the model with the reasons, and it finishes again" {
                do!
                    within
                        "finish-rejected"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: finish\nfinish {\"kind\":\"done\",\"summary\":\" \"}"
                                        Say.cell
                                            "// intent: finish\nfinish {\"kind\":\"needInput\",\"requests\":[\"r1\"]}"
                                        Say.cell
                                            "// intent: finish\nfinish {\"kind\":\"waiting\",\"jobs\":[\"j1\"],\"note\":\"tests\"}"
                                        Say.cell "// intent: finish\nfinish {\"kind\":\"done\"}"
                                        Say.finish "Done properly"
                                    ]

                                do! kickoff f "Finish"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (TurnResult.Done("Done properly", [], None)) "accepted"

                                let rs = receipts f "s1"

                                Expect.equal
                                    (rs |> List.map (fun r -> r.IsError))
                                    [ true; true; true; true; false ]
                                    "errors"

                                Expect.stringContains (text rs[0]) "the summary must say what was done" "the check"
                                Expect.stringContains (text rs[0]) "finishRejected" "the code"
                                Expect.stringContains (text rs[1]) "\"r1\" is not an open request" "unknown requests"
                                Expect.stringContains (text rs[2]) "there is no background job \"j1\"" "unknown jobs"
                                Expect.stringContains (text rs[3]) "summary" "the codec"
                                Expect.stringContains (text rs[4]) "The turn's result was accepted." "accepted"
                            })
                        ()
            }

            testTask "with evidence required, a done result without evidence is refused" {
                do!
                    within
                        "evidence"
                        [ "codeact.finish.evidence", Json.String "required" ]
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.finish "no evidence"
                                        Say.cell
                                            "// intent: finish\nfinish {\"kind\":\"done\",\"summary\":\"checked\",\"evidence\":[{\"kind\":\"check\",\"summary\":\"looked\"}]}"
                                    ]

                                do! kickoff f "Finish"
                                let! ending = ended f "s1" 1

                                match completed ending with
                                | TurnResult.Done("checked", [ _ ], None) -> ()
                                | other -> failtest $"{other}"

                                Expect.stringContains (text (receipts f "s1").[0]) "must list evidence" "refused"
                            })
                        ()
            }

            testTask "a steer sent during a step reaches the model at the next step boundary" {
                do!
                    within
                        "steer"
                        []
                        (fun f ->
                            task {
                                let hold = f.Model.Hold 2

                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: one\nprint one"
                                        Say.cell "// intent: two\nprint two"
                                        Say.finish "Done with both"
                                    ]

                                do! kickoff f "Count"
                                do! f.Model.Asked(2).WaitAsync(TimeSpan.FromSeconds 30.0)
                                do! prompt f "s1" "p2" "Also say three"
                                hold.SetResult()
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (TurnResult.Done("Done with both", [], None)) "done"

                                let requests = f.Model.Requests

                                Expect.isFalse
                                    ((requestText requests[1]).Contains "Also say three")
                                    "not before the boundary"

                                let last = requests[2].Messages |> List.last
                                Expect.equal last.Role Role.User "the user's turn"

                                match last.Parts with
                                | [ Part.ToolResult result; Part.Text steer ] ->
                                    Expect.equal result.CallId "call-2" "after the receipt"
                                    Expect.equal steer.Text "Also say three" "the steer"
                                | other -> failtest $"{other}"

                                let delivered =
                                    events f "s1"
                                    |> List.exists (fun r ->
                                        match r.Event with
                                        | Event.InputsDelivered(1, 3, [ _ ]) -> true
                                        | _ -> false)

                                Expect.isTrue delivered "delivered at step 3"
                            })
                        ()
            }

            testTask "a steer comes after every result of the step's reply; a queued prompt waits for a turn of its own" {
                do!
                    within
                        "steer-queue"
                        [ "agent.actionSurface", Json.String "hybrid" ]
                        (fun f ->
                            task {
                                f.Write("a.txt", "alpha\n")
                                let hold = f.Model.Hold 1

                                f.Model.AddAll
                                    [
                                        Reply.Parts(
                                            [
                                                Say.call "read" (Json.Object [ "path", Json.String "a.txt" ])
                                                Say.call "bash" (Json.Object [ "command", Json.String "echo two" ])
                                            ],
                                            StopReason.ToolUse
                                        )
                                        Say.finish "First done"
                                        Say.finish "Second done"
                                    ]

                                do! kickoff f "First"
                                do! f.Model.Asked(1).WaitAsync(TimeSpan.FromSeconds 30.0)

                                match!
                                    send f "s1" "p2" (Command.Prompt("Queued: count the sheep", PromptMode.Queue))
                                with
                                | Fx.Machines.Accepted _ -> ()
                                | other -> failtest $"the queued prompt was not taken: {other}"

                                do! prompt f "s1" "p3" "Steered: say three"
                                hold.SetResult()

                                let! first = ended f "s1" 1

                                Expect.equal
                                    (completed first)
                                    (TurnResult.Done("First done", [], None))
                                    "the first turn"

                                let! second = ended f "s1" 2

                                Expect.equal
                                    (completed second)
                                    (TurnResult.Done("Second done", [], None))
                                    "its own turn"

                                let requests = f.Model.Requests
                                Expect.equal requests.Length 3 "two calls, then the queued prompt's turn"

                                match (List.last requests[1].Messages).Parts with
                                | [ Part.ToolResult read; Part.ToolResult bash; Part.Text steer ] ->
                                    Expect.equal
                                        [ read.CallId; bash.CallId ]
                                        [ "call-1"; "call-2" ]
                                        "both results first"

                                    Expect.equal steer.Text "Steered: say three" "then the steer"
                                | other -> failtest $"{other}"

                                Expect.isFalse
                                    ((requestText requests[1]).Contains "count the sheep")
                                    "the queued prompt waits for the turn's end"

                                match (List.last requests[2].Messages).Parts |> List.last with
                                | Part.Text queued ->
                                    Expect.equal queued.Text "Queued: count the sheep" "turn 2's input"
                                | other -> failtest $"{other}"
                            })
                        ()
            }

            testTask "an interrupt stops a running cell; the kernel and its names live on" {
                do!
                    within
                        "interrupt"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell
                                            "// intent: wait\nlet x = 1\ncall w = fs.write [\"started.txt\", \"\"]\nsleep"
                                    ]

                                do! kickoff f "Wait"
                                do! f.Model.Asked(1).WaitAsync(TimeSpan.FromSeconds 30.0)
                                do! started f
                                let! _ = send f "s1" "stop" Command.Interrupt
                                let! ending = ended f "s1" 1
                                Expect.equal ending TurnEnd.Interrupted "interrupted"

                                f.Model.AddAll [ Say.cell "// intent: check\nx"; Say.finish "x is still there" ]
                                do! prompt f "s1" "p2" "Go on"
                                let! ending = ended f "s1" 2
                                Expect.equal (completed ending) (TurnResult.Done("x is still there", [], None)) "done"

                                let after = receipts f "s1" |> List.filter (fun r -> r.Turn = 2) |> List.head
                                Expect.stringContains (text after) "(number): 1" "the kernel kept x"
                                Expect.isFalse ((text after).Contains "A new kernel") "the same kernel"
                            })
                        ()
            }

            testTask "one kernel for the session: names declared in earlier cells and turns stay" {
                do!
                    within
                        "stays"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: declare a\nlet a = 1"
                                        Say.cell "// intent: declare b, read a\nlet b = 2\na"
                                        Say.finish "declared"
                                    ]

                                do! kickoff f "Declare"
                                let! _ = ended f "s1" 1

                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: read both\nb"
                                        Say.cell "// intent: and a\na"
                                        Say.finish "read"
                                    ]

                                do! prompt f "s1" "p2" "Read them"
                                let! ending = ended f "s1" 2
                                Expect.equal (completed ending) (TurnResult.Done("read", [], None)) "done"

                                let rs = receipts f "s1"
                                Expect.stringContains (text rs[1]) "(number): 1" "a, in the next cell"
                                let turn2 = rs |> List.filter (fun r -> r.Turn = 2)
                                Expect.stringContains (text turn2[0]) "(number): 2" "b, in the next turn"
                                Expect.stringContains (text turn2[1]) "(number): 1" "a, too"

                                for r in rs do
                                    Expect.isNone r.Restarted $"no new kernel: {text r}"
                                    Expect.isFalse ((text r).Contains "A new kernel") "nor a note of one"

                                let kernels =
                                    journal f "s1"
                                    |> List.choose (fun r ->
                                        match r.Event with
                                        | StepEvent.KernelLaunched _
                                        | StepEvent.KernelEnded _ -> Some r.Event
                                        | _ -> None)

                                Expect.equal kernels [ StepEvent.KernelLaunched 1 ] "one launch, never ended"
                            })
                        ()
            }

            testTask "an interrupt runs to its end when the cell answers before the kernel answers cell/cancel" {
                // Cut short when the cell's answer came first, the interrupt's `cell/cancel` would be
                // cancelled at the kernel (`$/cancelRequest`) or not depending on whether the kernel's
                // answer to it was handled yet: timing, which replay cannot take again.
                let sent = Collections.Concurrent.ConcurrentQueue<string>()

                let options =
                    { kernelOptions with
                        ExitGrace = TimeSpan.FromSeconds 30.0
                    }

                do!
                    (task {
                        use f =
                            createWith "interrupt-late" [] (fun _ ->
                                scriptKernelOver (cancelAnsweredLate (TimeSpan.FromMilliseconds 300.0) sent) options)

                        f.Model.AddAll
                            [
                                Say.cell "// intent: wait\nlet x = 1\ncall w = fs.write [\"started.txt\", \"\"]\nsleep"
                            ]

                        do! kickoff f "Wait"
                        do! started f
                        let! _ = send f "s1" "stop" Command.Interrupt
                        let! ending = ended f "s1" 1
                        Expect.equal ending TurnEnd.Interrupted "interrupted"

                        f.Model.AddAll [ Say.cell "// intent: check\nx"; Say.finish "x is still there" ]
                        do! prompt f "s1" "p2" "Go on"
                        let! ending = ended f "s1" 2
                        Expect.equal (completed ending) (TurnResult.Done("x is still there", [], None)) "done"
                    })
                        .WaitAsync(TimeSpan.FromSeconds 120.0)

                let messages = List.ofSeq sent

                let count (method: string) =
                    messages |> List.filter (fun m -> m.Contains method) |> List.length

                Expect.equal (count "\"cell/cancel\"") 1 "the interrupt asked the kernel once"
                Expect.equal (count Protocol.CancelRequest) 0 "and never cancelled what it asked"
            }

            testTask "a cell that cannot be interrupted is killed; the next cell starts a new kernel and says so" {
                do!
                    within
                        "kill"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll [ Say.cell "// intent: spin\nlet y = 2\nspin spinning" ]

                                do! kickoff f "Spin"
                                do! f.Model.Asked(1).WaitAsync(TimeSpan.FromSeconds 30.0)
                                // Not before: a cancel that reaches the cell before it blocks the kernel
                                // stops it, and the kernel lives on.
                                do! printed f "s1" "spinning"
                                let! _ = send f "s1" "stop" Command.Interrupt
                                let! ending = ended f "s1" 1
                                Expect.equal ending TurnEnd.Interrupted "interrupted"

                                f.Model.AddAll [ Say.cell "// intent: check\ny"; Say.finish "y is gone" ]
                                do! prompt f "s1" "p2" "Go on"
                                let! ending = ended f "s1" 2
                                completed ending |> ignore

                                let after = receipts f "s1" |> List.filter (fun r -> r.Turn = 2) |> List.head
                                Expect.stringContains (text after) "A new kernel started" "the restart is noted"

                                Expect.stringContains
                                    (text after)
                                    "the previous kernel was killed by the host: an interrupted cell did not stop"
                                    "and why"

                                Expect.stringContains (text after) "ReferenceError" "y is gone"
                                Expect.isTrue after.IsError "an error"

                                Expect.equal
                                    (after.Restarted |> Option.map _.Launch)
                                    (Some 2)
                                    "people see the new kernel"

                                Expect.contains
                                    (journal f "s1" |> List.map _.Event)
                                    (StepEvent.KernelEnded { Launch = 1; How = KernelEnd.Killed })
                                    "journaled with its reason"
                            })
                        ()
            }

            testTask "a kernel that exits during a cell: the receipt and the journal say how, the next cell's says why" {
                do!
                    within
                        "exit"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: declare\nlet w = 4"
                                        Say.cell "// intent: wait\ncall s = fs.write [\"started.txt\", \"\"]\nsleep"
                                        Say.cell "// intent: check\nw"
                                        Say.finish "w is gone"
                                    ]

                                do! kickoff f "Wait"
                                do! started f

                                // The kernel goes away while the cell runs (a crash, or a kill from outside).
                                match f.Kernel "s1" with
                                | Some kernel -> do! Eff.run () (Kernel.kill kernel)
                                | None -> failtest "no kernel"

                                let! _ = ended f "s1" 1
                                let rs = receipts f "s1"
                                let failed = rs[1]
                                Expect.isTrue failed.IsError "the cell failed"

                                Expect.stringContains
                                    (text failed)
                                    "The cell did not complete: the kernel exited. The next cell starts a new kernel"
                                    "how, and what follows"

                                Expect.isNone failed.Restarted "it ran in the first kernel"

                                Expect.stringContains
                                    (text rs[2])
                                    "(the previous kernel exited)"
                                    "the next cell's note"

                                Expect.stringContains (text rs[2]) "ReferenceError" "w is gone"

                                Expect.equal
                                    rs[2].Restarted
                                    (Some
                                        {
                                            Launch = 2
                                            Reason = "the previous kernel exited"
                                        })
                                    "people see it"

                                let kernels =
                                    journal f "s1"
                                    |> List.choose (fun r ->
                                        match r.Event with
                                        | StepEvent.KernelLaunched _
                                        | StepEvent.KernelEnded _ -> Some r.Event
                                        | _ -> None)

                                Expect.equal
                                    kernels
                                    [
                                        StepEvent.KernelLaunched 1
                                        StepEvent.KernelEnded
                                            {
                                                Launch = 1
                                                How = KernelEnd.Exited(None, "")
                                            }
                                        StepEvent.KernelLaunched 2
                                    ]
                                    "each launch's end is journaled before the next launch"
                            })
                        ()
            }

            testTask "a kernel that died between turns is restarted for the next cell" {
                do!
                    within
                        "crash"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll [ Say.cell "// intent: keep\nlet z = 3"; Say.finish "kept" ]
                                do! kickoff f "Keep z"
                                let! _ = ended f "s1" 1

                                // A crash, observed: the connection to the kernel has ended.
                                match f.Kernel "s1" with
                                | Some kernel ->
                                    do! Eff.run () (Kernel.kill kernel)
                                    let! _ = (Kernel.exited kernel).WaitAsync(TimeSpan.FromSeconds 10.0)
                                    ()
                                | None -> failtest "no kernel"

                                f.Model.AddAll [ Say.cell "// intent: check\nz"; Say.finish "z is gone" ]
                                do! prompt f "s1" "p2" "Go on"
                                let! _ = ended f "s1" 2
                                let after = receipts f "s1" |> List.filter (fun r -> r.Turn = 2) |> List.head
                                Expect.stringContains (text after) "A new kernel started" "noted"
                                Expect.stringContains (text after) "(the previous kernel exited)" "and why"
                                Expect.stringContains (text after) "ReferenceError" "z is gone"

                                Expect.contains
                                    (journal f "s1" |> List.map _.Event)
                                    (StepEvent.KernelEnded
                                        {
                                            Launch = 1
                                            How = KernelEnd.Exited(None, "")
                                        })
                                    "the end is journaled before the next launch"
                            })
                        ()
            }

            testTask "the loop guard warns, then stops a turn that repeats itself and asks the user" {
                do!
                    within
                        "loop"
                        []
                        (fun f ->
                            task {
                                let same = Say.cell "// intent: look\nprint same"
                                f.Model.AddAll [ same; same; same ]
                                do! kickoff f "Look"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (TurnResult.NeedInput [ "q-1-3" ]) "need input"
                                let! state = state f "s1"
                                let question = reasonOf (openQuestion state "q-1-3")
                                Expect.equal question.Reason "loop" "why"

                                Expect.stringContains
                                    (requestText f.Model.Requests[2])
                                    "you did the same thing 2 times"
                                    "warned first"
                            })
                        ()
            }

            testTask "text only: a nudge, then a question whose answer starts the next turn" {
                do!
                    within
                        "nudge"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll [ Say.text "Which file?"; Say.text "Which file should I change?" ]
                                do! kickoff f "Change the file"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (TurnResult.NeedInput [ "q-1-2" ]) "need input"
                                Expect.stringContains (requestText f.Model.Requests[1]) "had no tool call" "nudged"
                                let! state = state f "s1"
                                let question = reasonOf (openQuestion state "q-1-2")
                                Expect.equal question.Question "Which file should I change?" "the model's text"

                                f.Model.AddAll [ Say.finish "Changed it" ]
                                let! _ = send f "s1" "answer" (Command.Answer("q-1-2", Json.String "greeting.txt"))
                                let! ending = ended f "s1" 2

                                Expect.equal
                                    (completed ending)
                                    (TurnResult.Done("Changed it", [], None))
                                    "the next turn"

                                Expect.stringContains
                                    (requestText (List.last f.Model.Requests))
                                    "Request q-1-2 was answered: \"greeting.txt\""
                                    "the answer is its input"
                            })
                        ()
            }

            testTask "text only with the ask policy is a question at once" {
                do!
                    within
                        "ask"
                        [ "codeact.policy.textOnly", Json.String "ask" ]
                        (fun f ->
                            task {
                                f.Model.AddAll [ Say.text "Hello! What should I do?" ]
                                do! kickoff f "Hi"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (TurnResult.NeedInput [ "q-1-1" ]) "a question"
                                Expect.equal f.Model.Requests.Length 1 "one call"
                            })
                        ()
            }

            testTask "text only with the done policy ends the turn with the text as its summary" {
                do!
                    within
                        "done"
                        [ "codeact.policy.textOnly", Json.String "done" ]
                        (fun f ->
                            task {
                                f.Model.AddAll [ Say.text "All done." ]
                                do! kickoff f "Do it"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (TurnResult.Done("All done.", [], None)) "done"
                            })
                        ()
            }

            testTask "a reply cut at the output limit: its broken call does not run, and the model is told" {
                do!
                    within
                        "length"
                        []
                        (fun f ->
                            task {
                                let cut =
                                    Reply.Parts(
                                        [
                                            Part.ToolCall
                                                {
                                                    Id = ""
                                                    Name = "cell"
                                                    Arguments = "{\"code\": \"print a"
                                                    State = None
                                                }
                                        ],
                                        StopReason.MaxTokens
                                    )

                                f.Model.AddAll [ cut; Say.finish "Done in smaller steps" ]
                                do! kickoff f "Do it"
                                let! ending = ended f "s1" 1
                                completed ending |> ignore
                                Expect.stringContains (text (receipts f "s1").[0]) "not JSON" "nothing ran"

                                Expect.stringContains
                                    (requestText f.Model.Requests[1])
                                    "cut at the output limit"
                                    "told"
                            })
                        ()
            }

            testTask "cut replies fail the turn after the retries, or at once with the fail policy" {
                do!
                    within
                        "length-fail"
                        [ "codeact.policy.lengthRetries", Json.int 1 ]
                        (fun f ->
                            task {
                                let cut = Reply.Parts([ Part.text "and then" ], StopReason.MaxTokens)
                                f.Model.AddAll [ cut; cut ]
                                do! kickoff f "Do it"
                                let! ending = ended f "s1" 1
                                Expect.stringContains (failedWith ending) "2 times in a row" "failed"
                            })
                        ()

                do!
                    within
                        "length-fail-now"
                        [ "codeact.policy.lengthCut", Json.String "fail" ]
                        (fun f ->
                            task {
                                f.Model.AddAll [ Reply.Parts([ Part.text "and then" ], StopReason.MaxTokens) ]
                                do! kickoff f "Do it"
                                let! ending = ended f "s1" 1
                                Expect.stringContains (failedWith ending) "cut at the output limit" "failed"
                            })
                        ()
            }

            testTask "an empty reply is asked for again, unrecorded; too many fail the turn" {
                do!
                    within
                        "empty"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll [ Say.empty; Say.finish "Done" ]
                                do! kickoff f "Do it"
                                let! ending = ended f "s1" 1
                                completed ending |> ignore

                                match f.Model.Requests with
                                | [ a; b ] ->
                                    Expect.equal
                                        (Json.serializeCanonical (ChatRequest.codec.Encode a))
                                        (Json.serializeCanonical (ChatRequest.codec.Encode b))
                                        "the same request again"
                                | other -> failtest $"{other.Length} requests"
                            })
                        ()

                do!
                    within
                        "empty-fail"
                        [ "codeact.policy.emptyRetries", Json.int 1 ]
                        (fun f ->
                            task {
                                f.Model.AddAll [ Say.empty; Say.empty ]
                                do! kickoff f "Do it"
                                let! ending = ended f "s1" 1
                                Expect.stringContains (failedWith ending) "2 empty replies" "failed"
                            })
                        ()
            }

            testTask "model errors: retryable ones are retried, an overflow with nothing to compact fails, others fail" {
                do!
                    within
                        "rate-limit"
                        []
                        (fun f ->
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

                                do! kickoff f "Do it"
                                let! ending = ended f "s1" 1
                                completed ending |> ignore
                                Expect.equal f.Model.Requests.Length 3 "retried twice"
                            })
                        ()

                do!
                    within
                        "overflow"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.failed (
                                            ChatError.ContextOverflow
                                                {
                                                    Tokens = Some 300000
                                                    Limit = Some 200000
                                                    Message = "too long"
                                                }
                                        )
                                    ]

                                do! kickoff f "Do it"
                                let! ending = ended f "s1" 1

                                Expect.stringContains
                                    (failedWith ending)
                                    "does not fit the model's context window"
                                    "overflow"

                                Expect.stringContains
                                    (failedWith ending)
                                    "could not be compacted: there is nothing old enough to summarize yet"
                                    "nothing to compact"

                                Expect.equal f.Model.Requests.Length 1 "no summary asked for"
                            })
                        ()

                do!
                    within
                        "fatal"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll [ Say.failed (ChatError.AuthFailed "bad key") ]
                                do! kickoff f "Do it"
                                let! ending = ended f "s1" 1
                                Expect.stringContains (failedWith ending) "the model call failed" "failed"
                                Expect.equal f.Model.Requests.Length 1 "not retried"
                            })
                        ()
            }

            testTask "a turn that used its steps stops and asks to go on" {
                do!
                    within
                        "budget"
                        [ "codeact.budget.steps", Json.int 2 ]
                        (fun f ->
                            task {
                                f.Model.AddAll [ Say.cell "// intent: a\nprint a"; Say.cell "// intent: b\nprint b" ]
                                do! kickoff f "Do it"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (TurnResult.NeedInput [ "q-1-3" ]) "asks"
                                let! state = state f "s1"
                                Expect.equal (reasonOf (openQuestion state "q-1-3")).Reason "budget" "why"
                                Expect.equal f.Model.Requests.Length 2 "two calls"
                            })
                        ()
            }

            testTask "content exclusions refuse excluded files to cells and direct tools alike" {
                do!
                    within
                        "exclusions"
                        [
                            "agent.actionSurface", Json.String "hybrid"
                            "egress.exclusions.host", Json.Array [ Json.String "*.env" ]
                        ]
                        (fun f ->
                            task {
                                f.Write(".env", "SECRET=hunter2\n")

                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: read the env\ncall s = fs.read [\".env\"]\ns"
                                        Say.tool "read" [ "path", Json.String ".env" ]
                                        Say.tool
                                            "edit"
                                            [
                                                "path", Json.String ".env"
                                                "old", Json.String "SECRET"
                                                "new", Json.String "X"
                                            ]
                                        Say.finish "Could not read it"
                                    ]

                                do! kickoff f "Read the env"
                                let! _ = ended f "s1" 1
                                let rs = receipts f "s1" |> List.take 3

                                for r in rs do
                                    Expect.isTrue r.IsError "refused"
                                    Expect.stringContains (text r) "excluded" "excluded"
                                    Expect.isFalse ((text r).Contains "hunter2") "nothing of it"

                                for request in f.Model.Requests do
                                    Expect.isFalse ((requestText request).Contains "hunter2") "never sent"

                                Expect.equal (f.Read ".env") "SECRET=hunter2\n" "unchanged"
                            })
                        ()
            }

            testTask "a tool the surface does not have is refused with the tools it has" {
                do!
                    within
                        "unknown-tool"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll [ Say.tool "read" [ "path", Json.String "a.txt" ]; Say.finish "Done" ]

                                do! kickoff f "Read"
                                let! _ = ended f "s1" 1
                                let r = (receipts f "s1")[0]
                                Expect.isTrue r.IsError "refused"

                                Expect.stringContains
                                    (text r)
                                    "There is no tool named \"read\"; the tools are: cell."
                                    "why"
                            })
                        ()
            }

            testTask "calls after an accepted finish in the same reply do not run" {
                do!
                    within
                        "after-finish"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Reply.Parts(
                                            [
                                                Say.call
                                                    "cell"
                                                    (Json.Object
                                                        [
                                                            "code",
                                                            Json.String
                                                                "// intent: finish\nfinish {\"kind\":\"done\",\"summary\":\"first\"}"
                                                        ])
                                                Say.call
                                                    "cell"
                                                    (Json.Object [ "code", Json.String "// intent: more\nlet w = 1" ])
                                            ],
                                            StopReason.ToolUse
                                        )
                                    ]

                                do! kickoff f "Finish"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (TurnResult.Done("first", [], None)) "done"
                                let rs = receipts f "s1"
                                Expect.equal rs.Length 2 "both calls answered"
                                Expect.stringContains (text rs[1]) "Not run" "skipped"
                            })
                        ()
            }

            testTask "a cell's texts are bound before it runs: a 50 KB F# file with backticks, written byte for byte" {
                do!
                    within
                        "texts"
                        []
                        (fun f ->
                            task {
                                let posix = fsharpWithBackticks 50000
                                let marker = "let ``case 7 works``"
                                Expect.stringContains posix marker "the text has double backticks"

                                f.Model.AddAll
                                    [
                                        Say.cellWith
                                            "// intent: add the native layer\ncall w = fs.write [\"src/Native.fs\", \"$posix\"]\nprint wrote it"
                                            [ "posix", posix; "message", "Add `Native.fs`\n\nWith ``backticks``." ]
                                        Say.cellWith "// intent: the message\nmessage" [ "message", "again `bound`" ]
                                        Say.finish "Added the native layer"
                                    ]

                                do! kickoff f "Add the native layer"
                                let! ending = ended f "s1" 1

                                Expect.equal
                                    (completed ending)
                                    (TurnResult.Done("Added the native layer", [], None))
                                    "finished"

                                Expect.equal
                                    (IO.File.ReadAllBytes(f.File(IO.Path.Combine("src", "Native.fs"))))
                                    (Text.Encoding.UTF8.GetBytes posix)
                                    "byte for byte"

                                let rs = receipts f "s1"
                                Expect.isFalse rs[0].IsError $"the cell ran: {text rs[0]}"
                                Expect.stringContains (text rs[0]) "wrote it" "its output"
                                Expect.isFalse ((text rs[0]).Contains marker) "the receipt does not repeat the text"

                                Expect.stringContains
                                    (text rs[1])
                                    "again `bound`"
                                    "a text bound again by the next cell"

                                // The texts are part of the call's arguments: the reply holds them, and
                                // the step journal only a bounded preview of what the cell passed on.
                                let replied =
                                    contextEvents f "s1"
                                    |> List.pick (fun r ->
                                        match r.Event with
                                        | ContextEvent.Replied reply -> Some reply
                                        | _ -> None)

                                match replied.Parts with
                                | [ Part.ToolCall call ] ->
                                    Expect.stringContains call.Arguments "case 7 works" "the reply"
                                | other -> failtest $"{other}"

                                let started =
                                    journal f "s1"
                                    |> List.choose (fun r ->
                                        match r.Event with
                                        | StepEvent.HostCallStarted s -> Some s
                                        | _ -> None)

                                Expect.equal (started |> List.map _.Binding) [ "fs.write" ] "journaled"

                                for s in started do
                                    Expect.isLessThanOrEqual s.Args.Length (StepJournal.ArgsChars + 1) "bounded"

                                // The tools offered `texts` (version 2).
                                let cellTool = f.Model.Requests.Head.Tools |> List.exactlyOne
                                Expect.stringContains (Json.serialize cellTool.Parameters) "\"texts\"" "the schema"
                                do! expectReplays f "s1"
                            })
                        ()
            }

            testTask "texts with invalid names are refused before anything runs, saying which" {
                do!
                    within
                        "texts-invalid"
                        []
                        (fun f ->
                            task {
                                let ran = "// intent: try\ncall w = fs.write [\"ran.txt\", \"$a\"]"

                                f.Model.AddAll
                                    [
                                        Say.cellWith ran [ "a", "x"; "1x", "y" ]
                                        Say.cellWith ran [ "a", "x"; g, "y" ]
                                        // The script kernel's statements are its reserved words.
                                        Say.cellWith ran [ "a", "x"; "print", "y" ]
                                        Say.finish "Could not"
                                    ]

                                do! kickoff f "Try"
                                let! _ = ended f "s1" 1
                                let rs = receipts f "s1"

                                for r in rs |> List.take 3 do
                                    Expect.isTrue r.IsError $"refused: {text r}"

                                Expect.stringContains
                                    (text rs[0])
                                    "$.texts[\"1x\"]: \"1x\" is not a name"
                                    "an invalid name"

                                Expect.stringContains (text rs[1]) $"\"{g}\" is a name the kernel keeps" "the global"

                                Expect.stringContains
                                    (text rs[2])
                                    "The kernel cannot bind the text \"print\""
                                    "a name the kernel refused"

                                Expect.stringContains (text rs[2]) "Nothing ran." "nothing ran"
                                Expect.isFalse (IO.File.Exists(f.File "ran.txt")) "no cell ran"

                                // Arguments that do not decode never start; a name the kernel refuses
                                // fails the started call before its code runs.
                                let started =
                                    journal f "s1"
                                    |> List.choose (fun r ->
                                        match r.Event with
                                        | StepEvent.CallStarted s -> Some s.Call.Step
                                        | _ -> None)

                                Expect.equal started [ 3; 4 ] "journaled: the kernel's refusal and the finish"
                            })
                        ()
            }

            testTask "a conversation framed with version 1 of the tools keeps them; texts still decode" {
                do!
                    within
                        "tools-v1"
                        []
                        (fun f ->
                            task {
                                let v1 =
                                    {
                                        Surface = Fx.Variants.ActionSurface.CellsOnly
                                        Version = 1
                                    }

                                do! start f "s1"

                                // The frame a conversation started before T140 recorded.
                                do!
                                    run
                                        f
                                        (ContextLog.record
                                            (Fx.Sessions.Host.SessionLog.typed f.Store.Log)
                                            (ContextLog.typed f.Store.Log)
                                            "s1"
                                            "earlier/frame"
                                            (ContextEvent.Framed(Instructions.sections v1 "Cells are scripts." None [])))

                                f.Model.AddAll
                                    [
                                        Say.cellWith
                                            "// intent: write\ncall w = fs.write [\"a.txt\", \"$a\"]"
                                            [ "a", "`a`" ]
                                        Say.finish "Wrote"
                                    ]

                                do! prompt f "s1" "p1" "Write"
                                let! ending = ended f "s1" 1
                                completed ending |> ignore
                                Expect.equal (f.Read "a.txt") "`a`" "the text"

                                for request in f.Model.Requests do
                                    Expect.equal request.Tools (Tools.specs v1 "script") "the tools version 1 had"

                                let tools =
                                    contextEvents f "s1"
                                    |> List.choose (fun r ->
                                        match r.Event with
                                        | ContextEvent.Framed sections
                                        | ContextEvent.Updated { Changed = sections } ->
                                            sections |> List.tryFind (fun s -> s.Id = "tools")
                                        | _ -> None)

                                Expect.equal
                                    (tools |> List.map _.Version)
                                    [ Some "cells-only" ]
                                    "the tools section as framed, never updated"
                            })
                        ()
            }
        ]
