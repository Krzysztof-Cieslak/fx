/// The real terminal backend: the `fx` binary started on a pseudo-terminal (a PTY on Linux and macOS,
/// ConPTY on Windows) with no arguments, so it runs the TUI on an embedded host. Keys are typed, the
/// output is read on a `VtScreen`, the terminal is resized, and Ctrl+C twice ends it with the terminal
/// given back.
module Fx.Tui.Live.Tests.TerminalTests

open System
open System.IO
open System.Text
open System.Threading
open Expecto
open Fx
open Fx.Effects
open Fx.Pty
open Fx.Tui

let private windows = OperatingSystem.IsWindows()

let private binary =
    Path.Combine(AppContext.BaseDirectory, Product.binaryName + (if windows then ".exe" else ""))

let private sync (task: Threading.Tasks.Task<'T>) =
    task.WaitAsync(TimeSpan.FromSeconds 120.0).GetAwaiter().GetResult()

/// What the program wrote, on a screen and as plain text.
[<Sealed>]
type private Output(pty: Pty, columns: int, rows: int) =
    let gate = obj ()
    let screen = VtScreen(columns, rows)
    let all = StringBuilder()
    let decoder = Utf8Input()
    let mutable ended = false

    let reader =
        Thread(
            (fun () ->
                let mutable reading = true

                while reading do
                    match (pty.Read CancellationToken.None).GetAwaiter().GetResult() with
                    | Some bytes ->
                        let text = decoder.Decode(bytes, bytes.Length)

                        lock gate (fun () ->
                            screen.Write text
                            all.Append text |> ignore)
                    | None ->
                        reading <- false
                        lock gate (fun () -> ended <- true)),
            IsBackground = true
        )

    do reader.Start()

    member _.Text = lock gate (fun () -> screen.Text)
    member _.Plain = lock gate (fun () -> VtText.strip (all.ToString()))
    member _.Screen = screen
    member _.Ended = lock gate (fun () -> ended)

    member _.Resize(c: int, r: int) =
        lock gate (fun () -> screen.Resize(c, r))

    member this.WaitFor(what: string) =
        let deadline = DateTime.UtcNow.AddSeconds 120.0

        while not (this.Text.Contains what || this.Plain.Contains what) do
            if DateTime.UtcNow > deadline || this.Ended then
                failtest $"the terminal never showed {what}; screen:\n{this.Text}\n\nplain:\n{this.Plain}"

            Thread.Sleep 50

let private write (pty: Pty) (text: string) =
    sync (pty.Write (Encoding.UTF8.GetBytes text) CancellationToken.None)

[<Tests>]
let tests =
    testList
        "TUI on a terminal"
        [
            testCase "fx with no arguments runs the TUI on an embedded host; Ctrl+C twice gives the terminal back"
            <| fun () ->
                let root =
                    Path.Combine(Path.GetTempPath(), "tui-pty-" + Guid.NewGuid().ToString("N").Substring(0, 10))

                let home = Path.Combine(root, "home")
                let project = Path.Combine(root, "project")
                Directory.CreateDirectory home |> ignore
                Directory.CreateDirectory project |> ignore
                // No kernel: nothing to install, so no network; a turn ends at its hands.
                File.WriteAllText(
                    Path.Combine(home, "config.json"),
                    """{"extensions": {"disabled": ["fx.interpreter.bun-ts"]}}"""
                )

                let spec: PtySpec =
                    {
                        Program = Exec(binary, [])
                        Cwd = Some project
                        Env =
                            Inherit
                                [
                                    Fx.Host.HostPaths.homeVariable, Some home
                                    "ANTHROPIC_API_KEY", None
                                    "OPENAI_API_KEY", None
                                ]
                        Size = { Cols = 100; Rows = 30 }
                    }

                let pty =
                    match LocalTerminal.spawn (LocalTerminal.defaultShell ()) project spec with
                    | Ok pty -> pty
                    | Error e -> failtest (string e)

                try
                    let out = Output(pty, 100, 30)
                    out.WaitFor "env: default"
                    out.WaitFor "sandbox: "
                    write pty "hello"
                    out.WaitFor "› hello"
                    write pty "\r"
                    out.WaitFor "✗ Failed"
                    sync (pty.Resize { Cols = 72; Rows = 20 })
                    out.Resize(72, 20)
                    // The status line's hints are written again at the new width.
                    out.WaitFor "Enter send"
                    write pty "\u0003"
                    out.WaitFor "again to exit"
                    write pty "\u0003"
                    let code = sync (pty.Wait CancellationToken.None)
                    Expect.equal code 0 "exit code"
                    out.WaitFor "continues it"

                    if not windows then
                        Expect.isFalse out.Screen.OnAlternate "the shell's screen is back"
                        Expect.isTrue out.Screen.CursorVisible "the cursor is shown again"
                finally
                    sync (pty.Close())

                    try
                        Directory.Delete(root, true)
                    with _ ->
                        ()
        ]
