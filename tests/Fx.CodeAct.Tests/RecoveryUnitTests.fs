/// Crash recovery's pure parts: the step journal's JSON (version 1, kept), finding the call in doubt in
/// the logs, the decision card and its answers, the receipts of each decision, and kept values.
module Fx.CodeAct.Tests.RecoveryUnitTests

open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Models
open Fx.Envd
open Fx.Store
open Fx.Sessions
open Fx.Prompt
open Fx.CodeAct

let private context = ContextLog.stream "s1"
let private steps = StepJournal.stream "s1"

let private meta: EventMeta =
    {
        At = Instant.ofUnixMicroseconds 0L
        By = Attribution.system "test"
        Key = IdempotencyKey "k"
        Cause = None
    }

let private recorded (seq: int64) (event: StepEvent) : Recorded<StepEvent> =
    {
        Stream = steps
        Seq = seq
        Event = event
        Meta = meta
    }

let private journal (events: StepEvent list) =
    events |> List.mapi (fun i e -> recorded (int64 i + 1L) e)

let private item (stream: StreamId) (seq: int64) (event: ContextEvent) =
    LogItem.Context(EventRef.create stream seq, event)

let private toolCall (id: string) (code: string) : ToolCall =
    {
        Id = id
        Name = "cell"
        Arguments = Json.serialize (Json.Object [ "code", Json.String code ])
        State = None
    }

let private reply (turn: int) (step: int) (calls: ToolCall list) =
    ContextEvent.Replied
        {
            Turn = turn
            Step = step
            Shown = []
            Parts = calls |> List.map Part.ToolCall
            Used = None
        }

let private receipt (turn: int) (step: int) (call: string) =
    ContextEvent.Returned
        {
            Turn = turn
            Step = step
            Call = Some call
            Name = "cell"
            Pieces = [ Piece.Text "ok" ]
            IsError = false
            Restarted = None
            Approvals = []
        }

let private ref' (turn: int) (step: int) (index: int) (id: string) (attempt: int) : CallRef =
    {
        Turn = turn
        Step = step
        Index = index
        Id = id
        Attempt = attempt
    }

let private started (call: CallRef) : StepEvent =
    StepEvent.CallStarted
        {
            Call = call
            Tool = "cell"
            Cell = Some call.Name
            Intent = Some "migrate"
            Fingerprint = StepJournal.fingerprint "cell" "{}"
            Snapshot = None
        }

/// One event of every kind, as version 1 writes them.
let private everyKind: StepEvent list =
    let call = ref' 1 2 1 "call-1" 1

    [
        StepEvent.KernelLaunched 1
        StepEvent.CallStarted
            {
                Call = call
                Tool = "cell"
                Cell = Some "t1-s2-1"
                Intent = Some "run the migration"
                Fingerprint = StepJournal.fingerprint "cell" """{"code":"x"}"""
                Snapshot = Some { Commit = "c0ffee"; Tree = "7ree" }
            }
        StepEvent.HostCallStarted
            {
                Call = call
                Index = 1
                Binding = "shell.run"
                Args = """["dotnet test"]"""
                Approved = []
            }
        StepEvent.HostCallEnded
            {
                Call = call
                Index = 1
                Failed = false
                Summary = """{"exitCode":0}"""
            }
        StepEvent.Printed { Call = call; Text = "migrating…\n" }
        StepEvent.ValueKept
            {
                Call = call
                Cell = "t1-s2-1"
                Value = KeptJson.Inline(Json.Object [ "rows", Json.int 3 ])
                Type = "Object"
            }
        StepEvent.ValueKept
            {
                Call = { call with Attempt = 2 }
                Cell = "t1-s2-1-2"
                Value =
                    KeptJson.Stored
                        {
                            Hash = BlobHash.ofBytes (System.Text.Encoding.UTF8.GetBytes "[1,2,3]")
                            Size = 7L
                        }
                Type = "Array"
            }
        StepEvent.ValueLost
            {
                Call = call
                Cell = "t1-s2-1"
                Type = "Function"
                Reason = "a Function has no JSON form"
            }
    ]

