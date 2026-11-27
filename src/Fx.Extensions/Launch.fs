namespace Fx.Extensions

open System
open System.IO
open Fx.Effects
open Fx.Sdk

/// What the host knows when it starts an extension process: facts the composition root read once.
type LaunchContext =
    {
        /// The package directory (`${root}`).
        Root: string
        /// The workspace, for `cwd = workspace`.
        Workspace: string option
        /// The host's environment variables, from which the allowlist picks.
        HostEnvironment: (string * string) list
        /// Windows: `.exe` suffix, and variable names compared without case.
        IsWindows: bool
        /// Until the project is trusted, the launch's `UntrustedOverrides` are added.
        ProjectTrusted: bool
        /// The commands of the components the extension requires, as the installer placed them
        /// (`${bin.<name>}`): name and absolute path.
        Bin: (string * string) list
    }

    override this.ToString() = $"LaunchContext({this.Root})"

/// Turns a manifest's `LaunchSpec` (data) into the process to start (spec §6.2, §18).
[<RequireQualifiedAccess>]
module Launch =
    /// Replaces `${root}`, `${exeSuffix}` and `${bin.<name>}` (a command of a component the
    /// extension requires).
    let expand (context: LaunchContext) (text: string) : string =
        context.Bin
        |> List.fold
            (fun (text: string) (name, path) -> text.Replace("${bin." + name + "}", path, StringComparison.Ordinal))
            (text
                .Replace("${root}", context.Root, StringComparison.Ordinal)
                .Replace("${exeSuffix}", (if context.IsWindows then ".exe" else ""), StringComparison.Ordinal))

    /// The first `${bin.<name>}` left in a template: a command no component the extension requires
    /// exports.
    let private missingCommand (text: string) : string option =
        match text.IndexOf("${bin.", StringComparison.Ordinal) with
        | -1 -> None
        | start ->
            match text.IndexOf('}', start) with
            | -1 -> Some(text.Substring start)
            | close -> Some(text.Substring(start, close - start + 1))

    /// The process's variables: the allowlisted host variables, then `EnvSet` (which wins), without any
    /// that a deny pattern matches: the manifest's and the host's injection vectors, always.
    let environment (context: LaunchContext) (spec: LaunchSpec) : (string * string) list =
        let matches (patterns: string list) (name: string) =
            patterns
            |> List.exists (fun pattern -> Manifest.matchesEnvPattern context.IsWindows pattern name)

        let set = spec.EnvSet |> List.map (fun (name, value) -> name, expand context value)
        let setNames = set |> List.map fst

        let inherited =
            context.HostEnvironment
            |> List.filter (fun (name, _) -> matches spec.EnvAllowlist name && not (matches setNames name))

        inherited @ set
        |> List.filter (fun (name, _) -> not (matches (Manifest.deniedEnvironment @ spec.EnvDeny) name))

    /// The process to start, or why it cannot be.
    let procSpec (context: LaunchContext) (spec: LaunchSpec) : Result<ProcSpec, string> =
        let cwd =
            match spec.Cwd with
            | LaunchCwd.Owned -> Ok context.Root
            | LaunchCwd.Workspace ->
                match context.Workspace with
                | Some workspace -> Ok workspace
                | None -> Error "the extension runs in the workspace, and there is none"

        let exe = expand context spec.Exe

        let missing =
            spec.Exe :: spec.Args @ spec.UntrustedOverrides
            |> List.tryPick (expand context >> missingCommand)

        // A bare program name is looked up on PATH; a relative path is relative to the package.
        let exe =
            if Path.IsPathRooted exe || (exe.IndexOf '/' < 0 && exe.IndexOf '\\' < 0) then
                exe
            else
                Path.Combine(context.Root, exe)

        match missing with
        | Some placeholder ->
            Error $"its launch names {placeholder}, a command that none of the components it requires exports"
        | None ->
            cwd
            |> Result.map (fun cwd ->
                {
                    Program =
                        Exec(
                            exe,
                            LaunchSpec.arguments
                                context.ProjectTrusted
                                (spec.UntrustedOverrides |> List.map (expand context))
                                (spec.Args |> List.map (expand context))
                        )
                    Cwd = Some cwd
                    Env = Exactly(environment context spec)
                })
