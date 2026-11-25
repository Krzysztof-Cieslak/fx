/// The TUI end to end on a host in this process (embedded mode), through the client protocol, on a
/// fake terminal: keys typed, screens read. Without a kernel (no network) a turn fails at its hands;
/// with the bun-ts kernel (network, cached; skipped offline, required in CI) the first install's
/// permission is given on the TUI's card and a turn finishes with its result card.
module Fx.Tui.Live.Tests.EmbeddedTests

open System
open System.IO
open System.Threading
open Expecto
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Grants
open Fx.Grants.Host
open Fx.Host
open Fx.Host.Tests.Support
open Fx.Tui
open Fx.Tui.Live

/// The TUI running on a fake terminal against a host.
type Running =
    {
        Tty: FakeTty
        Task: Threading.Tasks.Task<Model>
    }

    /// Waits until the screen shows something.
    member this.WaitFor(what: string, ?seconds: float) =
        let deadline = DateTime.UtcNow.AddSeconds(defaultArg seconds 60.0)

        while not (this.Tty.Text.Contains what) do
            if this.Task.IsCompleted then
                failtest $"the TUI ended before showing {what}:\n{this.Tty.Text}"

            if DateTime.UtcNow > deadline then
                failtest $"the screen never showed {what}:\n{this.Tty.Text}"

            this.Tty.Changed.Wait(TimeSpan.FromMilliseconds 200.0) |> ignore

    /// Waits until the screen satisfies a condition.
    member this.WaitUntil(what: string, condition: string -> bool) =
        let deadline = DateTime.UtcNow.AddSeconds 60.0

        while not (condition this.Tty.Text) do
            if DateTime.UtcNow > deadline then
                failtest $"the screen never showed {what}:\n{this.Tty.Text}"

            this.Tty.Changed.Wait(TimeSpan.FromMilliseconds 200.0) |> ignore

    member this.Type(text: string) = this.Tty.Type text

    /// Ctrl+C twice, and the TUI's last model.
    member this.Quit() : Model =
        this.Type "\u0003"
        this.WaitFor "again to exit"
        this.Type "\u0003"
        this.Task.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

    override _.ToString() = "Running"

let private tui (host: Host) (choice: SessionChoice) : Running =
    let tty = FakeTty(100, 30)

    let env: TuiEnv =
        {
            Clock = SystemClock.clock
            Tty = tty.Tty
            Scope = Scope.root
        }

    let inbox = Inbox<Msg>()

    let peer =
        match waitFor 60.0 (Eff.run env (HostClient.embedded host (TuiClient.service inbox) App.answers)) with
        | Ok peer -> peer
        | Error e -> failtest $"no connection: {e}"

    let setup: Setup =
        {
            Product = Product.Name
            Binary = Product.binaryName
            Workspace = host.Options.Workspace
            Model = Some modelId
            Trusted = host.Config.ProjectTrusted
            Warnings = host.Warnings
            Keys = "tui/" + Guid.NewGuid().ToString("N")
        }

    let model = App.init setup 100 30 (SystemClock.clock.Now()).UnixMicroseconds

    {
        Tty = tty
        Task = Eff.run env (TuiClient.run peer inbox choice model)
    }

let private withoutKernel (place: Place) =
    Directory.CreateDirectory place.Data |> ignore

    File.WriteAllText(
        Path.Combine(place.Data, "config.json"),
        """{"extensions": {"disabled": ["fx.interpreter.bun-ts"]}}"""
    )

[<Tests>]
let tests =
    testList
        "TUI on an embedded host"
        [
            testCase
                "a session starts, a prompt runs a turn to its result card, a permission is given, Ctrl+C twice exits"
            <| fun () ->
                let p = place "tui"
                withoutKernel p
                let host = start (options p (Model()) [ "model.role.main", Json.String modelId ])

                try
                    let t = tui host (SessionChoice.New(Some "first"))
                    t.WaitFor "session "
                    t.WaitFor "env: default"
                    t.WaitFor "! runs unsandboxed"
                    t.Type "Hello there\r"
                    t.WaitFor "› Hello there"
                    t.WaitFor "✗ Failed"

                    // A permission the session's turn asks for (as T127b's guard does), answered on the card.
                    let session =
                        HostSessions.entries host |> List.exactlyOne |> fun entry -> entry.Session

                    let ask: GrantAsk =
                        {
                            Key = "tests/deploy"
                            Action = ActionName.create "shell.exec"
                            Resource = "make deploy"
                            Context =
                                {
                                    Session = Some session
                                    Run = None
                                    Repo = None
                                }
                            Requester =
                                {
                                    Principal = "system"
                                    Agent = None
                                    Extension = None
                                }
                            Summary = "Run make deploy"
                            Evidence = []
                            Suggestions = []
                            Scopes = []
                            Expires = None
                        }

                    let asking = Eff.run host.Env (GrantsService.request host.Grants ask)
                    t.WaitFor "Permission"
                    t.WaitFor "Run make deploy"
                    t.Type "s"

                    match waitFor 60.0 asking with
                    | Ok(AskOutcome.Pending id) ->
                        // The request is answered: wait for the log to say so.
                        let deadline = DateTime.UtcNow.AddSeconds 30.0

                        while (GrantsService.snapshot host.Grants).State.Requests[id].Status = RequestStatus.Pending do
                            if DateTime.UtcNow > deadline then
                                failtest "the permission was not answered"

                            Thread.Sleep 20

                        match (GrantsService.snapshot host.Grants).State.Requests[id].Status with
                        | RequestStatus.Answered grant ->
                            Expect.equal grant.Scope (GrantScope.Session session) "for this session"
                            Expect.stringStarts grant.By "user:" "given by a person"
                        | other -> failtest $"{other}"
                    | other -> failtest $"{other}"

                    t.WaitUntil("the card gone", fun text -> not (text.Contains "Permission"))
                    let final = t.Quit()
                    Expect.isTrue final.Quitting "quit"
                    Expect.equal final.Session.Value.Id session "the session"
                finally
                    stop host

            testCase "with the bun-ts kernel: the first install's permission on the card, then cells and a result card"
            <| fun () ->
                match waitFor 600.0 (Fx.Kernel.Tests.PinnedBun.executable ()) with
                | Ok _ -> ()
                | Error reason when Environment.GetEnvironmentVariable "CI" = "true" ->
                    failtest $"Bun is not available: {reason}"
                | Error reason -> skiptest $"Bun is not available: {reason}"

                let p = place "tui-live"
                p.Write("notes.md", "# Notes\nship on Friday\n")
                let model = Model()

                model.AddAll
                    [
                        $"// intent: read the notes\nconst notes = await {g}.fs.read(\"notes.md\")\nnotes.text"
                        $"// intent: finish\nawait {g}.finish({{ kind: \"done\", summary: \"Read the notes\", evidence: [{{ kind: \"file\", summary: \"notes.md says ship on Friday\" }}] }})"
                    ]

                let host = start (options p model [ "model.role.main", Json.String modelId ])

                try
                    let t = tui host (SessionChoice.New None)
                    // Creating the session creates the environment, which installs Bun: a permission.
                    t.WaitFor("Permission", 120.0)
                    t.WaitFor "component.install"
                    t.Type "h"
                    t.WaitFor("env: default", 300.0)
                    t.Type "Read the notes\r"
                    t.WaitFor "Cell     read the notes"
                    t.WaitFor("✓ Done  Read the notes", 120.0)
                    t.WaitFor "Evidence  notes.md says ship on Friday"
                    t.WaitFor "▸ 2 cells"
                    t.Quit() |> ignore
                finally
                    stop host
        ]
