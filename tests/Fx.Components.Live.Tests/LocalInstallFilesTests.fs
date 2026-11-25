module Fx.Components.Live.Tests.LocalInstallFilesTests

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Expecto
open Fx.Components
open Fx.Components.Fakes
open Fx.Components.Live
open Fx.Components.Live.Tests.LiveEnv
open Fx.Components.Tests.Support
open Fx.Effects
open Fx.Effects.Live
open Fx.Machines.Host
open Fx.Sdk

let private files = LocalInstallFiles.files
let private none = CancellationToken.None

let private wait (task: Threading.Tasks.Task<'T>) = task.GetAwaiter().GetResult()

let private extract (dir: string) (format: ArchiveFormat) (bytes: byte array) =
    let archive = Path.Combine(dir, "archive")
    File.WriteAllBytes(archive, bytes)

    files.Extract
        {
            Archive = archive
            Destination = Path.Combine(dir, "out")
            Options =
                {
                    Format = format
                    Strip = 0
                    Limits = ExtractLimits.defaults
                    Executables = []
                }
        }
        none
    |> wait

let private withDir (body: string -> unit) =
    let dir = tempDirectory ()

    try
        body dir
    finally
        removeDirectory dir

let private unixOnly () =
    if OperatingSystem.IsWindows() then
        skiptest "Linux and macOS only"

[<Tests>]
let tests =
    testSequenced
    <| testList
        "LocalInstallFiles"
        [
            test "unpacks files with executable bits on Linux and macOS" {
                unixOnly ()

                for format, pack in [ ArchiveFormat.TarGz, tarGz; ArchiveFormat.Zip, zip ] do
                    withDir (fun dir ->
                        let archive = pack [ File("bin/tool", "run", true); File("README", "read", false) ]

                        match extract dir format archive with
                        | Ok report -> Expect.equal report.Files 2 $"{format}: two files"
                        | Error e -> failtest $"{format}: {e}"

                        let out = Path.Combine(dir, "out")
                        let tool = File.GetUnixFileMode(Path.Combine(out, "bin", "tool"))
                        let readme = File.GetUnixFileMode(Path.Combine(out, "README"))
                        Expect.isTrue (tool.HasFlag UnixFileMode.UserExecute) $"{format}: tool is executable"
                        Expect.isFalse (readme.HasFlag UnixFileMode.UserExecute) $"{format}: README is not"
                        Expect.isFalse (tool.HasFlag UnixFileMode.SetUser) $"{format}: no setuid"
                        Expect.equal (File.ReadAllText(Path.Combine(out, "README"))) "read" $"{format}: content")
            }

            test "symbolic links are created inside on Linux and macOS, and refused on Windows" {
                withDir (fun dir ->
                    let archive =
                        tar [ File("lib/real", "content", false); Symlink("link", "lib/real") ]

                    match extract dir ArchiveFormat.Tar archive with
                    | Ok _ when not (OperatingSystem.IsWindows()) ->
                        let link = FileInfo(Path.Combine(dir, "out", "link"))
                        Expect.equal link.LinkTarget "lib/real" "the link"
                        Expect.equal (File.ReadAllText link.FullName) "content" "it resolves"
                    | Error(Unsupported(_, reason)) when OperatingSystem.IsWindows() ->
                        Expect.stringContains reason "Windows" "says why"
                    | other -> failtest $"{other}")
            }

            test "the destination must not exist" {
                withDir (fun dir ->
                    Directory.CreateDirectory(Path.Combine(dir, "out")) |> ignore

                    match extract dir ArchiveFormat.Tar (tar [ File("a", "x", false) ]) with
                    | Error(ArchiveError.WriteFailed(_, reason)) -> Expect.stringContains reason "exists" "refused"
                    | other -> failtest $"{other}")
            }

            test "locks are exclusive until released, also within one process" {
                withDir (fun dir ->
                    let path = Path.Combine(dir, "locks", "a.lock")

                    match wait (files.TryLock path none) with
                    | Ok(Some held) ->
                        match wait (files.TryLock path none) with
                        | Ok None -> ()
                        | other -> failtest $"expected the lock to be held, got {other}"

                        wait (held.Release())

                        match wait (files.TryLock path none) with
                        | Ok(Some again) -> wait (again.Release())
                        | other -> failtest $"after release: {other}"
                    | other -> failtest $"{other}")
            }

            test "deleting a tree never follows links out of it" {
                unixOnly ()

                withDir (fun dir ->
                    let outside = Directory.CreateDirectory(Path.Combine(dir, "outside")).FullName
                    File.WriteAllText(Path.Combine(outside, "keep"), "x")
                    let tree = Directory.CreateDirectory(Path.Combine(dir, "tree")).FullName
                    Directory.CreateSymbolicLink(Path.Combine(tree, "link"), outside) |> ignore

                    File.CreateSymbolicLink(Path.Combine(dir, "file-link"), Path.Combine(outside, "keep"))
                    |> ignore

                    Expect.equal (wait (files.DeleteTree tree none)) (Ok()) "deleted"
                    Expect.equal (wait (files.DeleteTree (Path.Combine(dir, "file-link")) none)) (Ok()) "link deleted"
                    Expect.isFalse (Directory.Exists tree) "the tree is gone"
                    Expect.isTrue (File.Exists(Path.Combine(outside, "keep"))) "what the links pointed to stays"
                    Expect.equal (wait (files.DeleteTree tree none)) (Ok()) "nothing there is fine")
            }

            test "replacing a file overwrites it whole, and hashing streams it" {
                withDir (fun dir ->
                    let path = Path.Combine(dir, "roots.json")
                    Expect.equal (wait (files.ReplaceFile path (utf8.GetBytes "one") none)) (Ok()) "first"
                    Expect.equal (wait (files.ReplaceFile path (utf8.GetBytes "two") none)) (Ok()) "second"
                    Expect.equal (File.ReadAllText path) "two" "replaced"

                    Expect.equal
                        (Directory.GetFiles dir |> Array.map Path.GetFileName)
                        [| "roots.json" |]
                        "no temporary file left"

                    match wait (files.Hash path none) with
                    | Ok hash ->
                        Expect.equal hash.Size 3L "size"

                        Expect.equal
                            hash.Digest
                            (Sha256(Convert.ToHexStringLower(SHA256.HashData(utf8.GetBytes "two"))))
                            "digest"
                    | Error e -> failtest (string e))
            }

            test "a real probe runs the unpacked program from its staging directory" {
                unixOnly ()

                withDir (fun outside ->
                    let archive =
                        tarGz [ File("tool-1.2.3/bin/tool", "#!/bin/sh\necho \"tool 1.2.3\"\n", true) ]

                    let fetch = FakeFetch(LocalFs.write)
                    fetch.Serve(toolUrl "1.2.3", archive)
                    let spec = specOf target (toolComponent "1.2.3" archive)
                    let shell = LocalShell.create (LocalShell.defaultShell ()) outside
                    let env = create fetch (Some shell)
                    let root = Path.Combine(outside, "runtimes")

                    let settings =
                        InstallerSettings.create root "test" (fun _ -> Eff.ret (ApprovalAnswer.Approved "test"))

                    let log = InMemoryEventLog<InstallerEvent>()
                    let actor = runIn env (ComponentInstaller.start settings log.Log)

                    match runIn env (ComponentInstaller.ensure actor "1" "h" spec) with
                    | Ok info ->
                        Expect.equal info.Dir (InstallRoot.installDir root spec.Key) "installed"
                        Expect.isTrue (File.Exists(Path.Combine(info.Dir, "bin", "tool"))) "the program"
                    | Error e -> failtest (string e)

                    runIn env (Fx.Machines.Host.Actor.stop actor))
            }
        ]
