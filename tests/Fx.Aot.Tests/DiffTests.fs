module Fx.Aot.Tests.DiffTests

open Expecto
open Fx.Aot.Diff
open Fx.Aot.Diff.Diff

let private run exitCode stdout stderr =
    {
        ExitCode = exitCode
        Stdout = stdout
        Stderr = stderr
    }

[<Tests>]
let tests =
    testList
        "Diff"
        [
            testList
                "lines"
                [
                    test "splits on newlines" { Expect.equal (lines "a\nb\n") [ "a"; "b" ] "lines" }
                    test "empty text has no lines" { Expect.equal (lines "") [] "lines" }

                    test "marks a missing final newline" {
                        Expect.equal (lines "a\nb") [ "a"; "b (no newline at end)" ] "lines"
                    }

                    test "shows carriage returns and other control characters" {
                        Expect.equal (lines "a\r\nb\tc\u0001\n") [ "a\\r"; "b\\tc\\u0001" ] "lines"
                    }
                ]

            testList
                "diffLines"
                [
                    test "identical input has only unchanged lines" {
                        Expect.equal (diffLines [ "a"; "b" ] [ "a"; "b" ]) [ Same "a"; Same "b" ] "diff"
                    }

                    test "a changed line is removed, then added" {
                        Expect.equal
                            (diffLines [ "a"; "b"; "c" ] [ "a"; "x"; "c" ])
                            [ Same "a"; Removed "b"; Added "x"; Same "c" ]
                            "diff"
                    }

                    test "insertions and deletions" {
                        Expect.equal
                            (diffLines [ "a"; "b"; "c" ] [ "b"; "c"; "d" ])
                            [ Removed "a"; Same "b"; Same "c"; Added "d" ]
                            "diff"
                    }

                    test "empty sides" {
                        Expect.equal (diffLines [] [ "a" ]) [ Added "a" ] "added"
                        Expect.equal (diffLines [ "a" ] []) [ Removed "a" ] "removed"
                    }
                ]

            test "render keeps context around changes and elides the rest" {
                let before = [ for i in 1..20 -> string i ]
                let after = before |> List.map (fun l -> if l = "10" then "ten" else l)

                Expect.equal
                    (render 2 (diffLines before after))
                    [ "  ..."; "  8"; "  9"; "- 10"; "+ ten"; "  11"; "  12"; "  ..." ]
                    "rendered"
            }

            testList
                "compare"
                [
                    test "identical runs have no differences" {
                        Expect.isEmpty (Diff.compare (run 0 "out\n" "err\n") (run 0 "out\n" "err\n")) "report"
                    }

                    test "reports a different exit code" {
                        Expect.equal
                            (Diff.compare (run 0 "" "") (run 134 "" ""))
                            [ "exit code: jit 0, native 134" ]
                            "report"
                    }

                    test "reports a stdout difference as a diff" {
                        Expect.equal
                            (Diff.compare (run 0 "same\njit\n" "") (run 0 "same\nnative\n" ""))
                            [ "stdout (- jit, + native):"; "  same"; "- jit"; "+ native" ]
                            "report"
                    }

                    test "reports a stderr difference" {
                        Expect.equal
                            (Diff.compare (run 1 "" "boom\n") (run 1 "" ""))
                            [ "stderr (- jit, + native):"; "- boom" ]
                            "report"
                    }

                    test "reports a difference in the final newline" {
                        Expect.equal
                            (Diff.compare (run 0 "a\n" "") (run 0 "a" ""))
                            [ "stdout (- jit, + native):"; "- a"; "+ a (no newline at end)" ]
                            "report"
                    }
                ]

            test "subject and case names are unique" {
                let subjects = Subjects.canaries :: Subjects.all
                let names = subjects |> List.map (fun s -> s.Name)
                Expect.equal (List.distinct names) names "subject names"

                for s in subjects do
                    match s.Cases with
                    | Subjects.Fixed cases ->
                        let caseNames = cases |> List.map (fun c -> c.Name)
                        Expect.equal (List.distinct caseNames) caseNames $"case names of {s.Name}"
                    | Subjects.Listed _ -> ()
            }
        ]
