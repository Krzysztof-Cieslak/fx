/// The capability conformance suites against the live interpreters, directly and through envd (in
/// process and as `fx envd --stdio`), and what only real processes and files show: resuming a real
/// process after a reconnect, spill files, file modes, the platform, and the binary's `envd` mode.
module Fx.Envd.Live.Tests.LiveTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open Expecto
open Fx.Effects
open Fx.Effects.Live
open Fx.Effects.Live.Tests.Conformance
open Fx.Effects.Tests.Conformance
open Fx.Envd
open Fx.Envd.Live
open Fx.Envd.Live.Tests.Harness
open Fx.Envd.Tests.Conformance
open Fx.Pty
open Fx.Pty.Tests.Conformance
open Fx.Store.Tests.Conformance

let private reaches = [ Reach.InProcess; Reach.Stdio ]

let private suites (reach: Reach) : Test list =
    let sessions = Sessions()
    let name = $"envd {reach}"

    [
        FsConformance.tests name (fun () ->
            let root = Scratch.directory ()
            let session = openSession reach root

            {
                Read = session.Client.FsRead
                Fs = session.Client.Fs
                Root = root
                Release = session.Close
            })

        FsOpsConformance.tests name (fun () ->
            let root = Scratch.directory ()
            let session = openSession reach root

            {
                Read = session.Client.FsRead
                Fs = session.Client.Fs
                Ops = session.Client.FsOps
                Root = root
                Watches = true
                Release = session.Close
            })

        // Beneath a root, the files behave as they do anywhere, except for links (T113b).
        FsConformance.tests $"{name}, beneath the root" (fun () ->
            let root = Scratch.directory ()
            let session = openSession reach root
            let client = EnvClient.beneath [ root ] session.Client

            {
                Read = client.FsRead
                Fs = client.Fs
                Root = root
                Release = session.Close
            })

        FsOpsConformance.tests $"{name}, beneath the root" (fun () ->
            let root = Scratch.directory ()
            let session = openSession reach root
            let client = EnvClient.beneath [ root ] session.Client

            {
                Read = client.FsRead
                Fs = client.Fs
                Ops = client.FsOps
                Root = root
                Watches = true
                Release = session.Close
            })

        BeneathConformance.tests name (fun root outside ->
            let session = openSession reach root
            let client = EnvClient.beneath [ root ] session.Client

            {
                Files =
                    {
                        FsRead = client.FsRead
                        Fs = client.Fs
                        FsOps = client.FsOps
                    }
                Root = root
                Outside = outside
                Release = session.Close
            })

        BlobsConformance.tests name (fun () ->
            let session = openSession reach (Scratch.directory ())
            sessions.Track(session.Client.Blobs, session)
            session.Client.Blobs)

        ShellConformance.tests
            name
            (fun cwd ->
                let session = openSession reach cwd
                sessions.Track(session.Client.Shell, session)
                session.Client.Shell)
            (fun shell -> sessions.Close shell)

        TerminalConformance.tests
            name
            (fun cwd ->
                let session = openSession reach cwd
                sessions.Track(session.Client.Terminal, session)
                session.Client.Terminal)
            (fun terminal -> sessions.Close terminal)

        SnapshotsConformance.tests
            name
            Scratch.directory
            (fun workspace ->
                let session = openSession reach workspace
                sessions.Track(session.Client.Snapshots, session)
                session.Client.Snapshots)
            (fun snapshots -> sessions.Close snapshots)
    ]

