namespace Fx.Pty

open System
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// The size of a terminal, in character cells.
type PtySize =
    {
        Cols: int
        Rows: int
    }

    override this.ToString() = $"{this.Cols}x{this.Rows}"

module PtySize =
    /// 80 columns by 24 rows: the traditional default.
    let standard = { Cols = 80; Rows = 24 }

    /// Both dimensions must be in 1..32767 (Windows stores them as 16-bit signed numbers).
    let isValid (size: PtySize) =
        size.Cols >= 1 && size.Cols <= 32767 && size.Rows >= 1 && size.Rows <= 32767

/// How to start a process on a new pseudo-terminal.
type PtySpec =
    {
        /// What to run. `Script` uses the terminal's shell (`/bin/sh -c` or `pwsh -Command`).
        Program: Program
        /// The working directory (absolute); `None` uses the terminal's.
        Cwd: string option
        Env: EnvVars
        /// The initial size of the terminal.
        Size: PtySize
    }

    override this.ToString() =
        let cwd = this.Cwd |> Option.defaultValue "<default>"
        $"{this.Program} (in {cwd}, {this.Env}, {this.Size})"

module PtySpec =
    /// Runs a program with arguments on a terminal of the standard size.
    let exec (file: string) (args: string list) : PtySpec =
        {
            Program = Exec(file, args)
            Cwd = None
            Env = Inherit []
            Size = PtySize.standard
        }

    /// Runs a command line with the terminal's shell, on a terminal of the standard size.
    let script (script: string) : PtySpec =
        {
            Program = Script script
            Cwd = None
            Env = Inherit []
            Size = PtySize.standard
        }

/// A process running on a pseudo-terminal (a PTY on Linux and macOS, ConPTY on Windows).
///
/// Every operation is a function with data in and out, like `Proc`, so it can be recorded and
/// sent over the environment protocol. The terminal merges standard output and standard error into
/// one stream of bytes with VT escape sequences, and line endings are the terminal's (`\r\n`).
type Pty =
    {
        /// The OS process id of the program (the session leader on Linux and macOS).
        Pid: int
        /// Types into the terminal: bytes as a keyboard would send them (`\r` is Enter, `\u0003`
        /// is Ctrl+C). Completes once the bytes are handed to the terminal; writing after the
        /// terminal closed is silently dropped. Cancelling stops waiting, not the write.
        Write: byte array -> CancellationToken -> Task<unit>
        /// The next piece of output; `None` once the terminal has closed and everything was read.
        Read: CancellationToken -> Task<byte array option>
        /// Changes the size of the terminal. The program sees the new size (and gets `SIGWINCH` on
        /// Linux and macOS). Does nothing once the terminal has closed.
        Resize: PtySize -> Task<unit>
        /// Waits for the program to exit and returns its exit code (128 + the signal number when a
        /// signal ended it on Linux and macOS).
        Wait: CancellationToken -> Task<int>
        /// Kills the program and its descendants (the process group and tree on Linux and macOS,
        /// the job object on Windows). Does nothing if it already exited. Output can still be read.
        Kill: unit -> Task<unit>
        /// Ends the session: kills what still runs, discards unread output and releases the terminal.
        Close: unit -> Task<unit>
    }

    override this.ToString() = $"pty process {this.Pid}"

/// A terminal that could not be started (an expected failure, e.g. a program that does not exist).
type PtyError =
    | NotStarted of program: string * reason: string
    | InvalidSize of PtySize

    override this.ToString() =
        match this with
        | NotStarted(program, reason) -> $"could not start {program}: {reason}"
        | InvalidSize size -> $"invalid terminal size {size}"

/// Starts processes on pseudo-terminals. Interpreter: `LocalTerminal` (this machine). The environment
/// protocol (T111) exposes the same shape remotely.
type Terminal =
    {
        /// Starts a process on a new terminal. The caller must make it exit or `Close` it;
        /// `Terminal.withPty` does.
        Spawn: PtySpec -> CancellationToken -> Task<Result<Pty, PtyError>>
    }

    override _.ToString() = "Terminal"

/// An environment that can start processes on pseudo-terminals.
type ITerminal =
    abstract Terminal: Terminal

module Terminal =
    /// Starts a process on a new terminal. Prefer `withPty`, which closes it after use.
    let spawn<'R when 'R :> ITerminal and 'R :> IScope> (spec: PtySpec) : Eff<'R, Result<Pty, PtyError>> =
        fun env -> env.Terminal.Spawn spec env.Scope.Cancellation

    let write<'R when 'R :> IScope> (pty: Pty) (bytes: byte array) : Eff<'R, unit> =
        fun env -> pty.Write bytes env.Scope.Cancellation

    let read<'R when 'R :> IScope> (pty: Pty) : Eff<'R, byte array option> =
        fun env -> pty.Read env.Scope.Cancellation

    let resize (pty: Pty) (size: PtySize) : Eff<'R, unit> = fun _ -> pty.Resize size

    let wait<'R when 'R :> IScope> (pty: Pty) : Eff<'R, int> =
        fun env -> pty.Wait env.Scope.Cancellation

    let kill (pty: Pty) : Eff<'R, unit> = fun _ -> pty.Kill()

    let close (pty: Pty) : Eff<'R, unit> = fun _ -> pty.Close()

    /// Starts a process on a terminal, uses it, and closes the terminal when the body ends in any way.
    let withPty<'R, 'T when 'R :> ITerminal and 'R :> IScopeWith<'R>>
        (spec: PtySpec)
        (body: Pty -> Eff<'R, 'T>)
        : Eff<'R, Result<'T, PtyError>> =
        eff {
            match! spawn spec with
            | Error e -> return Error e
            | Ok pty ->
                let! value = Eff.bracket (Eff.ret pty) close body
                return Ok value
        }
