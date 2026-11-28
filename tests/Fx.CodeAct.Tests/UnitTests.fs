/// The strategy's pure parts: tools from codecs, the loop guard, line edits, receipts, the merged
/// history, the checks of a result, questions and the recorded surface.
module Fx.CodeAct.Tests.UnitTests

open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Models
open Fx.Envd
open Fx.Kernel
open Fx.Interpreters
open Fx.Sessions
open Fx.Prompt
open Fx.Variants
open Fx.CodeAct

let private call (name: string) (arguments: string) : ToolCall =
    {
        Id = "c1"
        Name = name
        Arguments = arguments
        State = None
    }

let private meta (cause: EventRef option) : EventMeta =
    {
        At = Instant.ofUnixMicroseconds 0L
        By = Attribution.system "test"
        Key = IdempotencyKey "k"
        Cause = cause
    }

[<Tests>]
let tests =
    testList
        "CodeAct units"
        [
            test "the surfaces' tools come from codecs" {
                let cellsOnly = Tools.specs (Tools.current ActionSurface.CellsOnly) "TypeScript"
                Expect.equal (cellsOnly |> List.map (fun t -> t.Name)) [ "cell" ] "cells-only"

                Expect.equal
                    (Tools.specs (Tools.current ActionSurface.Hybrid) "TypeScript"
                     |> List.map (fun t -> t.Name))
                    [ "cell"; "read"; "edit"; "bash" ]
                    "hybrid"

                Expect.equal
                    (Json.serializeCanonical cellsOnly.Head.Parameters)
                    (Json.serializeCanonical (Schema.toJsonSchema Tools.cellCodec.Schema))
                    "the schema of the codec"

                Expect.stringContains cellsOnly.Head.Description "TypeScript code" "the language"
                Expect.stringContains cellsOnly.Head.Description "`texts`" "texts, from version 2"
            }

            test "every version of the tools renders as it did: conversations keep theirs (golden)" {
                let render (version: int) =
                    Json.Object
                        [
                            for surface in [ ActionSurface.CellsOnly; ActionSurface.Hybrid ] ->
                                string surface,
                                Json.Array(
                                    Tools.specs { Surface = surface; Version = version } "typescript"
                                    |> List.map ToolSpec.codec.Encode
                                )
                        ]
                    |> Json.serializeIndented

                // What conversations framed before T140 were sent: never rewritten.
                let v1 = render 1
                Expect.equal v1 (Golden.kept "tools-v1.json" v1) "version 1, as conversations framed with it got it"
                Golden.expect $"tools-v{Tools.Version}.json" (render Tools.Version)
            }

            test "texts decode with checked names; invalid ones are refused, saying which" {
                Expect.equal
                    (Tools.decode
                        ActionSurface.CellsOnly
                        (call "cell" """{"code":"x","texts":{"posix":"module ``A``\n","msg_2":"`fix`"}}"""))
                    (Ok(
                        ToolAction.Cell
                            {
                                Code = "x"
                                Texts = [ "posix", "module ``A``\n"; "msg_2", "`fix`" ]
                            }
                    ))
                    "two texts, in order"

                Expect.equal
                    (Tools.decode ActionSurface.Hybrid (call "cell" """{"code":"x","texts":null}"""))
                    (Ok(ToolAction.Cell { Code = "x"; Texts = [] }))
                    "no texts"

                let refused (arguments: string) (expected: string list) =
                    match Tools.decode ActionSurface.CellsOnly (call "cell" arguments) with
                    | Error message ->
                        Expect.stringContains message "nothing ran" "nothing ran"

                        for text in expected do
                            Expect.stringContains message text arguments
                    | Ok action -> failtest $"{arguments}: {action}"

                refused
                    """{"code":"x","texts":{"1x":"a","a-b":"b"}}"""
                    [ "$.texts[\"1x\"]: \"1x\" is not a name"; "\"a-b\" is not a name" ]

                let g = Fx.Product.kernelGlobal
                refused $"{{\"code\":\"x\",\"texts\":{{\"{g}\":\"a\"}}}}" [ $"\"{g}\" is a name the kernel keeps" ]
                refused """{"code":"x","texts":{"Out":"a"}}""" [ "\"Out\" is a name the kernel keeps" ]
                refused """{"code":"x","texts":{"a":1}}""" [ "$.texts.a: expected string" ]

                let many =
                    [ for i in 1 .. Tools.MaxTexts + 1 -> $"\"t{i}\":\"\"" ] |> String.concat ","

                refused
                    $"{{\"code\":\"x\",\"texts\":{{{many}}}}}"
                    [
                        $"$.texts: expected at most {Tools.MaxTexts} texts, got {Tools.MaxTexts + 1}"
                    ]

                let long = String.replicate (Tools.MaxNameChars + 1) "a"
                refused $"{{\"code\":\"x\",\"texts\":{{\"{long}\":\"\"}}}}" [ "is not a name" ]
            }

            test "tool calls decode into actions, or say why not" {
                Expect.equal
                    (Tools.decode ActionSurface.CellsOnly (call "cell" """{"code":"x"}"""))
                    (Ok(ToolAction.Cell { Code = "x"; Texts = [] }))
                    "a cell"

                Expect.equal
                    (Tools.decode ActionSurface.Hybrid (call "read" """{"path":"a.txt","offset":2}"""))
                    (Ok(
                        ToolAction.Read
                            {
                                Path = "a.txt"
                                Offset = Some 2
                                Limit = None
                            }
                    ))
                    "a read"

                Expect.equal
                    (Tools.decode ActionSurface.Hybrid (call "edit" """{"path":"a","old":"x","new":"y"}"""))
                    (Ok(
                        ToolAction.Edit
                            {
                                Path = "a"
                                Old = "x"
                                New = "y"
                                All = false
                            }
                    ))
                    "an edit"

                match Tools.decode ActionSurface.CellsOnly (call "bash" """{"command":"ls"}""") with
                | Error message -> Expect.stringContains message "There is no tool named \"bash\"" "not on cells-only"
                | Ok action -> failtest $"{action}"

                match Tools.decode ActionSurface.Hybrid (call "edit" """{"path":"a"}""") with
                | Error message -> Expect.stringContains message "old" "the missing field"
                | Ok action -> failtest $"{action}"

                match Tools.decode ActionSurface.Hybrid (call "cell" "{\"code\": ") with
                | Error message -> Expect.stringContains message "not JSON" "cut arguments"
                | Ok action -> failtest $"{action}"
            }

            test "the loop guard warns one before the limit and stops at it; 0 turns it off" {
                let print = LoopGuard.fingerprint "cell" """{"code":"x"}""" [ Piece.Text "same" ]

                Expect.equal
                    print
                    (LoopGuard.fingerprint "cell" """{ "code" : "x" }""" [ Piece.Text "same" ])
                    "arguments compare canonically"

                Expect.notEqual
                    print
                    (LoopGuard.fingerprint "cell" """{"code":"x"}""" [ Piece.Text "other" ])
                    "results count"

                let g1, v1 = LoopGuard.observe 3 print LoopGuard.empty
                let g2, v2 = LoopGuard.observe 3 print g1
                let _, v3 = LoopGuard.observe 3 print g2
                Expect.equal [ v1; v2; v3 ] [ LoopVerdict.Fine; LoopVerdict.Warn 2; LoopVerdict.Stop 3 ] "verdicts"

                let off =
                    [ 1..5 ]
                    |> List.fold (fun (g, _) _ -> LoopGuard.observe 0 print g) (LoopGuard.empty, LoopVerdict.Fine)
                    |> snd

                Expect.equal off LoopVerdict.Fine "off"
            }

            test "an edit becomes the lines between the common head and tail" {
                Expect.equal
                    (Hybrid.lineEdit [ "a"; "b"; "c" ] [ "a"; "B"; "c" ])
                    { Line = 2; Count = 1; Lines = [ "B" ] }
                    "one line"

                Expect.equal
                    (Hybrid.lineEdit [ "a"; "c" ] [ "a"; "b"; "c" ])
                    { Line = 2; Count = 0; Lines = [ "b" ] }
                    "an insertion"

                Expect.equal
                    (Hybrid.lineEdit [ "a"; "b"; "b" ] [ "a"; "b" ])
                    { Line = 3; Count = 1; Lines = [] }
                    "a deletion"

                let applied =
                    TextPatch.applyText "x\r\ny\r\nz\r\n" [ Hybrid.lineEdit [ "x"; "y"; "z" ] [ "x"; "Y"; "z" ] ]

                Expect.equal applied (Ok("x\r\nY\r\nz\r\n", 3)) "line endings kept"
            }

            test "a read shows numbered lines as a file piece, and says how to read on" {
                let file: FileText =
                    {
                        Path = "src/a.fs"
                        Text = "one\ntwo\nthree\nfour\n"
                        Lines = 4
                        Size = 19L
                        Digest = Fx.Store.BlobHash.ofBytes [| 1uy |]
                    }

                match Receipts.read file (Some 2) (Some 2) 100 with
                | [ Piece.File view; Piece.Text more ] ->
                    Expect.equal view.Text "     2\ttwo\n     3\tthree" "numbered"
                    Expect.equal view.Lines [ LineRange.create 2 3 ] "the lines shown"
                    Expect.stringContains more "Lines 2–3 of 4. Read on with offset 4." "more"
                | other -> failtest $"{other}"

                match Receipts.read file None None 100 with
                | [ Piece.File view ] -> Expect.isEmpty view.Lines "the whole file"
                | other -> failtest $"{other}"

                match Receipts.read file None None 3 with
                | [ Piece.File _; Piece.Text more ] -> Expect.stringContains more "offset 4" "bounded"
                | other -> failtest $"{other}"
            }

            test "a cell's value shows as its binding's direct tool would, matched by its JSON's digest" {
                let info: ValueInfo =
                    {
                        Type = "object"
                        Preview = { Text = "{…}"; Truncated = true }
                    }

                let ran: RunResult =
                    {
                        Command = "ls"
                        ExitCode = Some 0
                        Stdout = "a.txt\nb.txt\n"
                        Stderr = ""
                        Truncated = false
                        TimedOut = false
                    }

                let ranJson = CoreBindings.Codecs.runResult.Encode ran

                let file: FileText =
                    {
                        Path = "a.txt"
                        Text = "one\ntwo\n"
                        Lines = 2
                        Size = 8L
                        Digest = Fx.Store.BlobHash.ofBytes [| 1uy |]
                    }

                let fileJson = CoreBindings.Codecs.fileText.Encode file

                let returned =
                    [ Returned.ofResult "shell.run" ranJson; Returned.ofResult "fs.read" fileJson ]

                Expect.equal
                    (CellValue.ofValue info (Some(Json.String "hi")) [])
                    (CellValue.Text "hi")
                    "a string is its text"

                Expect.equal (CellValue.ofValue info (Some ranJson) returned) (CellValue.Ran ran) "a command's result"
                Expect.equal (CellValue.ofValue info (Some fileJson) returned) (CellValue.File file) "a file read"

                Expect.equal
                    (CellValue.ofValue info (Some ranJson) [ Returned.ofResult "fs.read" fileJson ])
                    (CellValue.Preview info)
                    "the same shape, not a result of this cell: the preview"

                Expect.equal (CellValue.ofValue info None returned) (CellValue.Preview info) "not encoded: the preview"
            }

            test "a cell's receipt shows a string, a command and a file as their tools would" {
                let limits: CellLimits =
                    {
                        ReceiptChars = 20000
                        OutputChars = 200
                        ReadLines = 100
                    }

                let result: CellResult =
                    {
                        CellId = "t1-s1-1"
                        Outcome =
                            CellOutcome.Completed(
                                Some
                                    {
                                        Type = "string"
                                        Preview = { Text = "\"…\""; Truncated = true }
                                    }
                            )
                        Output =
                            {
                                Text = ""
                                Truncated = false
                                TotalChars = 0L
                            }
                        Declared = [ "x" ]
                        Unassigned = []
                        Finished = false
                        Notified = 0
                        DurationMs = 0L
                    }

                let text (pieces: Piece list) =
                    pieces |> List.map Piece.render |> String.concat "\n"

                let long = String.replicate 100 "0123456789"

                let shown =
                    Receipts.cell limits KernelNote.Same (Some "x") Reads.none [] (Some(CellValue.Text long)) result
                    |> text

                Expect.stringContains shown "Value of Out[\"t1-s1-1\"] (string, 1000 chars):\n0123456789" "the text"
                Expect.stringContains shown "characters omitted" "cut in the middle at the output's limit"
                Expect.stringContains shown "Declared: x" "the names"

                let ran: RunResult =
                    {
                        Command = "ls"
                        ExitCode = Some 1
                        Stdout = "a.txt\n"
                        Stderr = "oops\n"
                        Truncated = false
                        TimedOut = false
                    }

                let shownRan =
                    Receipts.cell limits KernelNote.Same (Some "x") Reads.none [] (Some(CellValue.Ran ran)) result
                    |> text

                Expect.stringContains
                    shownRan
                    "(RunResult):\n$ ls\nexit code 1\na.txt\n[stderr]\noops"
                    "as bash shows it"

                let file: FileText =
                    {
                        Path = "a.txt"
                        Text = "one\ntwo\n"
                        Lines = 2
                        Size = 8L
                        Digest = Fx.Store.BlobHash.ofBytes [| 1uy |]
                    }

                match
                    Receipts.cell limits KernelNote.Same (Some "x") Reads.none [] (Some(CellValue.File file)) result
                with
                | [ Piece.Text head; Piece.File view; Piece.Text tail ] ->
                    Expect.stringContains head "(FileText): a.txt, 2 lines" "what it is"
                    Expect.equal view.Text "     1\tone\n     2\ttwo" "numbered lines, a file piece (seen)"
                    Expect.equal tail "Declared: x" "then the rest"
                | other -> failtest $"{other}"

                let preview =
                    Receipts.cell limits KernelNote.Same (Some "x") Reads.none [] None result
                    |> text

                Expect.stringContains
                    preview
                    "Value of Out[\"t1-s1-1\"] (string): \"…\""
                    "no value: the kernel's preview"
            }

            test "a stopped cell's receipt names what it never assigned and the calls it stopped waiting for approval" {
                let limits: CellLimits =
                    {
                        ReceiptChars = 20000
                        OutputChars = 200
                        ReadLines = 100
                    }

                let result (outcome: CellOutcome) : CellResult =
                    {
                        CellId = "t1-s1-1"
                        Outcome = outcome
                        Output =
                            {
                                Text = ""
                                Truncated = false
                                TotalChars = 0L
                            }
                        Declared = [ "tk"; "tl"; "c1" ]
                        Unassigned = [ "c1" ]
                        Finished = false
                        Notified = 0
                        DurationMs = 0L
                    }

                let waiting: HostCall list =
                    [
                        {
                            CellId = Some "t1-s1-1"
                            Binding = "shell.run"
                            Args = [ Value.Inline(Json.String "strace -f ls") ]
                        }
                        {
                            CellId = Some "t1-s1-1"
                            Binding = "fs.write"
                            Args = [ Value.Inline(Json.String "/etc/hosts"); Value.Inline(Json.String "x") ]
                        }
                    ]

                let text (pieces: Piece list) =
                    pieces |> List.map Piece.render |> String.concat "\n"

                let receipt outcome =
                    Receipts.cell limits KernelNote.Same (Some "x") Reads.none waiting None (result outcome)
                    |> text

                Expect.equal
                    (receipt CellOutcome.Cancelled)
                    (String.concat
                        "\n"
                        [
                            "The cell was cancelled."
                            "shell.run: strace -f ls (stopped while it waited for its approval)"
                            "fs.write: /etc/hosts (stopped while it waited for its approval)"
                            "Declared: tk, tl"
                            "Not assigned (the cell stopped first): c1"
                        ])
                    "the receipt"

                Expect.stringContains
                    (receipt (CellOutcome.TimedOut 600000))
                    "shell.run: strace -f ls (stopped while it waited for its approval)"
                    "a timeout too"

                let failed =
                    receipt (
                        CellOutcome.RuntimeError
                            {
                                Name = "Error"
                                Message = "boom"
                                Code = None
                                Location = None
                                Stack = ""
                            }
                    )

                Expect.isFalse (failed.Contains "stopped while it waited") "an error does not stop the calls"
                Expect.stringContains failed "Not assigned (the cell stopped first): c1" "unassigned after an error"
            }

            test "a host call is named by its binding and its first argument, cut" {
                let call (args: Value list) : HostCall =
                    {
                        CellId = None
                        Binding = "shell.run"
                        Args = args
                    }

                Expect.equal (Receipts.callTitle (call [])) "shell.run" "no arguments"

                Expect.equal
                    (Receipts.callTitle (call [ Value.Inline(Json.String "a\nb") ]))
                    "shell.run: a b"
                    "one line"

                let long =
                    Receipts.callTitle (call [ Value.Inline(Json.String(String.replicate 200 "x")) ])

                Expect.equal long ("shell.run: " + String.replicate Receipts.CallChars "x" + "…") "cut"

                Expect.equal
                    (Receipts.callTitle (call [ Value.Inline(Json.Object [ "a", Json.int 1 ]) ]))
                    "shell.run: {\"a\":1}"
                    "JSON"
            }

            test "a cell's commands share its timeout: options bounded, then refused once used up" {
                let command = Value.Inline(Json.String "make")

                let call (args: Value list) : HostCall =
                    {
                        CellId = Some "c"
                        Binding = "shell.run"
                        Args = args
                    }

                let optionsOf (bounded: HostCall) =
                    match bounded.Args with
                    | [ _; Value.Inline json ] -> Json.serializeCanonical json
                    | other -> failtest $"{other}"

                Expect.equal (optionsOf (CellCommands.bound 500 (call [ command ]))) """{"timeoutMs":500}""" "added"

                Expect.equal
                    (optionsOf (CellCommands.bound 500 (call [ command; Value.Inline Json.Null ])))
                    """{"timeoutMs":500}"""
                    "null options"

                Expect.equal
                    (optionsOf (
                        CellCommands.bound
                            500
                            (call [ command; Value.Inline(Json.Object [ "cwd", Json.String "src" ]) ])
                    ))
                    """{"cwd":"src","timeoutMs":500}"""
                    "added to the options"

                Expect.equal
                    (optionsOf (
                        CellCommands.bound
                            500
                            (call [ command; Value.Inline(Json.Object [ "timeoutMs", Json.int 9000 ]) ])
                    ))
                    """{"timeoutMs":500}"""
                    "lowered"

                Expect.equal
                    (optionsOf (
                        CellCommands.bound
                            500
                            (call [ command; Value.Inline(Json.Object [ "timeoutMs", Json.int 100 ]) ])
                    ))
                    """{"timeoutMs":100}"""
                    "a shorter one kept"

                let invalid = call [ command; Value.Inline(Json.String "fast") ]
                Expect.equal (CellCommands.bound 500 invalid) invalid "what does not decode is the binding's to refuse"

                Expect.equal (CellCommands.remaining 1000 System.TimeSpan.Zero) (Some 1000) "all of it"

                Expect.equal
                    (CellCommands.remaining 1000 (System.TimeSpan.FromMilliseconds 999.2))
                    None
                    "less than a millisecond left"

                Expect.equal
                    (CellCommands.remaining 1000 (System.TimeSpan.FromMilliseconds 400.0))
                    (Some 600)
                    "the rest"

                match CellCommands.usedUp 1000 with
                | HostCallError.Failed(code, message, _) ->
                    Expect.equal code "cellTimeout" "code"
                    Expect.stringContains message "1000 ms" "the timeout"
                | other -> failtest $"{other}"
            }

            test "context events go after the session event their cause names" {
                let session = Session.stream "s1"
                let context = ContextLog.stream "s1"

                let sessionEvent seq : Recorded<Event> =
                    {
                        Stream = session
                        Seq = seq
                        Event = Event.Closed(Attribution.system "test")
                        Meta = meta None
                    }

                let contextEvent seq cause : Recorded<ContextEvent> =
                    {
                        Stream = context
                        Seq = seq
                        Event = ContextEvent.Unpinned(string seq)
                        Meta = meta (Some(EventRef.create session cause))
                    }

                let refs =
                    ContextLog.merge
                        session
                        [ sessionEvent 1L; sessionEvent 2L; sessionEvent 3L ]
                        [ contextEvent 1L 1L; contextEvent 2L 1L; contextEvent 3L 3L ]
                        None
                    |> List.map (fun item -> string item.Ref)

                Expect.equal
                    refs
                    [
                        "session/s1#1"
                        "context/s1#1"
                        "context/s1#2"
                        "session/s1#2"
                        "session/s1#3"
                        "context/s1#3"
                    ]
                    "merged"

                let upTo =
                    ContextLog.merge
                        session
                        [ sessionEvent 1L; sessionEvent 2L ]
                        [ contextEvent 1L 1L; contextEvent 2L 2L ]
                        (Some 1L)
                    |> List.map (fun item -> string item.Ref)

                Expect.equal upTo [ "session/s1#1"; "context/s1#1" ] "a parent up to the fork point"
            }

            test "a result's checks: a summary, evidence when required, open requests, known jobs" {
                let empty = Session.initial

                let check policy result =
                    Ensure.all policy empty |> List.collect (fun c -> c result)

                Expect.isEmpty (check EvidencePolicy.Optional (TurnResult.Done("did it", [], None))) "fine"
                Expect.isNonEmpty (check EvidencePolicy.Optional (TurnResult.Done(" ", [], None))) "blank summary"
                Expect.isNonEmpty (check EvidencePolicy.Required (TurnResult.Done("did it", [], None))) "no evidence"
                Expect.isNonEmpty (check EvidencePolicy.Optional (TurnResult.NeedInput [ "r1" ])) "unknown request"
                Expect.isNonEmpty (check EvidencePolicy.Optional (TurnResult.Waiting([ "j1" ], "x"))) "unknown job"

                let issue =
                    check EvidencePolicy.Optional (TurnResult.NeedInput [ "r1" ]) |> List.head

                Expect.stringContains (string issue) "$.result.requests[0]" "at the result's path"
            }

            test "a question is an input request of its kind, bounded" {
                let request =
                    Questions.request
                        "q-1-2"
                        {
                            Question = String.replicate 9000 "a"
                            Reason = "reply"
                        }

                Expect.equal request.Kind Questions.KindId "kind"
                Expect.equal request.Reply Reply.AsInput "an input"
                Expect.equal request.Lifetime Lifetime.Session "across turns"

                match Questions.payload.Decode request.Payload with
                | Ok question -> Expect.isLessThanOrEqual question.Question.Length Questions.MaxChars "bounded"
                | Error issues -> failtest $"{issues}"

                Expect.isSome (RequestKinds.tryFind Questions.KindId Questions.kinds) "registered"
            }

            test "a conversation keeps the surface and the tools' version its frame recorded" {
                let sections =
                    Instructions.sections (Tools.current ActionSurface.Hybrid) "interpreter" None []

                Expect.equal
                    (Instructions.recordedTools sections)
                    (Some(Tools.current ActionSurface.Hybrid))
                    "hybrid, the current tools"

                Expect.equal
                    (Instructions.recordedTools (
                        Instructions.sections (Tools.current ActionSurface.CellsOnly) "x" None []
                    ))
                    (Some(Tools.current ActionSurface.CellsOnly))
                    "cells-only"

                let v1 =
                    {
                        Surface = ActionSurface.CellsOnly
                        Version = 1
                    }

                let framedV1 = Instructions.sections v1 "x" None []

                Expect.equal
                    (framedV1 |> List.find (fun s -> s.Id = "tools")).Version
                    (Some "cells-only")
                    "version 1 is named as frames before T140 recorded it"

                Expect.equal (Instructions.recordedTools framedV1) (Some v1) "a conversation framed with version 1"

                Expect.equal
                    (Tools.ofSectionVersion "hybrid/2")
                    (Some
                        {
                            Surface = ActionSurface.Hybrid
                            Version = 2
                        })
                    "hybrid/2"

                Expect.isNone (Tools.ofSectionVersion $"hybrid/{Tools.Version + 1}") "a version this host does not know"
                Expect.isNone (Tools.ofSectionVersion "cells/2") "an unknown surface"
                Expect.equal (Instructions.recordedTools []) None "none yet"

                Expect.equal
                    (sections |> List.map (fun s -> s.Id))
                    [ "instructions"; "tools"; "interpreter" ]
                    "the prefix's sections"
            }

            test "a sandbox that confines the hands' processes is described, from its policy" {
                let policy: Fx.Sdk.SandboxPolicy =
                    {
                        Workspace = "/src/app"
                        WorkspaceAccess = Fx.Sdk.WorkspaceAccess.ReadWrite
                        Writable = [ "/home/u/.fx/environments/e1/owned" ]
                        Readable = []
                        Protected = [ "/src/app/.git/hooks"; "/src/app/.git/config"; "/src/app/.fx" ]
                        Denied = [ "/home/u/.fx"; "/home/u/.ssh"; "/src/app/.env" ]
                        Temp = Some "/home/u/.fx/environments/e1/tmp"
                        Network = Fx.Sdk.NetworkAccess.Allowed
                    }

                let sections =
                    Instructions.sections (Tools.current ActionSurface.Hybrid) "interpreter" (Some policy) []

                Expect.equal
                    (sections |> List.map (fun s -> s.Id))
                    [ "instructions"; "tools"; "interpreter"; "sandbox" ]
                    "the sandbox last"

                let text = Instructions.sandbox policy

                Expect.stringContains
                    text
                    "except `.git/hooks`, `.git/config` and `.fx`: they stay read-only"
                    "what stays read-only, relative to the workspace"

                Expect.stringContains text "Device or resource busy" "how git's refusal reads"
                Expect.stringContains text "- `.env` cannot be read." "hidden in the workspace"
                Expect.stringContains text "Credentials are hidden" "credentials"
                Expect.stringContains text "the home directory included, is read-only" "the home"
                Expect.stringContains text "The network is available." "the network"
                Expect.isFalse (text.Contains "/home/u") "no paths outside the workspace"

                let reviewer =
                    Instructions.sandbox
                        { policy with
                            WorkspaceAccess = Fx.Sdk.WorkspaceAccess.ReadOnly
                            Network = Fx.Sdk.NetworkAccess.Denied
                        }

                Expect.stringContains reviewer "The workspace is read-only" "a reviewer"
                Expect.stringContains reviewer "There is no network" "no network"
            }

            test "quotes carry provenance, are withheld when a source is excluded, and are never seen" {
                let quote =
                    Piece.Quote
                        {
                            Text = "SECRET=1"
                            Sources = [ ContentSource.file ".env" ]
                        }

                Expect.equal (Piece.sources quote) [ ContentSource.file ".env" ] "sources"

                match Piece.parts [ quote ] with
                | [ Part.Text t ] -> Expect.equal t.Sources [ ContentSource.file ".env" ] "the part quotes it"
                | other -> failtest $"{other}"

                let exclusions =
                    match
                        Exclusions.create
                            [
                                {
                                    Origin = "host"
                                    Patterns = [ "*.env" ]
                                }
                            ]
                    with
                    | Ok e -> e
                    | Error issues -> failtest $"{issues}"

                Expect.equal (Withhold.pieces exclusions [ quote ]) ([ Piece.Withheld ".env" ], true) "withheld"
                Expect.equal (SeenRanges.addPieces [ quote ] SeenRanges.empty) SeenRanges.empty "not seen"

                match Piece.codec.Decode(Piece.codec.Encode quote) with
                | Ok decoded -> Expect.equal decoded quote "round trip"
                | Error issues -> failtest $"{issues}"
            }

            test "the strategy's slots are registered and read" {
                let variants = Fx.Variants.Fakes.FakeVariants.create CodeActSlots.read []

                let settings =
                    (Eff.run () (CodeActSettings.ofVariants variants)).GetAwaiter().GetResult()

                Expect.equal settings.Surface ActionSurface.CellsOnly "the default surface"
                Expect.equal settings.TextOnly TextOnlyPolicy.Nudge "text only"
                Expect.equal settings.MaxSteps 100 "steps"
                Expect.equal settings.Model None "no model until configured"

                let ids = CodeActSlots.all |> List.map (fun s -> s.Id.Value)
                Expect.allEqual (ids |> List.map (fun id -> id.StartsWith "codeact.")) true "namespaced"
            }
        ]
