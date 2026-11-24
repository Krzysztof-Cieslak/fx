namespace Fx.Effects

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

/// What a process runs.
type Program =
    /// A program and its arguments, passed as they are (no shell, no quoting to get right).
    | Exec of file: string * args: string list
    /// A command line for the interpreter's shell: `/bin/sh -c` by default on Linux and macOS,
    /// PowerShell 7 (`pwsh -Command`) on Windows. Which shell is a variant (spec §15).
    | Script of script: string

    override this.ToString() =
        match this with
        | Exec(file, args) -> String.concat " " (file :: args)
        | Script script -> $"script: {script}"

/// The environment variables of a process.
type EnvVars =
    /// The interpreter's own variables, with some set (`Some`) or removed (`None`).
    | Inherit of changes: (string * string option) list
    /// Exactly these variables. Prefer this for anything security-sensitive (spec §18).
    | Exactly of vars: (string * string) list

    override this.ToString() =
        match this with
        | Inherit changes ->
            let names = changes |> List.map fst |> String.concat ", "
            $"inherit (changing: {names})"
        | Exactly vars ->
            let names = vars |> List.map fst |> String.concat ", "
            $"exactly: {names}"

/// How to start a process.
type ProcSpec =
    {
        Program: Program
        /// The working directory (absolute); `None` uses the interpreter's.
        Cwd: string option
        Env: EnvVars
    }

    override this.ToString() =
        let cwd = this.Cwd |> Option.defaultValue "<default>"
        $"{this.Program} (in {cwd}, {this.Env})"

/// A process to run to completion, with all of its standard input.
type Command =
    {
        Spec: ProcSpec
        /// Written to standard input, which is then closed (so programs reading it never hang).
        Stdin: byte array
    }

    override this.ToString() =
        $"{this.Spec} with {this.Stdin.Length} bytes of input"

/// The result of a command that ran. A non-zero exit code is data, not an error.
type Output =
    {
        ExitCode: int
        Stdout: byte array
        Stderr: byte array
    }

    override this.ToString() =
        $"exit {this.ExitCode}, {this.Stdout.Length} bytes of output, {this.Stderr.Length} bytes of errors"

/// A piece of a running process's output.
type OutputChunk =
    | Stdout of byte array
    | Stderr of byte array

    override this.ToString() =
        match this with
        | Stdout bytes -> $"stdout ({bytes.Length} bytes)"
        | Stderr bytes -> $"stderr ({bytes.Length} bytes)"

/// A running process. Every operation is a function with data in and out, so it can be recorded.
type Proc =
    {
        /// The OS process id.
        Pid: int
        /// Writes to standard input. Writing after the process exited is silently dropped.
        Write: byte array -> CancellationToken -> Task<unit>
        /// Closes standard input.
        CloseStdin: unit -> Task<unit>
        /// The next piece of output from either stream; `None` once both have ended.
        Read: CancellationToken -> Task<OutputChunk option>
        /// Waits for the process to exit and returns its exit code.
        Wait: CancellationToken -> Task<int>
        /// Kills the process and its descendants. Does nothing if it already exited.
        Kill: unit -> Task<unit>
    }

    override this.ToString() = $"process {this.Pid}"

/// A process that could not be started (an expected failure, e.g. a program that does not exist).
type ShellError =
    | NotStarted of program: string * reason: string

    override this.ToString() =
        match this with
        | NotStarted(program, reason) -> $"could not start {program}: {reason}"

/// The shell capability (spec §4.2): run commands and spawn processes. Pipes only; terminals (PTY,
/// ConPTY) belong to the environment protocol (T111).
///
/// Interpreters: `LocalShell` (Fx.Effects.Live), `ScriptedShell` (Fx.Effects.Fakes).
type Shell =
    {
        /// Runs a command to completion. Cancelling kills the process tree.
        Run: Command -> CancellationToken -> Task<Result<Output, ShellError>>
        /// Starts a process. The caller must make it exit (or `Kill` it); `Shell.withProcess` does.
        Spawn: ProcSpec -> CancellationToken -> Task<Result<Proc, ShellError>>
    }

    override _.ToString() = "Shell"

/// An environment that can run processes.
type IShell =
    abstract Shell: Shell

/// The shell that runs `Script` programs: the program and the arguments before the script.
type ShellSpec =
    {
        Program: string
        Args: string list
    }

    override this.ToString() =
        String.concat " " (this.Program :: this.Args)

module ShellSpec =
    /// `/bin/sh -c <script>`: POSIX, present on every Linux (including Alpine) and macOS.
    let posix = { Program = "/bin/sh"; Args = [ "-c" ] }

    /// `pwsh -Command <script>`: PowerShell 7, the default on Windows. Never assume bash there.
    let pwsh =
        {
            Program = "pwsh"
            Args = [ "-NoLogo"; "-NoProfile"; "-NonInteractive"; "-Command" ]
        }

    /// The program and arguments that run a script with this shell.
    let invocation (shell: ShellSpec) (script: string) : string * string list = shell.Program, shell.Args @ [ script ]

