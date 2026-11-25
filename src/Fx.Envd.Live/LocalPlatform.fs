namespace Fx.Envd.Live

open System
open System.IO
open System.Runtime.InteropServices
open Fx
open Fx.Effects
open Fx.Envd

/// What envd finds out about the machine it runs on, once, at startup: the OS, architecture and
/// libc, the shell, case sensitivity, and the locale its terminals need. The only file that reads the
/// process environment for envd.
module LocalPlatform =
    /// Windows PowerShell 5.1, the fallback when PowerShell 7 is not installed.
    let windowsPowerShell: ShellSpec =
        {
            Program = "powershell"
            Args = [ "-NoLogo"; "-NoProfile"; "-NonInteractive"; "-Command" ]
        }

    let os () =
        if OperatingSystem.IsLinux() then "linux"
        elif OperatingSystem.IsMacOS() then "macos"
        elif OperatingSystem.IsWindows() then "windows"
        elif OperatingSystem.IsFreeBSD() then "freebsd"
        else "unknown"

    let arch () =
        match RuntimeInformation.OSArchitecture with
        | Architecture.X64 -> "x64"
        | Architecture.Arm64 -> "arm64"
        | Architecture.X86 -> "x86"
        | Architecture.Arm -> "arm"
        | other -> (string other).ToLowerInvariant()

    let libc () =
        if OperatingSystem.IsLinux() then
            if RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal) then
                Some "musl"
            else
                Some "glibc"
        else
            None

    /// Whether names compare case-sensitively in a directory: the directory's path with its case
    /// swapped must not exist. Without letters in the path, the OS's default.
    let caseSensitive (directory: string) : bool =
        let swapped =
            directory
            |> String.map (fun c ->
                if Char.IsUpper c then Char.ToLowerInvariant c
                elif Char.IsLower c then Char.ToUpperInvariant c
                else c)

        if swapped = directory then
            not (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        else
            not (Directory.Exists swapped)

    /// A program on `PATH` (with `.exe` on Windows).
    let findOnPath (program: string) : string option =
        let path =
            Environment.GetEnvironmentVariable "PATH"
            |> Option.ofObj
            |> Option.defaultValue ""

        let suffix = if OperatingSystem.IsWindows() then ".exe" else ""

        path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun dir -> Path.Combine(dir.Trim('"'), program + suffix))
        |> Array.tryFind File.Exists

    /// The shell for `Script` programs (spec §6.5): PowerShell 7 if it is on `PATH`, else Windows
    /// PowerShell, on Windows; `/bin/sh` elsewhere. A choice made by the host (`shell.windows`) wins.
    let defaultShell () : ShellSpec =
        if OperatingSystem.IsWindows() then
            if (findOnPath "pwsh").IsSome then
                ShellSpec.pwsh
            else
                windowsPowerShell
        else
            ShellSpec.posix

    /// A shell by name: `pwsh`, `powershell` or `sh`.
    let shellNamed (name: string) : ShellSpec option =
        match name with
        | "pwsh" -> Some ShellSpec.pwsh
        | "powershell" -> Some windowsPowerShell
        | "sh" -> Some ShellSpec.posix
        | _ -> None

    /// What terminals get (docs/spikes/pty.md): `TERM=xterm-256color`; a UTF-8 `LC_CTYPE` on Linux
    /// and macOS when envd's own environment has no locale; UTF-8 console encodings for PowerShell.
    let terminalDefaults (shell: ShellSpec) : TerminalDefaults =
        let hasLocale =
            [ "LC_ALL"; "LC_CTYPE"; "LANG" ]
            |> List.exists (fun name -> not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable name)))

        let locale =
            if hasLocale || OperatingSystem.IsWindows() then
                None
            elif OperatingSystem.IsMacOS() then
                Some("LC_CTYPE", "UTF-8")
            else
                Some("LC_CTYPE", "C.UTF-8")

        let powerShell =
            let name = Path.GetFileNameWithoutExtension shell.Program

            String.Equals(name, "pwsh", StringComparison.OrdinalIgnoreCase)
            || String.Equals(name, "powershell", StringComparison.OrdinalIgnoreCase)

        {
            Term = "xterm-256color"
            Locale = locale
            PowerShellUtf8 = OperatingSystem.IsWindows() && powerShell
        }

    /// The platform envd announces.
    let detect (workspace: string) (shell: ShellSpec) (features: string list) : Platform =
        {
            Os = os ()
            Arch = arch ()
            Libc = libc ()
            Shell = shell
            PathSeparator = string Path.DirectorySeparatorChar
            CaseSensitive = caseSensitive workspace
            Workspace = workspace
            Version = Product.Version
            Features = features
        }
