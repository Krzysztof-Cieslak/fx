namespace Fx.Host

open System.IO
open Fx

/// Where a host keeps what it owns, under one per-user data directory (spec §20 "Naming": `~/.fx`, or
/// `%LOCALAPPDATA%\fx` on Windows). Every path is absolute; nothing here touches the disk.
type HostPaths =
    {
        /// The data directory itself.
        Data: string
        /// The host's config file: `variants`, `environments`, `extensions` (docs/modules/host.md).
        Config: string
        /// The host's policy file: pins and forbids over every layer (`Policy.ofJson`).
        Policy: string
        /// The lock only one host process holds: two hosts never open one set of stores.
        Lock: string
        /// The event database (SQLite).
        Events: string
        /// The blob store's directory.
        Blobs: string
        /// The projects the user trusts (`fx trust`).
        Trust: string
        /// The host's stable machine id, created once.
        MachineId: string
        /// The component installer's root (the same `runtimes` directory environments on this machine
        /// use: `EnvLayout`).
        Runtimes: string
        /// The owner-only directory of the file credential store (`LocalPrivateFiles`).
        Credentials: string
    }

    override this.ToString() = $"HostPaths({this.Data})"

[<RequireQualifiedAccess>]
module HostPaths =
    /// The variable that overrides the data directory (`FX_HOME`).
    let homeVariable = Product.envVar "HOME"

    /// The paths under a data directory.
    let under (data: string) : HostPaths =
        let state = Path.Combine(data, "state")

        {
            Data = data
            Config = Path.Combine(data, "config.json")
            Policy = Path.Combine(data, "policy.json")
            Lock = Path.Combine(data, "host.lock")
            Events = Path.Combine(state, "events.db")
            Blobs = Path.Combine(state, "blobs")
            Trust = Path.Combine(data, "trust.json")
            MachineId = Path.Combine(data, "machine-id")
            Runtimes = Path.Combine(data, "runtimes")
            Credentials = Path.Combine(data, "credentials")
        }

    /// The per-user data directory from the process's variables: `FX_HOME` if set, else
    /// `%LOCALAPPDATA%\fx` on Windows and `$HOME/.fx` elsewhere. Relative values are refused: the data
    /// directory must not depend on where a command runs.
    let dataDirectory (variable: string -> string option) (windows: bool) : Result<string, string> =
        let set (name: string) =
            variable name |> Option.filter (fun value -> value.Trim() <> "")

        let absolute (name: string) (path: string) =
            if Path.IsPathFullyQualified path then
                Ok(Path.TrimEndingDirectorySeparator path)
            else
                Error $"{name} must be an absolute path, got {path}"

        match set homeVariable with
        | Some home -> absolute homeVariable home
        | None when windows ->
            match set "LOCALAPPDATA" with
            | Some local ->
                absolute "LOCALAPPDATA" local
                |> Result.map (fun d -> Path.Combine(d, Product.windowsDataDirName))
            | None -> Error $"neither {homeVariable} nor LOCALAPPDATA is set: no place for the host's data"
        | None ->
            match set "HOME" with
            | Some home ->
                absolute "HOME" home
                |> Result.map (fun d -> Path.Combine(d, Product.homeDirName))
            | None -> Error $"neither {homeVariable} nor HOME is set: no place for the host's data"

    /// The project's config file (`.fx/config.json` in the workspace), read only once the project is
    /// trusted (spec §18).
    let projectConfig (workspace: string) : string =
        Path.Combine(workspace, Product.projectDirName, "config.json")
