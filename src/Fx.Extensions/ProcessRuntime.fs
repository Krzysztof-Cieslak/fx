/// The `process` runtime (spec §16.1): an extension in a child process, in any language, speaking
/// the extension protocol over JSON-RPC on its standard input and output. The process is started
/// with the shell capability from the manifest's `LaunchSpec` (data), so nothing but the manifest
/// decides what runs, and the host's deny list always strips runtime injection vectors (spec §18).
[<RequireQualifiedAccess>]
module Fx.Extensions.ProcessRuntime

open System
open System.Text
open Fx.Effects
open Fx.Rpc
open Fx.Sdk

/// How the host runs extension processes.
type ProcessSettings =
    {
        /// The connection's limits.
        Limits: ConnectionLimits
        /// The host, as it introduces itself in `initialize`.
        Info: PeerInfo
        /// The host's environment variables (read once by the composition root), for allowlists.
        HostEnvironment: (string * string) list
        Workspace: string option
        IsWindows: bool
        /// How long a stopping process may take to exit before it is killed.
        StopTimeout: TimeSpan
        /// How much of a process's standard error is kept for diagnostics.
        StderrBytes: int
    }

    override this.ToString() =
        $"ProcessSettings({this.Info.Name}, stop timeout {this.StopTimeout.TotalSeconds} s)"

/// The end of a process's standard error, bounded.
type private Tail(capacity: int) =
    let buffer = Collections.Generic.Queue<byte>()
    let gate = obj ()

    member _.Append(bytes: byte array) =
        lock gate (fun () ->
            for b in bytes do
                buffer.Enqueue b

            while buffer.Count > capacity do
                buffer.Dequeue() |> ignore)

    member _.Text() =
        lock gate (fun () -> Encoding.UTF8.GetString(buffer.ToArray()))

/// A started extension process and the host's connection to it.
[<NoEquality; NoComparison>]
type Running =
    {
        Peer: Peer
        Proc: Proc
        Remote: RemotePeer
        /// The end of its standard error, for diagnostics.
        Stderr: unit -> string
    }

    override this.ToString() = $"extension process {this.Proc.Pid}"

let private withStderr (running: unit -> string) (message: string) =
    match running().Trim() with
    | "" -> message
    | text -> message + "; its standard error ends with: " + text

/// Starts the process a manifest describes and connects to it; its launch's untrusted overrides
/// are added unless the project is trusted. The connection lives in the scope of this effect:
/// cancelling it closes the connection.
let start<'R when 'R :> IShell and 'R :> IClock and 'R :> IScopeWith<'R>>
    (settings: ProcessSettings)
    (projectTrusted: bool)
    (manifest: Manifest)
    (root: string)
    : Eff<'R, Result<Running, string>> =
    eff {
        match manifest.Launch with
        | None -> return Error "the manifest says nothing about how to start it (launch)"
        | Some launch ->
            let context =
                {
                    Root = root
                    Workspace = settings.Workspace
                    HostEnvironment = settings.HostEnvironment
                    IsWindows = settings.IsWindows
                    ProjectTrusted = projectTrusted
                }

            match Launch.procSpec context launch with
            | Error reason -> return Error reason
            | Ok spec ->
                match! Shell.spawn spec with
                | Error e -> return Error(string e)
                | Ok proc ->
                    let tail = Tail settings.StderrBytes

                    let transport =
                        ProcTransport.create (ConnectionLimits.frames settings.Limits) proc tail.Append

                    let service = Service.create settings.Info [] |> Service.withLimits settings.Limits

                    match! Rpc.connect service RpcRemote.initializeMembers transport with
                    | Error e ->
                        do! Shell.kill proc
                        let! _ = Eff.shield (Shell.wait proc)
                        return Error(withStderr tail.Text $"the process did not complete the handshake: {e}")
                    | Ok peer ->
                        return
                            Ok
                                {
                                    Peer = peer
                                    Proc = proc
                                    Remote = RpcRemote.ofPeer peer
                                    Stderr = tail.Text
                                }
    }

/// Closes the connection, which closes the process's standard input, and waits for it to exit; kills
/// it after `StopTimeout`.
let stop (settings: ProcessSettings) (running: Running) : Eff<IExtensionEnv, unit> =
    eff {
        let! _ = Rpc.close running.Peer
        let! exited = Eff.timeout settings.StopTimeout (Shell.wait running.Proc)

        if exited.IsNone then
            do! Shell.kill running.Proc
            let! _ = Eff.shield (Shell.wait running.Proc)
            ()
    }

/// Activates the manifest's extension in a started process: the process must answer for the
/// manifest's id and version, and provide what the host's catalog can load.
let activate
    (running: Running)
    (manifest: Manifest)
    (load: ContributionDecl -> Remote option -> Fx.Contracts.Validation<Contribution, Fx.Contracts.Issue>)
    : Eff<IExtensionEnv, Result<Activation, string>> =
    eff {
        let! outcome =
            RemoteActivation.activate
                running.Remote
                {
                    Extension = manifest.Id
                    Manifest = None
                    Root = None
                }
                manifest
                load

        return outcome |> Result.mapError (withStderr running.Stderr)
    }
