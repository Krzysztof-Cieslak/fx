namespace Fx.Tui

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// The size of a terminal, in character cells.
type TtySize =
    {
        Columns: int
        Rows: int
    }

    override this.ToString() = $"{this.Columns}x{this.Rows}"

/// The terminal the TUI draws on (its controlling terminal, in raw mode): what a person types, what
/// they see, and its size. Data in and out, the cancellation token last, like every capability.
///
/// Interpreters: `LocalTty` (Fx.Tui.Live: termios or the Windows console), `FakeTty` (scripted
/// input, a `VtScreen` for output).
type Tty =
    {
        /// The next input as text (keys as VT sequences, pastes bracketed); waits until there is some.
        /// Cancelling the token fails it with an `OperationCanceledException`.
        Read: CancellationToken -> Task<string>
        /// Writes text with VT sequences.
        Write: string -> CancellationToken -> Task<unit>
        /// The size now.
        Size: CancellationToken -> Task<TtySize>
    }

    override _.ToString() = "Tty"

type ITty =
    abstract Tty: Tty

[<RequireQualifiedAccess>]
module Tty =
    let read<'R when 'R :> ITty and 'R :> IScope> : Eff<'R, string> =
        fun env -> env.Tty.Read env.Scope.Cancellation

    let write<'R when 'R :> ITty and 'R :> IScope> (text: string) : Eff<'R, unit> =
        fun env ->
            if text = "" then
                Task.FromResult()
            else
                env.Tty.Write text env.Scope.Cancellation

    let size<'R when 'R :> ITty and 'R :> IScope> : Eff<'R, TtySize> =
        fun env -> env.Tty.Size env.Scope.Cancellation

/// A terminal for tests: input the test types (`Type`), output drawn on a `VtScreen`, a size the
/// test sets. Deterministic: reads complete in the order input was typed.
[<Sealed>]
type FakeTty(columns: int, rows: int) =
    let gate = obj ()
    let screen = VtScreen(columns, rows)
    let input = Queue<string>()
    let mutable waiting: TaskCompletionSource<string> option = None
    let mutable size = { Columns = columns; Rows = rows }
    let written = Text.StringBuilder()

    let mutable changed =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let read (cancellation: CancellationToken) : Task<string> =
        lock gate (fun () ->
            if input.Count > 0 then
                Task.FromResult(input.Dequeue())
            else
                let source =
                    TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

                waiting <- Some source

                task {
                    use _ =
                        cancellation.Register(fun () -> source.TrySetCanceled cancellation |> ignore)

                    return! source.Task
                })

    let write (text: string) (_: CancellationToken) : Task<unit> =
        lock gate (fun () ->
            screen.Write text
            written.Append text |> ignore
            let previous = changed
            changed <- TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            previous.TrySetResult() |> ignore)

        Task.FromResult()

    member _.Tty: Tty =
        {
            Read = read
            Write = write
            Size = fun _ -> Task.FromResult(lock gate (fun () -> size))
        }

    /// Types text (keys as a terminal sends them: `\r` is Enter, `\u001b` Escape).
    member _.Type(text: string) =
        lock gate (fun () ->
            match waiting with
            | Some source when not source.Task.IsCompleted ->
                waiting <- None
                source.TrySetResult text |> ignore
            | _ -> input.Enqueue text)

    /// Changes the size: the next `Size` reports it and the screen keeps what fits.
    member _.Resize(c: int, r: int) =
        lock gate (fun () ->
            size <- { Columns = c; Rows = r }
            screen.Resize(c, r))

    /// The screen's rows now.
    member _.Lines: string list = lock gate (fun () -> screen.Lines)

    member _.Text: string = lock gate (fun () -> screen.Text)

    member _.Screen: VtScreen = screen

    /// Everything written so far.
    member _.Written: string = lock gate (fun () -> written.ToString())

    /// Completes after the next write.
    member _.Changed: Task = lock gate (fun () -> changed.Task)

    override _.ToString() = $"FakeTty({size})"
