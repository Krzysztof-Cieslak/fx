namespace Fx.Tui.Live

open System
open System.IO
open System.Threading
open Fx
open Fx.Effects
open Fx.Effects.Live
open Fx.Variants
open Fx.Host
open Fx.Tui

/// What `fx` (the TUI) is asked for.
type TuiArgs =
    {
        /// Show this session (`--session <id>`).
        Session: string option
        /// Show the workspace's newest session (`--continue`).
        Continue: bool
        /// The title of a new session (`--title`).
        Title: string option
    }

    override this.ToString() =
        match this.Session with
        | Some id -> "tui " + id
        | None when this.Continue -> "tui --continue"
        | None -> "tui"

module TuiArgs =
    let defaults: TuiArgs =
        {
            Session = None
            Continue = false
            Title = None
        }

/// An environment that reads files: finding a serving host's listener.
type private LocateEnv =
    {
        FsRead: FsRead
        Scope: Scope
    }

    interface IFsRead with
        member env.FsRead = env.FsRead

    interface IScope with
        member env.Scope = env.Scope

    override _.ToString() = "LocateEnv"

/// The TUI's composition roots (the imperative shell): `fx` (a host in this process and the TUI on
/// the terminal: embedded mode, spec §3) and `fx attach` (the TUI of a host `fx serve` runs).
[<RequireQualifiedAccess>]
module TuiMain =
    /// Whether the TUI can run: standard input and output are a terminal.
    let canRun () : bool = LocalTty.IsTerminal()

    let private choice (args: TuiArgs) (workspace: string) : SessionChoice =
        match args.Session with
        | Some id -> SessionChoice.Resume id
        | None when args.Continue -> SessionChoice.Latest workspace
        | None -> SessionChoice.New args.Title

    /// Runs the TUI on the terminal over a connection the caller makes, until the person quits.
    let private onTerminal
        (setup: Setup)
        (connect: Inbox<Msg> -> Following -> TuiEnv -> Threading.Tasks.Task<Result<Fx.Rpc.Peer, string>>)
        (choice: SessionChoice)
        (say: string -> unit)
        : int =
        match LocalTty.Open(Vt.enter, Vt.leave) with
        | Error e ->
            say e
            1
        | Ok tty ->
            let out = Console.Out
            let error = Console.Error
            // Nothing else may write on the TUI's screen.
            Console.SetOut TextWriter.Null
            Console.SetError TextWriter.Null
            use lifetime = new CancellationTokenSource()

            let env: TuiEnv =
                {
                    Clock = SystemClock.clock
                    Tty = tty.Tty
                    Scope = Scope.ofCancellation lifetime.Token
                }

            let outcome =
                try
                    try
                        let inbox = Inbox<Msg>()
                        let following = Following()

                        match (connect inbox following env).GetAwaiter().GetResult() with
                        | Error e -> Error e
                        | Ok peer ->
                            try
                                let size = (tty.Tty.Size CancellationToken.None).GetAwaiter().GetResult()
                                let now = (SystemClock.clock.Now()).UnixMicroseconds
                                let model = App.init setup size.Columns size.Rows now

                                let final =
                                    (Eff.run env (TuiClient.run peer inbox following choice model))
                                        .GetAwaiter()
                                        .GetResult()

                                Ok final
                            finally
                                peer.CloseAsync().Wait(TimeSpan.FromSeconds 10.0) |> ignore
                    with e ->
                        Error $"the TUI failed ({e.GetType().Name})"
                finally
                    tty.Restore()
                    (tty :> IDisposable).Dispose()
                    Console.SetOut out
                    Console.SetError error

            match outcome with
            | Error e ->
                say e
                1
            | Ok final ->
                match final.Session with
                | Some session ->
                    say $"session {session.Id}: `{Product.binaryName} --session {session.Id}` continues it"
                | None -> ()

                0

    /// `fx`: a host for the current directory in this process, and the TUI on the terminal.
    let embedded (options: HostOptions) (args: TuiArgs) (say: string -> unit) : int =
        say $"{Product.Name}: starting in {options.Workspace}…"

        match (Host.start options).GetAwaiter().GetResult() with
        | Error errors ->
            for e in errors do
                say $"{Product.Name}: {e}"

            1
        | Ok host ->
            try
                let variants = Variants.ofResolution host.Resolution

                let setup: Setup =
                    {
                        Product = Product.Name
                        Binary = Product.binaryName
                        Workspace = options.Workspace
                        Model = Setup.value (Slots.modelRole "main") variants
                        Trusted = host.Config.ProjectTrusted
                        Warnings = host.Warnings
                        Keys = "tui/" + string (options.Ids.NewId())
                        Terminal = Terminal.detect options.Variables (OperatingSystem.IsWindows())
                        Theme = Setup.value TuiSlots.theme variants
                    }

                let connect (inbox: Inbox<Msg>) (following: Following) (env: TuiEnv) =
                    task {
                        match!
                            Eff.run env (HostClient.embedded host (TuiClient.service inbox following) App.answers)
                        with
                        | Ok peer -> return Ok peer
                        | Error e -> return Error $"the TUI could not connect to the host: {e}"
                    }

                onTerminal setup connect (choice args options.Workspace) say
            finally
                (Host.stop host).Wait(TimeSpan.FromSeconds 60.0) |> ignore

    /// `fx attach`: the TUI of the host `fx serve` runs on this data directory.
    let attach
        (paths: HostPaths)
        (variables: (string * string) list)
        (workspace: string)
        (args: TuiArgs)
        (say: string -> unit)
        : int =
        let locateEnv =
            {
                FsRead = LocalFs.read
                Scope = Scope.root
            }

        match (Eff.run locateEnv (HostClient.locate paths)).GetAwaiter().GetResult() with
        | Error e ->
            say $"{Product.Name}: {e}; start one with `{Product.binaryName} serve`"
            1
        | Ok(listener, token) ->
            let setup: Setup =
                {
                    Product = Product.Name
                    Binary = Product.binaryName
                    Workspace = workspace
                    Model = None
                    Trusted = true
                    Warnings = []
                    Keys = "tui/" + string ((SystemIds.create SystemClock.clock).NewId())
                    Terminal = Terminal.detect variables (OperatingSystem.IsWindows())
                    // The serving host's resolution is not this process's: the default.
                    Theme = TuiSlots.theme.Default
                }

            let connect (inbox: Inbox<Msg>) (following: Following) (env: TuiEnv) =
                Eff.run
                    env
                    (HostClient.connect
                        listener
                        token
                        (TuiClient.service inbox following)
                        (HostClient.members App.answers))

            onTerminal setup connect (choice args workspace) say
