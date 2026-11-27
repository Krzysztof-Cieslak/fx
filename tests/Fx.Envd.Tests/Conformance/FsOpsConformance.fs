/// The conformance suite of envd's filesystem operations: what every `FsOps` interpreter must do. The
/// generic interpreter over `InMemoryFs` passes it here; `LocalFsOps` and the envd proxy (in process
/// and over a child's stdio) pass it in Fx.Envd.Live.Tests, which compiles the same file.
module Fx.Envd.Tests.Conformance.FsOpsConformance

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Envd
open Fx.Store

/// An interpreter under test, with an existing empty directory to work in.
type Fixture =
    {
        Read: FsRead
        Fs: Fs
        Ops: FsOps
        Root: string
        /// Whether the interpreter supports `Watch`.
        Watches: bool
        Release: unit -> unit
    }

    override this.ToString() = $"Fixture({this.Root})"

let private none = CancellationToken.None

let private wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 60.0).GetAwaiter().GetResult()

let private ok (result: Result<'T, 'E>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failtest $"expected success, got {e}"

let private utf8 (text: string) = UTF8Encoding(false).GetBytes text

let private text (bytes: byte array) = UTF8Encoding(false).GetString bytes

/// Tests that the interpreter made by `create` conforms; `create` is called once per test.
let tests (name: string) (create: unit -> Fixture) : Test =
    let test name (body: Fixture -> (string list -> string) -> unit) =
        testCase name (fun () ->
            let fixture = create ()

            try
                body fixture (fun segments -> Path.Combine(Array.ofList (fixture.Root :: segments)))
            finally
                fixture.Release())

    let write (f: Fixture) (path: string) (content: string) =
        ok (wait (f.Fs.WriteFile path (utf8 content) none))

    let read (f: Fixture) (path: string) =
        text (ok (wait (f.Read.ReadFile path none)))

    let patch (f: Fixture) (patches: FilePatch list) = wait (f.Ops.ApplyPatch patches none)

    testList
        $"FsOps conformance ({name})"
        [
            test "stats many paths at once, in order" (fun f at ->
                write f (at [ "a.txt" ]) "abc"
                ok (wait (f.Fs.CreateDirectory (at [ "d" ]) none))

                match wait (f.Ops.StatMany [ at [ "a.txt" ]; at [ "missing" ]; at [ "d" ] ] none) with
                | [ Ok file; Error(NotFound missing); Ok dir ] ->
                    Expect.equal (file.Kind, file.Size, file.Name) (EntryKind.File, 3L, "a.txt") "file"
                    Expect.equal missing (at [ "missing" ]) "missing"
                    Expect.equal dir.Kind EntryKind.Directory "directory"
                | other -> failtest $"{other}"

                Expect.equal (wait (f.Ops.StatMany [] none)) [] "no paths")

            test "reads ranges and reports the file's size" (fun f at ->
                let file = at [ "r.bin" ]
                write f file "0123456789"

                let range offset count =
                    ok (wait (f.Ops.ReadRange file offset count none))

                let chunk = range 2L 3
                Expect.equal (text chunk.Content, chunk.Offset, chunk.FileSize) ("234", 2L, 10L) "middle"
                Expect.isFalse chunk.AtEnd "not at the end"
                let tail = range 8L 100
                Expect.equal (text tail.Content) "89" "cut at the end"
                Expect.isTrue tail.AtEnd "at the end"
                Expect.equal (range 10L 5).Content [||] "at the end"
                Expect.equal (range 50L 5).Content [||] "past the end"
                Expect.equal (text (range 0L 4).Content) "0123" "a size limit"
                Expect.equal (range 0L 0).Content [||] "nothing"

                Expect.equal
                    (wait (f.Ops.ReadRange (at [ "missing" ]) 0L 1 none))
                    (Error(NotFound(at [ "missing" ])))
                    "missing"

                Expect.equal (wait (f.Ops.ReadRange f.Root 0L 1 none)) (Error(IsADirectory f.Root)) "directory"

                Expect.throwsT<ArgumentException>
                    (fun () -> wait (f.Ops.ReadRange file -1L 1 none) |> ignore)
                    "negative offset")

            test "writes atomically: creates and replaces files" (fun f at ->
                let file = at [ "w.txt" ]
                ok (wait (f.Ops.WriteAtomic file (utf8 "first") none))
                Expect.equal (read f file) "first" "created"
                ok (wait (f.Ops.WriteAtomic file (utf8 "second, longer") none))
                Expect.equal (read f file) "second, longer" "replaced"
                ok (wait (f.Ops.WriteAtomic file [||] none))
                Expect.equal (read f file) "" "emptied"

                Expect.equal
                    (ok (wait (f.Read.List f.Root none)) |> List.map (fun e -> e.Name))
                    [ "w.txt" ]
                    "no temporary files left"

                Expect.equal
                    (wait (f.Ops.WriteAtomic (at [ "no"; "x" ]) [||] none))
                    (Error(NotFound(at [ "no"; "x" ])))
                    "missing parent"

                Expect.equal (wait (f.Ops.WriteAtomic f.Root [||] none)) (Error(IsADirectory f.Root)) "directory"

                match wait (f.Ops.WriteAtomic (Path.Combine("rel", "x")) [||] none) with
                | Error(InvalidPath _) -> ()
                | other -> failtest $"relative: {other}")

            test "edits files by lines, keeping their line endings" (fun f at ->
                let file = at [ "e.txt" ]
                write f file "one\r\ntwo\r\nthree\r\n"
                let digest = FsOpsOver.digest (utf8 "one\r\ntwo\r\nthree\r\n")

                let result =
                    ok (
                        patch
                            f
                            [
                                Edit(
                                    file,
                                    Some digest,
                                    [
                                        {
                                            Line = 2
                                            Count = 1
                                            Lines = [ "TWO"; "2" ]
                                        }
                                        {
                                            Line = 4
                                            Count = 0
                                            Lines = [ "four" ]
                                        }
                                    ]
                                )
                            ]
                    )

                let expected = "one\r\nTWO\r\n2\r\nthree\r\nfour\r\n"
                Expect.equal (read f file) expected "edited"

                Expect.equal
                    result
                    [
                        {
                            Path = file
                            Digest = Some(FsOpsOver.digest (utf8 expected))
                            Lines = 5
                        }
                    ]
                    "result")

            test "a stale digest changes nothing" (fun f at ->
                let first = at [ "1.txt" ]
                let second = at [ "2.txt" ]
                write f first "a\n"
                write f second "b\n"
                let wrong = FsOpsOver.digest (utf8 "something else")

                match
                    patch
                        f
                        [
                            Edit(first, None, [ { Line = 1; Count = 1; Lines = [ "A" ] } ])
                            Edit(second, Some wrong, [ { Line = 1; Count = 1; Lines = [ "B" ] } ])
                        ]
                with
                | Error(Stale(path, expected, actual)) ->
                    Expect.equal (path, expected, actual) (second, wrong, FsOpsOver.digest (utf8 "b\n")) "stale"
                | other -> failtest $"{other}"

                Expect.equal (read f first) "a\n" "the first file is unchanged"
                Expect.equal (read f second) "b\n" "the second file is unchanged")

            test "creates and deletes files" (fun f at ->
                let created = at [ "new.txt" ]
                let doomed = at [ "old.txt" ]
                write f doomed "bye"

                let result =
                    ok (
                        patch
                            f
                            [
                                Create(created, "hello\nworld")
                                Delete(doomed, Some(FsOpsOver.digest (utf8 "bye")))
                            ]
                    )

                Expect.equal (read f created) "hello\nworld" "created"
                Expect.equal (wait (f.Read.Stat doomed none)) (Error(NotFound doomed)) "deleted"

                Expect.equal
                    result
                    [
                        {
                            Path = created
                            Digest = Some(FsOpsOver.digest (utf8 "hello\nworld"))
                            Lines = 2
                        }
                        {
                            Path = doomed
                            Digest = None
                            Lines = 0
                        }
                    ]
                    "result"

                match patch f [ Create(created, "again") ] with
                | Error(PatchFsError(AlreadyExists path)) -> Expect.equal path created "exists"
                | other -> failtest $"create over a file: {other}"

                match patch f [ Delete(at [ "missing" ], None) ] with
                | Error(PatchFsError(NotFound _)) -> ()
                | other -> failtest $"delete a missing file: {other}")

            test "edits that do not fit, and files that are not text, are refused" (fun f at ->
                let file = at [ "t.txt" ]
                write f file "1\n2\n3\n"

                let refused edits =
                    match patch f [ Edit(file, None, edits) ] with
                    | Error(InvalidEdit(path, _)) -> path = file
                    | _ -> false

                Expect.isTrue (refused [ { Line = 5; Count = 0; Lines = [ "x" ] } ]) "beyond the end"
                Expect.isTrue (refused [ { Line = 3; Count = 2; Lines = [] } ]) "past the last line"
                Expect.isTrue (refused [ { Line = 0; Count = 1; Lines = [] } ]) "line 0"

                Expect.isTrue
                    (refused [ { Line = 1; Count = 2; Lines = [] }; { Line = 2; Count = 1; Lines = [] } ])
                    "overlapping"

                Expect.equal (read f file) "1\n2\n3\n" "unchanged"
                let binary = at [ "b.bin" ]
                ok (wait (f.Fs.WriteFile binary [| 0xC3uy; 0x28uy |] none))

                Expect.equal
                    (patch f [ Edit(binary, None, [ { Line = 1; Count = 1; Lines = [ "x" ] } ]) ])
                    (Error(NotText binary))
                    "not UTF-8")

            test "searches files by glob and lines by pattern" (fun f at ->
                ok (wait (f.Fs.CreateDirectory (at [ "src"; "deep" ]) none))
                ok (wait (f.Fs.CreateDirectory (at [ ".git" ]) none))
                ok (wait (f.Fs.CreateDirectory (at [ "node_modules" ]) none))
                write f (at [ "src"; "a.fs" ]) "let x = 1\nlet needle = 2\n"
                write f (at [ "src"; "deep"; "b.fs" ]) "NEEDLE here\r\nnot here\r\n"
                write f (at [ "src"; "c.txt" ]) "needle in text\n"
                write f (at [ ".git"; "config" ]) "needle\n"
                write f (at [ "node_modules"; "m.fs" ]) "needle\n"
                ok (wait (f.Fs.WriteFile (at [ "bin.fs" ]) (Array.append (utf8 "needle") [| 0uy |]) none))

                let search query = ok (wait (f.Ops.Search query none))

                let files =
                    search
                        { SearchQuery.files f.Root with
                            Include = [ "*.fs" ]
                        }

                Expect.equal
                    files.Files
                    [ "bin.fs"; "node_modules/m.fs"; "src/a.fs"; "src/deep/b.fs" ]
                    "every .fs file, in walk order, without .git"

                let found =
                    search
                        { SearchQuery.lines f.Root "needle" with
                            Include = [ "**/*.fs" ]
                            Exclude = [ "node_modules" ]
                        }

                Expect.equal
                    found.Matches
                    [
                        {
                            Path = "src/a.fs"
                            Line = 2
                            Column = 5
                            Text = "let needle = 2"
                        }
                    ]
                    "case-sensitive, binary files skipped, excluded directories skipped"

                let ignoringCase =
                    search
                        { SearchQuery.lines f.Root "needle" with
                            Include = [ "src/**" ]
                            IgnoreCase = true
                        }

                Expect.equal
                    (ignoringCase.Matches |> List.map (fun m -> m.Path, m.Line, m.Text))
                    [
                        "src/a.fs", 2, "let needle = 2"
                        "src/c.txt", 1, "needle in text"
                        "src/deep/b.fs", 1, "NEEDLE here"
                    ]
                    "ignoring case, without the carriage return"

                Expect.equal ignoringCase.Files [ "src/a.fs"; "src/c.txt"; "src/deep/b.fs" ] "files with matches"
                Expect.isFalse ignoringCase.Truncated "complete"

                let limited =
                    search
                        { SearchQuery.lines f.Root "needle" with
                            IgnoreCase = true
                            MaxResults = 2
                        }

                Expect.equal limited.Matches.Length 2 "limited"
                Expect.isTrue limited.Truncated "truncated")

            test "paths longer than Windows' old 260-character limit work" (fun f at ->
                let segments = [ for i in 1..6 -> String('d', 45) + string i ]
                let directory = at segments
                ok (wait (f.Fs.CreateDirectory directory none))
                let file = Path.Combine(directory, String('f', 60) + ".txt")
                Expect.isGreaterThan file.Length 300 "a long path"
                ok (wait (f.Ops.WriteAtomic file (utf8 "one\n") none))

                ok (
                    patch
                        f
                        [
                            Edit(
                                file,
                                None,
                                [
                                    {
                                        Line = 2
                                        Count = 0
                                        Lines = [ "two" ]
                                    }
                                ]
                            )
                        ]
                )
                |> ignore

                Expect.equal (text (ok (wait (f.Ops.ReadRange file 0L 100 none))).Content) "one\ntwo\n" "read back"
                let found = ok (wait (f.Ops.Search (SearchQuery.lines f.Root "two") none))
                Expect.equal (found.Matches |> List.map (fun m -> m.Line)) [ 2 ] "found")

            test "an invalid query or a missing root is an error" (fun f at ->
                match wait (f.Ops.Search (SearchQuery.lines f.Root "(unclosed") none) with
                | Error(InvalidQuery _) -> ()
                | other -> failtest $"invalid pattern: {other}"

                match
                    wait (
                        f.Ops.Search
                            { SearchQuery.files f.Root with
                                Include = [ "[abc" ]
                            }
                            none
                    )
                with
                | Error(InvalidQuery _) -> ()
                | other -> failtest $"invalid glob: {other}"

                match wait (f.Ops.Search (SearchQuery.files (at [ "missing" ])) none) with
                | Error(SearchFsError(NotFound _)) -> ()
                | other -> failtest $"missing root: {other}")

            test "watching reports changes in batches until closed" (fun f at ->
                if f.Watches then
                    let watcher =
                        ok (
                            wait (
                                f.Ops.Watch
                                    {
                                        Root = f.Root
                                        Recursive = true
                                        Debounce = TimeSpan.FromMilliseconds 50.0
                                    }
                                    none
                            )
                        )

                    try
                        write f (at [ "watched.txt" ]) "x"

                        let seen = Collections.Generic.List<FileChange>()
                        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)

                        while not (seen |> Seq.exists (fun c -> c.Path.EndsWith "watched.txt")) do
                            match wait (watcher.Next timeout.Token) with
                            | Some batch -> seen.AddRange batch.Changes
                            | None -> failtest "the watcher closed"

                        let change = seen |> Seq.find (fun c -> c.Path.EndsWith "watched.txt")

                        Expect.isTrue
                            (change.Kind = ChangeKind.Added || change.Kind = ChangeKind.Modified)
                            $"added or modified: {change}"

                        // Changes the watcher saw before it was closed are not delivered after it
                        // (nor are the ones the OS reports late: Windows does, even after Dispose).
                        write f (at [ "late.txt" ]) "y"
                    finally
                        wait (watcher.Close())

                    Expect.equal (wait (watcher.Next none)) None "closed"
                else
                    match
                        wait (
                            f.Ops.Watch
                                {
                                    Root = f.Root
                                    Recursive = true
                                    Debounce = TimeSpan.Zero
                                }
                                none
                        )
                    with
                    | Error _ -> ()
                    | Ok watcher ->
                        wait (watcher.Close())
                        failtest "watching is not supported, yet a watcher started")

            test "watching a missing directory is an error" (fun f at ->
                if f.Watches then
                    match
                        wait (
                            f.Ops.Watch
                                {
                                    Root = at [ "missing" ]
                                    Recursive = true
                                    Debounce = TimeSpan.Zero
                                }
                                none
                        )
                    with
                    | Error(NotFound _) -> ()
                    | Error e -> failtest $"{e}"
                    | Ok watcher ->
                        wait (watcher.Close())
                        failtest "watching a missing directory")

            // A long session: envd's proxies move each file over a channel of their connection, and a
            // connection has at most 256 open at a time.
            test "serves more calls in a row than a connection has channels" (fun f at ->
                let file = at [ "many.txt" ]
                let missing = at [ "missing" ]

                for i in 1..300 do
                    ok (wait (f.Ops.WriteAtomic file (utf8 $"line {i}\n") none))

                    let edit =
                        {
                            Line = 2
                            Count = 0
                            Lines = [ "next" ]
                        }

                    ok (patch f [ Edit(file, None, [ edit ]) ]) |> ignore
                    let chunk = ok (wait (f.Ops.ReadRange file 0L 100 none))
                    Expect.equal (text chunk.Content) $"line {i}\nnext\n" $"read {i}"
                    Expect.equal (wait (f.Ops.ReadRange missing 0L 1 none)) (Error(NotFound missing)) $"missing {i}"

                    Expect.equal
                        (wait (f.Ops.WriteAtomic (at [ "missing"; "x" ]) [||] none))
                        (Error(NotFound(at [ "missing"; "x" ])))
                        $"no parent {i}")
        ]
