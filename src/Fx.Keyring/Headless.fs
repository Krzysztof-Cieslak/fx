namespace Fx.Keyring

open System
open Fx.Effects

/// The operating system families with a keyring backend.
[<RequireQualifiedAccess>]
type HostOs =
    | Linux
    | MacOS
    | Windows
    | Other of string

    override this.ToString() =
        match this with
        | Linux -> "linux"
        | MacOS -> "macos"
        | Windows -> "windows"
        | Other name -> name

/// What decides whether a keyring can be used, read once by the composition root
/// (`LocalHostFacts.read`) and passed on as a value, so the decisions below are pure and testable.
type HostFacts =
    {
        Os: HostOs
        /// The environment variables `Headless.variables` lists that are set.
        Variables: Map<string, string>
        /// Linux: whether `$XDG_RUNTIME_DIR/bus` exists (the systemd user bus).
        RuntimeBusExists: bool
        /// Linux: the kernel is WSL's (`/proc/sys/kernel/osrelease` mentions Microsoft).
        WslKernel: bool
        /// Linux: a container marker exists (`/.dockerenv`, `/run/.containerenv`).
        ContainerMarker: bool
    }

    override this.ToString() = $"HostFacts({this.Os})"

/// The kind of session the process runs in.
type HostSession =
    {
        Ssh: bool
        /// Linux: a display server (`DISPLAY` or `WAYLAND_DISPLAY`) that a prompt can appear on.
        Display: bool
        Wsl: bool
        Container: bool
        /// A CI runner (`CI` is set): nobody answers prompts.
        Ci: bool
    }

    override this.ToString() =
        let flags =
            [
                if this.Ssh then
                    "ssh"
                if this.Display then
                    "display"
                if this.Wsl then
                    "wsl"
                if this.Container then
                    "container"
                if this.Ci then
                    "ci"
            ]

        if flags.IsEmpty then "local" else String.concat "," flags

/// How the host picks and configures its keyring (spec §11.4) on a machine that may be headless.
module Headless =
    /// The environment variables the decisions read.
    let variables =
        [
            "DBUS_SESSION_BUS_ADDRESS"
            "XDG_RUNTIME_DIR"
            "DISPLAY"
            "WAYLAND_DISPLAY"
            "SSH_CONNECTION"
            "SSH_CLIENT"
            "SSH_TTY"
            "WSL_DISTRO_NAME"
            "WSL_INTEROP"
            "CI"
            "container"
        ]

    let private isSet (facts: HostFacts) (name: string) =
        match facts.Variables.TryFind name with
        | Some value -> not (String.IsNullOrEmpty value)
        | None -> false

    let session (facts: HostFacts) : HostSession =
        let set = isSet facts

        {
            Ssh = set "SSH_CONNECTION" || set "SSH_CLIENT" || set "SSH_TTY"
            Display = set "DISPLAY" || set "WAYLAND_DISPLAY"
            Wsl =
                facts.Os = HostOs.Linux
                && (facts.WslKernel || set "WSL_DISTRO_NAME" || set "WSL_INTEROP")
            Container = facts.Os = HostOs.Linux && (facts.ContainerMarker || set "container")
            Ci = set "CI"
        }

    /// Escapes a value in a D-Bus address (`unix:path=<value>`): bytes outside the optionally
    /// escaped set become `%xx`.
    let escapeAddressValue (value: string) : string =
        let sb = Text.StringBuilder()

        for b in Text.Encoding.UTF8.GetBytes value do
            let c = char b

            if Char.IsAsciiLetterOrDigit c || "-_/.\\*".Contains c then
                sb.Append c |> ignore
            else
                sb.Append('%').Append(b.ToString("x2")) |> ignore

        sb.ToString()

    /// The session bus to look for the Secret Service on (Linux): `DBUS_SESSION_BUS_ADDRESS`, else the
    /// systemd user bus `$XDG_RUNTIME_DIR/bus` (what libdbus and sd-bus fall back to). `None`: there
    /// is no session bus, so there is no Secret Service (containers, most CI, WSL without systemd).
    /// X11 autolaunch is never used: it starts a bus nobody else shares.
    let sessionBus (facts: HostFacts) : string option =
        match facts.Variables.TryFind "DBUS_SESSION_BUS_ADDRESS" with
        | Some address when not (String.IsNullOrWhiteSpace address) -> Some address
        | _ ->
            match facts.Variables.TryFind "XDG_RUNTIME_DIR" with
            | Some dir when not (String.IsNullOrEmpty dir) && facts.RuntimeBusExists ->
                let path = dir.TrimEnd('/') + "/bus"
                Some("unix:path=" + escapeAddressValue path)
            | _ -> None

    /// Whether a keyring prompt (unlock, access confirmation) can reach the user: never in CI or over
    /// SSH, and on Linux only with a display. A keyring opened without prompts fails with
    /// `KeyringError.Locked` instead of waiting for an answer that never comes.
    let prompts (facts: HostFacts) : Prompts =
        let s = session facts

        let canAsk =
            not s.Ci
            && not s.Ssh
            && (match facts.Os with
                | HostOs.Linux -> s.Display
                | HostOs.MacOS
                | HostOs.Windows -> true
                | HostOs.Other _ -> false)

        if canAsk then Prompts.Allow else Prompts.Never
