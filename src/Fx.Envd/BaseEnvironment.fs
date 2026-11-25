namespace Fx.Envd

open System
open Fx.Effects
open Fx.Pty

/// The variables of the processes envd starts when its host decides them (spec §18: an environment
/// never holds the host's credentials). envd in the host's own process (embedded mode) would
/// otherwise pass the host's variables on; with a base, a process's `Inherit` changes apply to the
/// base instead, so it sees only what the host left in it.
module BaseEnvironment =
    let private named (ignoreCase: bool) (name: string) (other: string) =
        String.Equals(
            name,
            other,
            if ignoreCase then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal
        )

    /// The base with the changes applied in order: `Some` sets a variable (replacing one with the
    /// same name), `None` removes it. Names compare without case on Windows (`ignoreCase`).
    let apply
        (ignoreCase: bool)
        (base': (string * string) list)
        (changes: (string * string option) list)
        : (string * string) list =
        changes
        |> List.fold
            (fun (vars: (string * string) list) (name, value) ->
                let others = vars |> List.filter (fun (n, _) -> not (named ignoreCase n name))

                match value with
                | Some value -> others @ [ name, value ]
                | None -> others)
            base'

    /// `Inherit` changes become exactly the base with the changes; `Exactly` stays as it is.
    let rebase (ignoreCase: bool) (base': (string * string) list) (env: EnvVars) : EnvVars =
        match env with
        | Inherit changes -> Exactly(apply ignoreCase base' changes)
        | Exactly vars -> Exactly vars

    /// A shell whose processes inherit the base instead of the interpreter's own variables.
    let shell (ignoreCase: bool) (base': (string * string) list) (inner: Shell) : Shell =
        {
            Run =
                fun command cancellation ->
                    inner.Run
                        { command with
                            Spec =
                                { command.Spec with
                                    Env = rebase ignoreCase base' command.Spec.Env
                                }
                        }
                        cancellation
            Spawn =
                fun spec cancellation ->
                    inner.Spawn
                        { spec with
                            Env = rebase ignoreCase base' spec.Env
                        }
                        cancellation
        }

    /// A terminal whose processes inherit the base instead of the interpreter's own variables.
    let terminal (ignoreCase: bool) (base': (string * string) list) (inner: Terminal) : Terminal =
        {
            Spawn =
                fun spec cancellation ->
                    inner.Spawn
                        { spec with
                            Env = rebase ignoreCase base' spec.Env
                        }
                        cancellation
        }
