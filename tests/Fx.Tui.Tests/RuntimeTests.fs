module Fx.Tui.Tests.RuntimeTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Effects
open Fx.Effects.Live
open Fx.Sessions
open Fx.Tui
open Fx.Tui.Tests.Script

type TestEnv =
    {
        Clock: Clock
        Tty: Tty
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface ITty with
        member env.Tty = env.Tty

    interface IScopeWith<TestEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

/// Waits until the fake terminal's screen satisfies a condition.
let waitFor (tty: FakeTty) (what: string) (condition: string -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds 30.0

    while not (condition tty.Text) do
        if DateTime.UtcNow > deadline then
            failtest $"the screen never showed {what}:\n{tty.Text}"

        tty.Changed.Wait(TimeSpan.FromMilliseconds 200.0) |> ignore

[<Tests>]
let tests =
    testList
        "TUI runtime"
        [
            testCase "keys from the terminal become commands; the screen follows; Ctrl+C twice quits"
            <| fun () ->
                let tty = FakeTty(60, 18)

                let env =
                    {
                        Clock = SystemClock.clock
                        Tty = tty.Tty
                        Scope = Scope.root
                    }

                let inbox = Inbox<Msg>()
                let sent = ConcurrentQueue<Cmd>()

                let interpret (cmd: Cmd) : Eff<TestEnv, unit> =
                    eff {
                        sent.Enqueue cmd
                        inbox.Post(Msg.Sent(string cmd, Ok()))
                    }

                // On the clock the loop's ticks read, so notices last as long as they would.
                let start =
                    { Screens.finished 60 18 with
                        Now = (SystemClock.clock.Now()).UnixMicroseconds
                    }

                let running = Eff.run env (Runtime.run inbox interpret start)
                waitFor tty "the result card" (fun text -> text.Contains "✓ Done")
                Expect.stringContains tty.Text "! runs unsandboxed" "the banner"

                tty.Type "fix the tests\r"
                waitFor tty "the prompt cleared" (fun text -> not (text.Contains "› fix the tests"))
                let deadline = DateTime.UtcNow.AddSeconds 30.0

                while sent.IsEmpty && DateTime.UtcNow < deadline do
                    Thread.Sleep 10

                match List.ofSeq sent with
                | [ Cmd.Prompt(_, _, "fix the tests", PromptMode.Steer) ] -> ()
                | other -> failtest $"{other}"

                // A paste with line ends stays in the prompt; Escape alone is the Escape key.
                tty.Type "\u001b[200~two\rlines\u001b[201~"
                waitFor tty "the pasted lines" (fun text -> text.Contains "  lines")
                tty.Type "\u001b"
                tty.Resize(40, 12)
                waitFor tty "the narrower screen" (fun _ -> tty.Lines.Head.Contains "fx" && tty.Lines.Length = 12)

                tty.Type "\u0003"
                waitFor tty "the cleared prompt" (fun text -> text.Contains "Ctrl+C again to exit")
                tty.Type "\u0003"
                let final = running.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()
                Expect.isTrue final.Quitting "quit"
                Expect.equal sent.Count 1 "one command"
        ]
