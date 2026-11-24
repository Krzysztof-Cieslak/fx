namespace Fx.Pty

open System
open System.Threading.Tasks
open Fx.Effects

/// Starts processes on pseudo-terminals on this machine: a PTY on Linux and macOS, ConPTY on
/// Windows. Findings and platform notes: docs/spikes/pty.md.
module LocalTerminal =
    /// The default shell for `Script` programs here: PowerShell 7 on Windows, `/bin/sh` elsewhere.
    /// Which shell to use is a variant (spec §15); this is only the bootstrap default.
    let defaultShell () : ShellSpec =
        if OperatingSystem.IsWindows() then
            ShellSpec.pwsh
        else
            ShellSpec.posix

    /// Starts one process on a new terminal.
    let spawn (shell: ShellSpec) (cwd: string) (spec: PtySpec) : Result<Pty, PtyError> =
        if not (PtySize.isValid spec.Size) then
            Error(InvalidSize spec.Size)
        else
            let info = StartInfo.create shell cwd spec

            if OperatingSystem.IsWindows() then
                WindowsPty.spawn info spec.Size
            else
                UnixPty.spawn info spec.Size

    /// A terminal that runs `Script` programs with `shell` and starts processes in `cwd` unless a
    /// spec says otherwise.
    let create (shell: ShellSpec) (cwd: string) : Terminal =
        {
            Spawn =
                fun spec cancellation ->
                    if cancellation.IsCancellationRequested then
                        Task.FromCanceled<Result<Pty, PtyError>> cancellation
                    else
                        Task.FromResult(spawn shell cwd spec)
        }
