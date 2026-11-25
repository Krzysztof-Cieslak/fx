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

                                match first.Pieces with
                                | [ Piece.Quote quote ] ->
                                    Expect.equal
                                        (quote.Sources |> List.map (fun s -> s.Path))
                                        [ "greeting.txt" ]
                                        "provenance"
                                | other -> failtest $"{other}"

                                let result = toolResults requests[1] |> List.head
                                Expect.equal result.CallId "call-1" "answers the call"

                                Expect.equal
                                    (result.Sources |> List.map (fun s -> s.Path))
                                    [ "greeting.txt" ]
                                    "the tool result quotes the file"

                                Expect.stringContains
                                    (text (receipts f "s1").[1])
                                    "hello, world"
                                    "the command's output"

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
                                Expect.stringContains (text after) "ReferenceError" "y is gone"
                                Expect.isTrue after.IsError "an error"
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
                                Expect.stringContains (text after) "ReferenceError" "z is gone"
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

            testTask "model errors: retryable ones are retried, an overflow asks for compaction, others fail" {
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
                                Expect.stringContains (failedWith ending) "needs compaction" "compaction"
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
        ]
