/// Compaction in CodeAct turns (spec §5, T206), with the script kernel and a scripted model whose catalog
/// gives a small context window: prepared in the background when a reply's prompt reached the threshold,
/// on overflow, when the window filled during a reply, and when a cell asks (`history.compact`); the
/// summary request shares the conversation's cache, and the logs replay into the requests sent.
module Fx.CodeAct.Tests.CompactionTests

open System
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Machines
open Fx.Models
open Fx.Sessions
open Fx.Prompt
open Fx.CodeAct
open Fx.CodeAct.Tests.Support

/// The model's context window: the threshold (80%) is 16000 tokens.
let private window = 20000

let private catalogOf (window: int) (reference: ModelRef) : ModelInfo option =
    Some(ModelInfo.create reference { Context = window; Output = 4096 })

/// Runs a test on a fixture whose model has this window, and disposes it.
let private compactingIn (window: int) (name: string) (selections: (string * Json) list) (body: Fixture -> Task<unit>) =
    fun () ->
        task {
            use f =
                createOver name selections (fun _ -> scriptKernel kernelOptions) None (catalogOf window)

            do! body(f).WaitAsync(TimeSpan.FromSeconds 120.0)
        }

/// Runs a test on a fixture whose model has a 20000-token window, and disposes it.
let private compacting = compactingIn window

let private kickoff (f: Fixture) (text: string) : Task =
    task {
        do! start f "s1"
        do! prompt f "s1" "p1" text
    }

/// The request without its last part (a summary request's instruction).
let private withoutInstruction (request: ChatRequest) : ChatRequest =
    let last = List.last request.Messages

    { request with
        Messages =
            (request.Messages |> List.truncate (request.Messages.Length - 1))
            @ [
                { last with
                    Parts = last.Parts |> List.truncate (last.Parts.Length - 1)
                }
            ]
    }

/// The request as the same model and tools would send it with more output: a summary's limit.
let private withOutput (output: int option) (request: ChatRequest) =
    { request with
        MaxOutputTokens = output
    }

let private summaryText = "Summary: the agent printed one."

/// The collapses the turn recorded, in order.
let private collapses (f: Fixture) : Collapse list =
    contextEvents f "s1"
    |> List.choose (fun r ->
        match r.Event with
        | ContextEvent.Collapsed collapse -> Some collapse
        | _ -> None)

/// What the context log recorded, as kinds in order.
let private kinds (f: Fixture) : string list =
    contextEvents f "s1"
    |> List.map (fun r ->
        match r.Event with
        | ContextEvent.Framed _ -> "framed"
        | ContextEvent.Updated _ -> "updated"
        | ContextEvent.Replied reply -> $"reply {reply.Step}"
        | ContextEvent.Returned receipt -> $"receipt {receipt.Step}"
        | ContextEvent.Pinned _ -> "pinned"
        | ContextEvent.Unpinned _ -> "unpinned"
        | ContextEvent.Collapsed _ -> "collapsed")

let private done' (summary: string) = TurnResult.Done(summary, [], None)

