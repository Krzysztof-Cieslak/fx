/// The private-files conformance suite: what every `PrivateFiles` interpreter must do. Also compiled
/// into Fx.Keyring.Tests, where `LocalPrivateFiles` runs it on every OS.
module Fx.Effects.Tests.Conformance.PrivateFilesConformance

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects

let private sync (task: Task<'T>) =
    task.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

let private none = CancellationToken.None

let private ok (label: string) (result: Result<'T, FsError>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failtest $"{label}: {e}"

/// Tests that the interpreters made by `create` conform. `create` is called once per test and must
/// give private files with nothing in them.
let tests (name: string) (create: unit -> PrivateFiles) : Test =
    testList
        $"PrivateFiles conformance ({name})"
        [
            testCase "a missing file reads as None and deleting it says there was none" (fun () ->
                let files = create ()
                Expect.isNone (sync (files.Read "absent.json" none) |> ok "read") "read"
                Expect.isFalse (sync (files.Delete "absent.json" none) |> ok "delete") "delete")

            testCase "a written file reads back, and a second write replaces it" (fun () ->
                let files = create ()
                sync (files.Write "a.json" "{\"first\":1}"B none) |> ok "write"
                Expect.equal (sync (files.Read "a.json" none) |> ok "read") (Some "{\"first\":1}"B) "first"
                sync (files.Write "a.json" "second"B none) |> ok "rewrite"
                Expect.equal (sync (files.Read "a.json" none) |> ok "read") (Some "second"B) "replaced")

            testCase "every byte value and empty content round-trip" (fun () ->
                let files = create ()
                let all = Array.init 256 byte
                sync (files.Write "bytes" all none) |> ok "write"
                Expect.equal (sync (files.Read "bytes" none) |> ok "read") (Some all) "all bytes"
                sync (files.Write "empty" [||] none) |> ok "write empty"
                Expect.equal (sync (files.Read "empty" none) |> ok "read empty") (Some [||]) "empty")

            testCase "files are independent, and delete removes one" (fun () ->
                let files = create ()
                sync (files.Write "one" "1"B none) |> ok "write one"
                sync (files.Write "two" "2"B none) |> ok "write two"
                Expect.isTrue (sync (files.Delete "one" none) |> ok "delete") "existed"
                Expect.isNone (sync (files.Read "one" none) |> ok "read one") "gone"
                Expect.equal (sync (files.Read "two" none) |> ok "read two") (Some "2"B) "kept")

            testCase "names that could leave the directory, or are not plain names, are refused" (fun () ->
                let files = create ()

                for bad in
                    [
                        ""
                        "."
                        ".."
                        "../x"
                        "a/b"
                        "a\\b"
                        ".hidden"
                        "-x"
                        "a b"
                        String('a', 65)
                        "x:y"
                    ] do
                    match sync (files.Write bad "x"B none) with
                    | Error(InvalidPath _) -> ()
                    | other -> failtest $"write {bad}: expected InvalidPath, got {other}"

                    match sync (files.Read bad none) with
                    | Error(InvalidPath _) -> ()
                    | other -> failtest $"read {bad}: expected InvalidPath, got {other}")

            testCase "a cancelled call does nothing" (fun () ->
                let files = create ()
                use cancelled = new CancellationTokenSource()
                cancelled.Cancel()

                let write =
                    try
                        sync (files.Write "c" "x"B cancelled.Token) |> ignore
                        false
                    with
                    | :? OperationCanceledException -> true
                    | :? AggregateException as e when (e.InnerException :? OperationCanceledException) -> true

                Expect.isTrue write "cancelled"
                Expect.isNone (sync (files.Read "c" none) |> ok "read") "nothing written")

            testCase "the protection is named" (fun () ->
                let files = create ()
                Expect.isFalse (String.IsNullOrWhiteSpace files.Protection) "protection")
        ]
