namespace Fx.Envd.Live

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Effects
open Fx.Effects.Live
open Fx.Envd
open Fx.Pty
open Fx.Rpc
open Fx.Store.Live

/// How `envd` is started (`<binary> envd --stdio [--workspace <dir>] [--state <dir>] [--shell <name>]`).
type EnvdOptions =
    {
        /// The workspace (default: the current directory).
        Workspace: string option
        /// envd's own state: blobs, spilled output, the shadow repository (default: a directory per
        /// workspace under the user's data directory).
        State: string option
        /// `pwsh`, `powershell` or `sh` (default: `LocalPlatform.defaultShell`).
        Shell: string option
        /// Bytes of each output stream kept in memory (the rest is in spill files).
        Retain: int
        /// The variables of the processes envd starts (`BaseEnvironment`); `None`: envd's own. The
        /// host sets it when envd runs in its process, so the host's credentials stay out (spec §18).
        Environment: (string * string) list option
    }

    override this.ToString() =
        let workspace = this.Workspace |> Option.defaultValue "<current directory>"
        $"envd for {workspace}"

module EnvdOptions =
    let defaults: EnvdOptions =
        {
            Workspace = None
            State = None
            Shell = None
            Retain = 1024 * 1024
            Environment = None
        }

/// A running envd built from the live interpreters, and what releases it.
type EnvdInstance =
    {
        Envd: Envd
        /// Where its state lives.
        State: string
        /// Kills its processes and closes its stores.
        Dispose: unit -> Task
    }

    override this.ToString() = $"envd ({this.State})"

/// envd running in the host's process (embedded mode): the host's end of the in-process connection.
type EmbeddedEnvd =
    {
        /// The host connects to envd over this (`EnvClient.connect`).
        Transport: Transport
        /// Where envd keeps its state.
        State: string
        /// Ends the connection, kills envd's processes and closes its stores.
        Stop: unit -> Task<unit>
    }

    override this.ToString() = $"embedded envd ({this.State})"