module Command =
    let ofSpec (spec: ProcSpec) : Command = { Spec = spec; Stdin = [||] }

    /// Runs a program with arguments, without a shell.
    let exec (file: string) (args: string list) : Command =
        ofSpec
            {
                Program = Exec(file, args)
                Cwd = None
                Env = Inherit []
            }

    /// Runs a command line with the interpreter's shell.
    let script (script: string) : Command =
        ofSpec
            {
                Program = Script script
                Cwd = None
                Env = Inherit []
            }

    let inDir (cwd: string) (command: Command) : Command =
        { command with
            Spec = { command.Spec with Cwd = Some cwd }
        }

    let withEnv (env: EnvVars) (command: Command) : Command =
        { command with
            Spec = { command.Spec with Env = env }
        }

    let withStdin (input: byte array) (command: Command) : Command = { command with Stdin = input }

    let withStdinText (input: string) (command: Command) : Command =
        withStdin (UTF8Encoding(false).GetBytes input) command

module Output =
    let private utf8 = UTF8Encoding(false)

    /// Standard output as UTF-8 text (invalid bytes become U+FFFD).
    let stdoutText (output: Output) = utf8.GetString output.Stdout

    /// Standard error as UTF-8 text (invalid bytes become U+FFFD).
    let stderrText (output: Output) = utf8.GetString output.Stderr

module Proc =
    /// Writes the input, closes standard input, reads all output and waits for the exit. Cancelling
    /// kills the process tree, waits for it to exit and rethrows. Interpreters build `Run` from this.
    let collect (proc: Proc) (stdin: byte array) (cancellation: CancellationToken) : Task<Output> =
        task {
            try
                if stdin.Length > 0 then
                    do! proc.Write stdin cancellation

                do! proc.CloseStdin()
                use stdout = new MemoryStream()
                use stderr = new MemoryStream()
                let mutable reading = true

                while reading do
                    match! proc.Read cancellation with
                    | Some(Stdout bytes) -> stdout.Write(bytes, 0, bytes.Length)
                    | Some(Stderr bytes) -> stderr.Write(bytes, 0, bytes.Length)
                    | None -> reading <- false

                let! exitCode = proc.Wait cancellation

                return
                    {
                        ExitCode = exitCode
                        Stdout = stdout.ToArray()
                        Stderr = stderr.ToArray()
                    }
            with :? OperationCanceledException as e ->
                do! proc.Kill()
                let! _ = proc.Wait CancellationToken.None
                return Internal.rethrow e
        }

module Shell =
    /// Runs a command to completion. Cancelled with the scope (the process tree is killed).
    let run<'R when 'R :> IShell and 'R :> IScope> (command: Command) : Eff<'R, Result<Output, ShellError>> =
        fun env -> env.Shell.Run command env.Scope.Cancellation

    /// Runs a program with arguments, without a shell.
    let exec (file: string) (args: string list) : Eff<'R, Result<Output, ShellError>> = run (Command.exec file args)

    /// Starts a process. Prefer `withProcess`, which makes sure it does not outlive its use.
    let spawn<'R when 'R :> IShell and 'R :> IScope> (spec: ProcSpec) : Eff<'R, Result<Proc, ShellError>> =
        fun env -> env.Shell.Spawn spec env.Scope.Cancellation

    let write<'R when 'R :> IScope> (proc: Proc) (bytes: byte array) : Eff<'R, unit> =
        fun env -> proc.Write bytes env.Scope.Cancellation

    let closeStdin (proc: Proc) : Eff<'R, unit> = fun _ -> proc.CloseStdin()

    let read<'R when 'R :> IScope> (proc: Proc) : Eff<'R, OutputChunk option> =
        fun env -> proc.Read env.Scope.Cancellation

    let wait<'R when 'R :> IScope> (proc: Proc) : Eff<'R, int> =
        fun env -> proc.Wait env.Scope.Cancellation

    let kill (proc: Proc) : Eff<'R, unit> = fun _ -> proc.Kill()

    /// Starts a process, uses it, and kills it (if still running) when the body ends in any way.
    let withProcess<'R, 'T when 'R :> IShell and 'R :> IScopeWith<'R>>
        (spec: ProcSpec)
        (body: Proc -> Eff<'R, 'T>)
        : Eff<'R, Result<'T, ShellError>> =
        eff {
            match! spawn spec with
            | Error e -> return Error e
            | Ok proc ->
                let! value = Eff.bracket (Eff.ret proc) kill body
                return Ok value
        }
