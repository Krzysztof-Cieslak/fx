/// The command-line parsers the grants guard splits `shell.run` lines with (sh and PowerShell): what
/// they split, what they give up on, and properties over random lines.
module Fx.CodeAct.Tests.ShellSyntaxTests

open System
open Expecto
open Fx.CodeAct

/// A command as the tests write it: words (`⟨…⟩` around one that expands), then `> target` for writes.
let private render (command: SimpleCommand) : string =
    let word (w: Word) =
        if w.Expands then "⟨" + w.Text + "⟩" else w.Text

    String.Join(" ", command.Words |> List.map word)
    + (command.Writes |> List.map (fun w -> " > " + word w) |> String.concat "")

let private split (dialect: ShellDialect) (line: string) : Result<string list, string> =
    match ShellSyntax.parse dialect line with
    | CommandLine.Parsed commands -> Ok(commands |> List.map render)
    | CommandLine.Opaque reason -> Error reason

let private parsed (dialect: ShellDialect) (line: string) (expected: string list) =
    match split dialect line with
    | Ok commands -> Expect.equal commands expected line
    | Error reason -> failtest $"{line}: opaque ({reason})"

let private opaque (dialect: ShellDialect) (line: string) (because: string) =
    match split dialect line with
    | Ok commands -> failtest $"{line}: split into {commands}, expected it opaque ({because})"
    | Error reason -> Expect.stringContains reason because line

let private sh = parsed ShellDialect.Sh
let private shOpaque = opaque ShellDialect.Sh
let private ps = parsed ShellDialect.PowerShell
let private psOpaque = opaque ShellDialect.PowerShell

/// Random lines from pieces, for the properties (seeded: the same lines on every run).
let private randomLine (random: Random) (pieces: string array) (count: int) =
    String.Join("", Array.init count (fun _ -> pieces[random.Next pieces.Length]))

