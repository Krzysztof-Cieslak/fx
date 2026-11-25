module Fx.Components.Tests.ArchiveTests

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Components
open Fx.Components.Tests.Support
open Fx.Sdk

/// What an extraction wrote, in order: `d path`, `f path (x)`, `l path -> target`, `c path <- source`.
let private extract
    (format: ArchiveFormat)
    (strip: int)
    (limits: ExtractLimits)
    (executables: string list)
    (bytes: byte array)
    =
    let written = ResizeArray<string>()
    let join (path: string list) = String.Join("/", path)

    let sink: ExtractSink =
        {
            CreateDirectory =
                fun path ->
                    written.Add $"d {join path}"
                    Task.FromResult(Ok())
            WriteFile =
                fun path executable content ->
                    use reader = new StreamReader(content)
                    let text = reader.ReadToEnd()
                    let mode = if executable then " (x)" else ""
                    written.Add $"f {join path}{mode}: {text}"
                    Task.FromResult(Ok())
            CreateSymlink =
                fun path target ->
                    written.Add $"l {join path} -> {target}"
                    Task.FromResult(Ok())
            CopyFile =
                fun path source executable ->
                    let mode = if executable then " (x)" else ""
                    written.Add $"c {join path}{mode} <- {join source}"
                    Task.FromResult(Ok())
        }

    use stream = new MemoryStream(bytes)

    let options =
        {
            Format = format
            Strip = strip
            Limits = limits
            Executables = executables
        }

    let result =
        (Archive.extract options stream sink CancellationToken.None).GetAwaiter().GetResult()

    result, List.ofSeq written

let private ok (result: Result<ExtractReport, ArchiveError>, written: string list) =
    match result with
    | Ok report -> report, written
    | Error e -> failtest $"expected success, got {e}"

let private unsafe (result: Result<ExtractReport, ArchiveError>, _) =
    match result with
    | Error(UnsafeEntry(_, reason)) -> reason
    | other -> failtest $"expected an unsafe entry, got {other}"

let private tooLarge (result: Result<ExtractReport, ArchiveError>, _) =
    match result with
    | Error(TooLarge reason) -> reason
    | other -> failtest $"expected a limit, got {other}"

let private formats =
    [ ArchiveFormat.TarGz, tarGz; ArchiveFormat.Tar, tar; ArchiveFormat.Zip, zip ]

let private defaults = ExtractLimits.defaults

