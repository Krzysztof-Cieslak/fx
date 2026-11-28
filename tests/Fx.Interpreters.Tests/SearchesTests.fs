/// Plain searches: which command lines are one, and how their output reads.
module Fx.Interpreters.Tests.SearchesTests

open Expecto
open Fx.Interpreters
open Fx.Sdk

let private words (line: string) = line.Split ' ' |> List.ofArray

let private recognize (line: string) =
    Searches.recognize Os.Linux (words line)

[<Tests>]
let tests =
    testList
        "searches"
        [
            testCase "grep -n, rg -n and git grep -n are plain searches"
            <| fun () ->
                Expect.equal (recognize "grep -rn retry") (Some SearchForm.WithPaths) "grep -r"
                Expect.equal (recognize "grep -rn retry src") (Some(SearchForm.OneOperand "src")) "grep -r, one operand"
                Expect.equal (recognize "grep -n retry a.ts b.ts") (Some SearchForm.WithPaths) "two files"
                Expect.equal (recognize "grep -n retry *.ts") (Some SearchForm.WithPaths) "a glob"
                Expect.equal (recognize "grep -Hn retry a.ts") (Some SearchForm.WithPaths) "named"
                Expect.equal (recognize "grep -n retry a.ts") (Some(SearchForm.OneOperand "a.ts")) "one file"
                Expect.equal (recognize "grep -n -e retry a.ts") (Some(SearchForm.OneOperand "a.ts")) "-e"

                Expect.equal
                    (recognize "grep --line-number -m 5 retry a.ts")
                    (Some(SearchForm.OneOperand "a.ts"))
                    "valued"

                Expect.equal (recognize "rg -n retry") (Some SearchForm.WithPaths) "rg"

                Expect.equal
                    (recognize "rg -n -g *.ts retry src")
                    (Some(SearchForm.OneOperand "src"))
                    "rg, one directory"

                Expect.equal (recognize "git grep -n retry") (Some SearchForm.WithPaths) "git grep"
                Expect.equal (recognize "git grep -n retry -- a.ts") (Some SearchForm.WithPaths) "git grep names files"

                Expect.equal
                    (Searches.recognize Os.Windows [ "RG.EXE"; "-n"; "retry" ])
                    (Some SearchForm.WithPaths)
                    "Windows names"

            testCase "anything that prints other lines is not"
            <| fun () ->
                for line in
                    [
                        "grep -r retry src"
                        "grep -rn -c retry src"
                        "grep -rln retry src"
                        "grep -rnL retry src"
                        "grep -rno retry src"
                        "grep -rnv retry src"
                        "grep -rn -A 2 retry src"
                        "grep -rn -3 retry src"
                        "grep -rn --context=2 retry src"
                        "grep -rnh retry src"
                        "grep -rnb retry src"
                        "grep -rnZ retry src"
                        "grep -n retry"
                        "grep -n retry -"
                        "grep -rn --color=always retry src"
                        "rg -n --count retry"
                        "rg -n -r x retry"
                        "rg -n --vimgrep retry"
                        "rg -n --json retry"
                        "rg -n -I retry"
                        "git grep -n -W retry"
                        "git grep -n --heading retry"
                        "git grep -n --full-name retry"
                        "git log -n 3"
                        "cat a.ts"
                    ] do
                    Expect.isNone (recognize line) line

            testCase "lines with paths"
            <| fun () ->
                Expect.equal
                    (Searches.parse SearchForm.WithPaths "src/a.ts:12:let x = 1\n./b.ts:3:a:4:b\r\n")
                    (Some
                        [
                            {
                                Path = "src/a.ts"
                                Line = 12
                                Text = "let x = 1"
                            }
                            {
                                Path = "./b.ts"
                                Line = 3
                                Text = "a:4:b"
                            }
                        ])
                    "the path ends at the first :<digits>:"

                Expect.equal
                    (Searches.parse SearchForm.WithPaths @"C:\w\a.ts:2:x")
                    (Some
                        [
                            {
                                Path = @"C:\w\a.ts"
                                Line = 2
                                Text = "x"
                            }
                        ])
                    "a drive"

                Expect.isNone (Searches.parse SearchForm.WithPaths "a:b.ts:2:x\n") "a path with a colon"
                Expect.isNone (Searches.parse SearchForm.WithPaths "a.ts:2:x\nBinary file b matches\n") "another line"
                Expect.isNone (Searches.parse SearchForm.WithPaths "a.ts-2-x\n") "a context line"
                Expect.isNone (Searches.parse SearchForm.WithPaths "a.ts:0:x\n") "line 0"
                Expect.isNone (Searches.parse SearchForm.WithPaths "") "nothing printed"

            testCase "one operand: the output says whether it was a file"
            <| fun () ->
                Expect.equal
                    (Searches.parse (SearchForm.OneOperand "a.ts") "2:x\n10:y:3:z\n")
                    (Some
                        [
                            { Path = "a.ts"; Line = 2; Text = "x" }
                            {
                                Path = "a.ts"
                                Line = 10
                                Text = "y:3:z"
                            }
                        ])
                    "a file: its lines"

                Expect.equal
                    (Searches.parse (SearchForm.OneOperand "src") "src/a.ts:2:x\n")
                    (Some
                        [
                            {
                                Path = "src/a.ts"
                                Line = 2
                                Text = "x"
                            }
                        ])
                    "a directory: paths"

            testCase "bounded"
            <| fun () ->
                let lines (n: int) (files: int) =
                    String.concat "" [ for i in 1..n -> "f" + string (i % files) + ".ts:1:x\n" ]

                Expect.isSome (Searches.parse SearchForm.WithPaths (lines Searches.MaxMatches 1)) "at the bound"
                Expect.isNone (Searches.parse SearchForm.WithPaths (lines (Searches.MaxMatches + 1) 1)) "more lines"

                Expect.isNone
                    (Searches.parse SearchForm.WithPaths (lines (Searches.MaxFiles + 1) (Searches.MaxFiles + 1)))
                    "more files"
        ]
