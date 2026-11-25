namespace Fx.Envd

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Pty
open Fx.Rpc
open Fx.Store

/// A call over the environment protocol failed for a reason other than the method's typed errors:
/// the connection was lost, envd crashed in the handler, or the peer broke the protocol. A defect
/// for the capability that made the call.
type EnvCallException(methodName: string, reason: string) =
    inherit Exception($"{methodName} failed: {reason}")
    member _.Method = methodName
    member _.Reason = reason

/// Reads the output records of an attachment (`exec/attach`): one at a time, in order. Every record
/// carries its stream and offset, so a reader that lost its connection knows where to resume.
[<Sealed>]
type Attachment internal (channel: Channel) =
    let mutable buffer: byte array = Array.zeroCreate 4096
    let mutable start = 0
    let mutable count = 0

    let append (data: ReadOnlySpan<byte>) =
        if start + count + data.Length > buffer.Length then
            if count + data.Length > buffer.Length then
                let larger = Array.zeroCreate<byte>(max (buffer.Length * 2) (count + data.Length))
                Buffer.BlockCopy(buffer, start, larger, 0, count)
                buffer <- larger
            else
                Buffer.BlockCopy(buffer, start, buffer, 0, count)

            start <- 0

        data.CopyTo(Span(buffer, start + count, data.Length))
        count <- count + data.Length

    /// The next record; `None` once both streams have ended and everything was read.
    member _.Next(cancellation: CancellationToken) : Task<OutputRecord option> =
        task {
            let mutable result = None
            let mutable finished = false

            while not finished do
                match OutputRecords.decode buffer start count with
                | OutputRecords.Decoded.Record(record, consumed) ->
                    start <- start + consumed
                    count <- count - consumed
                    result <- Some record
                    finished <- true
                | OutputRecords.Decoded.Invalid reason ->
                    raise (InvalidOperationException("invalid output record: " + reason))
                | OutputRecords.Decoded.Incomplete ->
                    match! channel.Read cancellation with
                    | Ok(Some data) -> append data.Span
                    | Ok None when count = 0 -> finished <- true
                    | Ok None -> raise (InvalidOperationException "the output ended inside a record")
                    | Error e -> raise (TransferFailedException e)

            return result
        }

    /// Stops receiving (the process goes on; attach again to resume).
    member _.Close() : unit =
        channel.Reset(Transfer.Abandoned, "detached")

    override _.ToString() = $"Attachment({channel})"

/// Watch batches the host received for one watch, waiting for `Watcher.Next`.
[<Sealed>]
type internal WatchInbox(capacity: int) =
    let gate = obj ()
    let batches = Queue<WatchBatch>()
    let mutable lost = false
    let mutable closed = false
    let mutable waiter: TaskCompletionSource<unit> option = None

    let wake () =
        match waiter with
        | Some w ->
            waiter <- None
            w.TrySetResult() |> ignore
        | None -> ()

    member _.Deliver(batch: WatchBatch, isClosed: bool) =
        lock gate (fun () ->
            if not closed then
                if not batch.Changes.IsEmpty || batch.Overflow then
                    if batches.Count >= capacity then
                        lost <- true
                    else
                        batches.Enqueue batch

                if isClosed then
                    closed <- true

                wake ())

    member _.Next(cancellation: CancellationToken) : Task<WatchBatch option> =
        task {
            let mutable result = None
            let mutable finished = false

            while not finished do
                let step =
                    lock gate (fun () ->
                        if batches.Count > 0 then
                            let batch = batches.Dequeue()
                            let overflow = batch.Overflow || lost
                            lost <- false
                            Choice1Of3 { batch with Overflow = overflow }
                        elif lost then
                            lost <- false
                            Choice1Of3 { Changes = []; Overflow = true }
                        elif closed then
                            Choice2Of3()
                        else
                            let w =
                                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                            waiter <- Some w
                            Choice3Of3 w.Task)

                match step with
                | Choice1Of3 batch ->
                    result <- Some batch
                    finished <- true
                | Choice2Of3() -> finished <- true
                | Choice3Of3 wait -> do! wait.WaitAsync cancellation

            return result
        }

    member this.Close() =
        this.Deliver({ Changes = []; Overflow = false }, true)

    override _.ToString() = "WatchInbox"