[<Tests>]
let tests =
    testList
        "Archive"
        [
            test "unpacks files and directories, strips leading segments, and keeps executable bits" {
                for format, pack in formats do
                    let archive =
                        pack
                            [
                                Dir "top"
                                Dir "top/bin"
                                File("top/bin/tool", "run", true)
                                File("top/README", "read", false)
                                File("top/lib/deep/x.txt", "x", false)
                                File("ignored-at-top-level", "?", false)
                            ]

                    let report, written = ok (extract format 1 defaults [] archive)

                    Expect.equal
                        written
                        [
                            "d bin"
                            "f bin/tool (x): run"
                            "f README: read"
                            "d lib"
                            "d lib/deep"
                            "f lib/deep/x.txt: x"
                        ]
                        $"{format}: stripped, parents created, top-level entry skipped"

                    Expect.equal (report.Files, report.Directories, report.Bytes) (3, 3, 8L) $"{format}: report"
            }

            test "the layout's executables are executable whatever the archive says" {
                for format, pack in formats do
                    let archive = pack [ File("tool", "run", false); File("data", "d", false) ]
                    let _, written = ok (extract format 0 defaults [ "tool" ] archive)
                    Expect.equal written [ "f tool (x): run"; "f data: d" ] $"{format}"
            }

            test "symbolic links inside the destination are created last; hard links become copies" {
                let archive =
                    tar
                        [
                            File("lib/real", "content", true)
                            Symlink("bin/tool", "../lib/real")
                            Hardlink("lib/copy", "lib/real")
                        ]

                let report, written = ok (extract ArchiveFormat.Tar 0 defaults [] archive)

                Expect.equal
                    written
                    [
                        "d lib"
                        "f lib/real (x): content"
                        "d bin"
                        "c lib/copy (x) <- lib/real"
                        "l bin/tool -> ../lib/real"
                    ]
                    "links after files"

                Expect.equal report.Links 1 "one link"

                let _, zipped =
                    ok (
                        extract
                            ArchiveFormat.Zip
                            0
                            defaults
                            []
                            (zip [ File("real", "x", false); Symlink("link", "real") ])
                    )

                Expect.equal zipped [ "f real: x"; "l link -> real" ] "zip links"
            }

            test "entries that climb out, are absolute, or are special are refused" {
                let cases =
                    [
                        [ File("../evil", "x", false) ], "climbs out"
                        [ File("a/../../evil", "x", false) ], "climbs out"
                        [ File("/etc/evil", "x", false) ], "absolute"
                        [ File("C:/evil", "x", false) ], "absolute"
                        [ File("a\\..\\..\\evil", "x", false) ], "climbs out"
                        [ Device "dev/null" ], "character device"
                        [ Symlink("link", "/etc/passwd") ], "absolute"
                        [ Symlink("link", "../../etc/passwd") ], "outside the destination"
                        [ Symlink("a/link", "../../x") ], "outside the destination"
                        [ Symlink("link", "dir"); File("link/evil", "x", false) ], "not a directory"
                        [ Dir "dir"; Symlink("x", "dir"); Symlink("y", "x/../..") ], "outside the destination"
                        [ Dir "dir"; Symlink("x", "dir"); Symlink("y", "x/sub") ], "passes through another link"
                        [ Hardlink("copy", "../../etc/passwd") ], "climbs out"
                        [ Hardlink("copy", "missing") ], "not a file written before"
                        [ File("nul", "x", false) ], "device name on Windows"
                        [ File("Com1.txt", "x", false) ], "device name on Windows"
                        [ File("ab:c", "x", false) ], "Windows forbids"
                        [ File("trailing.", "x", false) ], "ends with a dot"
                        [ File("Tool", "x", false); File("tool", "y", false) ], "only in case"
                        [ File("same", "x", false); File("same", "y", false) ], "duplicate"
                    ]

                for entries, reason in cases do
                    for format, pack in [ ArchiveFormat.Tar, tar; ArchiveFormat.TarGz, tarGz ] do
                        Expect.stringContains
                            (unsafe (extract format 0 defaults [] (pack entries)))
                            reason
                            $"{format}: {entries}"

                // The same through zip, which has no devices or hard links.
                Expect.stringContains
                    (unsafe (extract ArchiveFormat.Zip 0 defaults [] (zip [ File("../evil", "x", false) ])))
                    "climbs out"
                    "zip slip"

                Expect.stringContains
                    (unsafe (extract ArchiveFormat.Zip 0 defaults [] (zip [ Symlink("l", "../x") ])))
                    "outside"
                    "zip link"

                Expect.stringContains
                    (unsafe (extract ArchiveFormat.Zip 0 defaults [] (zip [ Device "d" ])))
                    "special"
                    "zip device"
            }

            test "limits count what is read, not what headers claim" {
                let big = String('0', 10_000)
                let archive = tarGz [ File("big", big, false) ]

                Expect.stringContains
                    (tooLarge (extract ArchiveFormat.TarGz 0 { defaults with MaxFileBytes = 1000L } [] archive))
                    "limit"
                    "one file"

                Expect.stringContains
                    (tooLarge (
                        extract
                            ArchiveFormat.TarGz
                            0
                            { defaults with
                                MaxTotalBytes = 15_000L
                            }
                            []
                            (tarGz [ File("a", big, false); File("b", big, false) ])
                    ))
                    "limit"
                    "in all"

                Expect.stringContains
                    (tooLarge (
                        extract
                            ArchiveFormat.Tar
                            0
                            { defaults with MaxEntries = 2 }
                            []
                            (tar [ Dir "a"; Dir "b"; Dir "c" ])
                    ))
                    "entries"
                    "entries"

                Expect.stringContains
                    (tooLarge (
                        extract
                            ArchiveFormat.Tar
                            0
                            { defaults with MaxPathLength = 10 }
                            []
                            (tar [ File("a/b/c/d/e/f/g", "x", false) ])
                    ))
                    "longer"
                    "path length"

                Expect.stringContains
                    (tooLarge (
                        extract
                            ArchiveFormat.Zip
                            0
                            { defaults with MaxDepth = 3 }
                            []
                            (zip [ File("a/b/c/d", "x", false) ])
                    ))
                    "deeper"
                    "depth"

                // Zip too.
                Expect.stringContains
                    (tooLarge (
                        extract
                            ArchiveFormat.Zip
                            0
                            { defaults with MaxFileBytes = 100L }
                            []
                            (zip [ File("big", big, false) ])
                    ))
                    "limit"
                    "zip"
            }

            test "corrupt archives are errors, not exceptions" {
                let cases =
                    [
                        ArchiveFormat.Zip, Text.Encoding.UTF8.GetBytes "not a zip"
                        ArchiveFormat.TarGz, Text.Encoding.UTF8.GetBytes "not gzip at all"
                        ArchiveFormat.TarGz, (tarGz [ File("a", "x", false) ])[0..20]
                        ArchiveFormat.Zip, tarGz [ File("a", "x", false) ]
                    ]

                for format, bytes in cases do
                    match extract format 0 defaults [] bytes with
                    | Error(Corrupt reason), _ -> Expect.stringContains reason (string format) $"{format}"
                    | other -> failtest $"{format}: expected corrupt, got {fst other}"
            }

            test "entry names are checked segment by segment" {
                Expect.equal (ArchivePath.entry 1 "./top/bin/tool") (Ok(Some [ "bin"; "tool" ])) "dot segments dropped"
                Expect.equal (ArchivePath.entry 1 "top/") (Ok None) "nothing left: skipped"
                Expect.equal (ArchivePath.entry 0 "a\\b") (Ok(Some [ "a"; "b" ])) "backslash separates"

                Expect.equal
                    (ArchivePath.linkTarget [ "bin"; "tool" ] "../lib/real")
                    (Ok [ "lib"; "real" ])
                    "relative target"

                Expect.isError (ArchivePath.linkTarget [ "tool" ] "../x") "escapes"
            }
        ]
