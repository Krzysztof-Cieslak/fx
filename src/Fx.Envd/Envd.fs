namespace Fx.Envd

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Pty
open Fx.Rpc
open Fx.Store

/// envd's file capabilities: the basic ones and its operations beyond them.
type EnvdFiles =
    {
        FsRead: FsRead
        Fs: Fs
        FsOps: FsOps
    }

    override _.ToString() = "EnvdFiles"

/// What envd serves: the capabilities of the machine it runs on, as interpreters. The composition
/// root builds them (the live ones in Fx.Envd.Live; fakes in tests).
type EnvdServices =
    {
        Platform: Platform
        Clock: Clock
        FsRead: FsRead
        Fs: Fs
        FsOps: FsOps
        /// The file capabilities beneath roots (T113b): a path under one of the roots (absolute) is
        /// resolved beneath the outermost of them without following symbolic links (a link on the way
        /// fails with `InvalidPath`, `Beneath`'s reason); other paths as by `FsRead`, `Fs` and `FsOps`.
        /// Watching is not affected.
        Beneath: string list -> EnvdFiles
        Shell: Shell
        Terminal: Terminal
        Blobs: Blobs
        Snapshots: Snapshots
        /// Where process output overflows to; `None` keeps only the last `Retain` bytes per stream.
        Spill: Spill option
        /// Bytes of each output stream kept in memory.
        Retain: int
    }

    override this.ToString() = $"EnvdServices({this.Platform})"

/// A process envd runs, with its output logs.
type internal Running =
    {
        Id: string
        Pid: int
        IsTerminal: bool
        Write: byte array -> CancellationToken -> Task<unit>
        CloseStdin: unit -> Task<unit>
        Resize: PtySize -> Task<unit>
        Kill: unit -> Task<unit>
        /// Ends it for good: kills it and releases its terminal.
        Close: unit -> Task<unit>
        Exit: Task<int>
        Out: OutputLog
        Err: OutputLog
    }

    override this.ToString() = $"process {this.Id} (pid {this.Pid})"

/// What envd keeps per connection: watches end with their connection, processes do not.
type internal Connection =
    {
        Lifetime: CancellationTokenSource
        Watches: Dictionary<string, Watcher>
    }

    override _.ToString() = "Connection"

