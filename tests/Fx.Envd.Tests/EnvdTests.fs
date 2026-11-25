/// envd and its proxies over fakes, in process: the conformance suites through the protocol, output
/// logs, sequenced output and resuming after a lost connection, and the handshake.
module Fx.Envd.Tests.EnvdTests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Effects.Tests.Conformance
open Fx.Envd
open Fx.Envd.Tests.Conformance
open Fx.Envd.Tests.Support
open Fx.Rpc
open Fx.Store
open Fx.Store.Fakes
open Fx.Store.Tests.Conformance

let private text (bytes: byte array) = Encoding.UTF8.GetString bytes

/// Reads an output log from an offset to its end.
let private readLog (log: OutputLog) (from: int64) : LogRead list =
    let reads = ResizeArray()
    let mutable offset = from
    let mutable go = true

    while go do
        let read = wait (log.Read(offset, 4, none))
        reads.Add read

        match read with
        | LogRead.Data(at, bytes) -> offset <- at + int64 bytes.Length
        | LogRead.Gap(_, until) -> offset <- until
        | LogRead.End _ -> go <- false

    List.ofSeq reads

[<Tests>]
let outputLogs =
    testList
        "OutputLog"
        [
            test "numbers bytes by offset and serves any offset" {
                let log = new OutputLog("t", 100, None)
                wait (log.Append("hello"B, none))
                wait (log.Append(" world"B, none))
                log.Finish()

                Expect.equal
                    (readLog log 0L)
                    [
                        LogRead.Data(0L, "hell"B)
                        LogRead.Data(4L, "o wo"B)
                        LogRead.Data(8L, "rld"B)
                        LogRead.End 11L
                    ]
                    "from the start"

                Expect.equal (readLog log 9L) [ LogRead.Data(9L, "ld"B); LogRead.End 11L ] "from an offset"
                Expect.equal log.Status.Total 11L "total"
            }

            test "a reader waits for output" {
                let log = new OutputLog("t", 100, None)
                let pending = log.Read(0L, 10, none)
                Expect.isFalse pending.IsCompleted "waiting"
                wait (log.Append("x"B, none))
                Expect.equal (wait pending) (LogRead.Data(0L, "x"B)) "woken"
                let atEnd = log.Read(1L, 10, none)
                log.Finish()
                Expect.equal (wait atEnd) (LogRead.End 1L) "ended"
            }

            test "without a spill, old output is dropped and reported as a gap" {
                let log = new OutputLog("t", 4, None)

                for chunk in [ "ab"B; "cd"B; "ef"B; "gh"B ] do
                    wait (log.Append(chunk, none))

                log.Finish()

                Expect.equal (readLog log 0L) [ LogRead.Gap(0L, 4L); LogRead.Data(4L, "efgh"B); LogRead.End 8L ] "gap"

                Expect.equal log.Status.Available 4L "available"
            }

            test "with a spill, everything stays readable, and disposing deletes the spill" {
                let spill = Spill.inMemory "/spill"
                let log = new OutputLog("p.out", 4, Some spill)

                for chunk in [ "ab"B; "cd"B; "ef"B; "gh"B ] do
                    wait (log.Append(chunk, none))

                log.Finish()

                Expect.equal
                    (readLog log 0L
                     |> List.choose (function
                         | LogRead.Data(_, b) -> Some(text b)
                         | _ -> None)
                     |> String.concat "")
                    "abcdefgh"
                    "whole"

                Expect.equal log.Status.SpillPath (Some "/spill/p.out") "spill path"
                Expect.equal log.Status.Available 0L "all available"
                wait (log.Dispose())
                Expect.equal (readLog log 0L |> List.last) (LogRead.End 8L) "ended"
            }
        ]

let private fakeEnvd (shell: Shell) =
    let fs = InMemoryFs(SystemClock.clock, false)
    Envd(services shell fs 1024 None), fs