[<Tests>]
let conformance =
    testList
        "Conformance"
        [
            FsOpsConformance.tests "LocalFsOps" (fun () ->
                let root = Scratch.directory ()

                {
                    Read = LocalFs.read
                    Fs = LocalFs.write
                    Ops = LocalFsOps.create SystemClock.clock (SystemIds.create SystemClock.clock)
                    Root = root
                    Watches = true
                    Release = ignore
                })

            FsConformance.tests "LocalBeneath" (fun () ->
                let root = Scratch.directory ()

                let files =
                    LocalBeneath.files SystemClock.clock (SystemIds.create SystemClock.clock) [ root ]

                {
                    Read = files.FsRead
                    Fs = files.Fs
                    Root = root
                    Release = ignore
                })

            FsOpsConformance.tests "LocalBeneath" (fun () ->
                let root = Scratch.directory ()

                let files =
                    LocalBeneath.files SystemClock.clock (SystemIds.create SystemClock.clock) [ root ]

                {
                    Read = files.FsRead
                    Fs = files.Fs
                    Ops = files.FsOps
                    Root = root
                    Watches = true
                    Release = ignore
                })

            BeneathConformance.tests "LocalBeneath" (fun root outside ->
                {
                    Files = LocalBeneath.files SystemClock.clock (SystemIds.create SystemClock.clock) [ root ]
                    Root = root
                    Outside = outside
                    Release = ignore
                })

            // One openat per component, where openat2 (Linux) is missing.
            FsOpsConformance.tests "LocalBeneath, walking" (fun () ->
                let root = Scratch.directory ()

                let files =
                    LocalBeneath.filesWith false SystemClock.clock (SystemIds.create SystemClock.clock) [ root ]

                {
                    Read = files.FsRead
                    Fs = files.Fs
                    Ops = files.FsOps
                    Root = root
                    Watches = true
                    Release = ignore
                })

            BeneathConformance.tests "LocalBeneath, walking" (fun root outside ->
                {
                    Files =
                        LocalBeneath.filesWith false SystemClock.clock (SystemIds.create SystemClock.clock) [ root ]
                    Root = root
                    Outside = outside
                    Release = ignore
                })

            SnapshotsConformance.tests
                "GitSnapshots"
                Scratch.directory
                (fun workspace ->
                    GitSnapshots.create
                        (LocalShell.create (LocalShell.defaultShell ()) workspace)
                        LocalFs.write
                        {
                            Git = "git"
                            GitDir = Path.Combine(Scratch.directory (), "shadow.git")
                            WorkTree = workspace
                            Exclude = []
                        })
                ignore

            for reach in reaches do
                yield! suites reach
        ]

let private script (posix: string) (pwsh: string) : Program = Script(if windows then pwsh else posix)

/// Reads an attachment until the text contains `marker`; returns the text and the next offset of `out`.
let private readUntil (attachment: Attachment) (from: int64) (marker: string) : string * int64 =
    let sb = StringBuilder()
    let mutable next = from

    while not ((sb.ToString()).Contains marker) do
        match wait (attachment.Next none) with
        | Some(OutputRecord.Data(OutputStream.Out, offset, bytes)) ->
            Expect.equal offset next "in sequence"
            sb.Append(Encoding.UTF8.GetString bytes) |> ignore
            next <- offset + int64 bytes.Length
        | Some _ -> ()
        | None -> failtest $"the output ended before {marker}: {sb}"

    sb.ToString(), next

/// Reads an attachment to its end; returns the text of `out`.
let private readAll (attachment: Attachment) : string =
    let sb = StringBuilder()
    let mutable go = true

    while go do
        match wait (attachment.Next none) with
        | Some(OutputRecord.Data(OutputStream.Out, offset, bytes)) ->
            Expect.equal offset (int64 (Encoding.UTF8.GetByteCount(sb.ToString()))) "in sequence"
            sb.Append(Encoding.UTF8.GetString bytes) |> ignore
        | Some _ -> ()
        | None -> go <- false

    sb.ToString()

let private start (client: EnvClient) (program: Program) =
    ok (
        wait (
            EnvClient.start
                client
                {
                    Program = program
                    Cwd = None
                    Env = Inherit []
                    Terminal = None
                }
                none
        )
    )