/// Routes `watch/changed` notifications to the watches of one connection.
[<Sealed>]
type internal WatchRouter() =
    let gate = obj ()
    let inboxes = Dictionary<string, WatchInbox>(StringComparer.Ordinal)
    let mutable next = 0L

    member _.Register() : string * WatchInbox =
        let id = "w" + string (Interlocked.Increment &next)
        let inbox = WatchInbox 1024
        lock gate (fun () -> inboxes[id] <- inbox)
        id, inbox

    member _.Remove(id: string) =
        let inbox =
            lock gate (fun () ->
                match inboxes.TryGetValue id with
                | true, inbox ->
                    inboxes.Remove id |> ignore
                    Some inbox
                | false, _ -> None)

        inbox |> Option.iter (fun i -> i.Close())

    member _.Deliver(id: string, batch: WatchBatch, closed: bool) =
        let inbox =
            lock gate (fun () ->
                match inboxes.TryGetValue id with
                | true, inbox -> Some inbox
                | false, _ -> None)

        inbox |> Option.iter (fun i -> i.Deliver(batch, closed))

    /// The connection ended: every watch ends.
    member _.CloseAll() =
        let all =
            lock gate (fun () ->
                let all = List.ofSeq inboxes.Values
                inboxes.Clear()
                all)

        for inbox in all do
            inbox.Close()

    override _.ToString() = "WatchRouter"

/// The host's side of an environment: the capabilities of one envd connection as proxies (spec §3).
/// Host code uses the same capability records whether the environment is in this process (embedded
/// mode) or remote; these pass the same conformance suites as the interpreters envd runs.
type EnvClient =
    {
        Peer: Peer
        Platform: Platform
        FsRead: FsRead
        Fs: Fs
        FsOps: FsOps
        Shell: Shell
        Terminal: Terminal
        Blobs: Blobs
        Snapshots: Snapshots
    }

    override this.ToString() = $"EnvClient({this.Platform})"

