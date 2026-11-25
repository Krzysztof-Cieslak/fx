namespace Fx.Envd

open System
open Fx.Effects
open Fx.Pty

/// What envd gives the programs it starts on terminals (docs/spikes/pty.md, recommendations 3 and 4):
/// a terminal type, a UTF-8 locale where the environment has none, and UTF-8 console encodings for
/// PowerShell scripts on Windows (a console starts with the OEM code page, which loses non-ASCII
/// input). A spec that sets these itself keeps its values.
type TerminalDefaults =
    {
        /// `TERM`, e.g. `xterm-256color`.
        Term: string
        /// A locale variable to set (e.g. `LC_CTYPE=C.UTF-8`), decided by the composition root from
        /// envd's own environment; `None` to leave the locale alone.
        Locale: (string * string) option
        /// Prefix `Script` programs with PowerShell's UTF-8 console encodings (Windows, PowerShell).
        PowerShellUtf8: bool
    }

    override this.ToString() = $"TERM={this.Term}"

module TerminalSetup =
    /// The prefix that makes PowerShell read and write its console as UTF-8.
    let powerShellUtf8Prefix =
        "[Console]::InputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); "

    let private localeNames = [ "LANG"; "LC_ALL"; "LC_CTYPE" ]

    let private named (name: string) (other: string) =
        String.Equals(name, other, StringComparison.OrdinalIgnoreCase)

    /// The spec with the defaults applied.
    let apply (defaults: TerminalDefaults) (spec: PtySpec) : PtySpec =
        let program =
            match spec.Program with
            | Script script when defaults.PowerShellUtf8 -> Script(powerShellUtf8Prefix + script)
            | other -> other

        let env =
            match spec.Env with
            | Inherit changes ->
                let has name =
                    changes |> List.exists (fun (n, _) -> named n name)

                let term = if has "TERM" then [] else [ "TERM", Some defaults.Term ]

                let locale =
                    match defaults.Locale with
                    | Some(name, value) when not (localeNames |> List.exists has) -> [ name, Some value ]
                    | _ -> []

                Inherit(term @ locale @ changes)
            | Exactly vars ->
                let has name =
                    vars |> List.exists (fun (n, _) -> named n name)

                let term = if has "TERM" then [] else [ "TERM", defaults.Term ]

                let locale =
                    match defaults.Locale with
                    | Some(name, value) when not (localeNames |> List.exists has) -> [ name, value ]
                    | _ -> []

                Exactly(term @ locale @ vars)

        { spec with
            Program = program
            Env = env
        }

    /// A terminal that applies the defaults to every spec.
    let wrap (defaults: TerminalDefaults) (terminal: Terminal) : Terminal =
        {
            Spawn = fun spec cancellation -> terminal.Spawn (apply defaults spec) cancellation
        }
