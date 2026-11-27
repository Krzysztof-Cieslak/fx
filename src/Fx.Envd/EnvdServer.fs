namespace Fx.Envd

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Rpc
open Fx.Store

/// envd's side of the environment protocol: the handlers over an `Envd`, and the service a transport
/// is accepted with (`Rpc.accept (EnvdServer.service envd) transport`).
module EnvdServer =
    let private run<'R, 'T when 'R :> IScope> (work: CancellationToken -> Task<'T>) : Eff<'R, 'T> =
        fun env -> work env.Scope.Cancellation

    let private ok<'R, 'T, 'E when 'R :> IScope> (work: CancellationToken -> Task<'T>) : Eff<'R, Result<'T, 'E>> =
        fun env ->
            task {
                let! value = work env.Scope.Cancellation
                return Ok value
            }

    /// Sends content and ends the channel; a failed transfer is a defect.
    let private send (channel: Channel) (content: byte array) (cancellation: CancellationToken) : Task<unit> =
        task {
            match! Transfer.writeAll channel content cancellation with
            | Ok() -> ()
            | Error e -> raise (TransferFailedException e)
        }

    let private fail (channel: Channel) (error: 'E) : Result<'T, 'E> =
        channel.Reset(ResetCode.Cancelled, "the request failed")
        Error error

    let private fsHandlers<'R when 'R :> IScope> (envd: Envd) : Handler<'R> list =
        let s = envd.Services

        [
            Handler.request EnvProtocol.fsStat (fun _ paths -> ok (fun ct -> s.FsOps.StatMany paths ct))
            Handler.request EnvProtocol.fsList (fun _ path -> run (s.FsRead.List path))
            Handler.request EnvProtocol.fsRead (fun context (path, offset, count, channelId) ->
                run (fun ct ->
                    task {
                        let! channel = Transfer.acceptChannel context.Peer channelId Flow.Outbound ct

                        let! chunk =
                            match count with
                            | Some count -> s.FsOps.ReadRange path offset count ct
                            | None ->
                                task {
                                    match! s.FsRead.ReadFile path ct with
                                    | Error e -> return Error e
                                    | Ok content ->
                                        let start = int (min offset content.LongLength)

                                        return
                                            Ok
                                                {
                                                    Offset = offset
                                                    Content = Array.sub content start (content.Length - start)
                                                    FileSize = content.LongLength
                                                }
                                }

                        match chunk with
                        | Error e -> return fail channel e
                        | Ok chunk ->
                            do! send channel chunk.Content ct

                            return
                                Ok
                                    {
                                        Offset = chunk.Offset
                                        Length = chunk.Content.Length
                                        FileSize = chunk.FileSize
                                    }
                    }))
            Handler.request EnvProtocol.fsWrite (fun context (path, channelId, atomic) ->
                run (fun ct ->
                    task {
                        let! channel = Transfer.acceptChannel context.Peer channelId Flow.Inbound ct

                        match! Transfer.readAll channel ct with
                        | Error e -> return raise (TransferFailedException e)
                        | Ok content ->
                            if atomic then
                                return! s.FsOps.WriteAtomic path content ct
                            else
                                return! s.Fs.WriteFile path content ct
                    }))
            Handler.request EnvProtocol.fsCreateDirectory (fun _ path -> run (s.Fs.CreateDirectory path))
            Handler.request EnvProtocol.fsDelete (fun _ path -> run (s.Fs.Delete path))
            Handler.request EnvProtocol.fsMove (fun _ (source, destination) -> run (s.Fs.Move source destination))
            Handler.request EnvProtocol.fsPatch (fun _ patches -> run (s.FsOps.ApplyPatch patches))
            Handler.request EnvProtocol.fsSearch (fun _ query -> run (s.FsOps.Search query))
            Handler.request EnvProtocol.watchStart (fun context (watch, spec) ->
                run (fun ct -> envd.StartWatch(context.Peer, watch, spec, ct)))
            Handler.request EnvProtocol.watchStop (fun context watch ->
                ok (fun _ -> envd.StopWatch(context.Peer, watch)))
        ]

    let private execHandlers<'R when 'R :> IScope> (envd: Envd) : Handler<'R> list =
        [
            Handler.request EnvProtocol.execStart (fun _ spec -> run (fun ct -> envd.Start(spec, ct)))
            Handler.request EnvProtocol.execAttach (fun context (id, out, err, channelId) ->
                run (fun ct ->
                    task {
                        let! channel = Transfer.acceptChannel context.Peer channelId Flow.Outbound ct
                        return envd.Attach(context.Peer, id, out, err, channel)
                    }))
            Handler.request EnvProtocol.execWrite (fun _ (id, data) -> run (fun ct -> envd.Write(id, data, ct)))
            Handler.request EnvProtocol.execCloseStdin (fun _ id -> run (fun _ -> envd.CloseStdin id))
            Handler.request EnvProtocol.execResize (fun _ (id, size) -> run (fun _ -> envd.Resize(id, size)))
            Handler.request EnvProtocol.execKill (fun _ id -> run (fun _ -> envd.Kill id))
            Handler.request EnvProtocol.execWait (fun _ id -> run (fun ct -> envd.Wait(id, ct)))
            Handler.request EnvProtocol.execStatus (fun _ id -> run (fun _ -> Task.FromResult(envd.Status id)))
            Handler.request EnvProtocol.execList (fun _ () -> ok (fun _ -> Task.FromResult(envd.List())))
            Handler.request EnvProtocol.execRelease (fun _ id -> ok (fun _ -> envd.Release id))
        ]

    let private blobHandlers<'R when 'R :> IScope> (envd: Envd) : Handler<'R> list =
        let blobs = envd.Services.Blobs

        [
            Handler.request EnvProtocol.blobsPut (fun context channelId ->
                ok (fun ct ->
                    task {
                        let! channel = Transfer.acceptChannel context.Peer channelId Flow.Inbound ct
                        let! writer = blobs.Create ct

                        try
                            let mutable result = None

                            while result.IsNone do
                                match! channel.Read ct with
                                | Ok(Some data) -> do! writer.Write (data.ToArray()) ct
                                | Ok None ->
                                    let! blob = writer.Commit ct
                                    result <- Some blob
                                | Error e -> raise (TransferFailedException e)

                            return result.Value
                        with e ->
                            do! writer.Abort()
                            return raise e
                    }))
            Handler.request EnvProtocol.blobsGet (fun context (hash, range, channelId) ->
                run (fun ct ->
                    task {
                        let! channel = Transfer.acceptChannel context.Peer channelId Flow.Outbound ct

                        let! content =
                            match range with
                            | None -> blobs.Get hash ct
                            | Some(offset, count) -> blobs.Read hash offset count ct

                        match content with
                        | Error e -> return fail channel e
                        | Ok content ->
                            do! send channel content ct
                            return Ok content.LongLength
                    }))
            Handler.request EnvProtocol.blobsStat (fun _ hash -> ok (fun ct -> blobs.Stat hash ct))
        ]

    let private snapshotHandlers<'R when 'R :> IScope> (envd: Envd) : Handler<'R> list =
        let snapshots = envd.Services.Snapshots

        [
            Handler.request EnvProtocol.snapshotTake (fun _ message -> run (snapshots.Take message))
            Handler.request EnvProtocol.snapshotDiff (fun _ (from, until) -> run (snapshots.Diff from until))
            Handler.request EnvProtocol.snapshotPatch (fun context (from, until, paths, channelId) ->
                run (fun ct ->
                    task {
                        let! channel = Transfer.acceptChannel context.Peer channelId Flow.Outbound ct

                        match! snapshots.Patch from until paths ct with
                        | Error e -> return fail channel e
                        | Ok text ->
                            let bytes = UTF8Encoding(false).GetBytes text
                            do! send channel bytes ct
                            return Ok bytes.Length
                    }))
            Handler.request EnvProtocol.snapshotRestore (fun _ (snapshot, paths) ->
                run (snapshots.Restore snapshot paths))
        ]

    /// Every handler of the environment protocol.
    let handlers<'R when 'R :> IScope> (envd: Envd) : Handler<'R> list =
        fsHandlers envd @ execHandlers envd @ blobHandlers envd @ snapshotHandlers envd

    /// Who envd is in `initialize`.
    let peerInfo: PeerInfo =
        {
            Name = Product.binaryName + "-envd"
            Version = Product.Version
        }

    /// The service envd accepts connections with: it refuses a host that does not speak this version
    /// of the protocol, and announces its platform.
    let service<'R when 'R :> IClock and 'R :> IScopeWith<'R>> (envd: Envd) : Service<'R> =
        Service.create peerInfo (handlers envd)
        |> Service.withAccept (fun remote ->
            Eff.ret (
                match Json.tryField EnvProtocol.Members.Protocol remote.Members with
                | Some(Json.String version) when version = EnvProtocol.Version ->
                    Ok
                        [
                            EnvProtocol.Members.Protocol, Json.String EnvProtocol.Version
                            EnvProtocol.Members.Platform, Codec.encode EnvCodecs.platform envd.Platform
                        ]
                | _ ->
                    Error(
                        RpcError.create
                            Protocol.ErrorCode.UnsupportedVersion
                            $"envd speaks version {EnvProtocol.Version} of the environment protocol"
                    )
            ))
