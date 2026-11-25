/// The pure parts of envd: globs, line edits, change coalescing, terminal defaults and output records.
module Fx.Envd.Tests.PureTests

open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Envd
open Fx.Pty
open Fx.Store

let private edit line count lines : TextEdit =
    {
        Line = line
        Count = count
        Lines = lines
    }

let private applied (text: string) (edits: TextEdit list) =
    match TextPatch.applyText text edits with
    | Ok(result, _) -> result
    | Error reason -> failtest reason

[<Tests>]
let globs =
    testList
        "Glob"
        [
            test "matches like git and ripgrep" {
                let cases =
                    [
                        "*.fs", "a.fs", true
                        "*.fs", "src/deep/a.fs", true
                        "*.fs", "a.fsx", false
                        "src/*.fs", "src/a.fs", true
                        "src/*.fs", "src/deep/a.fs", false
                        "/a.fs", "a.fs", true
                        "/a.fs", "src/a.fs", false
                        "src/**/*.fs", "src/a.fs", true
                        "src/**/*.fs", "src/x/y/a.fs", true
                        "**/test", "test", true
                        "**/test", "a/b/test", true
                        "dir/**", "dir/a/b", true
                        "dir/**", "dir", false
                        "?.txt", "a.txt", true
                        "?.txt", "ab.txt", false
                        "[ab].txt", "b.txt", true
                        "[!ab].txt", "c.txt", true
                        "[!ab].txt", "a.txt", false
                        "[a-c]x", "bx", true
                        "*.{fs,fsx}", "a.fsx", true
                        "*.{fs,fsx}", "a.cs", false
                        "a\\*b", "a*b", true
                        "a\\*b", "axb", false
                        "node_modules", "x/node_modules", true
                        "a.b", "aXb", false
                    ]

                for glob, path, expected in cases do
                    Expect.equal (Glob.isMatch false glob path) expected $"{glob} on {path}"
            }

            test "case-insensitive matching, and invalid globs" {
                Expect.isTrue (Glob.isMatch true "*.FS" "a.fs") "ignoring case"
                Expect.isFalse (Glob.isMatch false "*.FS" "a.fs") "case-sensitive"

                for glob in [ "[abc"; "{a,b"; "{a,{b}}"; "x\\"; "" ] do
                    Expect.isError (Glob.toRegex glob) glob
            }
        ]

[<Tests>]
let textPatch =
    testList
        "TextPatch"
        [
            test "replaces, inserts and deletes lines against the original numbering" {
                let text = "a\nb\nc\nd\n"
                Expect.equal (applied text [ edit 2 1 [ "B" ] ]) "a\nB\nc\nd\n" "replace"
                Expect.equal (applied text [ edit 1 0 [ "zero" ] ]) "zero\na\nb\nc\nd\n" "insert first"
                Expect.equal (applied text [ edit 5 0 [ "e" ] ]) "a\nb\nc\nd\ne\n" "append"
                Expect.equal (applied text [ edit 2 2 [] ]) "a\nd\n" "delete"

                Expect.equal
                    (applied text [ edit 4 1 [ "D" ]; edit 1 1 [ "A1"; "A2" ] ])
                    "A1\nA2\nb\nc\nD\n"
                    "several, in any order"

                Expect.equal
                    (applied text [ edit 2 0 [ "x" ]; edit 2 0 [ "y" ] ])
                    "a\nx\ny\nb\nc\nd\n"
                    "insertions in order"
            }

            test "keeps line endings, a missing final newline and empty files" {
                Expect.equal (applied "a\r\nb\r\n" [ edit 2 0 [ "x" ] ]) "a\r\nx\r\nb\r\n" "CRLF"
                Expect.equal (applied "a\r\nb\nc" [ edit 3 1 [ "C" ] ]) "a\r\nb\nC" "mixed, no final newline"
                Expect.equal (applied "a\nb" [ edit 3 0 [ "c" ] ]) "a\nb\nc" "append without a final newline"
                Expect.equal (applied "a\nb" [ edit 2 1 [] ]) "a" "delete the last line"
                Expect.equal (applied "" [ edit 1 0 [ "x" ] ]) "x\n" "an empty file"
                Expect.equal (applied "a\rb\n" [ edit 1 1 [ "c" ] ]) "c\n" "a lone CR is not a line ending"
                Expect.equal (TextPatch.lineCount "a\nb") 2 "line count"
                Expect.equal (TextPatch.lineCount "") 0 "empty"
            }

            test "keeps a byte order mark and refuses invalid UTF-8" {
                let bom = [| 0xEFuy; 0xBBuy; 0xBFuy |]

                match TextPatch.apply (Array.append bom "a\n"B) [ edit 1 1 [ "b" ] ] with
                | Ok(bytes, 1) -> Expect.equal bytes (Array.append bom "b\n"B) "bom kept"
                | other -> failtest $"{other}"

                match TextPatch.apply [| 0xFFuy |] [] with
                | Error(Choice1Of2()) -> ()
                | other -> failtest $"{other}"
            }

            test "refuses edits out of range or overlapping" {
                for edits in
                    [
                        [ edit 0 1 [] ]
                        [ edit 1 (-1) [] ]
                        [ edit 4 0 [] ]
                        [ edit 2 2 [] ]
                        [ edit 1 2 []; edit 2 0 [ "x" ] ]
                    ] do
                    Expect.isError (TextPatch.applyText "a\nb\n" edits) $"{edits}"
            }
        ]

