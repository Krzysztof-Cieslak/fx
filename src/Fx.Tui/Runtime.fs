namespace Fx.Tui

open System
open System.Threading.Channels
open System.Threading.Tasks
open Fx.Effects

/// Messages from anywhere (the terminal, the host's notifications, commands that finished) to the
/// TUI's loop, in the order they were posted.
[<Sealed>]
type Inbox<'Msg>() =
    let channel =
        Channel.CreateUnbounded<'Msg>(UnboundedChannelOptions(SingleReader = true, SingleWriter = false))

    member _.Post(msg: 'Msg) = channel.Writer.TryWrite msg |> ignore

    member _.Read<'R when 'R :> IScope>() : Eff<'R, 'Msg> =
        fun env -> channel.Reader.ReadAsync(env.Scope.Cancellation).AsTask()

    member _.TryRead() : 'Msg option =
        match channel.Reader.TryRead() with
        | true, msg -> Some msg
        | _ -> None

    override _.ToString() = "Inbox"

/// The Elm loop (spec §20: an Elm-style TUI): messages folded by `App.update`, the model settled and
/// the screen drawn by `View.render` and written as the difference from the last frame (the settled
/// model kept: the scroll follows the transcript), commands handed to the shell's
/// interpreter. Keys come from the terminal, decoded; ticks move time on and notice resizes.
[<RequireQualifiedAccess>]
module Runtime =
    /// How long a lone `ESC` waits for the rest of a sequence before it is the Escape key.
    let escapeWait = TimeSpan.FromMilliseconds 40.0

    /// How long quitting waits for the commands sent before it.
    let quitWait = TimeSpan.FromSeconds 5.0

    let private now<'R when 'R :> IClock> : Eff<'R, int64> =
        Clock.now |> Eff.map (fun instant -> instant.UnixMicroseconds)

    /// Reads the terminal and posts the keys, until cancelled.
    let input<'R when 'R :> ITty and 'R :> IClock and 'R :> IScopeWith<'R>> (inbox: Inbox<Msg>) : Eff<'R, unit> =
        eff {
            let mutable pending = ""

            while true do
                let! text =
                    if pending = "" then
                        Tty.read |> Eff.map Some
                    else
                        Eff.timeout escapeWait Tty.read

                let keys, rest =
                    match text with
                    | Some "" -> [], pending
                    | Some text -> Keys.decode pending text
                    | None -> Keys.flush pending

                pending <- rest

                for key in keys do
                    inbox.Post(Msg.Key key)
        }

    /// Posts the time (and the size when it changed) often while something moves on screen.
    let ticks<'R when 'R :> ITty and 'R :> IClock and 'R :> IScopeWith<'R>>
        (inbox: Inbox<Msg>)
        (busy: unit -> bool)
        (size: TtySize)
        : Eff<'R, unit> =
        eff {
            let mutable last = size

            while true do
                do! Clock.sleep (TimeSpan.FromMilliseconds(if busy () then 100.0 else 500.0))
                let! current = Tty.size

                if current <> last then
                    last <- current
                    inbox.Post(Msg.Resized(current.Columns, current.Rows))

                let! at = now
                inbox.Post(Msg.Tick at)
        }

    /// Runs the TUI until it quits, and gives its last model. `interpret` sends a command (it posts
    /// what came of it); commands run one at a time, in order, and those sent before quitting are
    /// sent before it returns.
    let run<'R when 'R :> ITty and 'R :> IClock and 'R :> IScopeWith<'R>>
        (inbox: Inbox<Msg>)
        (interpret: Cmd -> Eff<'R, unit>)
        (start: Model)
        : Eff<'R, Model> =
        eff {
            let commands = Inbox<Cmd>()
            let model = ref start
            let frame: Frame option ref = ref None

            let busy () =
                let m = model.Value

                m.Timeline.State.Running.IsSome
                || (match m.Phase with
                    | Phase.Starting _ -> true
                    | _ -> false)
                || m.Notice.IsSome

            let draw: Eff<'R, unit> =
                eff {
                    let settled, next = View.render model.Value
                    model.Value <- settled
                    let text = Vt.diff frame.Value next
                    do! Tty.write text
                    frame.Value <- Some next
                }

            let apply (msg: Msg) =
                let next, cmds = App.update msg model.Value
                model.Value <- next

                for cmd in cmds do
                    match cmd with
                    | Cmd.Redraw -> frame.Value <- None
                    | cmd -> commands.Post cmd

            let main: Eff<'R, unit> =
                eff {
                    do! draw

                    while not model.Value.Quitting do
                        let! msg = inbox.Read()
                        apply msg
                        let mutable more = true

                        while more && not model.Value.Quitting do
                            match inbox.TryRead() with
                            | Some msg -> apply msg
                            | None -> more <- false

                        do! draw

                    // The commands sent before quitting go out first (the worker ends at `Quit`),
                    // unless one takes too long.
                    do! Clock.sleep quitWait
                }

            let worker: Eff<'R, unit> =
                eff {
                    let mutable working = true

                    while working do
                        let! cmd = commands.Read()

                        match cmd with
                        | Cmd.Quit -> working <- false
                        | cmd ->
                            match! Eff.catch (interpret cmd) with
                            | Ok() -> ()
                            | Error(:? OperationCanceledException) -> ()
                            | Error e -> inbox.Post(Msg.Sent(string cmd, Error(e.GetType().Name)))
                }

            let size =
                {
                    Columns = start.Width
                    Rows = start.Height
                }

            do! Eff.race [ main; input inbox; ticks inbox busy size; worker ]
            return model.Value
        }
