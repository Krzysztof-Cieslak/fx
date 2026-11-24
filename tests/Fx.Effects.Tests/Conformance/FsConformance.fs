/// The filesystem conformance suite: what every `FsRead`/`Fs` interpreter must do. `InMemoryFs` and
/// `LocalFs` both pass it, which is what makes the fake a faithful stand-in. Also compiled into
/// Fx.Effects.Live.Tests.
module Fx.Effects.Tests.Conformance.FsConformance

open System.IO
open System.Text
open Expecto
open Fx.Effects

/// An interpreter under test, with an existing empty directory to work in.
type Fixture =
    {
        Read: FsRead
        Fs: Fs
        Root: string
        /// Cleans up after a test.
        Release: unit -> unit
    }

    override this.ToString() = $"Fixture({this.Root})"

type private Env =
    {
        Read: FsRead
        Fs: Fs
    }

    interface IFs with
        member env.FsRead = env.Read
        member env.Fs = env.Fs

    interface IScope with
        member _.Scope = Scope.root

    override _.ToString() = "Env"

let private run (fixture: Fixture) (effect: Eff<Env, 'T>) : 'T =
    (Eff.run { Read = fixture.Read; Fs = fixture.Fs } effect).GetAwaiter().GetResult()

let private ok (result: Result<'T, FsError>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failtest $"expected success, got {e}"

let private bytes (text: string) = Encoding.UTF8.GetBytes text

/// Tests that the interpreter made by `create` conforms. `create` is called once per test.
let tests (name: string) (create: unit -> Fixture) : Test =
    let test name (body: Fixture -> (string list -> string) -> unit) =
        testCase name (fun () ->
            let fixture = create ()

            try
                body fixture (fun segments -> Path.Combine(Array.ofList (fixture.Root :: segments)))
            finally
                fixture.Release())

    testList
        $"Fs conformance ({name})"
        [
            test "writes and reads files" (fun fs at ->
                let file = at [ "a.bin" ]
                ok (run fs (Fs.writeFile file [| 0uy; 1uy; 255uy |]))
                Expect.equal (ok (run fs (Fs.readFile file))) [| 0uy; 1uy; 255uy |] "content"
                ok (run fs (Fs.writeFile file [| 7uy |]))
                Expect.equal (ok (run fs (Fs.readFile file))) [| 7uy |] "replaced")

            test "text is UTF-8 without a byte order mark, and a mark is dropped when reading" (fun fs at ->
                let file = at [ "t.txt" ]
                ok (run fs (Fs.writeText file "zażółć\r\nline"))
                Expect.equal (ok (run fs (Fs.readFile file))) (bytes "zażółć\r\nline") "bytes"
                ok (run fs (Fs.writeFile file (Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (bytes "bom"))))
                Expect.equal (ok (run fs (Fs.readText file))) "bom" "text")

            test "reading a missing file or a directory fails" (fun fs at ->
                Expect.equal (run fs (Fs.readFile (at [ "missing" ]))) (Error(NotFound(at [ "missing" ]))) "missing"
                Expect.equal (run fs (Fs.readFile fs.Root)) (Error(IsADirectory fs.Root)) "directory")

            test "relative paths are rejected" (fun fs _ ->
                let relative = Path.Combine("dir", "file.txt")

                let isInvalid =
                    function
                    | Error(InvalidPath(path, _)) -> path = relative
                    | _ -> false

                Expect.isTrue (isInvalid (run fs (Fs.readFile relative))) "read"
                Expect.isTrue (isInvalid (run fs (Fs.stat relative))) "stat"
                Expect.isTrue (isInvalid (run fs (Fs.list relative))) "list"
                Expect.isTrue (isInvalid (run fs (Fs.writeFile relative [||]))) "write"
                Expect.isTrue (isInvalid (run fs (Fs.createDirectory relative))) "create"
                Expect.isTrue (isInvalid (run fs (Fs.delete relative))) "delete"
                Expect.isTrue (isInvalid (run fs (Fs.move relative (Path.Combine(fs.Root, "x"))))) "move")

            test "stat reports kind, name and size" (fun fs at ->
                ok (run fs (Fs.writeFile (at [ "f.txt" ]) (bytes "12345")))
                let file = ok (run fs (Fs.stat (at [ "f.txt" ])))
                Expect.equal (file.Kind, file.Name, file.Size) (EntryKind.File, "f.txt", 5L) "file"
                ok (run fs (Fs.createDirectory (at [ "d" ])))
                let dir = ok (run fs (Fs.stat (at [ "d" ])))
                Expect.equal (dir.Kind, dir.Name, dir.Size) (EntryKind.Directory, "d", 0L) "directory"
                Expect.equal (run fs (Fs.stat (at [ "nope" ]))) (Error(NotFound(at [ "nope" ]))) "missing"
                Expect.equal (ok (run fs (Fs.exists (at [ "f.txt" ])))) true "exists"
                Expect.equal (ok (run fs (Fs.exists (at [ "nope" ])))) false "does not exist")

            test "lists a directory sorted by name" (fun fs at ->
                for file in [ "b.txt"; "a.txt"; "C.txt" ] do
                    ok (run fs (Fs.writeFile (at [ file ]) (bytes file)))

                ok (run fs (Fs.createDirectory (at [ "sub"; "deeper" ])))
                let entries = ok (run fs (Fs.list fs.Root))

                Expect.equal
                    (entries |> List.map (fun e -> e.Name, e.Kind))
                    [
                        "C.txt", EntryKind.File
                        "a.txt", EntryKind.File
                        "b.txt", EntryKind.File
                        "sub", EntryKind.Directory
                    ]
                    "entries"

                Expect.equal
                    (entries |> List.map (fun e -> e.Path))
                    (entries |> List.map (fun e -> at [ e.Name ]))
                    "paths"

                Expect.equal (ok (run fs (Fs.list (at [ "sub"; "deeper" ])))) [] "empty"
                Expect.equal (run fs (Fs.list (at [ "a.txt" ]))) (Error(NotADirectory(at [ "a.txt" ]))) "file"
                Expect.equal (run fs (Fs.list (at [ "nope" ]))) (Error(NotFound(at [ "nope" ]))) "missing")

            test "writing needs an existing parent and cannot replace a directory" (fun fs at ->
                Expect.equal
                    (run fs (Fs.writeFile (at [ "no"; "f.txt" ]) [||]))
                    (Error(NotFound(at [ "no"; "f.txt" ])))
                    "missing parent"

                ok (run fs (Fs.createDirectory (at [ "d" ])))
                Expect.equal (run fs (Fs.writeFile (at [ "d" ]) [||])) (Error(IsADirectory(at [ "d" ]))) "directory")

            test "creates directories with their parents, idempotently" (fun fs at ->
                ok (run fs (Fs.createDirectory (at [ "x"; "y"; "z" ])))
                ok (run fs (Fs.createDirectory (at [ "x"; "y"; "z" ])))
                Expect.equal (ok (run fs (Fs.stat (at [ "x"; "y" ])))).Kind EntryKind.Directory "parent"
                ok (run fs (Fs.writeFile (at [ "f" ]) [||]))

                Expect.equal
                    (run fs (Fs.createDirectory (at [ "f" ])))
                    (Error(AlreadyExists(at [ "f" ])))
                    "over a file"

                match run fs (Fs.createDirectory (at [ "f"; "g" ])) with
                | Error(NotADirectory _) -> ()
                | other -> failtest $"under a file: {other}")

            test "deletes files and empty directories only" (fun fs at ->
                ok (run fs (Fs.createDirectory (at [ "d" ])))
                ok (run fs (Fs.writeFile (at [ "d"; "f" ]) [||]))
                Expect.equal (run fs (Fs.delete (at [ "d" ]))) (Error(DirectoryNotEmpty(at [ "d" ]))) "not empty"
                ok (run fs (Fs.delete (at [ "d"; "f" ])))
                ok (run fs (Fs.delete (at [ "d" ])))
                Expect.equal (ok (run fs (Fs.exists (at [ "d" ])))) false "gone"
                Expect.equal (run fs (Fs.delete (at [ "d" ]))) (Error(NotFound(at [ "d" ]))) "missing")

            test "moves files and directories" (fun fs at ->
                ok (run fs (Fs.createDirectory (at [ "src"; "inner" ])))
                ok (run fs (Fs.writeFile (at [ "src"; "inner"; "f.txt" ]) (bytes "moved")))
                ok (run fs (Fs.move (at [ "src" ]) (at [ "dst" ])))
                Expect.equal (ok (run fs (Fs.readText (at [ "dst"; "inner"; "f.txt" ])))) "moved" "content"
                Expect.equal (ok (run fs (Fs.exists (at [ "src" ])))) false "source gone"
                ok (run fs (Fs.move (at [ "dst"; "inner"; "f.txt" ]) (at [ "g.txt" ])))
                Expect.equal (ok (run fs (Fs.readText (at [ "g.txt" ])))) "moved" "file moved")

            test "moving fails on a missing source, an existing destination or a missing parent" (fun fs at ->
                ok (run fs (Fs.writeFile (at [ "a" ]) [||]))
                ok (run fs (Fs.writeFile (at [ "b" ]) [||]))
                Expect.equal (run fs (Fs.move (at [ "a" ]) (at [ "b" ]))) (Error(AlreadyExists(at [ "b" ]))) "exists"
                Expect.equal (run fs (Fs.move (at [ "zz" ]) (at [ "c" ]))) (Error(NotFound(at [ "zz" ]))) "missing"

                Expect.equal
                    (run fs (Fs.move (at [ "a" ]) (at [ "no"; "c" ])))
                    (Error(NotFound(at [ "no"; "c" ])))
                    "missing parent"

                ok (run fs (Fs.createDirectory (at [ "d" ])))

                match run fs (Fs.move (at [ "d" ]) (at [ "d"; "inside" ])) with
                | Error(InvalidPath _) -> ()
                | other -> failtest $"into itself: {other}")

            test "dot segments are normalized" (fun fs at ->
                ok (run fs (Fs.createDirectory (at [ "a" ])))
                ok (run fs (Fs.writeText (at [ "a"; ".."; "b.txt" ]) "up"))
                Expect.equal (ok (run fs (Fs.readText (at [ "b.txt" ])))) "up" "normalized"
                Expect.equal (ok (run fs (Fs.readText (at [ "a"; "."; ".."; "b.txt" ])))) "up" "read normalized")
        ]
