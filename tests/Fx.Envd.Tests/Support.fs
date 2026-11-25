/// envd over fakes, in process: a controllable shell, services from fakes, and connections.
module Fx.Envd.Tests.Support

open System
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Envd
open Fx.Pty
open Fx.Rpc
open Fx.Store.Fakes

let none = CancellationToken.None

/// Fails a test instead of hanging it.
let wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

let waitUnit (task: Task) =
    task.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

let ok (result: Result<'T, 'E>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failtest $"expected success, got {e}"

/// A process the test drives: it emits what the test says, and exits when told.
type ControlledProc() =
    let output = Channel.CreateUnbounded<OutputChunk>()

    let exit =
        TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

    let input = new MemoryStream()

    member _.Emit(chunk: OutputChunk) = output.Writer.TryWrite chunk |> ignore

    member _.Exit(code: int) =
        output.Writer.TryComplete() |> ignore
        exit.TrySetResult code |> ignore

    member _.Input = lock input (fun () -> input.ToArray())

    member this.Proc: Proc =
        {
            Pid = 4242
            Write =
                fun bytes _ ->
                    lock input (fun () -> input.Write(bytes, 0, bytes.Length))
                    Task.FromResult()
            CloseStdin = fun () -> Task.FromResult()
            Read =
                fun cancellation ->
                    task {
                        let! more = output.Reader.WaitToReadAsync cancellation

                        if more then
                            match output.Reader.TryRead() with
                            | true, chunk -> return Some chunk
                            | false, _ -> return! this.Proc.Read cancellation
                        else
                            return None
                    }
            Wait = fun cancellation -> exit.Task.WaitAsync cancellation
            Kill =
                fun () ->
                    this.Exit 137
                    Task.FromResult()
        }

/// A shell whose spawned processes are `ControlledProc`s the test gets from `Spawned`.
type ControlledShell() =
    let spawned = Channel.CreateUnbounded<ControlledProc>()

    member _.Spawned: ControlledProc = wait (spawned.Reader.ReadAsync(none).AsTask())

    member val Shell: Shell =
        {
            Run = fun _ _ -> invalidOp "ControlledShell does not run commands"
            Spawn =
                fun spec _ ->
                    match spec.Program with
                    | Exec("missing", _) -> Task.FromResult(Error(ShellError.NotStarted("missing", "not found")))
                    | _ ->
                        let proc = ControlledProc()
                        spawned.Writer.TryWrite proc |> ignore
                        Task.FromResult(Ok proc.Proc)
        }

let platform: Platform =
    {
        Os = "test"
        Arch = "x64"
        Libc = None
        Shell = ShellSpec.posix
        PathSeparator = string Path.DirectorySeparatorChar
        CaseSensitive = true
        Workspace = Path.GetTempPath()
        Version = "0.0.0"
        Features = [ Feature.Pty ]
    }

/// envd over fakes: `InMemoryFs`, a shell, no terminals, `InMemoryBlobs`, no snapshots.
let services (shell: Shell) (fs: InMemoryFs) (retain: int) (spill: Spill option) : EnvdServices =
    {
        Platform = platform
        Clock = SystemClock.clock
        FsRead = fs.FsRead
        Fs = fs.Fs
        FsOps = FsOpsOver.create fs.FsRead fs.Fs
        Shell = shell
        Terminal =
            {
                Spawn =
                    fun spec _ -> Task.FromResult(Error(PtyError.NotStarted(string spec.Program, "no terminals here")))
            }
        Blobs = InMemoryBlobs().Blobs
        Snapshots = Snapshot.unavailable "no snapshots here"
        Spill = spill
        Retain = retain
    }

let env () : ConnectionEnv =
    {
        Clock = SystemClock.clock
        Scope = Scope.root
    }

/// A connection to `envd` in process, and a way to cut it as a crash would.
type Connection =
    {
        Client: EnvClient
        /// Cuts the connection abruptly (both ends).
        Cut: unit -> unit
    }

    override _.ToString() = "Connection"

let connect (envd: Envd) : Connection =
    let client, server =
        InProcess.pair
            (256 * 1024)
            (ConnectionLimits.frames ConnectionLimits.defaults)
            (ConnectionLimits.frames ConnectionLimits.defaults)

    task {
        match! Eff.run (env ()) (Rpc.accept (EnvdServer.service envd) server) with
        | Ok peer ->
            let! _ = peer.Completion
            ()
        | Error _ -> ()
    }
    |> ignore

    match wait (Eff.run (env ()) (EnvClient.connect client)) with
    | Ok connected ->
        {
            Client = connected
            Cut =
                fun () ->
                    client.Abort()
                    server.Abort()
        }
    | Error e -> failtest $"connect failed: {e}"
