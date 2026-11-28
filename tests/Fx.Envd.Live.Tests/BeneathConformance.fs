/// The paths-beneath-roots suite (T113b): what the file capabilities do with symbolic links under a
/// root they resolve beneath (`EnvdServices.Beneath`, `EnvClient.beneath`). Live only: the fakes have
/// no links. Run against `LocalBeneath` directly and through envd.
module Fx.Envd.Live.Tests.BeneathConformance

open System
open System.IO
open System.Text
open System.Threading
open Expecto
open Fx.Effects
open Fx.Envd

/// Files that resolve paths beneath `Root`, and a directory outside it with a secret.
type Fixture =
    {
        Files: EnvdFiles
        Root: string
        Outside: string
        Release: unit -> unit
    }

    override this.ToString() = $"Fixture({this.Root})"

let private none = CancellationToken.None
let private wait (task: Threading.Tasks.Task<'T>) = task.GetAwaiter().GetResult()
let private utf8 (text: string) = UTF8Encoding(false).GetBytes text

/// Whether a result is the refusal of a path through a link.
let private throughLink (result: Result<'T, FsError>) =
    match result with
    | Error(InvalidPath(_, reason)) -> reason.StartsWith "through a symbolic link"
    | _ -> false

let private refused (what: string) (result: Result<'T, FsError>) =
    Expect.isTrue (throughLink result) $"{what}: refused as through a link, got {result}"

let private ok (result: Result<'T, 'E>) =
    match result with
    | Ok value -> value
    | Error e -> failtest $"expected success, got {e}"

/// Tests files made by `create root outside`: they must resolve beneath `root`.
let tests (name: string) (create: string -> string -> Fixture) : Test =
    let test name (body: Fixture -> (string list -> string) -> unit) =
        testCase name (fun () ->
            let scratch =
                Path.Combine(Path.GetTempPath(), "fx-beneath-" + Guid.NewGuid().ToString("N"))

            let root = Path.Combine(scratch, "root")
            let outside = Path.Combine(scratch, "outside")
            Directory.CreateDirectory root |> ignore
            Directory.CreateDirectory outside |> ignore
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret")
            let fixture = create root outside

            try
                body fixture (fun segments -> Path.Combine(Array.ofList (root :: segments)))
            finally
                fixture.Release()

                try
                    Directory.Delete(scratch, true)
                with _ ->
                    ())

    let dirLink (at: string) (target: string) =
        Directory.CreateSymbolicLink(at, target) |> ignore

    let fileLink (at: string) (target: string) =
        File.CreateSymbolicLink(at, target) |> ignore

    testList
        $"Beneath roots ({name})"
        [
            test "a directory link on the way is refused by every operation" (fun f at ->
                dirLink (at [ "link" ]) f.Outside
                let secret = at [ "link"; "secret.txt" ]
                refused "read" (wait (f.Files.FsRead.ReadFile secret none))
                refused "range" (wait (f.Files.FsOps.ReadRange secret 0L 10 none))
                refused "stat" (wait (f.Files.FsRead.Stat secret none))
                refused "list" (wait (f.Files.FsRead.List (at [ "link" ]) none))
                refused "write" (wait (f.Files.Fs.WriteFile (at [ "link"; "new.txt" ]) (utf8 "x") none))
                refused "atomic write" (wait (f.Files.FsOps.WriteAtomic (at [ "link"; "new.txt" ]) (utf8 "x") none))
                refused "create directory" (wait (f.Files.Fs.CreateDirectory (at [ "link"; "d"; "e" ]) none))
                refused "delete" (wait (f.Files.Fs.Delete secret none))
                refused "move" (wait (f.Files.Fs.Move secret (at [ "stolen.txt" ]) none))

                match wait (f.Files.FsOps.ApplyPatch [ Edit(secret, None, []) ] none) with
                | Error(PatchFsError(InvalidPath(_, reason))) when reason.StartsWith "through a symbolic link" -> ()
                | other -> failtest $"patch: {other}"

                match wait (f.Files.FsOps.Search (SearchQuery.lines (at [ "link" ]) "secret") none) with
                | Error(SearchFsError(InvalidPath(_, reason))) when reason.StartsWith "through a symbolic link" -> ()
                | other -> failtest $"search: {other}"

                Expect.equal (File.ReadAllText(Path.Combine(f.Outside, "secret.txt"))) "secret" "the secret is intact"
                Expect.isFalse (File.Exists(Path.Combine(f.Outside, "new.txt"))) "nothing written outside"
                Expect.isFalse (Directory.Exists(Path.Combine(f.Outside, "d"))) "no directory made outside")

            test "a link deeper down is refused too" (fun f at ->
                Directory.CreateDirectory(at [ "a"; "b" ]) |> ignore
                dirLink (at [ "a"; "b"; "c" ]) f.Outside
                refused "read" (wait (f.Files.FsRead.ReadFile (at [ "a"; "b"; "c"; "secret.txt" ]) none)))

            test "the link itself is an entry: stat and list report it, delete and move act on it" (fun f at ->
                dirLink (at [ "dlink" ]) f.Outside
                fileLink (at [ "flink" ]) (Path.Combine(f.Outside, "secret.txt"))

                Expect.equal
                    (ok (wait (f.Files.FsRead.Stat (at [ "dlink" ]) none))).Kind
                    EntryKind.Symlink
                    "stat dir link"

                Expect.equal
                    (ok (wait (f.Files.FsRead.Stat (at [ "flink" ]) none))).Kind
                    EntryKind.Symlink
                    "stat file link"

                let listed = ok (wait (f.Files.FsRead.List f.Root none))

                Expect.equal
                    (listed |> List.map (fun e -> e.Name, e.Kind))
                    [ "dlink", EntryKind.Symlink; "flink", EntryKind.Symlink ]
                    "list"

                ok (wait (f.Files.Fs.Move (at [ "flink" ]) (at [ "moved" ]) none))

                Expect.equal
                    (ok (wait (f.Files.FsRead.Stat (at [ "moved" ]) none))).Kind
                    EntryKind.Symlink
                    "moved as a link"

                ok (wait (f.Files.Fs.Delete (at [ "moved" ]) none))
                Expect.isTrue (File.Exists(Path.Combine(f.Outside, "secret.txt"))) "the target stays"
                Expect.equal (File.ReadAllText(Path.Combine(f.Outside, "secret.txt"))) "secret" "unchanged")

            test "a file link is not followed: reads and writes are refused, an atomic write replaces it" (fun f at ->
                let target = Path.Combine(f.Outside, "secret.txt")
                fileLink (at [ "flink" ]) target
                refused "read" (wait (f.Files.FsRead.ReadFile (at [ "flink" ]) none))
                refused "write" (wait (f.Files.Fs.WriteFile (at [ "flink" ]) (utf8 "overwritten") none))
                ok (wait (f.Files.FsOps.WriteAtomic (at [ "flink" ]) (utf8 "replaced") none))
                Expect.equal (File.ReadAllText target) "secret" "the target is unchanged"
                Expect.equal (ok (wait (f.Files.FsRead.Stat (at [ "flink" ]) none))).Kind EntryKind.File "a file now"

                Expect.equal
                    (ok (wait (f.Files.FsRead.ReadFile (at [ "flink" ]) none)))
                    (utf8 "replaced")
                    "its content")

            test "a directory swapped for a link between two calls is refused on the second" (fun f at ->
                Directory.CreateDirectory(at [ "d" ]) |> ignore
                File.WriteAllText(at [ "d"; "secret.txt" ], "mine")

                Expect.equal
                    (ok (wait (f.Files.FsRead.ReadFile (at [ "d"; "secret.txt" ]) none)))
                    (utf8 "mine")
                    "before"

                Directory.Delete(at [ "d" ], true)
                dirLink (at [ "d" ]) f.Outside
                refused "after the swap" (wait (f.Files.FsRead.ReadFile (at [ "d"; "secret.txt" ]) none))

                refused
                    "an atomic write after the swap"
                    (wait (f.Files.FsOps.WriteAtomic (at [ "d"; "secret.txt" ]) (utf8 "x") none))

                Expect.equal (File.ReadAllText(Path.Combine(f.Outside, "secret.txt"))) "secret" "intact")

            test "dot segments are normalized before resolving, so they do not climb out through a link" (fun f at ->
                dirLink (at [ "link" ]) f.Outside
                File.WriteAllText(at [ "here.txt" ], "here")
                // As written: root/link/../here.txt, which is root/here.txt (not outside/../here.txt).
                Expect.equal
                    (ok (wait (f.Files.FsRead.ReadFile (at [ "link"; ".."; "here.txt" ]) none)))
                    (utf8 "here")
                    "normalized")

            test "a root that does not exist yet: reads find nothing, creating a directory makes it" (fun f at ->
                try
                    Directory.Delete f.Root
                with :? IOException ->
                    // Windows keeps the directory of a process that runs there (envd's workspace).
                    skiptest "the root cannot be deleted here"

                Expect.equal
                    (wait (f.Files.FsRead.ReadFile (at [ "a.txt" ]) none))
                    (Error(NotFound(at [ "a.txt" ])))
                    "read"

                ok (wait (f.Files.Fs.CreateDirectory (at [ "x"; "y" ]) none))
                Expect.isTrue (Directory.Exists(at [ "x"; "y" ])) "created")

            test "paths outside the roots follow links as before" (fun f at ->
                let elsewhere = Path.Combine(f.Outside, "..", "via")
                dirLink elsewhere f.Outside

                Expect.equal
                    (ok (wait (f.Files.FsRead.ReadFile (Path.Combine(elsewhere, "secret.txt")) none)))
                    (utf8 "secret")
                    "outside a root"

                File.WriteAllText(at [ "plain.txt" ], "plain")
                ok (wait (f.Files.Fs.CreateDirectory (at [ "x"; "y" ]) none))
                Expect.isTrue (Directory.Exists(at [ "x"; "y" ])) "created beneath the root")
        ]