/// envd's composition root: builds `EnvdServices` from the real interpreters and serves the
/// environment protocol. The only envd code that decides where things live on disk.
module EnvdMain =
    /// The directory under which envd keeps per-workspace state: `%LOCALAPPDATA%\<product>\envd` on
    /// Windows, `~/.<product>/envd` elsewhere, the temporary directory as a last resort.
    let stateRoot () : string =
        let variable (name: string) =
            Environment.GetEnvironmentVariable name
            |> Option.ofObj
            |> Option.filter (fun v -> v <> "")

        if OperatingSystem.IsWindows() then
            match variable "LOCALAPPDATA" with
            | Some data -> Path.Combine(data, Product.windowsDataDirName, "envd")
            | None -> Path.Combine(Path.GetTempPath(), Product.windowsDataDirName + "-envd")
        else
            match variable "HOME" with
            | Some home -> Path.Combine(home, Product.homeDirName, "envd")
            | None -> Path.Combine(Path.GetTempPath(), Product.Name + "-envd")

    /// The state directory of a workspace: named by a hash of its path.
    let stateFor (workspace: string) : string =
        let key =
            if OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() then
                workspace.ToUpperInvariant()
            else
                workspace

        let hash =
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes key)).Substring(0, 16)

        Path.Combine(stateRoot (), hash)

    /// `state` relative to `workspace` with `/`, if it is inside it (snapshots must leave it out).
    let private inside (workspace: string) (state: string) : string option =
        let root =
            Path.TrimEndingDirectorySeparator workspace + string Path.DirectorySeparatorChar

        let comparison =
            if OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        if state.StartsWith(root, comparison) then
            Some(state.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/'))
        else
            None

    /// Builds envd for the options: checks the workspace, prepares the state directory, opens the
    /// blob store and finds git.
    let start (options: EnvdOptions) : Task<Result<EnvdInstance, string>> =
        let workspace =
            Path.GetFullPath(options.Workspace |> Option.defaultValue (Directory.GetCurrentDirectory()))
            |> Path.TrimEndingDirectorySeparator

        let shell =
            match options.Shell with
            | None -> Ok(LocalPlatform.defaultShell ())
            | Some name ->
                match LocalPlatform.shellNamed name with
                | Some shell -> Ok shell
                | None -> Error $"unknown shell {name}: expected pwsh, powershell or sh"

        match shell with
        | Error e -> Task.FromResult(Error e)
        | Ok _ when not (Directory.Exists workspace) ->
            Task.FromResult(Error $"the workspace {workspace} does not exist")
        | Ok shell ->
            let state =
                options.State
                |> Option.map Path.GetFullPath
                |> Option.defaultWith (fun () -> stateFor workspace)

            let output = Path.Combine(state, "output")
            Directory.CreateDirectory output |> ignore

            // Output spilled by an earlier envd belongs to processes that are gone.
            for file in Directory.GetFiles output do
                try
                    File.Delete file
                with
                | :? IOException
                | :? UnauthorizedAccessException -> ()

            let clock = SystemClock.clock
            let ids = SystemIds.create clock
            let ignoreCase = OperatingSystem.IsWindows()

            let localShell =
                match options.Environment with
                | Some vars -> BaseEnvironment.shell ignoreCase vars (LocalShell.create shell workspace)
                | None -> LocalShell.create shell workspace

            let localTerminal =
                match options.Environment with
                | Some vars -> BaseEnvironment.terminal ignoreCase vars (LocalTerminal.create shell workspace)
                | None -> LocalTerminal.create shell workspace

            match FileBlobStore.openAt (Path.Combine(state, "blobs")) clock ids Durability.Normal with
            | Error e -> Task.FromResult(Error $"cannot open the blob store: {e}")
            | Ok blobStore ->
                task {
                    let! git = localShell.Run (Command.exec "git" [ "--version" ]) CancellationToken.None

                    let snapshots, features =
                        match git with
                        | Ok output when output.ExitCode = 0 ->
                            GitSnapshots.create
                                localShell
                                LocalFs.write
                                {
                                    Git = "git"
                                    GitDir = Path.Combine(state, "snapshots.git")
                                    WorkTree = workspace
                                    Exclude = inside workspace state |> Option.toList
                                },
                            [ Feature.Pty; Feature.Watch; Feature.Snapshots ]
                        | _ -> Snapshot.unavailable "git was not found", [ Feature.Pty; Feature.Watch ]

                    let services =
                        {
                            Platform = LocalPlatform.detect workspace shell features
                            Clock = clock
                            FsRead = LocalFs.read
                            Fs = LocalFs.write
                            FsOps = LocalFsOps.create clock ids
                            Beneath = LocalBeneath.files clock ids
                            Shell = localShell
                            Terminal = TerminalSetup.wrap (LocalPlatform.terminalDefaults shell) localTerminal
                            Blobs = blobStore.Blobs
                            Snapshots = snapshots
                            Spill = Some(LocalSpill.create output)
                            Retain = options.Retain
                        }

                    let envd = Envd services

                    return
                        Ok
                            {
                                Envd = envd
                                State = state
                                Dispose =
                                    fun () ->
                                        task {
                                            do! envd.DisposeAsync()
                                            blobStore.Dispose()
                                        }
                            }
                }

    /// Serves one connection on a transport until it ends.
    let serve (envd: Envd) (transport: Transport) : Task<Result<CloseReason, ConnectError>> =
        task {
            let env =
                {
                    Clock = SystemClock.clock
                    Scope = Scope.root
                }

            match! Eff.run env (Rpc.accept (EnvdServer.service envd) transport) with
            | Error e -> return Error e
            | Ok peer ->
                let! reason = peer.Completion
                return Ok reason
        }

    /// envd in this process (embedded mode, spec §3): builds it for the options and serves it over the
    /// in-process transport. The host connects to the transport it returns; `Stop` ends the
    /// connection, kills envd's processes and closes its stores.
    let embed (options: EnvdOptions) : Task<Result<EmbeddedEnvd, string>> =
        task {
            match! start options with
            | Error message -> return Error message
            | Ok instance ->
                let limits = ConnectionLimits.frames ConnectionLimits.defaults
                let client, server = InProcess.pair (256 * 1024) limits limits
                let served = serve instance.Envd server

                return
                    Ok
                        {
                            Transport = client
                            State = instance.State
                            Stop =
                                fun () ->
                                    task {
                                        client.Abort()
                                        server.Abort()
                                        let! _ = served
                                        do! instance.Dispose()
                                    }
                        }
        }

    /// `envd --stdio`: serves one connection on standard input and output, then stops everything it
    /// started. Messages go to `log` (standard error): nothing else may write to the protocol's stream.
    let runStdio (options: EnvdOptions) (input: Stream) (output: Stream) (log: string -> unit) : int =
        let run () =
            task {
                match! start options with
                | Error message ->
                    log message
                    return 2
                | Ok instance ->
                    let transport =
                        StreamTransport.create (ConnectionLimits.frames ConnectionLimits.defaults) input output

                    let! served = serve instance.Envd transport
                    do! instance.Dispose()

                    match served with
                    | Ok _ -> return 0
                    | Error e ->
                        log $"envd: {e}"
                        return 1
            }

        run().GetAwaiter().GetResult()
