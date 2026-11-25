/// envd built from the live interpreters, reached in process (embedded mode) or as `fx envd --stdio`
/// in a child process (the binary next to these tests).
module Fx.Envd.Live.Tests.Harness

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx
open Fx.Effects
open Fx.Effects.Live
open Fx.Envd
open Fx.Envd.Live
open Fx.Rpc

let none = CancellationToken.None
let windows = OperatingSystem.IsWindows()

/// Fails a test instead of hanging it.
let wait (task: Task<'T>) : 'T =
    task.WaitAsync(TimeSpan.FromSeconds 60.0).GetAwaiter().GetResult()

let ok (result: Result<'T, 'E>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failtest $"expected success, got {e}"

/// Temporary directories under one root per test run, deleted when the process exits.
module Scratch =
    let root =
        Path.Combine(Path.GetTempPath(), "envd-tests-" + Guid.NewGuid().ToString("N"))

    do
        AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
            try
                Directory.Delete(root, true)
            with _ ->
                ())

    /// A new empty directory.
    let directory () =
        let dir = Path.Combine(root, Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        dir

/// How a test reaches envd.
[<RequireQualifiedAccess>]
type Reach =
    /// In this process, over the in-process transport (embedded mode).
    | InProcess
    /// `fx envd --stdio` as a child process.
    | Stdio

    override this.ToString() =
        match this with
        | InProcess -> "in process"
        | Stdio -> "stdio child"

/// A connection to envd for a workspace.
type Session =
    {
        Client: EnvClient
        Workspace: string
        State: string
        /// Cuts the connection abruptly (in process only).
        Cut: unit -> unit
        /// Closes the connection and stops envd.
        Close: unit -> unit
    }

    override this.ToString() = $"Session({this.Workspace})"

let connectionEnv () : ConnectionEnv =
    {
        Clock = SystemClock.clock
        Scope = Scope.root
    }

/// The `fx` binary next to the tests.
let binary =
    Path.Combine(AppContext.BaseDirectory, Product.binaryName + (if windows then ".exe" else ""))

let private connect (transport: Transport) : EnvClient =
    match wait (Eff.run (connectionEnv ()) (EnvClient.connect transport)) with
    | Ok client -> client
    | Error e -> failtest $"connect failed: {e}"

/// Builds envd in this process.
let startInProcess (options: EnvdOptions) : EnvdInstance =
    match wait (EnvdMain.start options) with
    | Ok instance -> instance
    | Error e -> failtest e

/// Connects to an envd in this process.
let attachInProcess (instance: EnvdInstance) : EnvClient * (unit -> unit) =
    let client, server =
        InProcess.pair
            (256 * 1024)
            (ConnectionLimits.frames ConnectionLimits.defaults)
            (ConnectionLimits.frames ConnectionLimits.defaults)

    EnvdMain.serve instance.Envd server |> ignore

    connect client,
    fun () ->
        client.Abort()
        server.Abort()

/// Starts `fx envd --stdio` for a workspace.
let startChild (workspace: string) (state: string) (retain: int) : Process =
    let info =
        ProcessStartInfo(
            binary,
            [
                "envd"
                "--stdio"
                "--workspace"
                workspace
                "--state"
                state
                "--retain"
                string retain
            ],
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        )

    match Process.Start info with
    | null -> failtest "envd did not start"
    | child -> child

let private openWith (reach: Reach) (workspace: string) (options: EnvdOptions -> EnvdOptions) : Session =
    let state = Scratch.directory ()

    match reach with
    | Reach.InProcess ->
        let instance =
            startInProcess (
                options
                    { EnvdOptions.defaults with
                        Workspace = Some workspace
                        State = Some state
                    }
            )

        let client, cut = attachInProcess instance

        {
            Client = client
            Workspace = workspace
            State = state
            Cut = cut
            Close =
                fun () ->
                    wait (EnvClient.close client) |> ignore
                    (instance.Dispose()).Wait(TimeSpan.FromSeconds 30.0) |> ignore
        }
    | Reach.Stdio ->
        let child = startChild workspace state (options EnvdOptions.defaults).Retain

        let transport =
            StreamTransport.create
                (ConnectionLimits.frames ConnectionLimits.defaults)
                child.StandardOutput.BaseStream
                child.StandardInput.BaseStream

        let client = connect transport

        {
            Client = client
            Workspace = workspace
            State = state
            Cut = fun () -> invalidOp "a stdio child cannot reconnect"
            Close =
                fun () ->
                    try
                        wait (EnvClient.close client) |> ignore
                    with _ ->
                        ()

                    if not (child.WaitForExit(TimeSpan.FromSeconds 15.0)) then
                        child.Kill true
                        child.WaitForExit()

                    child.Dispose()
        }

/// Opens envd for a workspace.
let openSession (reach: Reach) (workspace: string) : Session = openWith reach workspace id

/// Opens envd with options.
let openSessionWith (reach: Reach) (workspace: string) (options: EnvdOptions -> EnvdOptions) : Session =
    openWith reach workspace options

/// Sessions behind capability values, so suites that only get the value can close them.
type Sessions() =
    let open' = ConcurrentDictionary<obj, Session>(HashIdentity.Reference)

    member _.Track(key: obj, session: Session) = open'[key] <- session

    member _.Close(key: obj) =
        match open'.TryRemove key with
        | true, session -> session.Close()
        | false, _ -> ()