module EnvClient =
    let private call
        (peer: Peer)
        (m: Method<'P, 'T, 'E>)
        (value: 'P)
        (cancellation: CancellationToken)
        : Task<Result<'T, 'E>> =
        task {
            match! CallEnv.run cancellation (Rpc.call peer m value) with
            | Ok result -> return Ok result
            | Error(CallError.Failed e) -> return Error e
            | Error other -> return raise (EnvCallException(m.Name, string other))
        }

    /// A call whose typed errors are defects here.
    let private callOrThrow
        (peer: Peer)
        (m: Method<'P, 'T, 'E>)
        (value: 'P)
        (cancellation: CancellationToken)
        : Task<'T> =
        task {
            match! call peer m value cancellation with
            | Ok result -> return result
            | Error e -> return raise (EnvCallException(m.Name, string e))
        }

    /// A call on a process that may be gone already (released): then it does nothing.
    let private onProcess (peer: Peer) (m: Method<'P, unit, ExecError>) (value: 'P) (cancellation: CancellationToken) =
        task {
            match! call peer m value cancellation with
            | Ok()
            | Error(ExecError.UnknownProcess _) -> ()
            | Error e -> raise (EnvCallException(m.Name, string e))
        }

    // ---- Processes ------------------------------------------------------------------------------

    /// Starts a process (`exec/start`).
    let start (client: EnvClient) (spec: StartSpec) (cancellation: CancellationToken) =
        call client.Peer EnvProtocol.execStart spec cancellation

    /// Attaches to a process's output from the given offsets (`exec/attach`): how a host that
    /// reconnected resumes.
    let attach
        (client: EnvClient)
        (id: string)
        (outFrom: int64)
        (errFrom: int64)
        (cancellation: CancellationToken)
        : Task<Result<Attachment, ExecError>> =
        task {
            match! client.Peer.OpenChannel cancellation with
            | Error e -> return raise (TransferFailedException e)
            | Ok channel ->
                let! attached =
                    task {
                        try
                            return!
                                call client.Peer EnvProtocol.execAttach (id, outFrom, errFrom, channel.Id) cancellation
                        with e ->
                            channel.Reset(Transfer.Abandoned, "the call failed")
                            return raise e
                    }

                match attached with
                | Ok() -> return Ok(Attachment channel)
                | Error e ->
                    channel.Reset(Transfer.Abandoned, "the call failed")
                    return Error e
        }

    let status (client: EnvClient) (id: string) (cancellation: CancellationToken) =
        call client.Peer EnvProtocol.execStatus id cancellation

    let list (client: EnvClient) (cancellation: CancellationToken) =
        callOrThrow client.Peer EnvProtocol.execList () cancellation

    let wait (client: EnvClient) (id: string) (cancellation: CancellationToken) =
        call client.Peer EnvProtocol.execWait id cancellation

    let kill (client: EnvClient) (id: string) (cancellation: CancellationToken) =
        onProcess client.Peer EnvProtocol.execKill id cancellation

    let release (client: EnvClient) (id: string) (cancellation: CancellationToken) =
        callOrThrow client.Peer EnvProtocol.execRelease id cancellation

    /// Writes input in pieces of at most `EnvProtocol.maxInputChunk` bytes.
    let write (client: EnvClient) (id: string) (data: byte array) (cancellation: CancellationToken) =
        task {
            for chunk in Array.chunkBySize EnvProtocol.maxInputChunk data do
                do! onProcess client.Peer EnvProtocol.execWrite (id, chunk) cancellation
        }

    /// A started process as `Proc` (pipes) or `Pty` (terminal): its output, exit code and release.
    type private Remote(peer: Peer, id: string, attachment: Attachment) =
        let mutable outputEnded = false
        let mutable exitCode: int option = None
        let mutable released = 0

        member _.ReleaseIfDone() =
            if outputEnded && exitCode.IsSome && Interlocked.Exchange(&released, 1) = 0 then
                (callOrThrow peer EnvProtocol.execRelease id CancellationToken.None)
                    .ContinueWith(fun (t: Task<unit>) -> t.Exception |> ignore)
                |> ignore

        /// The next data, skipping gaps; `None` at the end.
        member this.Next(cancellation: CancellationToken) : Task<(OutputStream * byte array) option> =
            task {
                let mutable result = None
                let mutable finished = outputEnded

                while not finished do
                    match! attachment.Next cancellation with
                    | Some(OutputRecord.Data(stream, _, bytes)) ->
                        result <- Some(stream, bytes)
                        finished <- true
                    | Some(OutputRecord.Gap _) -> ()
                    | None ->
                        outputEnded <- true
                        finished <- true
                        this.ReleaseIfDone()

                return result
            }

        member this.Wait(cancellation: CancellationToken) : Task<int> =
            task {
                match exitCode with
                | Some code -> return code
                | None ->
                    match! call peer EnvProtocol.execWait id cancellation with
                    | Ok code ->
                        exitCode <- Some code
                        this.ReleaseIfDone()
                        return code
                    | Error e -> return raise (EnvCallException(EnvProtocol.execWait.Name, string e))
            }

        member _.Write (data: byte array) (cancellation: CancellationToken) : Task<unit> =
            task {
                for chunk in Array.chunkBySize EnvProtocol.maxInputChunk data do
                    do! onProcess peer EnvProtocol.execWrite (id, chunk) cancellation
            }

        member _.Kill() : Task<unit> =
            if exitCode.IsSome then
                Task.FromResult()
            else
                onProcess peer EnvProtocol.execKill id CancellationToken.None

        /// Ends it for good: kills it, waits for its exit, and releases it.
        member this.Close() : Task<unit> =
            task {
                outputEnded <- true
                attachment.Close()

                if Interlocked.CompareExchange(&released, 0, 0) = 0 then
                    do! this.Kill()
                    let! _ = this.Wait CancellationToken.None

                    if Interlocked.Exchange(&released, 1) = 0 then
                        do! callOrThrow peer EnvProtocol.execRelease id CancellationToken.None
            }

        override _.ToString() = $"remote process {id}"

    let private spawn (peer: Peer) (spec: StartSpec) (cancellation: CancellationToken) =
        task {
            match! call peer EnvProtocol.execStart spec cancellation with
            | Error e -> return Error e
            | Ok started ->
                match! peer.OpenChannel cancellation with
                | Error e -> return raise (TransferFailedException e)
                | Ok channel ->
                    match! call peer EnvProtocol.execAttach (started.Id, 0L, 0L, channel.Id) cancellation with
                    | Error e ->
                        channel.Reset(Transfer.Abandoned, "the call failed")
                        return raise (EnvCallException(EnvProtocol.execAttach.Name, string e))
                    | Ok() -> return Ok(started, Remote(peer, started.Id, Attachment channel))
        }

    let private shell (peer: Peer) : Shell =
        let spawnProc (spec: ProcSpec) (cancellation: CancellationToken) : Task<Result<Proc, ShellError>> =
            task {
                let start =
                    {
                        Program = spec.Program
                        Cwd = spec.Cwd
                        Env = spec.Env
                        Terminal = None
                    }

                match! spawn peer start cancellation with
                | Error(ExecError.NotStarted(program, reason)) -> return Error(ShellError.NotStarted(program, reason))
                | Error e -> return raise (EnvCallException(EnvProtocol.execStart.Name, string e))
                | Ok(started, remote) ->
                    return
                        Ok
                            {
                                Pid = started.Pid
                                Write = remote.Write
                                CloseStdin =
                                    fun () ->
                                        onProcess peer EnvProtocol.execCloseStdin started.Id CancellationToken.None
                                Read =
                                    fun cancellation ->
                                        task {
                                            match! remote.Next cancellation with
                                            | Some(OutputStream.Out, bytes) -> return Some(Stdout bytes)
                                            | Some(OutputStream.Err, bytes) -> return Some(Stderr bytes)
                                            | None -> return None
                                        }
                                Wait = remote.Wait
                                Kill = remote.Kill
                            }
            }

        {
            Run =
                fun command cancellation ->
                    task {
                        cancellation.ThrowIfCancellationRequested()

                        match! spawnProc command.Spec cancellation with
                        | Error e -> return Error e
                        | Ok proc ->
                            let! output = Proc.collect proc command.Stdin cancellation
                            return Ok output
                    }
            Spawn = spawnProc
        }

    let private terminal (peer: Peer) : Terminal =
        {
            Spawn =
                fun spec cancellation ->
                    task {
                        let start =
                            {
                                Program = spec.Program
                                Cwd = spec.Cwd
                                Env = spec.Env
                                Terminal = Some spec.Size
                            }

                        match! spawn peer start cancellation with
                        | Error(ExecError.NotStarted(program, reason)) ->
                            return Error(PtyError.NotStarted(program, reason))
                        | Error(ExecError.InvalidSize size) -> return Error(PtyError.InvalidSize size)
                        | Error e -> return raise (EnvCallException(EnvProtocol.execStart.Name, string e))
                        | Ok(started, remote) ->
                            return
                                Ok
                                    {
                                        Pid = started.Pid
                                        Write = remote.Write
                                        Read =
                                            fun cancellation ->
                                                task {
                                                    match! remote.Next cancellation with
                                                    | Some(_, bytes) -> return Some bytes
                                                    | None -> return None
                                                }
                                        Resize =
                                            fun size ->
                                                task {
                                                    match!
                                                        call
                                                            peer
                                                            EnvProtocol.execResize
                                                            (started.Id, size)
                                                            CancellationToken.None
                                                    with
                                                    | Ok()
                                                    | Error(ExecError.UnknownProcess _)
                                                    | Error(ExecError.InvalidSize _) -> ()
                                                    | Error e ->
                                                        raise (EnvCallException(EnvProtocol.execResize.Name, string e))
                                                }
                                        Wait = remote.Wait
                                        Kill = remote.Kill
                                        Close = remote.Close
                                    }
                    }
        }

    // ---- Files ----------------------------------------------------------------------------------

    let private readRange
        (peer: Peer)
        (path: string)
        (offset: int64)
        (count: int option)
        (cancellation: CancellationToken)
        =
        task {
            let! outcome =
                Transfer.download
                    peer
                    (fun channel -> call peer EnvProtocol.fsRead (path, offset, count, channel) cancellation)
                    cancellation

            return
                outcome
                |> Result.map (fun (reply: ReadReply, content) ->
                    {
                        Offset = reply.Offset
                        Content = content
                        FileSize = reply.FileSize
                    })
        }

    let private writeFile
        (peer: Peer)
        (atomic: bool)
        (path: string)
        (content: byte array)
        (cancellation: CancellationToken)
        =
        Transfer.upload
            peer
            content
            (fun channel -> call peer EnvProtocol.fsWrite (path, channel, atomic) cancellation)
            cancellation

    let private stat (peer: Peer) (path: string) (cancellation: CancellationToken) : Task<Result<Entry, FsError>> =
        task {
            match! callOrThrow peer EnvProtocol.fsStat [ path ] cancellation with
            | [ result ] -> return result
            | results ->
                return raise (EnvCallException(EnvProtocol.fsStat.Name, $"{results.Length} results for one path"))
        }

    let private fsRead (peer: Peer) : FsRead =
        {
            ReadFile =
                fun path cancellation ->
                    task {
                        let! chunk = readRange peer path 0L None cancellation
                        return chunk |> Result.map (fun c -> c.Content)
                    }
            Stat = stat peer
            List = fun path cancellation -> call peer EnvProtocol.fsList path cancellation
        }

    let private fs (peer: Peer) : Fs =
        {
            WriteFile = writeFile peer false
            CreateDirectory = fun path cancellation -> call peer EnvProtocol.fsCreateDirectory path cancellation
            Delete = fun path cancellation -> call peer EnvProtocol.fsDelete path cancellation
            Move =
                fun source destination cancellation -> call peer EnvProtocol.fsMove (source, destination) cancellation
        }

    let private fsOps (peer: Peer) (router: WatchRouter) : FsOps =
        {
            StatMany = fun paths cancellation -> callOrThrow peer EnvProtocol.fsStat paths cancellation
            ReadRange =
                fun path offset count cancellation ->
                    FsOps.checkRange offset count
                    readRange peer path offset (Some count) cancellation
            WriteAtomic = writeFile peer true
            ApplyPatch = fun patches cancellation -> call peer EnvProtocol.fsPatch patches cancellation
            Search = fun query cancellation -> call peer EnvProtocol.fsSearch query cancellation
            Watch =
                fun spec cancellation ->
                    task {
                        let id, inbox = router.Register()

                        let! started =
                            task {
                                try
                                    return! call peer EnvProtocol.watchStart (id, spec) cancellation
                                with e ->
                                    router.Remove id
                                    return raise e
                            }

                        match started with
                        | Error e ->
                            router.Remove id
                            return Error e
                        | Ok() ->
                            return
                                Ok
                                    {
                                        Next = inbox.Next
                                        Close =
                                            fun () ->
                                                task {
                                                    router.Remove id

                                                    try
                                                        do!
                                                            callOrThrow
                                                                peer
                                                                EnvProtocol.watchStop
                                                                id
                                                                CancellationToken.None
                                                    with :? EnvCallException ->
                                                        // The connection is gone, and the watch with it.
                                                        ()
                                                }
                                    }
                    }
        }

    // ---- Blobs and snapshots --------------------------------------------------------------------

    let private blobs (peer: Peer) : Blobs =
        let get (hash: BlobHash) (range: (int64 * int) option) (cancellation: CancellationToken) =
            task {
                let! outcome =
                    Transfer.download
                        peer
                        (fun channel -> call peer EnvProtocol.blobsGet (hash, range, channel) cancellation)
                        cancellation

                return outcome |> Result.map snd
            }

        {
            Put =
                fun content cancellation ->
                    task {
                        cancellation.ThrowIfCancellationRequested()

                        match!
                            Transfer.upload
                                peer
                                content
                                (fun channel -> call peer EnvProtocol.blobsPut channel cancellation)
                                cancellation
                        with
                        | Ok blob -> return blob
                        | Error(none: NoError) ->
                            return raise (EnvCallException(EnvProtocol.blobsPut.Name, string none))
                    }
            Get =
                fun hash cancellation ->
                    task {
                        cancellation.ThrowIfCancellationRequested()
                        return! get hash None cancellation
                    }
            Read =
                fun hash offset count cancellation ->
                    Blobs.checkRange offset count
                    get hash (Some(offset, count)) cancellation
            Stat = fun hash cancellation -> callOrThrow peer EnvProtocol.blobsStat hash cancellation
            Create =
                fun cancellation ->
                    task {
                        cancellation.ThrowIfCancellationRequested()

                        match! peer.OpenChannel cancellation with
                        | Error e -> return raise (TransferFailedException e)
                        | Ok channel ->
                            let pending = call peer EnvProtocol.blobsPut channel.Id CancellationToken.None
                            let mutable finished = false

                            let check () =
                                if finished then
                                    raise (ObjectDisposedException(nameof BlobWriter))

                            return
                                {
                                    Write =
                                        fun chunk cancellation ->
                                            task {
                                                check ()

                                                match! channel.Write(ReadOnlyMemory chunk, cancellation) with
                                                | Ok() -> ()
                                                | Error e -> raise (TransferFailedException e)
                                            }
                                    Commit =
                                        fun cancellation ->
                                            task {
                                                check ()
                                                finished <- true

                                                match! channel.CloseOutput cancellation with
                                                | Error e -> return raise (TransferFailedException e)
                                                | Ok() ->
                                                    match! pending with
                                                    | Ok blob -> return blob
                                                    | Error(none: NoError) ->
                                                        return
                                                            raise (
                                                                EnvCallException(EnvProtocol.blobsPut.Name, string none)
                                                            )
                                            }
                                    Abort =
                                        fun () ->
                                            if not finished then
                                                finished <- true
                                                channel.Reset(Transfer.Abandoned, "aborted")

                                                pending.ContinueWith(fun (t: Task<Result<BlobRef, NoError>>) ->
                                                    t.Exception |> ignore)
                                                |> ignore

                                            Task.FromResult()
                                }
                    }
        }

    let private snapshots (peer: Peer) : Snapshots =
        {
            Take = fun message cancellation -> call peer EnvProtocol.snapshotTake message cancellation
            Diff = fun from until cancellation -> call peer EnvProtocol.snapshotDiff (from, until) cancellation
            Patch =
                fun from until paths cancellation ->
                    task {
                        let! outcome =
                            Transfer.download
                                peer
                                (fun channel ->
                                    call peer EnvProtocol.snapshotPatch (from, until, paths, channel) cancellation)
                                cancellation

                        return outcome |> Result.map (fun (_, bytes) -> UTF8Encoding(false).GetString bytes)
                    }
            Restore =
                fun snapshot paths cancellation -> call peer EnvProtocol.snapshotRestore (snapshot, paths) cancellation
        }

    // ---- Connecting -----------------------------------------------------------------------------

    /// Who the host is in `initialize`.
    let peerInfo: PeerInfo =
        {
            Name = Product.binaryName + "-host"
            Version = Product.Version
        }

    let private ofPeer (peer: Peer) (platform: Platform) (router: WatchRouter) : EnvClient =
        peer.Completion.ContinueWith(fun (_: Task<CloseReason>) -> router.CloseAll())
        |> ignore

        {
            Peer = peer
            Platform = platform
            FsRead = fsRead peer
            Fs = fs peer
            FsOps = fsOps peer router
            Shell = shell peer
            Terminal = terminal peer
            Blobs = blobs peer
            Snapshots = snapshots peer
        }

    /// Connects to envd over a transport: the handshake checks the protocol version and reads envd's
    /// platform. The connection lives in the caller's scope (cancelling it closes the connection).
    let connect<'R when 'R :> IClock and 'R :> IScopeWith<'R>>
        (transport: Transport)
        : Eff<'R, Result<EnvClient, ConnectError>> =
        fun env ->
            task {
                let router = WatchRouter()

                let service =
                    Service.create
                        peerInfo
                        [
                            Handler.notification EnvProtocol.watchChanged (fun _ (watch, batch, closed) ->
                                fun _ ->
                                    router.Deliver(watch, batch, closed)
                                    Task.FromResult())
                        ]

                let members = [ EnvProtocol.Members.Protocol, Json.String EnvProtocol.Version ]

                match! Rpc.connect service members transport env with
                | Error e -> return Error e
                | Ok peer ->
                    match Json.tryField EnvProtocol.Members.Platform peer.Remote.Members with
                    | None ->
                        let! _ = peer.CloseAsync()

                        return
                            Error(
                                ConnectError.InvalidHandshake
                                    [
                                        Issue.at
                                            [ PathSegment.Key EnvProtocol.Members.Platform ]
                                            "envd did not announce its platform"
                                    ]
                            )
                    | Some json ->
                        match Codec.decode EnvCodecs.platform json with
                        | Error issues ->
                            let! _ = peer.CloseAsync()

                            return
                                Error(
                                    ConnectError.InvalidHandshake(
                                        issues |> List.map (Issue.under (PathSegment.Key EnvProtocol.Members.Platform))
                                    )
                                )
                        | Ok platform -> return Ok(ofPeer peer platform router)
            }

    /// Closes the connection (envd's processes go on running; a host can reconnect to them).
    let close (client: EnvClient) : Task<CloseReason> = client.Peer.CloseAsync()
