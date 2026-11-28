/// The core bindings over fakes: paths, bounded output, files, patches and their failures.
module Fx.Interpreters.Tests.CoreBindingsTests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Interpreters
open Fx.Interpreters.Tests.Support
open Fx.Kernel
open Fx.Sdk
open Fx.Store

let private settings = CoreSettings.defaults workspace os

/// The settings with command lines split as a shell would, roughly: CodeAct's `ShellSyntax.words`
/// is tested with it; here a line with any of the shell's operators is not one simple command.
let private searchSettings =
    { settings with
        Words =
            fun line ->
                if line.IndexOfAny([| '|'; '&'; ';'; '<'; '>'; '$'; '`'; '('; ')' |]) >= 0 then
                    None
                else
                    Some(line.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)
    }

let private inline' (text: string) = Value.Inline(parse text)
let private str (text: string) = Value.Inline(Json.String text)

let private call (f: Fakes) (binding: string) (args: Value list) =
    run
        f.Env
        (HostBinding.dispatch
            (CoreBindings.all settings)
            {
                CellId = Some "c1"
                Binding = binding
                Args = args
            })

let private json (result: Result<Value, HostCallError>) : Json =
    match result with
    | Ok(Value.Inline json) -> json
    | other -> failtest $"{other}"

let private field (name: string) (json: Json) : Json =
    match json with
    | Json.Object members ->
        members
        |> List.tryPick (fun (n, v) -> if n = name then Some v else None)
        |> Option.defaultWith (fun () -> failtest $"no {name} in {Json.serialize json}")
    | _ -> failtest $"not an object: {Json.serialize json}"

let private text (json: Json) =
    match json with
    | Json.String s -> s
    | other -> failtest $"not a string: {Json.serialize other}"

let private failedCode (result: Result<Value, HostCallError>) =
    match result with
    | Error(HostCallError.Failed(code, _, _)) -> code
    | other -> failtest $"expected a failure: {other}"

let private inWorkspace (relative: string) = CoreBindings.resolve settings relative

let private readText (f: Fakes) (relative: string) : string =
    match (f.Env.FsRead.ReadFile (inWorkspace relative) CancellationToken.None).Result with
    | Ok bytes -> Encoding.UTF8.GetString bytes
    | Error e -> failtest (string e)