[<Tests>]
let changes =
    testList
        "Changes"
        [
            test "coalesce per path, in first-change order" {
                let c path kind : FileChange = { Path = path; Kind = kind }

                Expect.equal
                    (Changes.coalesce
                        [
                            c "a" ChangeKind.Added
                            c "b" ChangeKind.Modified
                            c "a" ChangeKind.Modified
                            c "c" ChangeKind.Added
                            c "c" ChangeKind.Deleted
                            c "d" ChangeKind.Deleted
                            c "d" ChangeKind.Added
                            c "b" ChangeKind.Deleted
                        ])
                    [ c "a" ChangeKind.Added; c "b" ChangeKind.Deleted; c "d" ChangeKind.Modified ]
                    "coalesced"
            }

            test "a change queue debounces, coalesces and reports overflows" {
                let clock = Fx.Effects.Fakes.FakeClock(Instant.unixEpoch)
                let queue = ChangeQueue 2
                queue.Add { Path = "a"; Kind = ChangeKind.Added }

                queue.Add
                    {
                        Path = "a"
                        Kind = ChangeKind.Modified
                    }

                queue.Add { Path = "b"; Kind = ChangeKind.Added }
                // Pending changes: the debounce delay starts at once, on this thread.
                let next =
                    queue.Next(clock.Clock, System.TimeSpan.FromSeconds 1.0, System.Threading.CancellationToken.None)

                Expect.isFalse next.IsCompleted "debouncing"
                clock.Advance(System.TimeSpan.FromSeconds 1.0)

                Expect.equal
                    (next.GetAwaiter().GetResult())
                    (Some
                        {
                            Changes = [ { Path = "a"; Kind = ChangeKind.Added } ]
                            Overflow = true
                        })
                    "the third change overflowed the queue"

                queue.Complete()

                Expect.equal
                    (queue
                        .Next(clock.Clock, System.TimeSpan.Zero, System.Threading.CancellationToken.None)
                        .GetAwaiter()
                        .GetResult())
                    None
                    "completed"
            }
        ]

[<Tests>]
let terminalSetup =
    testList
        "TerminalSetup"
        [
            test "adds TERM and a locale unless the spec has them" {
                let defaults =
                    {
                        Term = "xterm-256color"
                        Locale = Some("LC_CTYPE", "C.UTF-8")
                        PowerShellUtf8 = false
                    }

                let spec = PtySpec.script "echo"

                Expect.equal
                    (TerminalSetup.apply defaults spec).Env
                    (Inherit [ "TERM", Some "xterm-256color"; "LC_CTYPE", Some "C.UTF-8" ])
                    "inherit"

                Expect.equal
                    (TerminalSetup.apply
                        defaults
                        { spec with
                            Env = Inherit [ "TERM", Some "dumb"; "LANG", Some "x" ]
                        })
                        .Env
                    (Inherit [ "TERM", Some "dumb"; "LANG", Some "x" ])
                    "kept"

                Expect.equal
                    (TerminalSetup.apply
                        defaults
                        { spec with
                            Env = Exactly [ "PATH", "/bin" ]
                        })
                        .Env
                    (Exactly [ "TERM", "xterm-256color"; "LC_CTYPE", "C.UTF-8"; "PATH", "/bin" ])
                    "exactly"

                Expect.equal (TerminalSetup.apply defaults spec).Program (Script "echo") "program unchanged"
            }

            test "PowerShell scripts get UTF-8 console encodings" {
                let defaults =
                    {
                        Term = "xterm-256color"
                        Locale = None
                        PowerShellUtf8 = true
                    }

                Expect.equal
                    (TerminalSetup.apply defaults (PtySpec.script "Write-Host hi")).Program
                    (Script(TerminalSetup.powerShellUtf8Prefix + "Write-Host hi"))
                    "script"

                Expect.equal
                    (TerminalSetup.apply defaults (PtySpec.exec "pwsh" [])).Program
                    (Exec("pwsh", []))
                    "exec unchanged"
            }
        ]