let private card (output: string) (calls: HostCallSeen list) : OutcomeUnknown =
    let call = ref' 1 2 1 "call-1" 1

    let halted: Halted =
        {
            Turn = 1
            Step = 2
            Call = toolCall "call-1" "// intent: migrate\nrun()"
            Started =
                match started call with
                | StepEvent.CallStarted s -> s
                | _ -> failwith "started"
            NotRun = []
        }

    Recovery.card
        halted
        {
            Output = output
            HostCalls = calls
            Changes =
                Some
                    [
                        {
                            Path = "db/schema.sql"
                            Kind = ChangeKind.Modified
                        }
                    ]
            ChangesNote = None
        }

let private text (pieces: Piece list) =
    pieces |> List.map Piece.render |> String.concat "\n"

[<Tests>]
let tests =
    testList
        "CodeAct recovery units"
        [
            test "the step journal's events read back what version 1 wrote" {
                let written =
                    everyKind
                    |> List.map (fun e -> Json.serializeCanonical (StepCodecs.event.Encode e) + "\n")
                    |> String.concat ""

                let kept = Golden.kept "step-events-v1.jsonl" written

                let read =
                    kept.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun line ->
                        match Codec.decodeText StepCodecs.event line with
                        | Ok event -> event
                        | Error issues -> failtest (Issue.render issues))
                    |> List.ofArray

                Expect.equal read everyKind "every kind reads back"
                Expect.equal written kept "and writes the same"
            }

            test "the step journal's kernelEnded events read back as they were first written" {
                let ends =
                    [
                        KernelEnd.Exited(Some 137, "")
                        KernelEnd.Exited(None, "panic: out of memory\n")
                        KernelEnd.NotStarted "the component bun is not installed"
                        KernelEnd.Killed
                        KernelEnd.HostStopped
                    ]
                    |> List.mapi (fun i how -> StepEvent.KernelEnded { Launch = i + 1; How = how })

                let written =
                    ends
                    |> List.map (fun e -> Json.serializeCanonical (StepCodecs.event.Encode e) + "\n")
                    |> String.concat ""

                let kept = Golden.kept "step-events-kernel-ended-v1.jsonl" written

                let read =
                    kept.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun line ->
                        match Codec.decodeText StepCodecs.event line with
                        | Ok event -> event
                        | Error issues -> failtest (Issue.render issues))
                    |> List.ofArray

                Expect.equal read ends "every way a kernel ends reads back"
                Expect.equal written kept "and writes the same"
            }

            test "the step journal's job events read back as they were first written" {
                let call = ref' 1 2 1 "call-1" 1

                let jobs =
                    [
                        StepEvent.JobStarted
                            {
                                Job = "j1"
                                Name = "build"
                                Command = "dotnet build"
                                Cwd = Some "src"
                                Call = Some call
                            }
                        StepEvent.JobStarted
                            {
                                Job = "j2"
                                Name = "gh pr checks --watch"
                                Command = "gh pr checks --watch"
                                Cwd = None
                                Call = None
                            }
                        StepEvent.JobEnded
                            {
                                Job = "j1"
                                How = JobEnd.Exited 1
                                Bytes = Some 2048L
                            }
                        StepEvent.JobEnded
                            {
                                Job = "j2"
                                How = JobEnd.Killed
                                Bytes = Some 0L
                            }
                        StepEvent.JobEnded
                            {
                                Job = "j3"
                                How = JobEnd.Failed "could not start /bin/sh: not found"
                                Bytes = None
                            }
                        StepEvent.JobEnded
                            {
                                Job = "j4"
                                How = JobEnd.Lost
                                Bytes = None
                            }
                        StepEvent.JobEnded
                            {
                                Job = "j5"
                                How = JobEnd.Reported "`make` exited with code 0; it printed nothing."
                                Bytes = None
                            }
                    ]

                let written =
                    jobs
                    |> List.map (fun e -> Json.serializeCanonical (StepCodecs.event.Encode e) + "\n")
                    |> String.concat ""

                let kept = Golden.kept "step-events-jobs-v1.jsonl" written

                let read =
                    kept.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun line ->
                        match Codec.decodeText StepCodecs.event line with
                        | Ok event -> event
                        | Error issues -> failtest (Issue.render issues))
                    |> List.ofArray

                Expect.equal read jobs "every job event reads back"
                Expect.equal written kept "and writes the same"
            }

            test "a kernel's end in words: signals, exit codes, its standard error" {
                Expect.equal
                    (KernelEnds.describe Fx.Sdk.Os.Linux (KernelEnd.Exited(Some 137, "")))
                    "was killed by signal 9 (SIGKILL)"
                    "a signal"

                Expect.equal
                    (KernelEnds.describe Fx.Sdk.Os.Windows (KernelEnd.Exited(Some 137, "")))
                    "exited with code 137"
                    "no signals on Windows"

                Expect.equal
                    (KernelEnds.describe Fx.Sdk.Os.MacOs (KernelEnd.Exited(Some 1, "error: boom\n")))
                    "exited with code 1; its standard error ended with: error: boom"
                    "a code and the end of standard error"

                let long = String.replicate 400 "x"
                let quoted = KernelEnds.describe Fx.Sdk.Os.Linux (KernelEnd.Exited(None, long))

                Expect.isTrue
                    (quoted.EndsWith("…" + String.replicate (KernelEnds.StderrChars - 1) "x"))
                    "the end, bounded"

                Expect.equal
                    (KernelEnds.describe Fx.Sdk.Os.Linux KernelEnd.HostStopped)
                    "stopped when the host restarted"
                    "a restart"
            }

            test "a call that started and has no receipt is in doubt; the calls after it did not run" {
                let calls =
                    [
                        toolCall "a" "// intent: one"
                        toolCall "b" "// intent: two"
                        toolCall "c" "// intent: three"
                    ]

                let items = [ item context 1L (reply 1 1 calls); item context 2L (receipt 1 1 "a") ]

                let halted =
                    Recovery.find context 1 items (journal [ started (ref' 1 1 1 "a" 1); started (ref' 1 1 2 "b" 1) ])

                match halted with
                | Some halted ->
                    Expect.equal halted.Call.Id "b" "the call in doubt"
                    Expect.equal halted.Started.Call.Index 2 "its place"
                    Expect.equal (halted.NotRun |> List.map (fun (i, c) -> i, c.Id)) [ 3, "c" ] "not run"
                | None -> failtest "nothing in doubt"
            }

            test "nothing is in doubt when every started call has its receipt, or none started" {
                let calls = [ toolCall "a" "x"; toolCall "b" "y" ]
                let items = [ item context 1L (reply 1 1 calls); item context 2L (receipt 1 1 "a") ]
                Expect.isNone (Recovery.find context 1 items (journal [ started (ref' 1 1 1 "a" 1) ])) "answered"
                Expect.isNone (Recovery.find context 1 items (journal [])) "never started"
                Expect.isNone (Recovery.find context 2 items (journal [ started (ref' 1 1 2 "b" 1) ])) "another turn"
                Expect.isNone (Recovery.find context 1 [] (journal [])) "no reply yet"
            }

            test "the latest attempt is in doubt; a fork's parent's turns are not the session's" {
                let calls = [ toolCall "a" "x" ]
                let items = [ item context 1L (reply 1 1 calls) ]

                let halted =
                    Recovery.find context 1 items (journal [ started (ref' 1 1 1 "a" 1); started (ref' 1 1 1 "a" 2) ])

                Expect.equal (halted |> Option.map (fun h -> h.Started.Call.Attempt)) (Some 2) "the rerun"

                Expect.equal
                    (halted |> Option.map (fun h -> Recovery.requestId h.Started.Call))
                    (Some "unknown-t1-s1-1-2")
                    "a request of its own"

                let parent = ContextLog.stream "p"
                let forked = [ item parent 1L (reply 1 1 calls) ]

                Expect.isNone
                    (Recovery.find context 1 forked (journal [ started (ref' 1 1 1 "a" 1) ]))
                    "the parent's reply"
            }

            test "the card shows the call and the evidence; answers name one of its options" {
                let decision =
                    card
                        "migrating…\n"
                        [
                            {
                                Binding = "shell.run"
                                Args = """["make migrate"]"""
                                Ended = None
                            }
                        ]

                Expect.stringContains decision.Question "cell t1-s2-1 (intent: migrate)" "what ran"
                Expect.equal decision.Call.Action "// intent: migrate\nrun()" "its code"
                Expect.equal decision.Scope "session" "a session decision"

                Expect.equal
                    (decision.Options |> List.map (fun o -> o.Id))
                    [ "completed"; "failed"; "rerun"; "inspect" ]
                    "options"

                let info = RequestKind.info Recovery.kind
                let payload = Recovery.payload.Encode decision
                Expect.isEmpty (info.CheckPayload payload) "the payload checks"

                Expect.isEmpty
                    (info.CheckAnswer payload (RequestKind.answer Recovery.kind { Option = "rerun"; Note = None }))
                    "an option"

                Expect.isNonEmpty
                    (info.CheckAnswer payload (RequestKind.answer Recovery.kind { Option = "maybe"; Note = None }))
                    "not an option"

                let request =
                    Recovery.request
                        {
                            Turn = 1
                            Step = 2
                            Call = toolCall "call-1" "x"
                            Started =
                                (match started (ref' 1 2 1 "call-1" 1) with
                                 | StepEvent.CallStarted s -> s
                                 | _ -> failwith "started")
                            NotRun = []
                        }
                        decision

                Expect.equal request.Id "unknown-t1-s2-1" "derived from the call"
                Expect.equal request.Kind Recovery.KindId "kind"
                Expect.equal request.Reply Reply.ToOpener "the paused turn waits for it"
                Expect.equal request.Lifetime Lifetime.Turn "it ends with the turn"
            }

            test "each decision makes a receipt that says what people decided, with the evidence" {
                let decision =
                    card
                        "migrating…\n"
                        [
                            {
                                Binding = "shell.run"
                                Args = """["make migrate"]"""
                                Ended = Some "exit 0"
                            }
                        ]

                let answered (option: string) (note: string option) =
                    Recovery.decided (
                        Resolution.Answered(RequestKind.answer Recovery.kind { Option = option; Note = note })
                    )

                let completed, error =
                    Recovery.receipt 20000 decision (answered "completed" (Some "the table is there"))

                Expect.isFalse error "completed is no error"
                let shown = text completed

                Expect.stringContains
                    shown
                    "The user decided that it completed. Their note: the table is there"
                    "decided"

                Expect.stringContains shown "migrating…" "its output"
                Expect.stringContains shown "shell.run [\"make migrate\"] → exit 0" "its host calls"
                Expect.stringContains shown "modified db/schema.sql" "the changes"

                for option, expected in [ "failed", "decided that it failed"; "inspect", "inspect what it did" ] do
                    let pieces, error = Recovery.receipt 20000 decision (answered option None)
                    Expect.isTrue error $"{option} is an error"
                    Expect.stringContains (text pieces) expected option

                Expect.equal (answered "rerun" (Some "go")) (Decided.Rerun(Some "go")) "rerun"

                let withdrawn = Recovery.decided (Resolution.Cancelled "someone withdrew it")
                let pieces, error = Recovery.receipt 20000 decision withdrawn
                Expect.isTrue error "withdrawn is an error"
                Expect.stringContains (text pieces) "nobody decided what happened (someone withdrew it)" "why"

                let long = card (String.replicate 3000 "x") []
                let pieces, _ = Recovery.receipt 300 long (answered "completed" None)
                Expect.isLessThanOrEqual (text pieces).Length 700 "bounded"
            }

            test "recall finds a kept value by its cell or its call, and says why one was lost" {
                let call = ref' 1 1 1 "call-1" 1
                let other = ref' 1 2 1 "call-2" 1

                let events =
                    journal
                        [
                            StepEvent.ValueKept
                                {
                                    Call = call
                                    Cell = "t1-s1-1"
                                    Value = KeptJson.Inline(Json.int 1)
                                    Type = "number"
                                }
                            StepEvent.ValueLost
                                {
                                    Call = other
                                    Cell = "t1-s2-1"
                                    Type = "Function"
                                    Reason = "a Function has no JSON form"
                                }
                            StepEvent.ValueKept
                                {
                                    Call = call
                                    Cell = "t1-s1-1"
                                    Value = KeptJson.Inline(Json.int 2)
                                    Type = "number"
                                }
                        ]

                let valueOf (result: Result<KeptValue, string>) =
                    result |> Result.map (fun kept -> kept.Value)

                Expect.equal (valueOf (Recall.find events "t1-s1-1")) (Ok(KeptJson.Inline(Json.int 2))) "the latest"
                Expect.equal (valueOf (Recall.find events "call-1")) (Ok(KeptJson.Inline(Json.int 2))) "by call"
                Expect.equal (Recall.kept events |> List.map (fun k -> k.Cell)) [ "t1-s1-1" ] "one per cell"

                match Recall.find events "t1-s2-1" with
                | Error reason -> Expect.stringContains reason "no JSON form" "why"
                | Ok _ -> failtest "lost"

                match Recall.find events "nope" with
                | Error reason -> Expect.stringContains reason "no value" "unknown"
                | Ok _ -> failtest "unknown"
            }
        ]
