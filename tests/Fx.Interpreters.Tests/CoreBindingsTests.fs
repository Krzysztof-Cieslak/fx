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
                        let f = fakes (fun _ -> Some(Error(NotStarted("sh", "missing"))))

                        Expect.equal (failedCode (call f "shell.run" [ str "x" ])) "notStarted" "code"

                    testCase "a command past its timeout is killed and reported"
                    <| fun () ->
                        let f = fakes (fun _ -> None)

                        let hanging: Shell =
                            { f.Env.Shell with
                                Run =
                                    fun _ cancellation ->
                                        task {
                                            do! Task.Delay(Timeout.Infinite, cancellation)
                                            return Error(NotStarted("never", "never"))
                                        }
                            }

                        let env =
                            { f.Env with
                                Shell = hanging
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
                                        Args = [ str "sleep 100"; inline' """{"timeoutMs": 20}""" ]
                                    })
                            |> json

                        Expect.equal (field "timedOut" result) (Json.Bool true) "timed out"

                        Expect.isFalse
                            (match result with
                             | Json.Object m -> m |> List.exists (fun (n, _) -> n = "exitCode")
                             | _ -> true)
                            "no exit code"

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

            testCase "the declarations name every binding with its schemas"
            <| fun () ->
                let paths = CoreBindings.all<Env> settings |> List.map (fun b -> b.Decl.Path)

                Expect.equal paths [ "shell.run"; "fs.read"; "fs.write"; "fs.list"; "fs.stat"; "fs.patch" ] "paths"
        ]