[<Tests>]
let live =
    testList
        "Live envd"
        [
            test "a host resumes a real process's output after it reconnects" {
                let instance =
                    startInProcess
                        { EnvdOptions.defaults with
                            Workspace = Some(Scratch.directory ())
                            State = Some(Scratch.directory ())
                        }

                try
                    let first, cut = attachInProcess instance

                    let started =
                        start
                            first
                            (script
                                "echo first; read line; echo \"second $line\""
                                "Write-Output first; $line = [Console]::In.ReadLine(); Write-Output \"second $line\"")

                    let attachment = ok (wait (EnvClient.attach first started.Id 0L 0L none))
                    let _, next = readUntil attachment 0L "first"
                    cut ()
                    let second, _ = attachInProcess instance
                    wait (EnvClient.write second started.Id (Encoding.UTF8.GetBytes "go\n") none)
                    let resumed = ok (wait (EnvClient.attach second started.Id next 0L none))
                    let rest, _ = readUntil resumed next "second go"
                    Expect.isFalse (rest.Contains "first") "resumed after what was read"
                    Expect.equal (ok (wait (EnvClient.wait second started.Id none))) 0 "exit code"
                finally
                    (instance.Dispose()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
            }

            test "output beyond the memory window is in a greppable spill file" {
                for reach in reaches do
                    let session =
                        openSessionWith reach (Scratch.directory ()) (fun o -> { o with Retain = 4096 })

                    try
                        let started =
                            start
                                session.Client
                                (script
                                    "i=0; while [ $i -lt 3000 ]; do echo \"line $i\"; i=$((i+1)); done"
                                    "foreach ($i in 0..2999) { Write-Output \"line $i\" }")

                        let attachment = ok (wait (EnvClient.attach session.Client started.Id 0L 0L none))
                        let all = readAll attachment
                        Expect.equal (ok (wait (EnvClient.wait session.Client started.Id none))) 0 "exit code"

                        let lines =
                            all.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)

                        Expect.equal lines.Length 3000 "every line, from the start"
                        let status = ok (wait (EnvClient.status session.Client started.Id none))

                        match status.Out.SpillPath with
                        | Some path ->
                            // As grep would open it while envd still has it open for writing (Windows).
                            use stream =
                                new FileStream(
                                    path,
                                    FileMode.Open,
                                    FileAccess.Read,
                                    FileShare.ReadWrite ||| FileShare.Delete
                                )

                            use reader = new StreamReader(stream)
                            Expect.equal (reader.ReadToEnd()) all $"{reach}: the spill file holds the whole output"
                        | None -> failtest "no spill file"

                        wait (EnvClient.release session.Client started.Id none)
                        Expect.isFalse (File.Exists status.Out.SpillPath.Value) "released: the spill file is gone"
                    finally
                        session.Close()
            }

            test "atomic writes keep the file's mode" {
                if not windows then
                    let dir = Scratch.directory ()
                    let file = Path.Combine(dir, "run.sh")
                    File.WriteAllText(file, "old")

                    let mode =
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

                    File.SetUnixFileMode(file, mode)
                    let ops = LocalFsOps.create SystemClock.clock (SystemIds.create SystemClock.clock)
                    ok (wait (ops.WriteAtomic file (Encoding.UTF8.GetBytes "new") none))
                    Expect.equal (File.ReadAllText file) "new" "content"
                    Expect.equal (File.GetUnixFileMode file) mode "mode"
            }

            test "envd announces the platform it runs on" {
                for reach in reaches do
                    let workspace = Scratch.directory ()
                    let session = openSession reach workspace

                    try
                        let platform = session.Client.Platform
                        Expect.equal platform.Os (LocalPlatform.os ()) "os"
                        Expect.equal platform.Arch (LocalPlatform.arch ()) "arch"
                        Expect.equal platform.Libc (LocalPlatform.libc ()) "libc"
                        Expect.equal platform.PathSeparator (string Path.DirectorySeparatorChar) "separator"
                        Expect.equal platform.Workspace workspace "workspace"

                        Expect.equal
                            platform.CaseSensitive
                            (not (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()))
                            "case sensitivity"

                        Expect.containsAll
                            platform.Features
                            [ Feature.Pty; Feature.Watch; Feature.Snapshots ]
                            "features"

                        if windows then
                            Expect.equal platform.Shell ShellSpec.pwsh "PowerShell 7 on Windows"
                        else
                            Expect.equal platform.Shell ShellSpec.posix "/bin/sh elsewhere"
                    finally
                        session.Close()
            }

            test "terminals type and print UTF-8, PowerShell included" {
                for reach in reaches do
                    let session = openSession reach (Scratch.directory ())

                    try
                        let program =
                            script
                                "echo @@ready; read line; echo \"@@got=$line\""
                                "Write-Host '@@ready'; $line = [Console]::ReadLine(); Write-Host \"@@got=$line\""

                        match
                            wait (
                                session.Client.Terminal.Spawn
                                    { PtySpec.script "" with
                                        Program = program
                                    }
                                    none
                            )
                        with
                        | Error e -> failtest (string e)
                        | Ok pty ->
                            try
                                let transcript = Transcript pty

                                Expect.isTrue (wait (transcript.ReadUntil (fun t -> t.Contains "@@ready") none)) "ready"

                                wait (pty.Write (Encoding.UTF8.GetBytes "héllo ✓\r") none)
                                wait (transcript.ReadToEnd none)
                                Expect.stringContains transcript.Plain "@@got=héllo ✓" $"{reach}: the line came back"
                            finally
                                wait (pty.Close())
                    finally
                        session.Close()
            }

            // A channel counts against the connection's limit (256) until both ends let go of it: a
            // transfer that left it half open broke every file and process call of a long session.
            test "every transfer lets go of its channel" {
                for reach in reaches do
                    let session = openSession reach (Scratch.directory ())
                    let client = session.Client

                    // The host's end is released before the call returns.
                    let settled (what: string) =
                        Expect.equal client.Peer.OpenChannels 0 $"{reach}, {what}: no channel stays open"

                    try
                        let file = Path.Combine(session.Workspace, "f.txt")
                        ok (wait (client.Fs.WriteFile file "abc"B none))
                        settled "a write"
                        ok (wait (client.FsOps.WriteAtomic file "abcd"B none))
                        settled "an atomic write"
                        Expect.equal (ok (wait (client.FsRead.ReadFile file none))) "abcd"B "read"
                        settled "a read"

                        Expect.isError
                            (wait (client.FsRead.ReadFile (Path.Combine(session.Workspace, "no")) none))
                            "no file"

                        settled "a read that failed"
                        let blob = wait (client.Blobs.Put "blob"B none)
                        settled "a put"
                        Expect.equal (wait (client.Blobs.Get blob.Hash none)) (Ok "blob"B) "get"
                        settled "a get"

                        let echo =
                            if windows then
                                Command.exec "cmd.exe" [ "/d"; "/c"; "echo hi" ]
                            else
                                Command.exec "/bin/sh" [ "-c"; "echo hi" ]

                        let output = ok (wait (client.Shell.Run echo none))
                        Expect.equal ((Output.stdoutText output).TrimEnd()) "hi" "ran"
                        settled "a run"

                        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 1.0)

                        let cancelled =
                            try
                                wait (
                                    client.Shell.Run
                                        (Command.script (if windows then "Start-Sleep -Seconds 60" else "sleep 60"))
                                        timeout.Token
                                )
                                |> ignore

                                false
                            with :? OperationCanceledException ->
                                true

                        Expect.isTrue cancelled "the run was cancelled"
                        settled "a cancelled run"
                        let mutable left = wait (EnvClient.list client none)

                        for _ in 1..250 do
                            if not left.IsEmpty then
                                Thread.Sleep 20
                                left <- wait (EnvClient.list client none)

                        Expect.isEmpty left "the processes of the runs were released"

                        let started = start client (script "echo out" "Write-Output out")
                        let attachment = ok (wait (EnvClient.attach client started.Id 0L 0L none))
                        Expect.equal ((readAll attachment).TrimEnd()) "out" "attached"
                        settled "an attachment read to its end"
                        wait (EnvClient.release client started.Id none)

                        match
                            wait (
                                client.Terminal.Spawn
                                    { PtySpec.script "" with
                                        Program = script "echo term" "Write-Output term"
                                    }
                                    none
                            )
                        with
                        | Error e -> failtest (string e)
                        | Ok pty ->
                            let transcript = Transcript pty
                            wait (transcript.ReadToEnd none)
                            wait (pty.Close())
                            Expect.stringContains transcript.Plain "term" "the terminal ran"
                            settled "a terminal"

                        let first = ok (wait (client.Snapshots.Take "first" none))
                        ok (wait (client.Fs.WriteFile file "changed"B none))
                        let second = ok (wait (client.Snapshots.Take "second" none))

                        Expect.stringContains
                            (ok (wait (client.Snapshots.Patch first second None none)))
                            "changed"
                            "patch"

                        settled "a patch"
                    finally
                        session.Close()
            }

            test "fx envd refuses a missing workspace and needs --stdio" {
                let run (args: string list) =
                    let info =
                        ProcessStartInfo(
                            binary,
                            args,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false
                        )

                    use child = Process.Start info |> Option.ofObj |> Option.get
                    child.StandardInput.Close()
                    let stdout = child.StandardOutput.ReadToEnd()
                    let stderr = child.StandardError.ReadToEnd()
                    child.WaitForExit()
                    child.ExitCode, stdout, stderr

                let missing = Path.Combine(Scratch.directory (), "missing")
                let code, stdout, stderr = run [ "envd"; "--stdio"; "--workspace"; missing ]
                Expect.equal code 2 "exit code"
                Expect.equal stdout "" "nothing on the protocol's stream"
                Expect.stringContains stderr "does not exist" "says why"
                let code, _, _ = run [ "envd" ]
                Expect.equal code 2 "--stdio is required"

                let code, stdout, _ =
                    run
                        [
                            "envd"
                            "--stdio"
                            "--workspace"
                            Scratch.directory ()
                            "--state"
                            Scratch.directory ()
                        ]

                Expect.equal (code, stdout) (1, "") "no handshake: envd ends, with nothing on standard output"
            }
        ]
