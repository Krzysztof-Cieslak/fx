/// The `ProcessStartInfo` for a terminal process. .NET resolves the program and builds the argument
/// and environment arrays; the platform modules then create the process themselves.
module internal Fx.Pty.StartInfo

open System.Diagnostics
open Fx.Effects

let create (shell: ShellSpec) (defaultCwd: string) (spec: PtySpec) : ProcessStartInfo =
    let file, args =
        match spec.Program with
        | Exec(file, args) -> file, args
        | Script script -> ShellSpec.invocation shell script

    let info =
        ProcessStartInfo(file, WorkingDirectory = Option.defaultValue defaultCwd spec.Cwd, UseShellExecute = false)

    for arg in args do
        info.ArgumentList.Add arg

    match spec.Env with
    | Inherit changes ->
        for name, value in changes do
            match value with
            | Some value -> info.Environment[name] <- value
            | None -> info.Environment.Remove name |> ignore
    | Exactly vars ->
        info.Environment.Clear()

        for name, value in vars do
            info.Environment[name] <- value

    info
