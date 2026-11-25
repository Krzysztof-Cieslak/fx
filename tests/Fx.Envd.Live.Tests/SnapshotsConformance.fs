/// The workspace-snapshot conformance suite: what every `Snapshots` interpreter must do, on real files
/// with the git CLI. `GitSnapshots` passes it directly and through envd (in process and on stdio).
module Fx.Envd.Live.Tests.SnapshotsConformance

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open Expecto
open Fx.Envd

let private none = CancellationToken.None

let private wait (task: Threading.Tasks.Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 120.0).GetAwaiter().GetResult()

let private ok (result: Result<'T, SnapshotError>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failtest $"expected success, got {e}"

/// Runs git in a directory (to make the workspace a user's repository).
let private git (dir: string) (args: string list) =
    let info =
        ProcessStartInfo("git", args, WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true)

    use p = Process.Start info |> Option.ofObj |> Option.get
    p.StandardOutput.ReadToEnd() |> ignore
    p.WaitForExit()
    Expect.equal p.ExitCode 0 $"git {args}"

/// Tests that the snapshots made by `create` conform. `create` gets an empty workspace directory and
/// is called once per test; `release` cleans up.
let tests (name: string) (directory: unit -> string) (create: string -> Snapshots) (release: Snapshots -> unit) : Test =
    let test name (body: Snapshots -> (string list -> string) -> unit) =
        testCase name (fun () ->
            let workspace = directory ()
            let snapshots = create workspace

            try
                body snapshots (fun segments -> Path.Combine(Array.ofList (workspace :: segments)))
            finally
                release snapshots)

    let write (path: string) (text: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path |> string) |> ignore
        File.WriteAllText(path, text)

    testList
        $"Snapshots conformance ({name})"
        [
            test "records the workspace and compares snapshots" (fun s at ->
                write (at [ "a.txt" ]) "a"
                write (at [ "b.txt" ]) "b"
                write (at [ "sub"; "c.txt" ]) "c"
                let first = ok (wait (s.Take "first" none))
                write (at [ "a.txt" ]) "a, changed"
                File.Delete(at [ "b.txt" ])
                write (at [ "sub"; "d.txt" ]) "d"
                let second = ok (wait (s.Take "second" none))
                Expect.notEqual second.Tree first.Tree "a new tree"

                Expect.equal
                    (ok (wait (s.Diff first second none)))
                    [
                        {
                            Path = "a.txt"
                            Kind = ChangeKind.Modified
                        }
                        {
                            Path = "b.txt"
                            Kind = ChangeKind.Deleted
                        }
                        {
                            Path = "sub/d.txt"
                            Kind = ChangeKind.Added
                        }
                    ]
                    "diff"

                let third = ok (wait (s.Take "" none))
                Expect.equal third.Tree second.Tree "nothing changed: the same tree"
                Expect.equal (ok (wait (s.Diff second third none))) [] "no differences")

            test "honours .gitignore and leaves the user's repository alone" (fun s at ->
                let workspace = Path.GetDirectoryName(at [ "x" ]) |> string
                git workspace [ "init"; "--quiet" ]
                write (at [ ".gitignore" ]) "ignored.txt\nbuild/\n"
                write (at [ "ignored.txt" ]) "secret"
                write (at [ "build"; "out.bin" ]) "binary"
                write (at [ "kept.txt" ]) "kept"
                let first = ok (wait (s.Take "first" none))
                write (at [ "ignored.txt" ]) "changed secret"
                write (at [ "new.txt" ]) "new"
                let second = ok (wait (s.Take "second" none))

                Expect.equal
                    (ok (wait (s.Diff first second none)))
                    [
                        {
                            Path = "new.txt"
                            Kind = ChangeKind.Added
                        }
                    ]
                    "ignored files are not recorded"

                Expect.isFalse (File.Exists(at [ ".git"; "index" ])) "the user's index was never written"

                Expect.isFalse
                    (Directory.Exists(at [ ".git"; "refs"; Fx.Product.Name ]))
                    "no refs in the user's repository")

            test "a unified diff between snapshots, of everything or some paths" (fun s at ->
                write (at [ "a.txt" ]) "old line\n"
                write (at [ "b.txt" ]) "b\n"
                let first = ok (wait (s.Take "first" none))
                write (at [ "a.txt" ]) "new line\n"
                write (at [ "b.txt" ]) "b changed\n"
                let second = ok (wait (s.Take "second" none))
                let all = ok (wait (s.Patch first second None none))
                Expect.stringContains all "-old line" "removed"
                Expect.stringContains all "+new line" "added"
                Expect.stringContains all "b.txt" "both files"
                let some = ok (wait (s.Patch first second (Some [ "a.txt" ]) none))
                Expect.stringContains some "+new line" "a.txt"
                Expect.isFalse (some.Contains "b.txt") "only a.txt")

            test "restores the workspace byte for byte" (fun s at ->
                write (at [ ".gitattributes" ]) "* text=auto eol=crlf\n"
                write (at [ ".gitignore" ]) "ignored.txt\n"
                let lf = Encoding.UTF8.GetBytes "line 1\nline 2\n"
                File.WriteAllBytes(at [ "lf.txt" ], lf)
                write (at [ "doomed.txt" ]) "will be deleted"
                write (at [ "ignored.txt" ]) "before"
                let snapshot = ok (wait (s.Take "before" none))
                File.WriteAllText(at [ "lf.txt" ], "changed")
                File.Delete(at [ "doomed.txt" ])
                write (at [ "added"; "new.txt" ]) "added later"
                write (at [ "ignored.txt" ]) "after"
                ok (wait (s.Restore snapshot None none))
                Expect.equal (File.ReadAllBytes(at [ "lf.txt" ])) lf "the same bytes (no CRLF conversion)"
                Expect.equal (File.ReadAllText(at [ "doomed.txt" ])) "will be deleted" "deleted file back"
                Expect.isFalse (File.Exists(at [ "added"; "new.txt" ])) "added file gone"
                Expect.equal (File.ReadAllText(at [ "ignored.txt" ])) "after" "ignored files untouched")

            test "records and restores paths longer than 260 characters" (fun s at ->
                let segments = [ for i in 1..6 -> String('d', 45) + string i ]
                let file = at (segments @ [ String('f', 60) + ".txt" ])
                Expect.isGreaterThan file.Length 300 "a long path"
                write file "deep"
                let snapshot = ok (wait (s.Take "deep" none))
                File.Delete file
                ok (wait (s.Restore snapshot None none))
                Expect.equal (File.ReadAllText file) "deep" "restored")

            test "restores some paths only" (fun s at ->
                write (at [ "a.txt" ]) "a"
                write (at [ "b.txt" ]) "b"
                let snapshot = ok (wait (s.Take "before" none))
                write (at [ "a.txt" ]) "a, changed"
                write (at [ "b.txt" ]) "b, changed"
                ok (wait (s.Restore snapshot (Some [ "a.txt" ]) none))
                Expect.equal (File.ReadAllText(at [ "a.txt" ])) "a" "restored"
                Expect.equal (File.ReadAllText(at [ "b.txt" ])) "b, changed" "left alone")
        ]
