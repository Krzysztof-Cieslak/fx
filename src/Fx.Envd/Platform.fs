namespace Fx.Envd

open System
open Fx.Effects

/// What an environment runs on, announced by envd in the `initialize` handshake (spec §9.2
/// `Platform`), so the host never applies its own path, shell or case rules to the environment.
type Platform =
    {
        /// `linux`, `macos`, `windows`, or another lowercase OS name.
        Os: string
        /// `x64`, `arm64`, `x86`, `arm`, or another lowercase architecture name.
        Arch: string
        /// `glibc` or `musl` on Linux; `None` elsewhere.
        Libc: string option
        /// The shell that runs `Script` programs: `/bin/sh -c`, or PowerShell on Windows.
        Shell: ShellSpec
        /// The directory separator of paths in the environment (`/` or `\`).
        PathSeparator: string
        /// Whether file names in the workspace compare case-sensitively.
        CaseSensitive: bool
        /// The workspace: an absolute path, and the default working directory of processes.
        Workspace: string
        /// The version of envd.
        Version: string
        /// The optional method groups envd serves (`Feature.*`).
        Features: string list
    }

    override this.ToString() =
        let libc =
            match this.Libc with
            | Some libc -> "-" + libc
            | None -> ""

        let features = String.Join(", ", this.Features)
        $"{this.Os}{libc}-{this.Arch} (shell {this.Shell}, workspace {this.Workspace}, features: {features})"

/// The optional method groups of the environment protocol.
module Feature =
    /// Processes on pseudo-terminals (`exec/start` with a terminal).
    [<Literal>]
    let Pty = "pty"

    /// Watching directories (`watch/*`).
    [<Literal>]
    let Watch = "watch"

    /// Workspace snapshots in a shadow git repository (`snapshot/*`); needs the git CLI.
    [<Literal>]
    let Snapshots = "snapshots"

module Platform =
    /// Whether envd announced a feature.
    let has (feature: string) (platform: Platform) = List.contains feature platform.Features