[<Tests>]
let conformance =
    testList
        "Through the protocol, over fakes"
        [
            FsConformance.tests "envd proxy, InMemoryFs" (fun () ->
                let envd, fs = fakeEnvd (ControlledShell()).Shell
                let connection = connect envd

                let root =
                    Path.Combine(Path.GetTempPath(), "envd-fs-" + Guid.NewGuid().ToString("N"))

                ok (wait (fs.Fs.CreateDirectory root none))

                {
                    Read = connection.Client.FsRead
                    Fs = connection.Client.Fs
                    Root = root
                    Release = connection.Cut
                })

            FsOpsConformance.tests "generic, InMemoryFs" (fun () ->
                let fs = InMemoryFs(SystemClock.clock, false)
                let root = Path.Combine(Path.GetTempPath(), "fsops-" + Guid.NewGuid().ToString("N"))
                ok (wait (fs.Fs.CreateDirectory root none))

                {
                    Read = fs.FsRead
                    Fs = fs.Fs
                    Ops = FsOpsOver.create fs.FsRead fs.Fs
                    Root = root
                    Watches = false
                    Release = ignore
                })

            FsOpsConformance.tests "envd proxy, generic over InMemoryFs" (fun () ->
                let envd, fs = fakeEnvd (ControlledShell()).Shell
                let connection = connect envd

                let root =
                    Path.Combine(Path.GetTempPath(), "envd-fsops-" + Guid.NewGuid().ToString("N"))

                ok (wait (fs.Fs.CreateDirectory root none))

                {
                    Read = connection.Client.FsRead
                    Fs = connection.Client.Fs
                    Ops = connection.Client.FsOps
                    Root = root
                    Watches = false
                    Release = connection.Cut
                })

            BlobsConformance.tests "host cache over the envd proxy" (fun () ->
                let envd, _ = fakeEnvd (ControlledShell()).Shell
                BlobCache.over (InMemoryBlobs().Blobs) (connect envd).Client.Blobs)

            test "the host cache fetches a blob once" {
                let remote = InMemoryBlobs()
                let mutable fetched = 0

                let counting =
                    { remote.Blobs with
                        Get =
                            fun hash cancellation ->
                                fetched <- fetched + 1
                                remote.Blobs.Get hash cancellation
                    }

                let local = InMemoryBlobs()
                let cache = BlobCache.over local.Blobs counting
                let blob = wait (remote.Blobs.Put "cached"B none)
                Expect.equal (wait (cache.Stat blob.Hash none)) (Some blob) "known remotely"

                for _ in 1..3 do
                    Expect.equal (wait (cache.Get blob.Hash none)) (Ok "cached"B) "content"

                Expect.equal fetched 1 "fetched once"
                Expect.equal local.Count 1 "kept locally"
            }

            BlobsConformance.tests "envd proxy, InMemoryBlobs" (fun () ->
                let envd, _ = fakeEnvd (ControlledShell()).Shell
                (connect envd).Client.Blobs)
        ]

/// Reads an attachment until `count` bytes of a stream arrived; returns them and the next offset.
let private collect (attachment: Attachment) (stream: OutputStream) (from: int64) (count: int) : string * int64 =
    let sb = StringBuilder()
    let mutable next = from

    while sb.Length < count do
        match wait (attachment.Next none) with
        | Some(OutputRecord.Data(s, offset, bytes)) when s = stream ->
            Expect.equal offset next "in sequence"
            sb.Append(text bytes) |> ignore
            next <- offset + int64 bytes.Length
        | Some(OutputRecord.Data _) -> ()
        | Some(OutputRecord.Gap _ as gap) -> failtest $"unexpected {gap}"
        | None -> failtest "the output ended"

    sb.ToString(), next

let private startSpec =
    {
        Program = Exec("prog", [])
        Cwd = None
        Env = Inherit []
        Terminal = None
    }