[<Tests>]
let tests =
    testList
        "Shell syntax"
        [
            testList
                "sh"
                [
                    test "simple commands and lists" {
                        sh "ls -la" [ "ls -la" ]
                        sh "  git status && npm test  " [ "git status"; "npm test" ]
                        sh "a; b || c | d & e" [ "a"; "b"; "c"; "d"; "e" ]
                        sh "a |& b" [ "a"; "b" ]
                        sh "cd x\nmake\n" [ "cd x"; "make" ]
                        sh "ls \\\n  -la" [ "ls -la" ]
                        sh "! grep -q x file" [ "grep -q x file" ]
                        sh "time make" [ "time make" ]
                        sh "" []
                        sh "  # only a comment" []
                        sh "ls # rm -rf /" [ "ls" ]
                        sh "echo a#b" [ "echo a#b" ]
                        sh "FOO=bar make all" [ "FOO=bar make all" ]
                        sh "a &&\n b" [ "a"; "b" ]
                        sh "a |\n b" [ "a"; "b" ]
                        sh "make\r\nmake test\r\n" [ "make"; "make test" ]
                    }

                    test "quotes, escapes and expansions" {
                        sh "echo \"a b\" 'c d' e\\ f" [ "echo a b c d e f" ]
                        sh "echo 'a\"b' \"c'd\" \"e\\\"f\"" [ "echo a\"b c'd e\"f" ]
                        sh "echo '$HOME' \\$HOME \"$HOME\" $HOME" [ "echo $HOME $HOME ⟨$HOME⟩ ⟨$HOME⟩" ]
                        sh "echo ${HOME}/x $1 $@ $? $" [ "echo ⟨${HOME}/x⟩ ⟨$1⟩ ⟨$@⟩ ⟨$?⟩ $" ]
                        sh "rm -rf ~/x" [ "rm -rf ⟨~/x⟩" ]
                        sh "echo {a,b} [ab] x*.fs ?" [ "echo ⟨{a,b}⟩ ⟨[ab]⟩ x*.fs ?" ]
                        sh "printf $'a\\nb'" [ "printf ⟨a\\nb⟩" ]
                        sh "echo \"${x:-y}\"" [ "echo ⟨${x:-y}⟩" ]
                    }

                    test "command substitutions are split too, before the command that holds them" {
                        sh "echo $(git rev-parse HEAD)" [ "git rev-parse HEAD"; "echo ⟨$(git rev-parse HEAD)⟩" ]
                        sh "echo `date`" [ "date"; "echo ⟨`date`⟩" ]
                        sh "echo \"$(cat $(ls))\"" [ "ls"; "cat ⟨$(ls)⟩"; "echo ⟨$(cat $(ls))⟩" ]
                        sh "x=$(rm -rf build)" [ "rm -rf build"; "⟨x=$(rm -rf build)⟩" ]
                        sh "echo \"`rm -rf x`\"" [ "rm -rf x"; "echo ⟨`rm -rf x`⟩" ]
                        sh "echo $(a; b && c)" [ "a"; "b"; "c"; "echo ⟨$(a; b && c)⟩" ]
                        sh "echo $()" [ "echo ⟨$()⟩" ]
                    }

                    test "subshells and groups are flattened" {
                        sh "(cd x && make) | tee log" [ "cd x"; "make"; "tee log" ]
                        sh "{ a; b; } && c" [ "a"; "b"; "c" ]
                        sh "echo }" [ "echo ⟨}⟩" ]
                    }

                    test "redirections: what is written, without the null device and descriptors" {
                        sh "echo hi > out.txt 2>&1" [ "echo hi > out.txt" ]
                        sh "echo hi >/dev/null 2>/dev/null 1>&2 >&-" [ "echo hi" ]

                        sh
                            "cmd &> log; cmd &>> log2; cmd >> log3; cmd >| log4; cmd <> rw"
                            [ "cmd > log"; "cmd > log2"; "cmd > log3"; "cmd > log4"; "cmd > rw" ]

                        sh "cmd < in.txt <<< \"$(id)\"" [ "id"; "cmd" ]
                        sh "exec 3>file" [ "exec > file" ]
                        sh "echo >&2 hi" [ "echo hi" ]
                        sh "echo hi >& out" [ "echo hi > out" ]
                        sh "> truncated" [ " > truncated" ]
                        sh "echo a2>x" [ "echo a2 > x" ]
                        sh "echo hi > \"$f\"" [ "echo hi > ⟨$f⟩" ]
                    }

                    test "what it does not follow is opaque" {
                        shOpaque "if true; then rm -rf /; fi" "control flow (if)"
                        shOpaque "for f in *; do rm $f; done" "control flow (for)"
                        shOpaque "while true; do :; done" "control flow (while)"
                        shOpaque "case $x in a) rm;; esac" "control flow (case)"
                        shOpaque "f() { rm -rf /; }" "parenthesis"
                        shOpaque "function f { rm; }" "control flow (function)"
                        shOpaque "cat <<EOF\nx\nEOF" "here-document"
                        shOpaque "diff <(ls a) <(ls b)" "process substitution"
                        shOpaque "tee >(rm x)" "process substitution"
                        shOpaque "echo $((1+2))" "arithmetic"
                        shOpaque "((x++))" "arithmetic"
                        shOpaque "[[ -f x ]]" "control flow ([[)"
                        shOpaque "{ a; b; } > out" "redirection of a group"
                        shOpaque "(a) > out" "redirection of a group"
                        shOpaque "echo 'unclosed" "unclosed quote"
                        shOpaque "echo \"unclosed" "unclosed quote"
                        shOpaque "echo $(unclosed" "unclosed command substitution"
                        shOpaque "echo `unclosed" "unclosed backquote"
                        shOpaque "echo `a \\`b\\` c`" "nested backquotes"
                        shOpaque "echo ${x:-$(rm)}" "nested expansion"
                        shOpaque "a ;; b" "case clause"
                        shOpaque "a &&" "missing after"
                        shOpaque "; a" "empty command"
                        shOpaque "a | | b" "empty command"
                        shOpaque "arr=(a b)" "parenthesis"
                        shOpaque "echo \\$(rm -rf /)" "parenthesis"
                        shOpaque "cat > " "without a target"
                        shOpaque "echo x\\" "backslash at the end"
                        shOpaque "{ a; b" "unclosed group"
                        shOpaque "(a; b" "unclosed subshell"
                        shOpaque "a )" "unexpected"
                        shOpaque "a\u0000b" "NUL"
                        shOpaque (String('x', ShellSyntax.MaxLength + 1)) "too long"
                    }

                    test "deep or long lines are opaque" {
                        let deep =
                            String.replicate (ShellSyntax.MaxDepth + 1) "echo $("
                            + "x"
                            + String(')', ShellSyntax.MaxDepth + 1)

                        shOpaque deep "nested too deep"

                        let many = String.Join("; ", Array.create (ShellSyntax.MaxCommands + 1) "a")
                        shOpaque many "too many commands"
                    }
                ]

            testList
                "PowerShell"
                [
                    test "commands, pipelines and statements" {
                        ps "Get-ChildItem -Recurse" [ "Get-ChildItem -Recurse" ]
                        ps "git status; npm test" [ "git status"; "npm test" ]
                        ps "dotnet build && dotnet test || echo failed" [ "dotnet build"; "dotnet test"; "echo failed" ]
                        ps "Get-Content x | Select-String foo" [ "Get-Content x"; "Select-String foo" ]
                        ps "Start-Sleep 10 &" [ "Start-Sleep 10" ]
                        ps "ls `\n  -Force" [ "ls -Force" ]
                        ps "ls # comment" [ "ls" ]
                        ps "<# block #> ls" [ "ls" ]
                        ps "a\nb\r\nc" [ "a"; "b"; "c" ]
                        ps "& \"C:\\Program Files\\x.exe\" -a" [ "& C:\\Program Files\\x.exe -a" ]
                        ps ". .\\profile.ps1" [ ". .\\profile.ps1" ]
                        ps ".\\build.ps1 -Release" [ ".\\build.ps1 -Release" ]
                        ps "Get-Item -Path C:\\x,C:\\y" [ "Get-Item -Path C:\\x,C:\\y" ]
                        ps "" []
                    }

                    test "quotes, escapes and expansions" {
                        ps "echo 'it''s' \"a`\"b\" \"c\"\"d\" a` b" [ "echo it's a\"b c\"d a b" ]
                        ps "Remove-Item -Recurse $env:TEMP\\x" [ "Remove-Item -Recurse ⟨$env:TEMP\\x⟩" ]
                        ps "echo \"$name.txt\" '$name'" [ "echo ⟨$name.txt⟩ $name" ]
                        ps "Copy-Item @params" [ "Copy-Item ⟨@params⟩" ]
                        ps "echo ${a b} $_ $? $$" [ "echo ⟨${a b}⟩ ⟨$_⟩ ⟨$?⟩ ⟨$$⟩" ]
                        ps "Get-Item ~\\x a[0]" [ "Get-Item ⟨~\\x⟩ ⟨a[0]⟩" ]
                    }

                    test "subexpressions are split too" {
                        ps "Write-Output $(Remove-Item x)" [ "Remove-Item x"; "Write-Output ⟨$(Remove-Item x)⟩" ]
                        ps "echo (Get-Date)" [ "Get-Date"; "echo ⟨(Get-Date)⟩" ]
                        ps "echo @(ls; pwd)" [ "ls"; "pwd"; "echo ⟨@(ls; pwd)⟩" ]
                        ps "echo \"now: $(Get-Date)\"" [ "Get-Date"; "echo ⟨now: $(Get-Date)⟩" ]
                    }

                    test "redirections" {
                        ps "echo hi > out.txt" [ "echo hi > out.txt" ]
                        ps "echo hi >> out.txt 2> err.txt" [ "echo hi > out.txt > err.txt" ]
                        ps "echo hi *> all.txt" [ "echo hi > all.txt" ]
                        ps "echo hi 2>&1 *>&1 > $null 2>$NULL > NUL" [ "echo hi" ]
                        ps "echo hi > $f" [ "echo hi > ⟨$f⟩" ]
                    }

                    test "what it does not follow is opaque" {
                        psOpaque "ForEach-Object { Remove-Item $_ }" "script block"
                        psOpaque "Start-Job { rm x }" "script block"
                        psOpaque "if ($x) { rm y }" "statement (if)"
                        psOpaque "foreach ($f in $files) { }" "statement (foreach)"
                        psOpaque "function f { }" "statement (function)"
                        psOpaque "[IO.File]::Delete('x')" "expression"
                        psOpaque "$x = 1" "expression"
                        psOpaque "5 > x" "expression"
                        psOpaque "(Get-Item x).Delete()" "expression"
                        psOpaque "echo (Get-Item x).Delete()" "member access"
                        psOpaque "echo $x.Delete()" "member access"
                        psOpaque "echo $x[0]" "member access"
                        psOpaque "cmd --% /c dir" "stop-parsing"
                        psOpaque "echo @{a=1}" "hashtable"
                        psOpaque "echo @'\nx\n'@" "here-string"
                        psOpaque "echo [int]1" "type"
                        psOpaque "echo “x”" "typographic"
                        psOpaque "echo ‘x’" "typographic"
                        psOpaque "cmd < in.txt" "the < operator"
                        psOpaque "echo 'unclosed" "unclosed quote"
                        psOpaque "echo \"unclosed" "unclosed quote"
                        psOpaque "echo $(unclosed" "unclosed subexpression"
                        psOpaque "<# unclosed" "unclosed block comment"
                        psOpaque "a; ; b" "empty command"
                        psOpaque "a &&" "missing after"
                        psOpaque "&\"x.exe\"" "empty command"
                        psOpaque "& { rm x }" "script block"
                        psOpaque "echo a(b)" "parenthesis inside a word"
                        psOpaque "echo hi >& x" "a stream that does not exist"
                        psOpaque "echo hi > " "without a target"
                    }
                ]

            testList
                "resources"
                [
                    test "words become one line of single spaces, without control characters" {
                        let words (texts: string list) =
                            texts |> List.map (fun t -> { Text = t; Expands = false })

                        Expect.equal
                            (ShellSyntax.resource (words [ "git"; "commit"; "-m"; "fix\nthe  bug" ]))
                            (Some "git commit -m fix the bug")
                            "split"

                        Expect.equal (ShellSyntax.resource (words [ "echo"; "" ])) (Some "echo") "empty words"
                        Expect.equal (ShellSyntax.resource (words [ ""; " " ])) None "no words"
                        Expect.equal (ShellSyntax.normalize "  a\t\tb\r\n c\u0007d ") "a b c d" "normalized"
                    }
                ]

            testList
                "properties"
                [
                    test "lines of plain commands split into exactly those commands, in order" {
                        let random = Random 1234
                        let names = [| "ls"; "git"; "make"; "npm"; "cat"; "Get-Item"; "echo" |]
                        let args = [| "-la"; "status"; "test"; "file.txt"; "a/b"; "-Force"; "x_1" |]
                        let separators = [| "; "; " && "; " || "; " | "; "\n"; " & " |]

                        for dialect in [ ShellDialect.Sh; ShellDialect.PowerShell ] do
                            for _ in 1..500 do
                                let commands =
                                    List.init (1 + random.Next 6) (fun _ ->
                                        names[random.Next names.Length]
                                        :: List.init (random.Next 4) (fun _ -> args[random.Next args.Length])
                                        |> String.concat " ")

                                let line =
                                    commands
                                    |> List.mapi (fun i c ->
                                        if i = 0 then
                                            c
                                        else
                                            separators[random.Next separators.Length] + c)
                                    |> String.concat ""

                                parsed dialect line commands
                    }

                    test "substitutions at any depth split into their commands, before the command that holds them" {
                        let random = Random 99
                        let names = [| "ls"; "git"; "make"; "cat"; "echo" |]
                        let args = [| "-la"; "status"; "file.txt"; "x_1" |]
                        let separators = [| "; "; " && "; " | "; "\n" |]

                        // A list's text and the commands it splits into.
                        let rec list (depth: int) : string * string list =
                            let commands = List.init (1 + random.Next 3) (fun _ -> command depth)

                            let text =
                                commands
                                |> List.mapi (fun i (t, _) ->
                                    if i = 0 then
                                        t
                                    else
                                        separators[random.Next separators.Length] + t)
                                |> String.concat ""

                            text, commands |> List.collect snd

                        and command (depth: int) : string * string list =
                            let words =
                                List.init (random.Next 4) (fun _ ->
                                    if depth < 3 && random.Next 4 = 0 then
                                        let inner, expected = list (depth + 1)
                                        let text = "$(" + inner + ")"
                                        text, "⟨" + text + "⟩", expected
                                    else
                                        let arg = args[random.Next args.Length]
                                        arg, arg, [])

                            let name = names[random.Next names.Length]
                            let text = String.concat " " (name :: (words |> List.map (fun (t, _, _) -> t)))
                            let shown = String.concat " " (name :: (words |> List.map (fun (_, r, _) -> r)))
                            text, (words |> List.collect (fun (_, _, e) -> e)) @ [ shown ]

                        for dialect in [ ShellDialect.Sh; ShellDialect.PowerShell ] do
                            for _ in 1..300 do
                                let line, expected = list 0
                                parsed dialect line expected
                    }

                    test
                        "the parsers are total: any line is split or opaque with a reason, and parts are valid resources" {
                        let random = Random 4321

                        let pieces =
                            [|
                                "a"
                                " "
                                "rm"
                                "-rf"
                                "$"
                                "("
                                ")"
                                "`"
                                "'"
                                "\""
                                "\\"
                                ";"
                                "&"
                                "|"
                                "<"
                                ">"
                                "{"
                                "}"
                                "["
                                "]"
                                "#"
                                "\n"
                                "~"
                                "*"
                                "@"
                                "$("
                                "<#"
                                "#>"
                                "2>&1"
                                "="
                                ","
                                "."
                                "--%"
                                "\t"
                                "\u0007"
                            |]

                        for dialect in [ ShellDialect.Sh; ShellDialect.PowerShell ] do
                            for _ in 1..5000 do
                                let line = randomLine random pieces (random.Next 24)

                                match ShellSyntax.parse dialect line with
                                | CommandLine.Opaque reason -> Expect.isNotEmpty reason line
                                | CommandLine.Parsed commands ->
                                    for command in commands do
                                        Expect.isFalse (command.Words.IsEmpty && command.Writes.IsEmpty) line

                                        match ShellSyntax.resource command.Words with
                                        | Some resource ->
                                            Expect.equal
                                                (Fx.Grants.Resource.validate Fx.Grants.ResourceKind.Command resource)
                                                (Ok())
                                                resource
                                        | None -> ()
                    }
                ]
        ]