[<Tests>]
let tests =
    testList
        "core bindings"
        [
            testList
                "paths"
                [
                    testCase "relative paths resolve under the workspace, absolute ones stay"
                    <| fun () ->
                        let sep = if os = Os.Windows then "\\" else "/"
                        Expect.equal (inWorkspace "src/a.ts") (workspace + sep + "src" + sep + "a.ts") "relative"
                        Expect.equal (inWorkspace "./src/../b.ts") (workspace + sep + "b.ts") "dots"
                        Expect.equal (inWorkspace ".") workspace "the workspace"
                        Expect.equal (inWorkspace "") workspace "empty"
                        let absolute = path [ "elsewhere"; "x" ]
                        Expect.equal (inWorkspace absolute) absolute "absolute"

                    testCase "paths are shown relative to the workspace, with /"
                    <| fun () ->
                        Expect.equal (CoreBindings.display settings (inWorkspace "src/a.ts")) "src/a.ts" "inside"
                        Expect.equal (CoreBindings.display settings workspace) "." "the workspace"
                        let outside = path [ "workspace2"; "a" ]
                        Expect.equal (CoreBindings.display settings outside) outside "a sibling with the same prefix"

                    testCase "the same on every target"
                    <| fun () ->
                        let windows = CoreSettings.defaults @"C:\w" Os.Windows
                        Expect.equal (CoreBindings.resolve windows "src/a.ts") @"C:\w\src\a.ts" "Windows"
                        Expect.equal (CoreBindings.display windows @"c:\W\src\a.ts") "src/a.ts" "without case"
                        let linux = CoreSettings.defaults "/w" Os.Linux
                        Expect.equal (CoreBindings.resolve linux "src\\a.ts") "/w/src/a.ts" "backslashes"
                        Expect.equal (CoreBindings.display linux "/W/src/a.ts") "/W/src/a.ts" "with case"
                ]

            testList
                "bounded text"
                [
                    testCase "within the limit, text is whole"
                    <| fun () -> Expect.equal (CoreBindings.bound 10 "short") ("short", false) "whole"

                    testCase "beyond it, the head and the tail with a marker"
                    <| fun () ->
                        let text = String.replicate 30 "a" + String.replicate 30 "b"
                        let bounded, cut = CoreBindings.bound 20 text
                        Expect.isTrue cut "cut"
                        Expect.stringStarts bounded (String.replicate 10 "a") "head"
                        Expect.stringEnds bounded (String.replicate 10 "b") "tail"
                        Expect.stringContains bounded "[40 chars omitted]" "marker"

                    testCase "never half a surrogate pair"
                    <| fun () ->
                        let text = String.replicate 20 "😀"
                        let bounded, _ = CoreBindings.bound 11 text
                        Expect.isFalse (Char.IsHighSurrogate bounded[bounded.Length - 1]) "the end"

                        for i in 0 .. bounded.Length - 1 do
                            if Char.IsLowSurrogate bounded[i] then
                                Expect.isTrue (i > 0 && Char.IsHighSurrogate bounded[i - 1]) $"pair at {i}"
                ]

            testList
                "shell.run"
                [
                    testCase "runs a script in the workspace and reports its output"
                    <| fun () ->
                        let f =
                            fakes (fun command ->
                                Some(Ok(ScriptedShell.output 3 "out\n" (Encoding.UTF8.GetString command.Stdin))))

                        let result =
                            call f "shell.run" [ str "make test"; inline' """{"cwd": "src", "stdin": "input"}""" ]
                            |> json

                        Expect.equal (field "exitCode" result) (Json.int 3) "exit code"
                        Expect.equal (field "stdout" result |> text) "out\n" "stdout"
                        Expect.equal (field "stderr" result |> text) "input" "stdin"
                        Expect.equal (field "timedOut" result) (Json.Bool false) "not timed out"

                        match f.Shell.Calls with
                        | [ command ] ->
                            Expect.equal command.Spec.Program (Program.Script "make test") "the shell runs it"
                            Expect.equal command.Spec.Cwd (Some(inWorkspace "src")) "in the cwd given"
                            Expect.equal command.Spec.Env (Inherit []) "the environment's variables"
                        | calls -> failtest $"{calls}"

                    testCase "long output is bounded"
                    <| fun () ->
                        let f =
                            fakes (fun _ -> Some(Ok(ScriptedShell.output 0 (String.replicate 40000 "x") "")))

                        let result = call f "shell.run" [ str "yes" ] |> json
                        Expect.equal (field "truncated" result) (Json.Bool true) "truncated"
                        Expect.isLessThan (text (field "stdout" result)).Length 16100 "bounded"

                    testCase "a command that does not start is the failure notStarted"
                    <| fun () ->
                        let f = fakes (fun _ -> None)

                        let missing: Shell =
                            { f.Env.Shell with
                                Spawn = fun _ _ -> Task.FromResult(Error(NotStarted("sh", "missing")))
                            }

                        let result =
                            run
                                { f.Env with Shell = missing }
                                (HostBinding.dispatch
                                    (CoreBindings.all settings)
                                    {
                                        CellId = None
                                        Binding = "shell.run"
                                        Args = [ str "x" ]
                                    })

                        Expect.equal (failedCode result) "notStarted" "code"

                    testCase "a command past its timeout is killed and keeps what it printed"
                    <| fun () ->
                        let f = fakes (fun _ -> None)

                        let killed =
                            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                        let mutable kills = 0
                        let mutable reads = 0

                        // Prints a line on each stream, then nothing until it is killed; what it printed
                        // before it died ("last") is still read.
                        let hanging: Proc =
                            {
                                Pid = 42
                                Write = fun _ _ -> Task.FromResult()
                                CloseStdin = fun () -> Task.FromResult()
                                Read =
                                    fun cancellation ->
                                        task {
                                            reads <- reads + 1

                                            match reads with
                                            | 1 -> return Some(Stdout(Encoding.UTF8.GetBytes "partial\n"))
                                            | 2 -> return Some(Stderr(Encoding.UTF8.GetBytes "warning\n"))
                                            | 3 ->
                                                do! killed.Task.WaitAsync cancellation
                                                return Some(Stdout(Encoding.UTF8.GetBytes "last\n"))
                                            | 4 -> return Some(Stdout(Encoding.UTF8.GetBytes "last\n"))
                                            | _ -> return None
                                        }
                                Wait =
                                    fun cancellation ->
                                        task {
                                            do! killed.Task.WaitAsync cancellation
                                            return 137
                                        }
                                Kill =
                                    fun () ->
                                        kills <- kills + 1
                                        killed.TrySetResult() |> ignore
                                        Task.FromResult()
                            }

                        let env =
                            { f.Env with
                                Shell =
                                    { f.Env.Shell with
                                        Spawn = fun _ _ -> Task.FromResult(Ok hanging)
                                    }
                                Clock = SystemClock.clock
                            }

                        let result =
                            run
                                env
                                (HostBinding.dispatch
                                    (CoreBindings.all settings)
                                    {
                                        CellId = None
                                        Binding = "shell.run"
                                        Args = [ str "make watch"; inline' """{"timeoutMs": 20}""" ]
                                    })
                            |> json

                        Expect.equal (field "timedOut" result) (Json.Bool true) "timed out"
                        Expect.equal (field "stdout" result |> text) "partial\nlast\n" "what it printed, to its end"
                        Expect.equal (field "stderr" result |> text) "warning\n" "its errors"
                        Expect.equal kills 1 "killed once (with its children: Proc.Kill)"

                        Expect.isFalse
                            (match result with
                             | Json.Object m -> m |> List.exists (fun (n, _) -> n = "exitCode" || n = "matches")
                             | _ -> true)
                            "no exit code, no matches"

                    testCase "a command stopped before its end is killed"
                    <| fun () ->
                        let f = fakes (fun _ -> None)

                        let killed =
                            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                        let hanging: Proc =
                            {
                                Pid = 42
                                Write = fun _ _ -> Task.FromResult()
                                CloseStdin = fun () -> Task.FromResult()
                                Read =
                                    fun cancellation ->
                                        task {
                                            do! killed.Task.WaitAsync cancellation
                                            return None
                                        }
                                Wait = fun _ -> Task.FromResult 137
                                Kill =
                                    fun () ->
                                        killed.TrySetResult() |> ignore
                                        Task.FromResult()
                            }

                        use cancel = new CancellationTokenSource()

                        let env =
                            { f.Env with
                                Shell =
                                    { f.Env.Shell with
                                        Spawn = fun _ _ -> Task.FromResult(Ok hanging)
                                    }
                                Scope = Scope.ofCancellation cancel.Token
                            }

                        let running =
                            runTask
                                env
                                (HostBinding.dispatch
                                    (CoreBindings.all settings)
                                    {
                                        CellId = None
                                        Binding = "shell.run"
                                        Args = [ str "sleep 100" ]
                                    })

                        cancel.Cancel()

                        try
                            running.GetAwaiter().GetResult() |> ignore
                            failtest "not cancelled"
                        with :? OperationCanceledException ->
                            ()

                        Expect.isTrue killed.Task.IsCompleted "killed"

                    testCase "arguments of the wrong type are refused"
                    <| fun () ->
                        let f = fakes (fun _ -> None)

                        match call f "shell.run" [ inline' "42" ] with
                        | Error(HostCallError.InvalidArguments _) -> ()
                        | other -> failtest $"{other}"
                ]

            testList
                "files"
                [
                    testCase "write, read, stat and list"
                    <| fun () ->
                        let f = fakes (fun _ -> None)
                        let written = call f "fs.write" [ str "src/app.ts"; str "one\ntwo\n" ] |> json
                        Expect.equal (field "path" written |> text) "src/app.ts" "relative"
                        Expect.equal (field "lines" written) (Json.int 2) "lines"
                        let digest = field "digest" written

                        Expect.equal
                            (text digest)
                            (string (BlobHash.ofBytes (Encoding.UTF8.GetBytes "one\ntwo\n")))
                            "digest"

                        let read = call f "fs.read" [ str "src/app.ts" ] |> json
                        Expect.equal (field "text" read |> text) "one\ntwo\n" "text"
                        Expect.equal (field "digest" read) digest "same digest"
                        Expect.equal (field "size" read) (Json.int 8) "size"

                        let stat = call f "fs.stat" [ str "src" ] |> json
                        Expect.equal (field "kind" stat |> text) "directory" "a directory"

                        let listed = call f "fs.list" [] |> json

                        match listed with
                        | Json.Array [ entry ] ->
                            Expect.equal (field "path" entry |> text) "src" "the workspace's entries"
                            Expect.equal (field "name" entry |> text) "src" "name"
                        | other -> failtest $"{Json.serialize other}"

                        match call f "fs.list" [ str "src" ] |> json with
                        | Json.Array [ entry ] -> Expect.equal (field "path" entry |> text) "src/app.ts" "nested"
                        | other -> failtest $"{Json.serialize other}"

                    testCase "a byte order mark is not text"
                    <| fun () ->
                        let f = fakes (fun _ -> None)

                        f.Fs.AddFile(
                            inWorkspace "bom.txt",
                            Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (Encoding.UTF8.GetBytes "x")
                        )

                        Expect.equal (call f "fs.read" [ str "bom.txt" ] |> json |> field "text" |> text) "x" "text"

                    testCase "failures carry their kind as the code"
                    <| fun () ->
                        let f = fakes (fun _ -> None)
                        Expect.equal (failedCode (call f "fs.read" [ str "missing.ts" ])) "notFound" "not found"
                        Expect.equal (failedCode (call f "fs.stat" [ str "missing.ts" ])) "notFound" "stat"
                        f.Fs.AddFile(inWorkspace "bin.dat", [| 0xFFuy; 0xFEuy; 0x00uy |])
                        Expect.equal (failedCode (call f "fs.read" [ str "bin.dat" ])) "notText" "binary"
                        f.Fs.AddText(inWorkspace "big.txt", String.replicate (settings.ReadBytes + 1) "x")
                        Expect.equal (failedCode (call f "fs.read" [ str "big.txt" ])) "tooLarge" "too large"

                    testCase "fs.read refuses files content exclusions keep from models, with why"
                    <| fun () ->
                        let exclusions =
                            match
                                Fx.Models.Exclusions.create
                                    [
                                        {
                                            Origin = "host"
                                            Patterns = [ "*.env"; "secrets/" ]
                                        }
                                    ]
                            with
                            | Ok e -> e
                            | Error issues -> failtest (string issues)

                        let excluding =
                            { settings with
                                Exclusions = exclusions
                            }

                        let f = fakes (fun _ -> None)
                        f.Fs.AddText(inWorkspace ".env", "TOKEN=SECRET")
                        f.Fs.AddDirectory(inWorkspace "secrets")
                        f.Fs.AddText(inWorkspace "secrets/k.txt", "SECRET")
                        f.Fs.AddText(inWorkspace "readme.md", "hello")

                        let read (path: string) =
                            run
                                f.Env
                                (HostBinding.dispatch
                                    (CoreBindings.all excluding)
                                    {
                                        CellId = Some "c1"
                                        Binding = "fs.read"
                                        Args = [ str path ]
                                    })

                        let workspaceName = System.IO.Path.GetFileName(workspace.TrimEnd([| '/'; '\\' |]))

                        for path in
                            [
                                ".env"
                                "./secrets/k.txt"
                                @"secrets\k.txt"
                                inWorkspace "secrets/k.txt"
                                "../" + workspaceName + "/secrets/k.txt"
                            ] do
                            match read path with
                            | Error(HostCallError.Failed("excluded", message, Some data)) ->
                                Expect.isFalse (message.Contains "SECRET") $"{path}: no content"
                                Expect.equal (field "origin" data |> text) "host" $"{path}: the origin"
                            | other -> failtest $"{path}: {other}"

                        Expect.equal (read "readme.md" |> json |> field "text" |> text) "hello" "other files"

                        Expect.equal
                            (CoreBindings.exclusionPath excluding (inWorkspace "a/../b/c.txt"))
                            "b/c.txt"
                            "relative"

                        Expect.equal (CoreBindings.exclusionPath excluding workspace) "" "the workspace"

                        Expect.equal
                            (call f "fs.read" [ str ".env" ] |> json |> field "text" |> text)
                            "TOKEN=SECRET"
                            "no exclusions"

                    testCase "patches apply together, and a stale digest fails them all"
                    <| fun () ->
                        let f = fakes (fun _ -> None)
                        f.Fs.AddDirectory(inWorkspace "src")
                        f.Fs.AddText(inWorkspace "src/a.ts", "a1\na2\n")
                        let read = call f "fs.read" [ str "src/a.ts" ] |> json
                        let digest = field "digest" read |> text

                        let patches =
                            $$"""[{"op": "edit", "path": "src/a.ts", "digest": "{{digest}}", "edits": [{"line": 2, "count": 1, "lines": ["b2"]}]},
                                  {"op": "create", "path": "src/b.ts", "text": "new\n"}]"""

                        match call f "fs.patch" [ inline' patches ] |> json with
                        | Json.Array [ a; b ] ->
                            Expect.equal (field "path" a |> text) "src/a.ts" "edited"
                            Expect.equal (field "path" b |> text) "src/b.ts" "created"
                        | other -> failtest $"{Json.serialize other}"

                        Expect.equal
                            (call f "fs.read" [ str "src/a.ts" ] |> json |> field "text" |> text)
                            "a1\nb2\n"
                            "edited"

                        let stale =
                            $$"""[{"op": "edit", "path": "src/a.ts", "digest": "{{digest}}", "edits": [{"line": 1, "count": 1, "lines": ["x"]}]}]"""

                        Expect.equal (failedCode (call f "fs.patch" [ inline' stale ])) "stale" "stale"
                ]

            testList
                "ranged reads and anchored edits"
                [
                    let lines (count: int) =
                        String.Join("", [ for i in 1..count -> $"line {i}\n" ])

                    let decodeRange (json: Json) =
                        match CoreBindings.Codecs.fileRange.Decode json with
                        | Ok range -> range
                        | Error issues -> failtest (string issues)

                    let editedOf (result: Result<Value, HostCallError>) : Edited =
                        match CoreBindings.Codecs.edited.Decode(json result) with
                        | Ok edited -> edited
                        | Error issues -> failtest (string issues)

                    let change (old: string) (replacement: string) =
                        Json.Object [ "old", Json.String old; "new", Json.String replacement ]

                    testCase "a ranged read gives those lines, the whole file's digest and line count"
                    <| fun () ->
                        let f = fakes (fun _ -> None)
                        f.Fs.AddText(inWorkspace "a.txt", lines 10)
                        let whole = call f "fs.read" [ str "a.txt" ] |> json
                        Expect.equal (field "start" whole) (Json.int 1) "a whole file starts at 1"
                        Expect.equal (field "end" whole) (Json.int 10) "and ends at its last line"
                        let part = call f "fs.read" [ str "a.txt"; inline' """{"lines": [3, 4]}""" ] |> json
                        Expect.equal (field "text" part |> text) "line 3\nline 4\n" "those lines"
                        Expect.equal (field "start" part, field "end" part) (Json.int 3, Json.int 4) "where"
                        Expect.equal (field "digest" part) (field "digest" whole) "the file's digest"
                        Expect.equal (field "lines" part) (Json.int 10) "the file's lines"
                        Expect.equal (field "size" part) (field "size" whole) "the file's size"

                        let tail =
                            call f "fs.read" [ str "a.txt"; inline' """{"lines": [9, 20]}""" ] |> json

                        Expect.equal (field "end" tail) (Json.int 10) "lines past the end are left out"

                        let after =
                            call f "fs.read" [ str "a.txt"; inline' """{"lines": [11, 12]}""" ] |> json

                        Expect.equal
                            (field "text" after |> text, field "end" after)
                            ("", Json.int 10)
                            "an empty range at the end"

                        Expect.equal
                            (failedCode (call f "fs.read" [ str "a.txt"; inline' """{"lines": [12, 13]}""" ]))
                            "outOfRange"
                            "beyond it"

                        match call f "fs.read" [ str "a.txt"; inline' """{"lines": [4, 3]}""" ] with
                        | Error(HostCallError.InvalidArguments _) -> ()
                        | other -> failtest $"backwards: {other}"

                    testCase "fs.edit replaces text that occurs once, and gives the lines it left as a fresh range"
                    <| fun () ->
                        let f = fakes (fun _ -> None)
                        f.Fs.AddText(inWorkspace "a.txt", "one\ntwo\nthree\n")

                        let edited =
                            call f "fs.edit" [ str "a.txt"; Value.Inline(change "two" "2\n2b") ] |> editedOf

                        Expect.equal (readText f "a.txt") "one\n2\n2b\nthree\n" "edited"
                        Expect.equal edited.Path "a.txt" "the path"
                        Expect.equal edited.Lines 4 "its lines"
                        Expect.equal edited.Replacements 1 "one"

                        Expect.equal
                            edited.Digest
                            (BlobHash.ofBytes (Encoding.UTF8.GetBytes "one\n2\n2b\nthree\n"))
                            "the new digest"

                        match edited.Ranges with
                        | [ range ] ->
                            Expect.equal (range.Start, range.End, range.Text) (2, 3, "2\n2b\n") "the new lines"
                            Expect.equal range.Digest edited.Digest "of the new version"

                            let again =
                                call
                                    f
                                    "fs.editRange"
                                    [ Value.Inline(CoreBindings.Codecs.fileRange.Encode range); str "two" ]
                                |> editedOf

                            Expect.equal (readText f "a.txt") "one\ntwo\nthree\n" "the same lines edited again"
                            Expect.equal (again.Ranges |> List.map (fun r -> r.Start, r.End)) [ 2, 2 ] "fresh again"
                        | other -> failtest $"{other}"

                    testCase "missing, ambiguous and unchanged text fail as hybrid's edit says them; nothing is written"
                    <| fun () ->
                        let f = fakes (fun _ -> None)
                        f.Fs.AddText(inWorkspace "a.txt", "x x\n")

                        let failure (changes: Json) =
                            match call f "fs.edit" [ str "a.txt"; Value.Inline changes ] with
                            | Error(HostCallError.Failed(code, message, _)) -> code, message
                            | other -> failtest $"{other}"

                        let code, message = failure (change "y" "z")
                        Expect.equal code "missing" "missing"

                        Expect.stringContains
                            message
                            "`old` does not occur in a.txt: copy it from the file as read"
                            "worded"

                        let code, message = failure (change "x" "z")
                        Expect.equal code "ambiguous" "ambiguous"
                        Expect.stringContains message "`old` occurs 2 times in a.txt" "worded"
                        Expect.stringContains message "set `all`" "how"
                        let code, message = failure (change "x x" "x x")

                        Expect.equal
                            (code, message)
                            ("unchanged", "`old` and `new` are the same: nothing to change.")
                            "the same"

                        let code, message = failure (Json.Array [ change "x x" "a"; change "x" "b" ])
                        Expect.equal code "missing" "after the change before it"

                        Expect.stringStarts
                            message
                            "Change 2 of 2: `old` does not occur in a.txt (as the changes before it left it)"
                            "which"

                        Expect.equal (readText f "a.txt") "x x\n" "nothing written"

                        match call f "fs.edit" [ str "a.txt"; Value.Inline(change "" "z") ] with
                        | Error(HostCallError.InvalidArguments _) -> ()
                        | other -> failtest $"empty old: {other}"

                    testCase "all replaces every occurrence; several changes apply in order, written together"
                    <| fun () ->
                        let f = fakes (fun _ -> None)
                        f.Fs.AddText(inWorkspace "a.txt", "x = 1\ny\nx = 2\nz\n")

                        let edited =
                            call
                                f
                                "fs.edit"
                                [
                                    str "a.txt"
                                    Value.Inline(
                                        Json.Array
                                            [
                                                Json.Object
                                                    [
                                                        "old", Json.String "x"
                                                        "new", Json.String "w"
                                                        "all", Json.Bool true
                                                    ]
                                                change "w = 2" "w = 20"
                                                change "z\n" ""
                                            ]
                                    )
                                ]
                            |> editedOf

                        Expect.equal (readText f "a.txt") "w = 1\ny\nw = 20\n" "edited"
                        Expect.equal edited.Replacements 4 "replacements"

                        Expect.equal
                            (edited.Ranges |> List.map (fun r -> r.Start, r.End, r.Text))
                            [ 1, 1, "w = 1\n"; 3, 3, "w = 20\n"; 4, 3, "" ]
                            "a fresh range for each region, in file order"

                    testCase "CRLF files keep their line endings; text matches with either ending"
                    <| fun () ->
                        let f = fakes (fun _ -> None)
                        f.Fs.AddText(inWorkspace "w.txt", "a\r\nb\r\nc\r\n")

                        let edited =
                            call f "fs.edit" [ str "w.txt"; Value.Inline(change "a\nb" "A\r\nB\nB2") ]
                            |> editedOf

                        Expect.equal (readText f "w.txt") "A\r\nB\r\nB2\r\nc\r\n" "CRLF kept"

                        Expect.equal
                            (edited.Ranges |> List.map (fun r -> r.Text))
                            [ "A\r\nB\r\nB2\r\n" ]
                            "as in the file"

                        let range =
                            call f "fs.read" [ str "w.txt"; inline' """{"lines": [4, 4]}""" ] |> json

                        Expect.equal (field "text" range |> text) "c\r\n" "a range as it is"
                        call f "fs.editRange" [ Value.Inline range; str "C\n" ] |> editedOf |> ignore
                        Expect.equal (readText f "w.txt") "A\r\nB\r\nB2\r\nC\r\n" "replaced, CRLF"

                    testCase "a range whose lines changed is stale, with the lines there now; nothing is written"
                    <| fun () ->
                        let f = fakes (fun _ -> None)
                        f.Fs.AddText(inWorkspace "a.txt", lines 5)

                        let range =
                            call f "fs.read" [ str "a.txt"; inline' """{"lines": [2, 3]}""" ] |> json

                        let unrelated =
                            call f "fs.read" [ str "a.txt"; inline' """{"lines": [5, 5]}""" ] |> json

                        call f "fs.editRange" [ Value.Inline unrelated; str "five" ]
                        |> editedOf
                        |> ignore

                        Expect.equal (readText f "a.txt") "line 1\nline 2\nline 3\nline 4\nfive\n" "edited"

                        call f "fs.editRange" [ Value.Inline range; str "two\nthree" ]
                        |> editedOf
                        |> ignore

                        Expect.equal
                            (readText f "a.txt")
                            "line 1\ntwo\nthree\nline 4\nfive\n"
                            "a range whose lines did not change still applies after another edit"

                        match call f "fs.editRange" [ Value.Inline range; str "x" ] with
                        | Error(HostCallError.Failed("stale", message, Some data)) ->
                            Expect.stringContains message "Lines 2–3 of a.txt are not what the range holds" "why"
                            let now = decodeRange data
                            Expect.equal (now.Start, now.End, now.Text) (2, 3, "two\nthree\n") "the lines there now"
                        | other -> failtest $"{other}"

                        f.Fs.AddText(inWorkspace "short.txt", "a\n")

                        let beyond =
                            Json.Object
                                [
                                    "path", Json.String "short.txt"
                                    "start", Json.int 2
                                    "end", Json.int 3
                                    "text", Json.String "b\nc\n"
                                    "digest", field "digest" range
                                    "lines", Json.int 3
                                ]

                        match call f "fs.editRange" [ Value.Inline beyond; str "x" ] with
                        | Error(HostCallError.Failed("stale", _, Some data)) ->
                            let now = decodeRange data
                            Expect.equal (now.Start, now.End, now.Text) (2, 1, "") "past the end: none"
                        | other -> failtest $"{other}"

                        Expect.equal (readText f "short.txt") "a\n" "nothing written"

                        match call f "fs.editRange" [ Value.Inline range; str "two\nthree\n" ] with
                        | Error(HostCallError.Failed("stale", _, _)) -> ()
                        | other -> failtest $"{other}"

                        let current =
                            call f "fs.read" [ str "a.txt"; inline' """{"lines": [2, 3]}""" ] |> json

                        Expect.equal
                            (failedCode (call f "fs.editRange" [ Value.Inline current; str "two\r\nthree\n" ]))
                            "unchanged"
                            "the same lines"

                    testCase "an empty range inserts; empty text removes the lines"
                    <| fun () ->
                        let f = fakes (fun _ -> None)
                        f.Fs.AddText(inWorkspace "a.txt", "a\nb")

                        let atEnd =
                            call f "fs.read" [ str "a.txt"; inline' """{"lines": [3, 3]}""" ] |> json

                        let edited = call f "fs.editRange" [ Value.Inline atEnd; str "c\nd\n" ] |> editedOf
                        Expect.equal (readText f "a.txt") "a\nb\nc\nd" "appended, without a final line ending as before"
                        Expect.equal (edited.Ranges |> List.map (fun r -> r.Start, r.End)) [ 3, 4 ] "the new lines"

                        let first =
                            call f "fs.read" [ str "a.txt"; inline' """{"lines": [1, 2]}""" ] |> json

                        let removed = call f "fs.editRange" [ Value.Inline first; str "" ] |> editedOf
                        Expect.equal (readText f "a.txt") "c\nd" "removed"

                        Expect.equal
                            (removed.Ranges |> List.map (fun r -> r.Start, r.End))
                            [ 1, 0 ]
                            "an empty range where they were"

                    testCase "edits of files content exclusions keep from models are refused before anything is read"
                    <| fun () ->
                        let exclusions =
                            match
                                Fx.Models.Exclusions.create
                                    [
                                        {
                                            Origin = "host"
                                            Patterns = [ "*.env" ]
                                        }
                                    ]
                            with
                            | Ok e -> e
                            | Error issues -> failtest (string issues)

                        let excluding =
                            { settings with
                                Exclusions = exclusions
                            }

                        let f = fakes (fun _ -> None)
                        f.Fs.AddText(inWorkspace ".env", "TOKEN=SECRET\n")

                        let callWith (binding: string) (args: Value list) =
                            run
                                f.Env
                                (HostBinding.dispatch
                                    (CoreBindings.all excluding)
                                    {
                                        CellId = Some "c1"
                                        Binding = binding
                                        Args = args
                                    })

                        let range =
                            Json.Object
                                [
                                    "path", Json.String ".env"
                                    "start", Json.int 1
                                    "end", Json.int 1
                                    "text", Json.String "x\n"
                                    "digest", Json.String(string (BlobHash.ofBytes [| 1uy |]))
                                    "lines", Json.int 1
                                ]

                        for binding, args in
                            [
                                "fs.read", [ str ".env"; inline' """{"lines": [1, 1]}""" ]
                                "fs.edit", [ str ".env"; Value.Inline(change "SECRET" "x") ]
                                "fs.editRange", [ Value.Inline range; str "x" ]
                            ] do
                            match callWith binding args with
                            | Error(HostCallError.Failed("excluded", message, _)) ->
                                Expect.isFalse (message.Contains "SECRET") $"{binding}: no content"
                            | other -> failtest $"{binding}: {other}"

                        Expect.equal (readText f ".env") "TOKEN=SECRET\n" "unchanged"

                    testCase "a file that changed between the read and the write is not overwritten"
                    <| fun () ->
                        let f = fakes (fun _ -> None)
                        f.Fs.AddText(inWorkspace "a.txt", "a\n")
                        let ops = f.Env.FsOps

                        let racing: Fx.Envd.FsOps =
                            { ops with
                                ApplyPatch =
                                    fun patches cancellation ->
                                        f.Fs.AddText(inWorkspace "a.txt", "changed\n")
                                        ops.ApplyPatch patches cancellation
                            }

                        let result =
                            run
                                { f.Env with FsOps = racing }
                                (HostBinding.dispatch
                                    (CoreBindings.all settings)
                                    {
                                        CellId = None
                                        Binding = "fs.edit"
                                        Args = [ str "a.txt"; Value.Inline(change "a" "b") ]
                                    })

                        Expect.equal (failedCode result) "stale" "stale"
                        Expect.equal (readText f "a.txt") "changed\n" "kept"
                ]

            testList
                "searches"
                [
                    // The files a search finds in, and a shell that prints what grep would.
                    let searching (printed: string) =
                        let f =
                            fakes (fun command ->
                                match command.Spec.Program with
                                | Program.Script "false" -> Some(Ok(ScriptedShell.output 1 printed ""))
                                | _ -> Some(ScriptedShell.ok printed))

                        f.Fs.AddText(inWorkspace "src/retry.ts", "let a = 1\nretry(3)\nlet b = 2\n")
                        f.Fs.AddText(inWorkspace "src/loop.ts", "retry(1)\r\nwhile (true) {}\r\n")
                        f.Fs.AddText(inWorkspace ".env", "retry=SECRET\n")
                        f

                    let searchWith (settings: CoreSettings) (f: Fakes) (command: string) =
                        run
                            f.Env
                            (HostBinding.dispatch
                                (CoreBindings.all settings)
                                {
                                    CellId = None
                                    Binding = "shell.run"
                                    Args = [ str command ]
                                })
                        |> json

                    let search = searchWith searchSettings

                    let matches (result: Json) : FileRange list =
                        match result with
                        | Json.Object members ->
                            match members |> List.tryFind (fun (n, _) -> n = "matches") with
                            | Some(_, value) ->
                                match Codec.decode (Codec.list CoreBindings.Codecs.fileRange) value with
                                | Ok ranges -> ranges
                                | Error issues -> failtest (string issues)
                            | None -> []
                        | _ -> failtest $"not an object: {Json.serialize result}"

                    let found = "src/retry.ts:2:retry(3)\nsrc/loop.ts:1:retry(1)\r\n"

                    testCase "a plain search's lines come back as ranges of their files"
                    <| fun () ->
                        let f = searching found
                        let result = search f "grep -rn retry src"
                        let ranges = matches result
                        Expect.equal (field "stdout" result |> text) found "its output, as it printed it"

                        Expect.equal
                            (ranges |> List.map (fun r -> r.Path, r.Start, r.End, r.Text, r.Lines))
                            [
                                "src/retry.ts", 2, 2, "retry(3)\n", 3
                                "src/loop.ts", 1, 1, "retry(1)\r\n", 2
                            ]
                            "one-line ranges, with the files' own line endings"

                        Expect.equal
                            ranges.Head.Digest
                            (BlobHash.ofBytes (Encoding.UTF8.GetBytes "let a = 1\nretry(3)\nlet b = 2\n"))
                            "the file's digest"

                    testCase "one file searched: its lines without the file's name"
                    <| fun () ->
                        let f = searching "2:retry(3)\n"

                        for command in [ "grep -n retry src/retry.ts"; "rg -n -e retry src/retry.ts" ] do
                            Expect.equal
                                (matches (search f command) |> List.map (fun r -> r.Path, r.Start))
                                [ "src/retry.ts", 2 ]
                                command

                    testCase "paths relative to the command's working directory"
                    <| fun () ->
                        let f = searching "retry.ts:2:retry(3)\n../src/loop.ts:1:retry(1)\n"

                        let result =
                            run
                                f.Env
                                (HostBinding.dispatch
                                    (CoreBindings.all searchSettings)
                                    {
                                        CellId = None
                                        Binding = "shell.run"
                                        Args = [ str "git grep -n retry -- . ../src"; inline' """{"cwd": "src"}""" ]
                                    })
                            |> json

                        Expect.equal
                            (matches result |> List.map (fun r -> r.Path, r.Start))
                            [ "src/retry.ts", 2; "src/loop.ts", 1 ]
                            "relative to the workspace"

                    testCase "a match is a range fs.editRange takes"
                    <| fun () ->
                        let f = searching found
                        let first = matches (search f "rg -n retry") |> List.head

                        let edited =
                            call
                                f
                                "fs.editRange"
                                [ Value.Inline(CoreBindings.Codecs.fileRange.Encode first); str "retry(5)" ]
                            |> json

                        Expect.equal (field "replacements" edited) (Json.int 1) "replaced"
                        Expect.equal (readText f "src/retry.ts") "let a = 1\nretry(5)\nlet b = 2\n" "the line"

                    testCase "no matches unless every line is its file's line"
                    <| fun () ->
                        let none (why: string) (f: Fakes) (command: string) =
                            Expect.isEmpty (matches (search f command)) why

                        // What the command line says.
                        none "not a search" (searching found) "cat src/retry.ts"
                        none "without -n" (searching found) "grep -r retry src"
                        none "a pipe" (searching found) "grep -rn retry src | head"
                        none "a count" (searching found) "grep -rnc retry src"
                        none "file names only" (searching found) "grep -rnl retry src"
                        none "files without" (searching found) "rg -n --files-without-match retry"
                        none "only the matching parts" (searching found) "rg -no retry"
                        none "context" (searching found) "grep -rn -C 2 retry src"
                        none "context, long" (searching found) "rg -n --context=2 retry"
                        none "inverted" (searching found) "git grep -nv retry"
                        none "standard input" (searching "2:retry(3)\n") "grep -n retry"
                        none "a failed command" (searching found) "false"
                        none "a list of commands" (searching found) "grep -rn retry src || false"

                        // What it printed.
                        none
                            "a line that is not its file's (it changed since)"
                            (searching "src/retry.ts:2:retry(4)\n")
                            "grep -rn retry src"

                        none "a line past the end" (searching "src/retry.ts:9:retry(3)\n") "grep -rn retry src"
                        none "a file that is not there" (searching "src/gone.ts:1:retry(3)\n") "grep -rn retry src"
                        none "a path with a colon" (searching "src/a:b.ts:2:retry(3)\n") "grep -rn retry src"

                        none
                            "a line that is not a file line"
                            (searching (found + "Binary file x matches\n"))
                            "grep -rn retry src"

                        let outside = searching (path [ "etc"; "hosts" ] + ":1:retry\n")
                        outside.Fs.AddText(path [ "etc"; "hosts" ], "retry\n")
                        none "outside the workspace" outside "grep -rn retry /etc"

                        let many = String.replicate (Searches.MaxMatches + 1) "src/retry.ts:2:retry(3)\n"
                        none "more lines than the bound" (searching many) "grep -rn retry src"

                        let cut = { searchSettings with OutputChars = 30 }

                        Expect.isEmpty
                            (matches (searchWith cut (searching found) "grep -rn retry src"))
                            "output that was cut"

                    testCase "excluded files and files the sandbox denies give none"
                    <| fun () ->
                        let exclusions =
                            match
                                Fx.Models.Exclusions.create
                                    [
                                        {
                                            Origin = "host"
                                            Patterns = [ "*.env" ]
                                        }
                                    ]
                            with
                            | Ok e -> e
                            | Error issues -> failtest (string issues)

                        let printed = ".env:1:retry=SECRET\n"

                        let f = searching printed

                        Expect.isEmpty
                            (matches (
                                searchWith
                                    { searchSettings with
                                        Exclusions = exclusions
                                    }
                                    f
                                    "grep -rn retry ."
                            ))
                            "excluded"

                        let policy: SandboxPolicy =
                            {
                                Workspace = workspace
                                WorkspaceAccess = WorkspaceAccess.ReadWrite
                                Writable = []
                                Readable = []
                                Protected = []
                                Denied = [ inWorkspace ".env" ]
                                Temp = None
                                Network = NetworkAccess.Allowed
                            }

                        Expect.isEmpty
                            (matches (
                                searchWith
                                    { searchSettings with
                                        Sandbox = Some policy
                                    }
                                    f
                                    "grep -rn retry ."
                            ))
                            "denied"

                        Expect.equal
                            (matches (
                                searchWith
                                    { searchSettings with
                                        Sandbox = Some policy
                                    }
                                    (searching found)
                                    "grep -rn retry src"
                             )
                             |> List.length)
                            2
                            "the rest of the workspace is readable"
                ]

            testList
                "shell.run on this machine"
                [
                    let withWorkspace (body: Env -> CoreSettings -> unit) =
                        let dir =
                            IO.Path.Combine(IO.Path.GetTempPath(), "core-bindings-" + Guid.NewGuid().ToString("N"))

                        IO.Directory.CreateDirectory dir |> ignore

                        let env: Env =
                            {
                                Clock = SystemClock.clock
                                FsRead = LocalFs.read
                                Fs = LocalFs.write
                                FsOps = Fx.Envd.FsOpsOver.create LocalFs.read LocalFs.write
                                Shell = LocalShell.create (LocalShell.defaultShell ()) dir
                                Scope = Scope.root
                            }

                        try
                            body env { searchSettings with Workspace = dir }
                        finally
                            try
                                IO.Directory.Delete(dir, true)
                            with _ ->
                                ()

                    let shellRun (env: Env) (settings: CoreSettings) (command: string) (options: string) =
                        run
                            env
                            (HostBinding.dispatch
                                (CoreBindings.all settings)
                                {
                                    CellId = None
                                    Binding = "shell.run"
                                    Args = [ str command; inline' options ]
                                })
                        |> json

                    let alive (pid: int) =
                        try
                            if OperatingSystem.IsLinux() then
                                let stat = IO.File.ReadAllText $"/proc/{pid}/stat"
                                let close = stat.LastIndexOf ')'
                                close > 0 && close + 2 < stat.Length && not ("ZX".Contains stat[close + 2])
                            else
                                use proc = Diagnostics.Process.GetProcessById pid
                                not proc.HasExited
                        with _ ->
                            false

                    testCase "a command that times out keeps what it printed, and its children are killed"
                    <| fun () ->
                        withWorkspace (fun env settings ->
                            let command =
                                if OperatingSystem.IsWindows() then
                                    "Write-Output started; $p = Start-Process -FilePath pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 60' -NoNewWindow -PassThru; Write-Output \"child $($p.Id)\"; $p.WaitForExit()"
                                else
                                    "echo started; sleep 60 & echo \"child $!\"; wait"

                            let started = DateTime.UtcNow
                            let result = shellRun env settings command """{"timeoutMs": 3000}"""
                            Expect.equal (field "timedOut" result) (Json.Bool true) "timed out"

                            Expect.isLessThan
                                (DateTime.UtcNow - started)
                                (TimeSpan.FromSeconds 30.0)
                                "it did not wait for the children"

                            let stdout = (field "stdout" result |> text).Replace("\r\n", "\n")
                            Expect.stringStarts stdout "started\nchild " "what it printed"
                            let child = int (stdout.Substring("started\nchild ".Length).Trim())
                            let mutable waited = 0

                            while alive child && waited < 100 do
                                Threading.Thread.Sleep 100
                                waited <- waited + 1

                            Expect.isFalse (alive child) "its child was killed")

                    testCase "a plain search's matches, checked against the files"
                    <| fun () ->
                        if OperatingSystem.IsWindows() then
                            skiptest "grep: Windows has no grep (git grep needs a repository)"

                        withWorkspace (fun env settings ->
                            IO.Directory.CreateDirectory(IO.Path.Combine(settings.Workspace, "src"))
                            |> ignore

                            IO.File.WriteAllText(IO.Path.Combine(settings.Workspace, "src", "a.ts"), "x\nretry(3)\n")
                            IO.File.WriteAllText(IO.Path.Combine(settings.Workspace, "b.ts"), "retry(1)\r\ny\r\n")

                            let result = shellRun env settings "grep -rn retry ." "{}"

                            let ranges =
                                match result with
                                | Json.Object members ->
                                    match members |> List.tryFind (fun (n, _) -> n = "matches") with
                                    | Some(_, value) ->
                                        match Codec.decode (Codec.list CoreBindings.Codecs.fileRange) value with
                                        | Ok ranges -> ranges
                                        | Error issues -> failtest (string issues)
                                    | None -> failtest $"no matches: {Json.serialize result}"
                                | _ -> failtest "not an object"

                            Expect.equal
                                (ranges |> List.map (fun r -> r.Path, r.Start, r.Text) |> List.sort)
                                [ "b.ts", 1, "retry(1)\r\n"; "src/a.ts", 2, "retry(3)\n" ]
                                "both lines"

                            let piped = shellRun env settings "grep -rn retry . | sort" "{}"

                            Expect.isFalse
                                (match piped with
                                 | Json.Object m -> m |> List.exists (fun (n, _) -> n = "matches")
                                 | _ -> true)
                                "a pipe: none")
                ]

            testCase "the declarations name every binding with its schemas"
            <| fun () ->
                let paths = CoreBindings.all<Env> settings |> List.map (fun b -> b.Decl.Path)

                Expect.equal
                    paths
                    [
                        "shell.run"
                        "fs.read"
                        "fs.write"
                        "fs.list"
                        "fs.stat"
                        "fs.patch"
                        "fs.edit"
                        "fs.editRange"
                    ]
                    "paths"
        ]