[<Tests>]
let processes =
    testList
        "Processes through the protocol"
        [
            test "a proxied process streams sequenced output and returns its exit code" {
                let shell = ControlledShell()
                let envd, _ = fakeEnvd shell.Shell
                let client = (connect envd).Client

                match
                    wait (
                        client.Shell.Spawn
                            {
                                Program = Exec("prog", [])
                                Cwd = None
                                Env = Inherit []
                            }
                            none
                    )
                with
                | Error e -> failtest (string e)
                | Ok proc ->
                    let child = shell.Spawned
                    wait (proc.Write "input"B none)
                    child.Emit(Stdout "out 1 "B)
                    child.Emit(Stderr "err"B)
                    child.Emit(Stdout "out 2"B)
                    child.Exit 3
                    let output = wait (Proc.collect proc [||] none)
                    Expect.equal (text output.Stdout) "out 1 out 2" "stdout"
                    Expect.equal (text output.Stderr) "err" "stderr"
                    Expect.equal output.ExitCode 3 "exit code"
                    Expect.equal (text child.Input) "input" "input"
                    // Read to the end and waited for: the proxy releases the process.
                    let mutable released = false

                    for _ in 1..100 do
                        if not released then
                            released <- wait (EnvClient.list client none) |> List.isEmpty
                            Thread.Sleep 20

                    Expect.isTrue released "released"
            }

            test "a host that lost its connection resumes from the offsets it has" {
                let shell = ControlledShell()
                let envd, _ = fakeEnvd shell.Shell
                let first = connect envd
                let started = ok (wait (EnvClient.start first.Client startSpec none))
                let child = shell.Spawned
                let attachment = ok (wait (EnvClient.attach first.Client started.Id 0L 0L none))
                child.Emit(Stdout "before "B)
                let before, next = collect attachment OutputStream.Out 0L 7
                Expect.equal before "before " "read before the cut"
                first.Cut()
                // The process goes on while nobody is connected.
                child.Emit(Stdout "while away"B)
                child.Emit(Stderr "e"B)
                child.Exit 0
                let second = connect envd
                let mutable status = ok (wait (EnvClient.status second.Client started.Id none))

                // The output pump and the exit run on their own: wait until both are seen.
                while not (status.Out.Ended && status.Err.Ended && status.ExitCode.IsSome) do
                    Thread.Sleep 10
                    status <- ok (wait (EnvClient.status second.Client started.Id none))

                Expect.equal status.Out.Total 17L "all output was kept"
                Expect.equal status.ExitCode (Some 0) "exited"
                let resumed = ok (wait (EnvClient.attach second.Client started.Id next 0L none))
                let rest, _ = collect resumed OutputStream.Out next 10
                Expect.equal rest "while away" "resumed exactly where it stopped"
                Expect.equal (ok (wait (EnvClient.wait second.Client started.Id none))) 0 "exit code"
                wait (EnvClient.release second.Client started.Id none)

                Expect.equal
                    (wait (EnvClient.status second.Client started.Id none))
                    (Error(ExecError.UnknownProcess started.Id))
                    "released"
            }

            test "output beyond the retention is a gap without a spill, and complete with one" {
                for spill in [ None; Some(Spill.inMemory "/spill") ] do
                    let shell = ControlledShell()
                    let fs = InMemoryFs(SystemClock.clock, false)
                    let envd = Envd(services shell.Shell fs 8 spill)
                    let client = (connect envd).Client
                    let started = ok (wait (EnvClient.start client startSpec none))
                    let child = shell.Spawned

                    for i in 0..9 do
                        child.Emit(Stdout(Encoding.UTF8.GetBytes(string i + "abc")))

                    child.Exit 0
                    ok (wait (EnvClient.wait client started.Id none)) |> ignore

                    // Everything appended (and evicted) before reading.
                    while not (ok (wait (EnvClient.status client started.Id none))).Out.Ended do
                        Thread.Sleep 10

                    let attachment = ok (wait (EnvClient.attach client started.Id 0L 0L none))
                    let records = ResizeArray()
                    let mutable go = true

                    while go do
                        match wait (attachment.Next none) with
                        | Some record -> records.Add record
                        | None -> go <- false

                    let data =
                        records
                        |> Seq.choose (function
                            | OutputRecord.Data(OutputStream.Out, _, bytes) -> Some(text bytes)
                            | _ -> None)
                        |> String.concat ""

                    match spill with
                    | None ->
                        Expect.equal records[0] (OutputRecord.Gap(OutputStream.Out, 0L, 32L)) "the gap comes first"
                        Expect.equal data "8abc9abc" "the retained tail"
                    | Some _ ->
                        Expect.equal data "0abc1abc2abc3abc4abc5abc6abc7abc8abc9abc" "everything"
                        let status = ok (wait (EnvClient.status client started.Id none))
                        Expect.equal status.Out.SpillPath (Some($"/spill/{started.Id}.out")) "the greppable path"
            }

            test "unknown processes and programs are expected failures" {
                let envd, _ = fakeEnvd (ControlledShell()).Shell
                let client = (connect envd).Client

                Expect.equal
                    (wait (
                        EnvClient.start
                            client
                            { startSpec with
                                Program = Exec("missing", [])
                            }
                            none
                    ))
                    (Error(ExecError.NotStarted("missing", "not found")))
                    "not started"

                Expect.equal (wait (EnvClient.wait client "nope" none)) (Error(ExecError.UnknownProcess "nope")) "wait"

                match wait (EnvClient.attach client "nope" 0L 0L none) with
                | Error(ExecError.UnknownProcess "nope") -> ()
                | other -> failtest $"{other}"

                match wait (client.Terminal.Spawn (Fx.Pty.PtySpec.exec "x" []) none) with
                | Error(Fx.Pty.PtyError.NotStarted _) -> ()
                | other -> failtest $"{other}"

                match wait (client.Snapshots.Take "x" none) with
                | Error(Unavailable _) -> ()
                | other -> failtest $"{other}"
            }
        ]

[<Tests>]
let handshake =
    testList
        "Handshake"
        [
            test "envd announces its platform" {
                let envd, _ = fakeEnvd (ControlledShell()).Shell
                Expect.equal (connect envd).Client.Platform platform "platform"
            }

            test "envd refuses a host that speaks another version" {
                let envd, _ = fakeEnvd (ControlledShell()).Shell

                let client, server =
                    InProcess.pair
                        (64 * 1024)
                        (ConnectionLimits.frames ConnectionLimits.defaults)
                        (ConnectionLimits.frames ConnectionLimits.defaults)

                Eff.run (env ()) (Rpc.accept (EnvdServer.service envd) server) |> ignore

                let service = Service.create EnvClient.peerInfo []

                match
                    wait (
                        Eff.run (env ()) (Rpc.connect service [ EnvProtocol.Members.Protocol, Json.String "0" ] client)
                    )
                with
                | Error(ConnectError.Refused e) -> Expect.equal e.Code Protocol.ErrorCode.UnsupportedVersion "code"
                | other -> failtest $"{other}"
            }
        ]
