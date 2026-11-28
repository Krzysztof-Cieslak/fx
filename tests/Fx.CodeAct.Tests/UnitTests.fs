/// The strategy's pure parts: tools from codecs, the loop guard, receipts, the merged
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

            test "a read shows numbered lines as a file piece, and says how to read on" {
                let file: FileText =
                    {
                        Path = "src/a.fs"
                        Text = "one\ntwo\nthree\nfour\n"
                        Lines = 4
                        Size = 19L
                        Digest = Fx.Store.BlobHash.ofBytes [| 1uy |]
                        Start = 1
                        End = 4
                    }

                let bounds (lines: int) : Receipts.ReadBounds = { MaxLines = lines; MaxChars = 16000 }

                match Receipts.read file (Some 2) (Some 2) (bounds 100) with
                | [ Piece.File view; Piece.Text more ] ->
                    Expect.equal view.Text "     2\ttwo\n     3\tthree" "numbered"
                    Expect.equal view.Lines [ LineRange.create 2 3 ] "the lines shown"
                    Expect.stringContains more "Lines 2–3 of 4. Read on with offset 4." "more"
                | other -> failtest $"{other}"

                match Receipts.read file None None (bounds 100) with
                | [ Piece.File view ] -> Expect.isEmpty view.Lines "the whole file"
                | other -> failtest $"{other}"

                match Receipts.read file (Some 1) None (bounds 3) with
                | [ Piece.File _; Piece.Text more ] -> Expect.stringContains more "offset 4" "bounded, from an offset"
                | other -> failtest $"{other}"

                match Receipts.read file None None (bounds 3) with
                | [ Piece.File head; Piece.Text gap; Piece.File tail ] ->
                    Expect.equal head.Lines [ LineRange.create 1 1 ] "the head"
                    Expect.equal gap "[Lines 2–3 of 4 not shown: read them with offset 2.]" "what is left out"
                    Expect.equal tail.Lines [ LineRange.create 4 4 ] "the tail"
                | other -> failtest $"{other}"
            }

            test "a large file shows its head and tail within the characters, and the lines left out" {
                let lines = [ for i in 1..400 -> System.String('x', 90) + string i ]
                let text = String.concat "\n" lines + "\n"

                let file: FileText =
                    {
                        Path = "src/big.fs"
                        Text = text
                        Lines = 400
                        Size = int64 text.Length
                        Digest = Fx.Store.BlobHash.ofBytes [| 1uy |]
                        Start = 1
                        End = 400
                    }

                let bounds: Receipts.ReadBounds = { MaxLines = 2000; MaxChars = 4000 }

                match Receipts.read file None None bounds with
                | [ Piece.File head; Piece.Text gap; Piece.File tail ] ->
                    Expect.isLessThanOrEqual (head.Text.Length + tail.Text.Length) 4000 "within the characters"
                    let headLast = head.Lines.Head.Last
                    let tailFirst = tail.Lines.Head.First
                    Expect.equal head.Lines.Head.First 1 "from the start"
                    Expect.equal tail.Lines.Head.Last 400 "to the end"

                    Expect.equal
                        gap
                        $"[Lines {headLast + 1}–{tailFirst - 1} of 400 not shown: read them with offset {headLast + 1}.]"
                        "the gap"
                | other -> failtest $"{other}"

                let minified =
                    { file with
                        Text = System.String('y', 50000) + "\n"
                        Lines = 1
                        End = 1
                    }

                match Receipts.read minified None None bounds with
                | [ Piece.File one ] ->
                    Expect.isLessThan one.Text.Length 2200 "a long line is cut"
                    Expect.stringEnds one.Text "… [50000 characters]" "saying how long it is"
                | other -> failtest $"{other}"
            }

            test "a Markdown file left out in part shows its headings, outside code fences" {
                let body = [ for i in 1..50 -> $"text {i}" ]

                let lines =
                    [
                        "# Title"
                        yield! body
                        "## Part one"
                        "```sh"
                        "# not a heading"
                        "```"
                        yield! body
                        "### Detail"
                        "#hashtag is not one"
                        "    # indented code"
                        yield! body
                        "## Part two"
                    ]

                let text = String.concat "\n" lines + "\n"

                let file: FileText =
                    {
                        Path = "docs/Guide.md"
                        Text = text
                        Lines = lines.Length
                        Size = int64 text.Length
                        Digest = Fx.Store.BlobHash.ofBytes [| 1uy |]
                        Start = 1
                        End = lines.Length
                    }

                Expect.equal
                    (Receipts.markdownHeadings 1 lines)
                    [ 1, "# Title"; 52, "## Part one"; 106, "### Detail"; 159, "## Part two" ]
                    "headings"

                let bounds: Receipts.ReadBounds = { MaxLines = 20; MaxChars = 16000 }

                match Receipts.read file None None bounds with
                | [ Piece.File _; Piece.Text _; Piece.File _; Piece.Text note; Piece.File outline ] ->
                    Expect.equal note "[Its headings:]" "introduced"

                    Expect.equal
                        outline.Text
                        "     1\t# Title\n    52\t## Part one\n   106\t### Detail\n   159\t## Part two"
                        "numbered"

                    Expect.equal
                        (outline.Lines |> List.map (fun r -> r.First))
                        [ 1; 52; 106; 159 ]
                        "the heading lines are seen"
                | other -> failtest $"{other}"

                match Receipts.read { file with Path = "docs/guide.txt" } None None bounds with
                | [ Piece.File _; Piece.Text _; Piece.File _ ] -> ()
                | other -> failtest $"not Markdown: {other}"

                let ranged =
                    { file with
                        Text = String.concat "\n" (List.take 40 lines) + "\n"
                        End = 40
                    }

                match Receipts.read ranged None None bounds with
                | [ Piece.File _; Piece.Text _; Piece.File _ ] -> ()
                | other -> failtest $"a ranged read has no outline: {other}"
            }

            test "an outline of many headings keeps the upper levels" {
                let lines =
                    [
                        for part in 1..10 do
                            $"# Part {part}"

                            for section in 1..9 do
                                $"## Section {part}.{section}"
                                "text"
                    ]

                let text = String.concat "\n" lines + "\n"

                let file: FileText =
                    {
                        Path = "README.md"
                        Text = text
                        Lines = lines.Length
                        Size = int64 text.Length
                        Digest = Fx.Store.BlobHash.ofBytes [| 1uy |]
                        Start = 1
                        End = lines.Length
                    }

                match Receipts.read file None None { MaxLines = 10; MaxChars = 16000 } |> List.rev with
                | Piece.File outline :: Piece.Text note :: _ ->
                    Expect.equal note "[Its headings (10 of 100, the deeper ones left out):]" "what was left out"
                    Expect.equal outline.Lines.Length 10 "the parts"
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
                        StdoutOverflow = None
                        StderrOverflow = None
                        Matches = []
                    }

                let ranJson = CoreBindings.Codecs.runResult.Encode ran

                let file: FileText =
                    {
                        Path = "a.txt"
                        Text = "one\ntwo\n"
                        Lines = 2
                        Size = 8L
                        Digest = Fx.Store.BlobHash.ofBytes [| 1uy |]
                        Start = 1
                        End = 2
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
                        ReadChars = 16000
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
                                Whole = None
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
                    Receipts.cell
                        limits
                        KernelNote.Same
                        (Some "x")
                        Reads.none
                        []
                        (Some(CellValue.Text long))
                        None
                        result
                    |> text

                Expect.stringContains shown "Value of Out[\"t1-s1-1\"] (string, 1000 chars):\n0123456789" "the text"
                Expect.stringContains shown "characters omitted" "cut in the middle at the output's limit"
                Expect.stringContains shown "Declared: x" "the names"

                let overflow: Overflow =
                    {
                        Hash = Fx.Store.BlobHash.ofBytes (System.Text.Encoding.UTF8.GetBytes long)
                        Bytes = 1000L
                        Path = Some "/env/envd/blobs/sha256/ab/cd"
                    }

                let shownStored =
                    Receipts.cell
                        limits
                        KernelNote.Same
                        (Some "x")
                        Reads.none
                        []
                        (Some(CellValue.Text long))
                        (Some overflow)
                        result
                    |> text

                Expect.stringContains
                    shownStored
                    "characters omitted; all of it: /env/envd/blobs/sha256/ab/cd …"
                    "the marker names the blob's file (T201a)"

                let ran: RunResult =
                    {
                        Command = "ls"
                        ExitCode = Some 1
                        Stdout = "a.txt\n"
                        Stderr = "oops\n"
                        Truncated = false
                        TimedOut = false
                        StdoutOverflow = None
                        StderrOverflow = None
                        Matches = []
                    }

                let shownRan =
                    Receipts.cell limits KernelNote.Same (Some "x") Reads.none [] (Some(CellValue.Ran ran)) None result
                    |> text

                Expect.stringContains
                    shownRan
                    "(RunResult):\n$ ls\nexit code 1\na.txt\n[stderr]\noops"
                    "as bash shows it"

                Expect.isFalse (shownRan.Contains "editRange") "no matches, no hint"
                Expect.isFalse (shownRan.Contains "was cut") "nothing cut, no overflow line"

                let shownCut =
                    Receipts.cell
                        limits
                        KernelNote.Same
                        (Some "x")
                        Reads.none
                        []
                        (Some(
                            CellValue.Ran
                                { ran with
                                    StdoutOverflow = Some overflow
                                }
                        ))
                        None
                        result
                    |> text

                Expect.stringContains
                    shownCut
                    "[stdout was cut; all of it: /env/envd/blobs/sha256/ab/cd]"
                    "where all of stdout is"

                let found: RunResult =
                    { ran with
                        Command = "grep -n retry a.txt"
                        ExitCode = Some 0
                        Stdout = "2:retry\n"
                        Stderr = ""
                        Matches =
                            [
                                {
                                    Path = "a.txt"
                                    Start = 2
                                    End = 2
                                    Text = "retry\n"
                                    Digest = Fx.Store.BlobHash.ofBytes [| 1uy |]
                                    Lines = 3
                                }
                            ]
                    }

                let g = Fx.Product.kernelGlobal

                let shownFound =
                    Receipts.cell
                        limits
                        KernelNote.Same
                        (Some "x")
                        Reads.none
                        []
                        (Some(CellValue.Ran found))
                        None
                        result
                    |> text

                Expect.stringContains
                    shownFound
                    $"exit code 0\n2:retry\n[1 match, each a line checked against its file: `{g}.fs.editRange(Out[\"t1-s1-1\"].matches[0], text)` replaces the first one's line.]"
                    "a search's value ends with how to edit a match"

                let range (path: string) (line: int) (text: string) : FileRange =
                    {
                        Path = path
                        Start = line
                        End = line
                        Text = text
                        Digest = Fx.Store.BlobHash.ofBytes [| 1uy |]
                        Lines = 9
                    }

                let grepped: GrepResult =
                    {
                        Pattern = "retry"
                        Matches =
                            [
                                {
                                    Range = range "src/a.ts" 2 "retry(1)\n"
                                    Column = 1
                                    Context = None
                                }
                                {
                                    Range = range "src/b.ts" 7 "  retry(2)\r\n"
                                    Column = 3
                                    Context = None
                                }
                            ]
                        Files = [ { Path = "src/a.ts"; Matches = 1 }; { Path = "src/b.ts"; Matches = 4 } ]
                        Truncated = true
                        FilesTruncated = false
                    }

                let shownGrep =
                    Receipts.cell
                        limits
                        KernelNote.Same
                        (Some "x")
                        Reads.none
                        []
                        (Some(CellValue.Grep grepped))
                        None
                        result
                    |> text

                Expect.stringContains
                    shownGrep
                    $"Value of Out[\"t1-s1-1\"] (GrepResult): the first 2 of 5 matches in 2 files for \"retry\"\nsrc/a.ts:2: retry(1)\nsrc/b.ts:7:   retry(2)\n[Each match is a line checked against its file: `{g}.fs.editRange(Out[\"t1-s1-1\"].matches[0], text)` replaces the first one's line.]"
                    "a search shows as ripgrep prints it"

                let withContext =
                    { grepped with
                        Matches =
                            [
                                {
                                    Range = range "src/a.ts" 2 "retry(1)\n"
                                    Column = 1
                                    Context =
                                        Some
                                            { range "src/a.ts" 1 "let a\nretry(1)\nlet b\n" with
                                                End = 3
                                            }
                                }
                                {
                                    Range = range "src/b.ts" 7 "x\n"
                                    Column = 1
                                    Context = Some(range "src/b.ts" 7 "x\n")
                                }
                            ]
                    }

                Expect.equal
                    (Receipts.grepLines withContext)
                    "src/a.ts-1- let a\nsrc/a.ts:2: retry(1)\nsrc/a.ts-3- let b\n--\nsrc/b.ts:7: x"
                    "context lines, groups apart"

                let shownFiles =
                    Receipts.cell
                        limits
                        KernelNote.Same
                        (Some "x")
                        Reads.none
                        []
                        (Some(
                            CellValue.Files
                                {
                                    Files = [ "a.ts"; "src/b.ts" ]
                                    Truncated = false
                                }
                        ))
                        None
                        result
                    |> text

                Expect.stringContains
                    shownFiles
                    "Value of Out[\"t1-s1-1\"] (RepoFiles): 2 files\na.ts\nsrc/b.ts"
                    "paths, one a line"

                match Receipts.ran 1000 found with
                | [ Piece.Text _; Piece.Text _; Piece.Text hint ] ->
                    Expect.stringStarts hint "[1 match, each checked against its file: `edit` one" "bash's hint"
                    Expect.stringContains hint $"`{g}.fs.editRange`" "and the cells' way"
                | other -> failtest $"{other}"

                let file: FileText =
                    {
                        Path = "a.txt"
                        Text = "one\ntwo\n"
                        Lines = 2
                        Size = 8L
                        Digest = Fx.Store.BlobHash.ofBytes [| 1uy |]
                        Start = 1
                        End = 2
                    }

                match
                    Receipts.cell
                        limits
                        KernelNote.Same
                        (Some "x")
                        Reads.none
                        []
                        (Some(CellValue.File file))
                        None
                        result
                with
                | [ Piece.Text head; Piece.File view; Piece.Text tail ] ->
                    Expect.stringContains head "(FileText): a.txt, 2 lines" "what it is"
                    Expect.equal view.Text "     1\tone\n     2\ttwo" "numbered lines, a file piece (seen)"
                    Expect.equal tail "Declared: x" "then the rest"
                | other -> failtest $"{other}"

                let preview =
                    Receipts.cell limits KernelNote.Same (Some "x") Reads.none [] None None result
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
                        ReadChars = 16000
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
                                Whole = None
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
                    Receipts.cell limits KernelNote.Same (Some "x") Reads.none waiting None None (result outcome)
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

            test "a cell's ranged read shows its lines from where they start; an edit the lines it left" {
                let limits: CellLimits =
                    {
                        ReceiptChars = 20000
                        OutputChars = 200
                        ReadLines = 3
                        ReadChars = 16000
                    }

                let result: CellResult =
                    {
                        CellId = "t1-s1-1"
                        Outcome =
                            CellOutcome.Completed(
                                Some
                                    {
                                        Type = "object"
                                        Preview = { Text = "{…}"; Truncated = true }
                                    }
                            )
                        Output =
                            {
                                Text = ""
                                Truncated = false
                                TotalChars = 0L
                                Whole = None
                            }
                        Declared = []
                        Unassigned = []
                        Finished = false
                        Notified = 0
                        DurationMs = 0L
                    }

                let digest = Fx.Store.BlobHash.ofBytes [| 1uy |]

                let range: FileText =
                    {
                        Path = "a.txt"
                        Text = "three\nfour\n"
                        Lines = 10
                        Size = 60L
                        Digest = digest
                        Start = 3
                        End = 4
                    }

                let rangeJson = CoreBindings.Codecs.fileText.Encode range

                let info =
                    (match result.Outcome with
                     | CellOutcome.Completed(Some i) -> i
                     | _ -> failtest "no value")

                Expect.equal
                    (CellValue.ofValue info (Some rangeJson) [ Returned.ofResult "fs.read" rangeJson ])
                    (CellValue.File range)
                    "a ranged read is a file read"

                match
                    Receipts.cell
                        limits
                        KernelNote.Same
                        (Some "x")
                        Reads.none
                        []
                        (Some(CellValue.File range))
                        None
                        result
                with
                | [ Piece.Text _; Piece.File view; Piece.Text more ] ->
                    Expect.equal view.Text "     3\tthree\n     4\tfour" "numbered from where it starts"
                    Expect.equal view.Lines [ LineRange.create 3 4 ] "those lines are seen"
                    Expect.equal more "[Lines 3–4 of 10.]" "where they are"
                | other -> failtest $"{other}"

                let long =
                    { range with
                        Text = "a\nb\nc\nd\ne\n"
                        Start = 1
                        End = 5
                    }

                match
                    Receipts.cell
                        limits
                        KernelNote.Same
                        (Some "x")
                        Reads.none
                        []
                        (Some(CellValue.File long))
                        None
                        result
                with
                | [ Piece.Text _; Piece.File head; Piece.Text gap; Piece.File tail ] ->
                    Expect.equal head.Lines [ LineRange.create 1 1 ] "the head"
                    Expect.equal tail.Lines [ LineRange.create 5 5 ] "the tail"
                    Expect.stringContains gap "Lines 2–4 of 10 not shown" "what is left out"
                    Expect.stringContains gap "fs.read(\"a.txt\", { lines: [2, 4] })" "how to read it"
                | other -> failtest $"{other}"

                let edited: Edited =
                    {
                        Path = "a.txt"
                        Digest = digest
                        Lines = 10
                        Replacements = 3
                        Ranges =
                            [
                                {
                                    Path = "a.txt"
                                    Start = 2
                                    End = 3
                                    Text = "b2\nb3\n"
                                    Digest = digest
                                    Lines = 10
                                }
                                {
                                    Path = "a.txt"
                                    Start = 6
                                    End = 5
                                    Text = ""
                                    Digest = digest
                                    Lines = 10
                                }
                                {
                                    Path = "a.txt"
                                    Start = 8
                                    End = 9
                                    Text = "h\ni\n"
                                    Digest = digest
                                    Lines = 10
                                }
                            ]
                    }

                let editedJson = CoreBindings.Codecs.edited.Encode edited

                Expect.equal
                    (CellValue.ofValue info (Some editedJson) [ Returned.ofResult "fs.edit" editedJson ])
                    (CellValue.Edited edited)
                    "an edit's result"

                match
                    Receipts.cell
                        limits
                        KernelNote.Same
                        (Some "x")
                        Reads.none
                        []
                        (Some(CellValue.Edited edited))
                        None
                        result
                with
                | [ Piece.Text head
                    Piece.Text summary
                    Piece.File first
                    Piece.Text removed
                    Piece.File second
                    Piece.Text more ] ->
                    Expect.stringContains head "(Edited): a.txt, 10 lines" "what it is"
                    Expect.equal summary "Edited a.txt (3 replacements)." "what changed"
                    Expect.equal first.Text "     2\tb2\n     3\tb3" "the lines a change left"
                    Expect.equal first.Lines [ LineRange.create 2 3 ] "seen"
                    Expect.equal removed "[Lines removed before line 6.]" "a removal"
                    Expect.equal second.Text "     8\th" "up to the receipt's lines"
                    Expect.equal more "[1 more changed line is not shown.]" "the rest"
                | other -> failtest $"{other}"
            }

            test "the sandbox guard sees the paths edits write" {
                let call (binding: string) (args: Json list) : HostCall =
                    {
                        CellId = Some "c1"
                        Binding = binding
                        Args = args |> List.map Value.Inline
                    }

                let range =
                    Json.Object
                        [
                            "path", Json.String "src/b.ts"
                            "start", Json.int 1
                            "end", Json.int 1
                            "text", Json.String "a\n"
                            "digest", Json.String(string (Fx.Store.BlobHash.ofBytes [| 1uy |]))
                            "lines", Json.int 1
                        ]

                let change = Json.Object [ "old", Json.String "a"; "new", Json.String "b" ]

                Expect.equal
                    (SandboxGuard.paths "/w" Fx.Sdk.Os.Linux (call "fs.edit" [ Json.String "src/a.ts"; change ]))
                    [ "/w/src/a.ts", Fx.Sdk.PathAccess.ReadWrite ]
                    "fs.edit"

                Expect.equal
                    (SandboxGuard.paths "/w" Fx.Sdk.Os.Linux (call "fs.editRange" [ range; Json.String "b" ]))
                    [ "/w/src/b.ts", Fx.Sdk.PathAccess.ReadWrite ]
                    "fs.editRange: the range's file"

                Expect.equal
                    (SandboxGuard.paths "/w" Fx.Sdk.Os.Linux (call "fs.read" [ Json.String "a"; Json.Object [] ]))
                    [ "/w/a", Fx.Sdk.PathAccess.ReadOnly ]
                    "a ranged read"
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

                // j2 runs; j3 ended (T143).
                let jobs (id: string) =
                    match id with
                    | "j2" -> None
                    | "j3" -> Some "job j3 ended already"
                    | other -> Some $"there is no background job {other}"

                let check policy result =
                    Ensure.all policy empty jobs |> List.collect (fun c -> c result)

                Expect.isEmpty (check EvidencePolicy.Optional (TurnResult.Done("did it", [], None))) "fine"
                Expect.isNonEmpty (check EvidencePolicy.Optional (TurnResult.Done(" ", [], None))) "blank summary"
                Expect.isNonEmpty (check EvidencePolicy.Required (TurnResult.Done("did it", [], None))) "no evidence"
                Expect.isNonEmpty (check EvidencePolicy.Optional (TurnResult.NeedInput [ "r1" ])) "unknown request"
                Expect.isNonEmpty (check EvidencePolicy.Optional (TurnResult.Waiting([ "j1" ], "x"))) "unknown job"
                Expect.isEmpty (check EvidencePolicy.Optional (TurnResult.Waiting([ "j2" ], "x"))) "a running job"

                let ended =
                    check EvidencePolicy.Optional (TurnResult.Waiting([ "j2"; "j3" ], "x"))
                    |> List.exactlyOne

                Expect.stringContains (string ended) "$.result.jobs[1]" "the ended job, at its place"
                Expect.stringContains (string ended) "ended already" "why"

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