[<Tests>]
let records =
    testList
        "OutputRecords"
        [
            test "round-trip, and decode piece by piece" {
                let all =
                    [
                        OutputRecord.Data(OutputStream.Out, 0L, "hello"B)
                        OutputRecord.Data(OutputStream.Err, 1L <<< 40, [||])
                        OutputRecord.Gap(OutputStream.Err, 5L, 1000L)
                    ]

                let bytes = all |> List.map OutputRecords.encode |> Array.concat
                let decoded = ResizeArray()
                let mutable start = 0

                while start < bytes.Length do
                    match OutputRecords.decode bytes start (bytes.Length - start) with
                    | OutputRecords.Decoded.Record(record, consumed) ->
                        decoded.Add record
                        start <- start + consumed
                    | other -> failtest $"{other}"

                Expect.equal (List.ofSeq decoded) all "round trip"

                for cut in 0 .. (OutputRecords.encode all.Head).Length - 1 do
                    Expect.equal (OutputRecords.decode bytes 0 cut) OutputRecords.Decoded.Incomplete $"cut at {cut}"

                match OutputRecords.decode [| 9uy; 0uy |] 0 2 with
                | OutputRecords.Decoded.Invalid _ -> ()
                | other -> failtest $"{other}"
            }
        ]

let private roundTrip (codec: Codec<'T>) (values: 'T list) =
    for value in values do
        let json = Codec.encode codec value
        Expect.equal (Codec.decode codec json) (Ok value) $"{Json.serialize json}"

[<Tests>]
let codecs =
    testList
        "EnvCodecs"
        [
            test "errors are tagged unions that round-trip" {
                roundTrip
                    EnvCodecs.fsError
                    [
                        NotFound "/a"
                        AlreadyExists "/a"
                        NotADirectory "/a"
                        IsADirectory "/a"
                        DirectoryNotEmpty "/a"
                        AccessDenied "/a"
                        InvalidPath("a", "not absolute")
                        IoFailed("/a", "disk full")
                    ]

                Expect.equal
                    (Json.serialize (Codec.encode EnvCodecs.fsError (InvalidPath("a", "not absolute"))))
                    """{"kind":"invalidPath","path":"a","reason":"not absolute"}"""
                    "wire format"

                let digest = BlobHash.ofBytes [| 1uy |]

                roundTrip
                    EnvCodecs.patchError
                    [
                        Stale("/a", digest, BlobHash.ofBytes [||])
                        InvalidEdit("/a", "overlap")
                        NotText "/a"
                        PatchFsError(NotFound "/a")
                    ]

                roundTrip EnvCodecs.searchError [ InvalidQuery "bad"; SearchFsError(AccessDenied "/a") ]
                roundTrip EnvCodecs.blobError [ BlobError.NotFound digest; BlobError.Corrupt digest ]
                roundTrip EnvCodecs.snapshotError [ Unavailable "no git"; GitFailed("add", 128, "fatal") ]

                roundTrip
                    EnvProtocol.execError
                    [
                        ExecError.NotStarted("x", "missing")
                        ExecError.InvalidSize { Cols = 0; Rows = 1 }
                        ExecError.UnknownProcess "7"
                    ]
            }

            test "patches and process specs round-trip" {
                roundTrip
                    EnvCodecs.filePatch
                    [
                        Create("/a", "text")
                        Edit("/a", Some(BlobHash.ofBytes [||]), [ { Line = 1; Count = 0; Lines = [ "x" ] } ])
                        Edit("/a", None, [])
                        Delete("/a", None)
                    ]

                roundTrip EnvCodecs.program [ Exec("git", [ "status"; "-s" ]); Exec("ls", []); Script "echo hi" ]

                roundTrip
                    EnvCodecs.envVars
                    [ Inherit [ "A", Some "1"; "B", None ]; Inherit []; Exactly [ "PATH", "/bin" ] ]
            }
        ]
