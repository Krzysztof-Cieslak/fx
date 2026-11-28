/// Edits by text (`TextEdits`, pure): changes applied in order, turned into the line edits of one
/// patch, and where their lines end up.
module Fx.Interpreters.Tests.TextEditsTests

open System
open Expecto
open Fx.Envd
open Fx.Interpreters

let private change (old: string) (replacement: string) : TextChange =
    {
        Old = old
        New = replacement
        All = false
    }

let private everywhere (old: string) (replacement: string) : TextChange =
    { change old replacement with
        All = true
    }

let private edits (text: string) (changes: TextChange list) : TextEdit list =
    match TextEdits.apply text changes with
    | Ok changed -> changed.Edits
    | Error why -> failtest $"{why}"

let private applied (text: string) (changes: TextChange list) : string =
    match TextPatch.applyText text (edits text changes) with
    | Ok(edited, _) -> edited
    | Error reason -> failtest reason

/// The text the changes make, by plain string replacement (what the edits must amount to).
let private expected (text: string) (changes: TextChange list) : string =
    changes
    |> List.fold
        (fun text c ->
            if c.All then
                text.Replace(c.Old, c.New, StringComparison.Ordinal)
            else
                TextEdits.replaceFirst text c.Old c.New)
        text

[<Tests>]
let tests =
    testList
        "text edits"
        [
            test "an edit becomes the lines between the common head and tail" {
                Expect.equal
                    (TextEdits.lineEdit [ "a"; "b"; "c" ] [ "a"; "B"; "c" ])
                    { Line = 2; Count = 1; Lines = [ "B" ] }
                    "one line"

                Expect.equal
                    (TextEdits.lineEdit [ "a"; "c" ] [ "a"; "b"; "c" ])
                    { Line = 2; Count = 0; Lines = [ "b" ] }
                    "an insertion"

                Expect.equal
                    (TextEdits.lineEdit [ "a"; "b"; "b" ] [ "a"; "b" ])
                    { Line = 3; Count = 1; Lines = [] }
                    "a deletion"

                let applied =
                    TextPatch.applyText "x\r\ny\r\nz\r\n" [ TextEdits.lineEdit [ "x"; "y"; "z" ] [ "x"; "Y"; "z" ] ]

                Expect.equal applied (Ok("x\r\nY\r\nz\r\n", 3)) "line endings kept"
            }

            test "lines, slices and occurrences" {
                Expect.equal (TextEdits.lines "a\r\nb\n") [ "a"; "b" ] "endings dropped"
                Expect.equal (TextEdits.lines "a\n\n") [ "a"; "" ] "an empty last line"
                Expect.equal (TextEdits.lines "") [] "none"
                Expect.equal (TextEdits.slice "a\nb\r\nc" 2 3) "b\r\nc" "as they are"
                Expect.equal (TextEdits.slice "a\nb\n" 2 2) "b\n" "with the ending"
                Expect.equal (TextEdits.slice "a\nb\n" 3 3) "" "past the end"
                Expect.equal (TextEdits.slice "a\nb\n" 2 1) "" "none"
                Expect.equal (TextEdits.occurrences "aaaa" "aa") 2 "without overlaps"
                Expect.equal (TextEdits.occurrences "abc" "") 0 "nothing occurs as empty text"
            }

            test "a change whose text occurs once replaces it: the line that changed" {
                Expect.equal
                    (edits "a\nhello\nb\n" [ change "hello" "hi" ])
                    [
                        {
                            Line = 2
                            Count = 1
                            Lines = [ "hi" ]
                        }
                    ]
                    "one line"

                Expect.equal
                    (edits "a\nb\nc\nd\n" [ change "b\nc\n" "" ])
                    [ { Line = 2; Count = 2; Lines = [] } ]
                    "whole lines removed"

                Expect.equal
                    (edits "a\nb\n" [ change "a\n" "a\nnew\n" ])
                    [
                        {
                            Line = 2
                            Count = 0
                            Lines = [ "new" ]
                        }
                    ]
                    "a line inserted, trimmed to it"

                Expect.equal
                    (edits "a\nb" [ change "b" "c" ])
                    [ { Line = 2; Count = 1; Lines = [ "c" ] } ]
                    "the last line, unended"
            }

            test "missing, ambiguous and unchanged changes fail, naming the change" {
                Expect.equal (TextEdits.apply "abc" [ change "x" "y" ]) (Error(ChangeFailure.Missing 0)) "missing"

                Expect.equal
                    (TextEdits.apply "a a a" [ change "a" "b" ])
                    (Error(ChangeFailure.Ambiguous(0, 3)))
                    "ambiguous"

                Expect.equal (TextEdits.apply "a" [ change "a" "a" ]) (Error(ChangeFailure.Same 0)) "the same"

                Expect.equal
                    (TextEdits.apply "ab" [ change "a" "x"; change "a" "y" ])
                    (Error(ChangeFailure.Missing 1))
                    "missing after the change before it"

                Expect.equal
                    (TextEdits.apply "a\nb\n" [ change "a" "x"; change "x" "a" ])
                    (Error ChangeFailure.Unchanged)
                    "changes that undo each other"
            }

            test "with all, every occurrence; each region of lines is its own edit" {
                match TextEdits.apply "x = 1\ny\nx = 2\nz\nx = 3\n" [ everywhere "x" "w" ] with
                | Ok changed ->
                    Expect.equal changed.Replacements 3 "replacements"

                    Expect.equal
                        changed.Edits
                        [
                            {
                                Line = 1
                                Count = 1
                                Lines = [ "w = 1" ]
                            }
                            {
                                Line = 3
                                Count = 1
                                Lines = [ "w = 2" ]
                            }
                            {
                                Line = 5
                                Count = 1
                                Lines = [ "w = 3" ]
                            }
                        ]
                        "three edits"
                | Error why -> failtest $"{why}"
            }

            test "changes apply in order, each to the text the ones before it left" {
                let text = "let a = 1\nlet b = 2\nlet c = 3\n"

                let changes =
                    [
                        change "a = 1" "a = 10"
                        change "a = 10\nlet b" "a = 10\nlet bb"
                        change "c" "cc"
                    ]

                Expect.equal (applied text changes) "let a = 10\nlet bb = 2\nlet cc = 3\n" "applied"

                Expect.equal
                    (edits text changes)
                    [
                        {
                            Line = 1
                            Count = 2
                            Lines = [ "let a = 10"; "let bb = 2" ]
                        }
                        {
                            Line = 3
                            Count = 1
                            Lines = [ "let cc = 3" ]
                        }
                    ]
                    "changes that overlap are one edit; the next line is another"

                Expect.equal
                    (edits "ab\ncd\n" [ change "a" "x"; change "b" "y" ])
                    [
                        {
                            Line = 1
                            Count = 1
                            Lines = [ "xy" ]
                        }
                    ]
                    "changes on one line are one edit"
            }

            test "the lines of each edit once applied: fresh ranges" {
                let text = "a\nb\nc\nd\ne\n"
                let changes = [ change "b\n" "b1\nb2\nb3\n"; change "d\n" "" ]
                let es = edits text changes
                let after = applied text changes
                Expect.equal after "a\nb1\nb2\nb3\nc\ne\n" "applied"
                let placed = TextEdits.placed es
                Expect.equal placed [ 2, 4; 6, 5 ] "an insertion moves the lines after it; a removal is empty"
                Expect.equal (TextEdits.slice after 2 4) "b1\nb2\nb3\n" "the new lines"
            }

            test "the edits always amount to the changes, line by line (many generated cases)" {
                let random = Random 20261128
                let words = [| "a"; "b"; "ab"; "x"; ""; "a b"; "ba" |]

                let line () =
                    String.Join(" ", Array.init (random.Next 3) (fun _ -> words[random.Next words.Length]))

                let mutable checkedCases = 0

                for _ in 1..2000 do
                    let lines = List.init (random.Next(1, 8)) (fun _ -> line ())

                    let text = String.Join("\n", lines) + (if random.Next 2 = 0 then "\n" else "")

                    let mutable current = text
                    let mutable changes = []

                    for _ in 1 .. random.Next(1, 4) do
                        if current.Length > 0 then
                            let start = random.Next current.Length
                            let length = random.Next(1, min 6 (current.Length - start) + 1)
                            let old = current.Substring(start, length)
                            let replacement = [| ""; "q"; "\n"; "q\nr"; old + "\n" |][random.Next 5]
                            let all = random.Next 3 = 0

                            let c =
                                if all then
                                    everywhere old replacement
                                else
                                    change old replacement

                            if all || TextEdits.occurrences current old = 1 then
                                changes <- changes @ [ c ]
                                current <- expected current [ c ]

                    // What the file holds after the edits: `TextPatch` keeps a final line ending if
                    // the file had one (or was empty), and writes what was asked otherwise.
                    let wanted =
                        if
                            (text = "" || text.EndsWith '\n')
                            && current <> ""
                            && not (current.EndsWith '\n')
                        then
                            current + "\n"
                        else
                            current

                    match TextEdits.apply text changes with
                    | Error ChangeFailure.Unchanged ->
                        Expect.equal wanted text $"unchanged: {Fx.Contracts.Json.quote text}"
                    | Error(ChangeFailure.Same _) -> ()
                    | Error why -> failtest $"{why}: {Fx.Contracts.Json.quote text}"
                    | Ok changed ->
                        checkedCases <- checkedCases + 1

                        match TextPatch.applyText text changed.Edits with
                        | Error reason -> failtest $"{reason}: {Fx.Contracts.Json.quote text}"
                        | Ok(edited, _) ->
                            let shown =
                                changes
                                |> List.map (fun c ->
                                    $"{Fx.Contracts.Json.quote c.Old} -> {Fx.Contracts.Json.quote c.New} (all {c.All})")
                                |> String.concat "; "

                            Expect.equal
                                edited
                                wanted
                                $"the text the changes make: {Fx.Contracts.Json.quote text} with {shown}"

                            let count = TextPatch.lineCount edited

                            for edit, (first, last) in List.zip changed.Edits (TextEdits.placed changed.Edits) do
                                // A last empty line without a line ending is no line: the file ends with one.
                                let wantedLines =
                                    if last > count then
                                        edit.Lines |> List.truncate (edit.Lines.Length - 1)
                                    else
                                        edit.Lines

                                Expect.equal
                                    (TextEdits.lines (TextEdits.slice edited first last))
                                    wantedLines
                                    "each edit's lines where they were placed"

                Expect.isGreaterThan checkedCases 500 "enough cases"
            }
        ]
