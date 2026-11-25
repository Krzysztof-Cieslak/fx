/// The TUI (T130): its pure core rendering scripted screens (text widths, rows, frames, the VT
/// difference, key decoding), the Elm loop on a fake terminal, the client protocol's new notices,
/// and the terminal check's P/Invoke (no terminal here: both runs say so).
module Fx.Aot.Scenarios.Tui

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Machines
open Fx.Machines.Host
open Fx.Sessions
open Fx.Host
open Fx.Tui
open Fx.Tui.Live
open Fx.Tui.Tests

let private view () : string list =
    [
        for name, make in Screens.all do
            for width, height in [ 80, 24; 32, 14 ] do
                $"== {name} {width}x{height}"
                yield! (Screens.render (make width height)).TrimEnd('\n').Split('\n')

        "== widths"

        for sample in [ "hello"; "日本語"; "😀"; "e\u0301"; "👍🏽"; "◇ ◆ ▸ ✓ ✗" ] do
            $"{Vt.visible sample} = {Text.width sample}"

        "== wrap"
        yield! Text.wrap 12 "the quick brown fox jumps over 日本語のテキスト"

        "== keys"

        for input in
            [
                "hi\r"
                "\u001b[A\u001bOB\u001b[1;5C\u001b[Z"
                "\u001b[3~\u001b[5~\u001b[13;2u\u001b\r"
                "\u001b[200~pasted\rtext\u001b[201~"
                "\u0003\u0004\u007f\t"
            ] do
            let keys, rest = Keys.decode "" input
            let shown = keys |> List.map string |> String.concat " "
            $"{Vt.visible input} -> {shown} (pending {Vt.visible rest})"

        "== diff"

        let a = View.view (Screens.finished 40 10)
        let b = View.view (Screens.running 40 10)
        Vt.visible (Vt.diff (Some a) b)
    ]

type private Env =
    {
        Clock: Clock
        Tty: Tty
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface ITty with
        member env.Tty = env.Tty

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

let private waitFor (tty: FakeTty) (condition: string -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds 30.0

    while not (condition tty.Text) do
        if DateTime.UtcNow > deadline then
            invalidOp $"the screen never changed as expected:\n{tty.Text}"

        tty.Changed.Wait(TimeSpan.FromMilliseconds 200.0) |> ignore

let private loop () : Task<string list> =
    task {
        let tty = FakeTty(60, 18)

        let env =
            {
                Clock = SystemClock.clock
                Tty = tty.Tty
                Scope = Scope.root
            }

        let inbox = Inbox<Msg>()
        let sent = ConcurrentQueue<Cmd>()

        let interpret (cmd: Cmd) : Eff<Env, unit> =
            eff {
                sent.Enqueue cmd
                inbox.Post(Msg.Sent(string cmd, Ok()))
            }

        let running = Eff.run env (Runtime.run inbox interpret (Screens.grant 60 18))
        waitFor tty (fun text -> text.Contains "Permission")
        tty.Type "y"
        waitFor tty (fun text -> text.Contains "sending…")
        tty.Type "\u001b"
        waitFor tty (fun text -> text.Contains "interrupting")
        tty.Type "\u0003"
        tty.Type "\u0003"
        let! final = running.WaitAsync(TimeSpan.FromSeconds 30.0)

        return [ $"quitting: {final.Quitting}"; yield! sent |> Seq.map string ]
    }

let private protocol () : string list =
    let notice: ContextNotice =
        {
            Session = "s1"
            Item =
                {
                    Stream = StreamId.create "context" "s1"
                    Seq = 3L
                    Event =
                        Fx.Prompt.ContextEvent.Returned
                            {
                                Turn = 1
                                Step = 2
                                Call = Some "toolu_1"
                                Name = "bash"
                                Pieces = [ Fx.Prompt.Piece.Text "exit 0" ]
                                IsError = false
                            }
                    Meta =
                        {
                            At = Instant 1_795_600_800_000_000L
                            By = Attribution.system "host"
                            Key = IdempotencyKey "k"
                            Cause = Some(EventRef.create (Session.stream "s1") 5L)
                        }
                }
        }

    let streaming: StreamingNotice =
        {
            Session = "s1"
            Turn = 2
            Fresh = true
            Text = "Writing 日本語"
            Tool = Some "cell"
        }

    let context = Json.serialize (Codec.encode ClientProtocol.contextEvent notice)
    let stream = Json.serialize (Codec.encode ClientProtocol.streaming streaming)

    [
        context
        stream
        $"context round trip: {Codec.decodeText ClientProtocol.contextEvent context = Ok notice}"
        $"streaming round trip: {Codec.decodeText ClientProtocol.streaming stream = Ok streaming}"
        $"least role of session/context: {ClientProtocol.leastRole ClientProtocol.context.Name}"
        // Standard input and output are redirected in both runs: the P/Invoke answers the same.
        $"terminal: {TuiMain.canRun ()}"
    ]

let scenarios: Scenario list =
    [
        Scenario.lines "tui-view" view
        Scenario.task "tui-loop" loop
        Scenario.lines "tui-protocol" protocol
    ]