/// The state of envd (spec §3 "hands"): the processes it runs, which outlive connections (a host
/// that reconnects re-attaches to them and resumes their output), and the watches of each
/// connection. One `Envd` serves any number of connections, one after another or at once.
[<Sealed>]
type Envd(services: EnvdServices) =
    let gate = obj ()
    let processes = Dictionary<string, Running>(StringComparer.Ordinal)
    let connections = Dictionary<Peer, Connection>(HashIdentity.Reference)
    let lifetime = new CancellationTokenSource()
    let mutable nextId = 0L
    let mutable disposed = false

    /// Bytes of output per attachment record.
    let recordPayload = 64 * 1024 - OutputRecords.dataHeaderSize

    let find (id: string) : Running option =
        lock gate (fun () ->
            match processes.TryGetValue id with
            | true, running -> Some running
            | false, _ -> None)

    let pumpPipes (proc: Proc) (out: OutputLog) (err: OutputLog) : Task =
        task {
            try
                try
                    let mutable go = true

                    while go do
                        match! proc.Read lifetime.Token with
                        | Some(Stdout bytes) -> do! out.Append(bytes, lifetime.Token)
                        | Some(Stderr bytes) -> do! err.Append(bytes, lifetime.Token)
                        | None -> go <- false
                with _ ->
                    // Released (its logs ended) or envd is stopping.
                    ()
            finally
                out.Finish()
                err.Finish()
        }

    let pumpTerminal (pty: Pty) (out: OutputLog) (err: OutputLog) : Task =
        err.Finish()

        task {
            try
                try
                    let mutable go = true

                    while go do
                        match! pty.Read lifetime.Token with
                        | Some bytes -> do! out.Append(bytes, lifetime.Token)
                        | None -> go <- false
                with _ ->
                    ()
            finally
                out.Finish()
        }

    let logs (id: string) =
        new OutputLog(id + ".out", services.Retain, services.Spill),
        new OutputLog(id + ".err", services.Retain, services.Spill)

    let register (running: Running) =
        let refused =
            lock gate (fun () ->
                if disposed then
                    true
                else
                    processes[running.Id] <- running
                    false)

        if refused then
            running.Close() |> ignore
            raise (ObjectDisposedException(nameof Envd))

    let statusOf (running: Running) : ProcessStatus =
        {
            Id = running.Id
            Pid = running.Pid
            Terminal = running.IsTerminal
            ExitCode =
                if running.Exit.IsCompletedSuccessfully then
                    Some running.Exit.Result
                else
                    None
            Out = running.Out.Status
            Err = running.Err.Status
        }

    let closeConnection (peer: Peer) : Task =
        let watches =
            lock gate (fun () ->
                match connections.TryGetValue peer with
                | true, connection ->
                    connections.Remove peer |> ignore
                    connection.Lifetime.Cancel()
                    List.ofSeq connection.Watches.Values
                | false, _ -> [])

        task {
            for watcher in watches do
                try
                    do! watcher.Close()
                with _ ->
                    ()
        }

    let connectionOf (peer: Peer) : Connection =
        let connection, isNew =
            lock gate (fun () ->
                match connections.TryGetValue peer with
                | true, connection -> connection, false
                | false, _ ->
                    let connection =
                        {
                            Lifetime = CancellationTokenSource.CreateLinkedTokenSource lifetime.Token
                            Watches = Dictionary<string, Watcher>(StringComparer.Ordinal)
                        }

                    connections[peer] <- connection
                    connection, true)

        if isNew then
            peer.Completion.ContinueWith(fun (_: Task<CloseReason>) -> closeConnection peer)
            |> ignore

        connection

    member _.Services = services
    member _.Platform = services.Platform

    /// Starts a process on pipes or on a terminal.
    member _.Start(spec: StartSpec, cancellation: CancellationToken) : Task<Result<Started, ExecError>> =
        let id = string (Interlocked.Increment &nextId)

        task {
            match spec.Terminal with
            | None ->
                let! spawned =
                    services.Shell.Spawn
                        {
                            Program = spec.Program
                            Cwd = spec.Cwd
                            Env = spec.Env
                        }
                        cancellation

                match spawned with
                | Error(ShellError.NotStarted(program, reason)) -> return Error(ExecError.NotStarted(program, reason))
                | Ok proc ->
                    let out, err = logs id

                    let running =
                        {
                            Id = id
                            Pid = proc.Pid
                            IsTerminal = false
                            Write = proc.Write
                            CloseStdin = proc.CloseStdin
                            Resize = fun _ -> Task.FromResult()
                            Kill = proc.Kill
                            Close = proc.Kill
                            Exit = proc.Wait CancellationToken.None
                            Out = out
                            Err = err
                        }

                    register running
                    pumpPipes proc out err |> ignore
                    return Ok { Id = id; Pid = proc.Pid }
            | Some size ->
                let! spawned =
                    services.Terminal.Spawn
                        {
                            Program = spec.Program
                            Cwd = spec.Cwd
                            Env = spec.Env
                            Size = size
                        }
                        cancellation

                match spawned with
                | Error(PtyError.NotStarted(program, reason)) -> return Error(ExecError.NotStarted(program, reason))
                | Error(PtyError.InvalidSize size) -> return Error(ExecError.InvalidSize size)
                | Ok pty ->
                    let out, err = logs id

                    let running =
                        {
                            Id = id
                            Pid = pty.Pid
                            IsTerminal = true
                            Write = pty.Write
                            CloseStdin = fun () -> Task.FromResult()
                            Resize = pty.Resize
                            Kill = pty.Kill
                            Close = pty.Close
                            Exit = pty.Wait CancellationToken.None
                            Out = out
                            Err = err
                        }

                    register running
                    pumpTerminal pty out err |> ignore
                    return Ok { Id = id; Pid = pty.Pid }
        }

    /// Streams a process's output from the given offsets over a channel, in the background, until
    /// both streams have ended (then it closes the channel's output), the channel fails, or the
    /// connection ends.
    member _.Attach
        (peer: Peer, id: string, outFrom: int64, errFrom: int64, channel: Channel)
        : Result<unit, ExecError> =
        match find id with
        | None ->
            channel.Reset(ResetCode.Cancelled, "no such process")
            Error(ExecError.UnknownProcess id)
        | Some running ->
            let connection = connectionOf peer
            let token = connection.Lifetime.Token

            let pump (log: OutputLog) (stream: OutputStream) (from: int64) : Task<bool> =
                task {
                    let mutable offset = from
                    let mutable outcome = None

                    while outcome.IsNone do
                        let! read = log.Read(offset, recordPayload, token)

                        let record =
                            match read with
                            | LogRead.Data(at, bytes) ->
                                offset <- at + int64 bytes.Length
                                Some(OutputRecord.Data(stream, at, bytes))
                            | LogRead.Gap(from, until) ->
                                offset <- until
                                Some(OutputRecord.Gap(stream, from, until))
                            | LogRead.End _ -> None

                        match record with
                        | None -> outcome <- Some true
                        | Some record ->
                            match! channel.Write(ReadOnlyMemory(OutputRecords.encode record), token) with
                            | Ok() -> ()
                            | Error _ -> outcome <- Some false

                    return outcome.Value
                }

            task {
                try
                    let! completed =
                        Task.WhenAll(
                            pump running.Out OutputStream.Out outFrom,
                            pump running.Err OutputStream.Err errFrom
                        )

                    if Array.forall (fun ok -> ok) completed then
                        let! _ = channel.CloseOutput token
                        ()
                with _ ->
                    channel.Reset(ResetCode.Cancelled, "the attachment ended")
            }
            |> ignore

            Ok()

    member _.Write(id: string, data: byte array, cancellation: CancellationToken) : Task<Result<unit, ExecError>> =
        match find id with
        | None -> Task.FromResult(Error(ExecError.UnknownProcess id))
        | Some running ->
            task {
                do! running.Write data cancellation
                return Ok()
            }

    member _.CloseStdin(id: string) : Task<Result<unit, ExecError>> =
        match find id with
        | None -> Task.FromResult(Error(ExecError.UnknownProcess id))
        | Some running ->
            task {
                do! running.CloseStdin()
                return Ok()
            }

    member _.Resize(id: string, size: PtySize) : Task<Result<unit, ExecError>> =
        match find id with
        | None -> Task.FromResult(Error(ExecError.UnknownProcess id))
        | Some _ when not (PtySize.isValid size) -> Task.FromResult(Error(ExecError.InvalidSize size))
        | Some running ->
            task {
                do! running.Resize size
                return Ok()
            }

    member _.Kill(id: string) : Task<Result<unit, ExecError>> =
        match find id with
        | None -> Task.FromResult(Error(ExecError.UnknownProcess id))
        | Some running ->
            task {
                do! running.Kill()
                return Ok()
            }

    member _.Wait(id: string, cancellation: CancellationToken) : Task<Result<int, ExecError>> =
        match find id with
        | None -> Task.FromResult(Error(ExecError.UnknownProcess id))
        | Some running ->
            task {
                let! code = running.Exit.WaitAsync cancellation
                return Ok code
            }

    member _.Status(id: string) : Result<ProcessStatus, ExecError> =
        match find id with
        | None -> Error(ExecError.UnknownProcess id)
        | Some running -> Ok(statusOf running)

    member _.List() : ProcessStatus list =
        lock gate (fun () -> List.ofSeq processes.Values)
        |> List.sortBy (fun r -> r.Id.Length, r.Id)
        |> List.map statusOf

    /// Forgets a process: kills it if it runs, drops its output. Idempotent.
    member _.Release(id: string) : Task<unit> =
        let running =
            lock gate (fun () ->
                match processes.TryGetValue id with
                | true, running ->
                    processes.Remove id |> ignore
                    Some running
                | false, _ -> None)

        match running with
        | None -> Task.FromResult()
        | Some running ->
            task {
                try
                    do! running.Close()
                with _ ->
                    ()

                do! running.Out.Dispose()
                do! running.Err.Dispose()
            }

    /// Starts a watch for a connection; its changes go to the peer as `watch/changed`.
    member _.StartWatch
        (peer: Peer, watch: string, spec: WatchSpec, cancellation: CancellationToken)
        : Task<Result<unit, FsError>> =
        let connection = connectionOf peer

        if lock gate (fun () -> connection.Watches.ContainsKey watch) then
            Task.FromResult(Error(InvalidPath(spec.Root, $"watch {watch} is already in use")))
        else
            task {
                match! services.FsOps.Watch spec cancellation with
                | Error e -> return Error e
                | Ok watcher ->
                    lock gate (fun () -> connection.Watches[watch] <- watcher)
                    let token = connection.Lifetime.Token

                    task {
                        try
                            let mutable go = true

                            while go do
                                match! watcher.Next token with
                                | Some batch ->
                                    let! _ =
                                        CallEnv.run
                                            token
                                            (Rpc.notify peer EnvProtocol.watchChanged (watch, batch, false))

                                    ()
                                | None ->
                                    go <- false

                                    let! _ =
                                        CallEnv.run
                                            token
                                            (Rpc.notify
                                                peer
                                                EnvProtocol.watchChanged
                                                (watch, { Changes = []; Overflow = false }, true))

                                    ()
                        with _ ->
                            ()

                        lock gate (fun () -> connection.Watches.Remove watch |> ignore)
                        do! watcher.Close()
                    }
                    |> ignore

                    return Ok()
            }

    member _.StopWatch(peer: Peer, watch: string) : Task<unit> =
        let watcher =
            lock gate (fun () ->
                match connections.TryGetValue peer with
                | true, connection ->
                    match connection.Watches.TryGetValue watch with
                    | true, watcher ->
                        connection.Watches.Remove watch |> ignore
                        Some watcher
                    | false, _ -> None
                | false, _ -> None)

        match watcher with
        | Some watcher -> watcher.Close()
        | None -> Task.FromResult()

    /// Stops everything: kills every process, ends every watch.
    member this.DisposeAsync() : Task =
        let ids, peers =
            lock gate (fun () ->
                disposed <- true
                List.ofSeq processes.Keys, List.ofSeq connections.Keys)

        task {
            for id in ids do
                do! this.Release id

            for peer in peers do
                do! closeConnection peer

            lifetime.Cancel()
        }

    override _.ToString() = $"Envd({services.Platform})"