[<Tests>]
let tests =
    testList
        "CodeAct compaction"
        [
            testTask "a reply whose prompt reached the threshold is summarized while the step's actions run" {
                do!
                    compacting
                        "background"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: one\nprint one"
                                        Say.sized 17000 (Say.cell "// intent: two\nprint two")
                                        Say.text summaryText
                                        Say.finish "Done"
                                    ]

                                do! kickoff f "Count the sheep"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (done' "Done") "done"

                                let requests = f.Model.Requests
                                Expect.equal requests.Length 4 "two calls, the summary, the last call"
                                let summary = requests[2]
                                Expect.isTrue (isSummary summary) "the third request asks for a summary"

                                // The second call's request as it was sent: the provider's cache hits.
                                Expect.equal
                                    (withoutInstruction summary |> withOutput requests[1].MaxOutputTokens)
                                    requests[1]
                                    "the summary request is the second call's, then the instruction"

                                Expect.equal summary.MaxOutputTokens (Some 8192) "the summary's own limit"

                                Expect.equal
                                    (kinds f)
                                    [
                                        "framed"
                                        "reply 1"
                                        "receipt 1"
                                        "reply 2"
                                        "receipt 2"
                                        "collapsed"
                                        "reply 3"
                                        "receipt 3"
                                    ]
                                    "recorded before the next call"

                                let last = requestText requests[3]
                                Expect.equal requests[3].System requests[1].System "the prefix is never rewritten"
                                Expect.stringContains last summaryText "the summary"
                                Expect.isFalse (last.Contains "Count the sheep") "the prompt is summarized"
                                Expect.stringContains last "print two" "the newest step stays"

                                match collapses f with
                                | [ collapse ] ->
                                    match collapse.Summary with
                                    | [ Piece.Quote quote ] -> Expect.equal quote.Text summaryText "the summary"
                                    | other -> failtest $"{other}"
                                | other -> failtest $"{other}"

                                do! expectReplays f "s1"
                            })
                        ()
            }

            testTask "a prompt over the threshold before a call is compacted first" {
                // 10% of 5000 tokens: less than the instructions and tools alone, so every step but the
                // first (nothing to summarize yet) compacts.
                do!
                    compactingIn
                        5000
                        "threshold"
                        [ "codeact.compaction.threshold", Json.int 10 ]
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: one\nprint one"
                                        Say.text summaryText
                                        Say.finish "Done"
                                    ]

                                do! kickoff f "Count the sheep"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (done' "Done") "done"

                                let requests = f.Model.Requests
                                Expect.equal requests.Length 3 "the first call, the summary, the second call"
                                Expect.isTrue (isSummary requests[1]) "compacted before the second call"

                                Expect.equal
                                    (withoutInstruction requests[1] |> withOutput requests[2].MaxOutputTokens).System
                                    requests[2].System
                                    "the same prefix"

                                let second = requestText requests[2]
                                Expect.stringContains second summaryText "with the summary"
                                Expect.isFalse (second.Contains "Count the sheep") "the prompt is summarized"
                                do! expectReplays f "s1"
                            })
                        ()
            }

            testTask "a request that overflows is compacted and sent again; the summary reads an earlier call's cache" {
                do!
                    compacting
                        "overflow"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: one\nprint one"
                                        Say.cell "// intent: two\nprint two"
                                        Say.failed (
                                            ChatError.ContextOverflow
                                                {
                                                    Tokens = Some 25000
                                                    Limit = Some window
                                                    Message = "too long"
                                                }
                                        )
                                        Say.text summaryText
                                        Say.finish "Done"
                                    ]

                                do! kickoff f "Count the sheep"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (done' "Done") "done"

                                let requests = f.Model.Requests
                                Expect.equal requests.Length 5 "the overflow, the summary, the call again"
                                Expect.isTrue (isSummary requests[3]) "the summary after the overflow"

                                // The newest history is small: the cut keeps the second step, so the summary
                                // request is the second call's.
                                Expect.equal
                                    (withoutInstruction requests[3] |> withOutput requests[1].MaxOutputTokens)
                                    requests[1]
                                    "the second call's request, then the instruction"

                                let again = requestText requests[4]
                                Expect.stringContains again summaryText "sent again with the summary"
                                Expect.isFalse (again.Contains "print one") "the first step is summarized"
                                Expect.stringContains again "print two" "the second stays"
                            })
                        ()
            }

            testTask "a cell asks to compact (history.compact) with what to keep; the next step compacts first" {
                do!
                    compacting
                        "asked"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: one\nprint one"
                                        Say.cell "// intent: compact\ncall r = history.compact [\"the plan\"]\nr"
                                        Say.text summaryText
                                        Say.finish "Done"
                                    ]

                                do! kickoff f "Count the sheep"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (done' "Done") "done"

                                let receipt = text (receipts f "s1").[1]
                                Expect.stringContains receipt "compacted before your next step" "the binding's answer"

                                let requests = f.Model.Requests
                                Expect.equal requests.Length 4 "the summary before the third call"
                                Expect.isTrue (isSummary requests[2]) "asked for"

                                Expect.stringContains
                                    (requestText requests[2])
                                    "Keep in particular: the plan"
                                    "the focus"

                                Expect.equal (collapses f).Length 1 "one collapse"
                                Expect.stringContains (requestText requests[3]) summaryText "used"
                                do! expectReplays f "s1"
                            })
                        ()
            }

            testTask
                "after compaction, history.list names the collapsed range and history.read gives its originals back (T201b)" {
                do!
                    compacting
                        "read-back"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: one\nprint one"
                                        Say.cell "// intent: compact\ncall r = history.compact\nr"
                                        Say.text summaryText
                                        Say.cell "// intent: list\ncall l = history.list\nl"
                                        Say.cell
                                            "// intent: read\ncall t = history.read [\"r1\", {\"maxChars\": 100000}]\nt"
                                        Say.cell "// intent: missing\ncall m = history.read [\"r9\"]\nm"
                                        Say.cell "// intent: open\ncall h = history.open [\"r1\"]\nh"
                                        Say.cell
                                            "// intent: read by handle\ncall th = history.read [\"$h\", {\"maxChars\": 100000}]\nth"
                                        Say.cell "// intent: recall the handle\ncall back = recall [\"t1-s6-1\"]\nback"
                                        Say.finish "Done"
                                    ]

                                do! kickoff f "Count the sheep"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (done' "Done") "done"

                                let rs = receipts f "s1"
                                let listed = text rs[2]
                                Expect.stringContains listed "\"r1\"" "the range's id"
                                Expect.stringContains listed summaryText "its summary"

                                let read = text rs[3]
                                Expect.stringContains read "Count the sheep" "the prompt, read back"
                                Expect.stringContains read "print one" "the first cell, read back"

                                Expect.stringContains (text rs[4]) "notFound" "a range that is not there"
                                let opened = text rs[5]
                                Expect.stringContains opened "HistoryRange" "a handle of its type"
                                Expect.stringContains opened "r1, entries" "its preview"
                                Expect.stringContains opened ": HistoryRange] r1, entries" "by reference (Piece.Ref)"

                                Expect.stringContains
                                    (text rs[7])
                                    "a handle of the host, which has no JSON form"
                                    "recall says why"

                                Expect.isFalse (opened.Contains "h-") "never its id"
                                Expect.stringContains (text rs[6]) "Count the sheep" "read through the handle"
                                do! expectReplays f "s1"
                            })
                        ()
            }

            testTask "a cell that asks and finishes: the collapse is recorded before the turn ends" {
                do!
                    compacting
                        "asked-at-end"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: one\nprint one"
                                        Say.cell
                                            "// intent: compact and finish\ncall r = history.compact\nfinish {\"kind\":\"done\",\"summary\":\"Done\"}"
                                        Say.text summaryText
                                        Say.finish "Again"
                                    ]

                                do! kickoff f "Count the sheep"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (done' "Done") "done"
                                Expect.equal (collapses f).Length 1 "compacted at the turn's end"
                                Expect.isTrue (isSummary f.Model.Requests[2]) "the summary"

                                do! prompt f "s1" "p2" "Once more"
                                let! next = ended f "s1" 2
                                Expect.equal (completed next) (done' "Again") "the next turn"
                                let first = requestText f.Model.Requests[3]
                                Expect.stringContains first summaryText "starts compacted"
                                Expect.isFalse (first.Contains "Count the sheep") "summarized"
                            })
                        ()
            }

            testTask "the window filled during a reply: its calls run, and the next call compacts first" {
                do!
                    compacting
                        "window"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: one\nprint one"
                                        Reply.Parts(
                                            [
                                                Say.call
                                                    "cell"
                                                    (Json.Object [ "code", Json.String "// intent: two\nprint two" ])
                                            ],
                                            StopReason.ContextWindow
                                        )
                                        Say.text summaryText
                                        Say.finish "Done"
                                    ]

                                do! kickoff f "Count the sheep"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (done' "Done") "done"
                                Expect.stringContains (text (receipts f "s1").[1]) "two" "the cut reply's call ran"
                                Expect.isTrue (isSummary f.Model.Requests[2]) "compacted before the next call"
                                Expect.stringContains (requestText f.Model.Requests[3]) summaryText "used"
                            })
                        ()
            }

            testTask "a summary that fails leaves the history as it was, and the turn goes on" {
                do!
                    compacting
                        "summary-fails"
                        []
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: one\nprint one"
                                        Say.sized 17000 (Say.cell "// intent: two\nprint two")
                                        Say.failed (ChatError.InvalidRequest "no")
                                        Say.sized 17500 (Say.cell "// intent: three\nprint three")
                                        Say.finish "Done"
                                    ]

                                do! kickoff f "Count the sheep"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (done' "Done") "done"

                                let requests = f.Model.Requests
                                Expect.equal requests.Length 5 "no second summary in the turn"
                                Expect.isTrue (isSummary requests[2]) "the summary that failed"
                                Expect.isEmpty (collapses f) "nothing collapsed"
                                Expect.stringContains (requestText requests[4]) "Count the sheep" "all of it"
                                do! expectReplays f "s1"
                            })
                        ()
            }

            testTask "compaction off: cells cannot ask, and an overflow fails the turn" {
                do!
                    compacting
                        "off"
                        [ "codeact.compaction.mode", Json.String "off" ]
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.sized 17000 (Say.cell "// intent: ask\ncall r = history.compact\nr")
                                        Say.failed (
                                            ChatError.ContextOverflow
                                                {
                                                    Tokens = None
                                                    Limit = None
                                                    Message = "too long"
                                                }
                                        )
                                    ]

                                do! kickoff f "Count the sheep"
                                let! ending = ended f "s1" 1
                                Expect.stringContains (failedWith ending) "compaction is off" "fails"
                                Expect.stringContains (text (receipts f "s1").[0]) "compaction is off" "refused"
                                Expect.equal f.Model.Requests.Length 2 "no summary"
                                Expect.isEmpty (collapses f) "nothing collapsed"
                            })
                        ()
            }

            testTask "mode overflow: the threshold starts nothing" {
                do!
                    compacting
                        "overflow-only"
                        [ "codeact.compaction.mode", Json.String "overflow" ]
                        (fun f ->
                            task {
                                f.Model.AddAll
                                    [
                                        Say.cell "// intent: one\nprint one"
                                        Say.sized 19000 (Say.cell "// intent: two\nprint two")
                                        Say.finish "Done"
                                    ]

                                do! kickoff f "Count the sheep"
                                let! ending = ended f "s1" 1
                                Expect.equal (completed ending) (done' "Done") "done"
                                Expect.equal f.Model.Requests.Length 3 "no summary"
                            })
                        ()
            }
        ]
