/// `fx.sandbox.bubblewrap` (spec §9.1, §16.4): the sandbox driver of Linux. Every process starts as
/// `bwrap … -- <program>` in new namespaces (user, pid, ipc, uts, cgroup, and network unless the
/// policy allows it), with the root file system read-only, the policy's paths mounted over it in
/// order (longer paths last), a private `/tmp`, and a seccomp filter (`Seccomp`). bwrap and its
/// children die with envd (`--die-with-parent`). A native extension built only on the public SDK.
/// See README.md and docs/modules/sandbox.md.
module Fx.Ext.SandboxBubblewrap.Bubblewrap

open System
open System.Text
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Sdk

/// The extension's id: `fx.sandbox.bubblewrap`.
let id = Product.extensionId "sandbox.bubblewrap"

/// What specs and the `sandbox.driver` variant name.
[<Literal>]
let Id = "bubblewrap"

/// Optional abilities the probe finds.
module Features =
    /// bwrap ≥ 0.8 stops sandboxed processes from creating user namespaces (`--disable-userns`).
    [<Literal>]
    let DisableUserns = "disable-userns"

    /// The seccomp filter loads (`--seccomp`).
    [<Literal>]
    let Seccomp = "seccomp"

/// The name of the seccomp program among the probe's files.
[<Literal>]
let SeccompFile = "seccomp"

/// The POSIX shell that hands bwrap the seccomp program: bwrap reads it from a descriptor, and
/// processes start with none but the standard three. `sh -c '<launcher>' <file> <bwrap> <args>…`
/// opens the file as descriptor 3 for bwrap alone.
[<Literal>]
let Sh = "/bin/sh"

[<Literal>]
let SeccompLauncher = "exec \"$@\" 3< \"$0\""

/// What a process starts as: the namespaces and the base of the file system, before the policy's
/// paths. Pipes get a new session (no controlling terminal to type into); terminals keep theirs
/// (the seccomp filter refuses `TIOCSTI`).
let baseArgs (ready: SandboxReady) (network: NetworkAccess) (terminal: bool) : string list =
    [
        "--die-with-parent"
        "--unshare-all"
        if network = NetworkAccess.Allowed then
            "--share-net"
        if List.contains Features.DisableUserns ready.Features then
            // --unshare-all only tries a user namespace; --disable-userns needs one for sure.
            "--unshare-user"
            "--disable-userns"
        if not terminal then
            "--new-session"
        "--ro-bind"
        "/"
        "/"
        "--dev"
        "/dev"
        "--proc"
        "/proc"
        "--tmpfs"
        "/tmp"
    ]

/// The mounts of a policy's paths, shortest first so that longer ones go over them: denied
/// directories become empty tmpfs mounts (read-only once everything inside is mounted) and denied
/// files `/dev/null`; readable and protected paths are bound read-only, writable ones read-write.
/// The directories between a writable root and a read-only or denied path inside it are bound onto
/// themselves: mount points cannot be renamed, and a rename would carry the inner path's mount
/// away. Paths that do not exist are left out.
let mounts (request: SandboxRequest) : Result<string list, string> =
    let policy = request.Policy
    let rules = SandboxPolicy.rules policy |> List.filter (fun rule -> rule.Root <> "/")

    let ancestors =
        SandboxPolicy.guarded policy
        |> List.filter (fun dir -> Sandbox.kindOf request dir = PathKind.Directory)
        |> List.map (fun dir ->
            {
                Root = dir
                Access = SandboxPolicy.access policy dir
            })

    let all = rules @ ancestors |> List.sortBy (fun rule -> rule.Root.Length, rule.Root)

    let targets =
        all
        |> List.map (fun rule ->
            match Sandbox.target request rule.Root with
            | Ok target -> Ok(rule, target)
            // Only a path that must be hidden or kept read-only fails the start; access the
            // sandbox cannot grant is simply not granted.
            | Error e when
                rule.Access <> PathAccess.ReadWrite
                && not (policy.Readable |> List.exists (fun r -> SandboxPath.trim r = rule.Root))
                ->
                Error e
            | Error _ -> Ok(rule, None))

    match
        targets
        |> List.choose (fun t ->
            match t with
            | Error e -> Some e
            | Ok _ -> None)
    with
    | _ :: _ as errors -> Error(String.Join("; ", errors))
    | [] ->
        let placed =
            targets
            |> List.choose (fun t ->
                match t with
                | Ok(rule, Some(target, kind)) -> Some(rule, target, kind)
                | _ -> None)

        let mounts =
            placed
            |> List.collect (fun (rule, target, kind) ->
                match rule.Access, kind with
                | PathAccess.NoAccess, PathKind.Directory -> [ "--tmpfs"; target ]
                | PathAccess.NoAccess, _ -> [ "--ro-bind"; "/dev/null"; target ]
                | PathAccess.ReadOnly, _ -> [ "--ro-bind"; target; target ]
                | PathAccess.ReadWrite, _ -> [ "--bind"; target; target ])

        let remounts =
            placed
            |> List.filter (fun (rule, _, kind) -> rule.Access = PathAccess.NoAccess && kind = PathKind.Directory)
            |> List.collect (fun (_, target, _) -> [ "--remount-ro"; target ])

        Ok(mounts @ remounts)

/// The process that runs the request in bwrap. Pure.
let wrap (request: SandboxRequest) : Result<ProcSpec, string> =
    let ready = request.Ready
    let proc = request.Process

    match mounts request with
    | Error e -> Error e
    | Ok mounts ->
        let program, args = Sandbox.commandLine request.Shell proc.Program

        let bwrapArgs =
            baseArgs ready request.Policy.Network proc.Terminal
            @ mounts
            @ [
                match proc.Cwd with
                | Some cwd ->
                    "--chdir"
                    Sandbox.realPath ready cwd
                | None -> ()
                "--"
                program
                yield! args
            ]

        let command =
            match ready.Files.TryFind SeccompFile with
            | Some file when List.contains Features.Seccomp ready.Features ->
                Exec(Sh, [ "-c"; SeccompLauncher; file; ready.Program; "--seccomp"; "3" ] @ bwrapArgs)
            | _ -> Exec(ready.Program, bwrapArgs)

        Ok
            {
                Program = command
                Cwd = proc.Cwd
                Env = Sandbox.withTemp request.Policy proc.Env
            }

// ---- The probe ------------------------------------------------------------------------------------

/// What the discovery script found.
type Discovery =
    {
        /// bwrap on `PATH` (absolute), if any.
        Bwrap: string option
        Version: string
        /// `kernel.apparmor_restrict_unprivileged_userns` (Ubuntu ≥ 23.10).
        AppArmorRestricts: bool
        /// `kernel.unprivileged_userns_clone = 0` (older Debian kernels).
        UsernsCloneOff: bool
        /// `user.max_user_namespaces = 0`.
        NoUserNamespaces: bool
        Aliases: (string * string) list
    }

    override this.ToString() = $"bwrap {this.Version}"

/// Finds bwrap and its version, the kernel's user-namespace settings, and the real paths of the
/// roots (`readlink -f`), one `key=value` line each; the roots come as the script's arguments.
let discoveryScript =
    String.Join(
        "\n",
        [
            "b=$(command -v bwrap 2>/dev/null) || b="
            "printf 'bwrap=%s\\n' \"$b\""
            "[ -n \"$b\" ] && printf 'version=%s\\n' \"$(\"$b\" --version 2>/dev/null)\""
            "printf 'apparmor=%s\\n' \"$(cat /proc/sys/kernel/apparmor_restrict_unprivileged_userns 2>/dev/null)\""
            "printf 'userns_clone=%s\\n' \"$(cat /proc/sys/kernel/unprivileged_userns_clone 2>/dev/null)\""
            "printf 'max_userns=%s\\n' \"$(cat /proc/sys/user/max_user_namespaces 2>/dev/null)\""
            "for p in \"$@\"; do"
            "  if [ -e \"$p\" ] || [ -L \"$p\" ]; then"
            "    r=$(readlink -f -- \"$p\" 2>/dev/null) && [ -n \"$r\" ] && [ \"$r\" != \"$p\" ] && printf 'alias=%s\\t%s\\n' \"$p\" \"$r\""
            "  fi"
            "done"
            "exit 0"
        ]
    )

/// Reads the discovery script's output.
let parseDiscovery (output: string) : Discovery =
    let lines =
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun line -> line.TrimEnd '\r')
        |> List.ofArray

    let value (key: string) =
        lines
        |> List.tryPick (fun line ->
            if line.StartsWith(key + "=", StringComparison.Ordinal) then
                Some(line.Substring(key.Length + 1).Trim())
            else
                None)
        |> Option.defaultValue ""

    let version =
        let raw = value "version"

        if raw.StartsWith("bubblewrap ", StringComparison.Ordinal) then
            raw.Substring("bubblewrap ".Length)
        else
            raw

    {
        Bwrap =
            match value "bwrap" with
            | "" -> None
            | path -> Some path
        Version = version
        AppArmorRestricts = value "apparmor" = "1"
        UsernsCloneOff = value "userns_clone" = "0"
        NoUserNamespaces = value "max_userns" = "0"
        Aliases =
            lines
            |> List.choose (fun line ->
                if line.StartsWith("alias=", StringComparison.Ordinal) then
                    match line.Substring("alias=".Length).Split('\t') with
                    | [| given; real |] when SandboxPath.isAbsolute given && SandboxPath.isAbsolute real ->
                        Some(SandboxPath.trim given, SandboxPath.trim real)
                    | _ -> None
                else
                    None)
    }

/// Whether a bwrap version is at least `major.minor`.
let atLeast (major: int) (minor: int) (version: string) : bool =
    match version.Split('.') |> Array.map Int32.TryParse |> List.ofArray with
    | (true, a) :: (true, b) :: _ -> a > major || (a = major && b >= minor)
    | _ -> false

/// What to do about user namespaces that bwrap cannot create, from what the kernel says.
let explain (discovery: Discovery) (errors: string) : SandboxUnavailable =
    let said =
        let trimmed = errors.Trim()

        if trimmed = "" then "" else $" (bwrap: {trimmed})"

    if discovery.AppArmorRestricts then
        {
            Reason =
                "bubblewrap cannot create its sandbox: AppArmor restricts unprivileged user namespaces (kernel.apparmor_restrict_unprivileged_userns = 1)"
                + said
            Remedy =
                Some(
                    "let bwrap create them with an AppArmor profile: write /etc/apparmor.d/bwrap with "
                    + "'abi <abi/4.0>, include <tunables/global> profile bwrap /usr/bin/bwrap flags=(unconfined) { userns, }' "
                    + "and run 'sudo apparmor_parser -r /etc/apparmor.d/bwrap' (or turn the restriction off: "
                    + "'sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0'); see docs/modules/sandbox.md"
                )
        }
    elif discovery.UsernsCloneOff then
        {
            Reason =
                "bubblewrap cannot create its sandbox: the kernel does not let unprivileged users create user namespaces (kernel.unprivileged_userns_clone = 0)"
                + said
            Remedy = Some "allow them: 'sudo sysctl -w kernel.unprivileged_userns_clone=1'"
        }
    elif discovery.NoUserNamespaces then
        {
            Reason =
                "bubblewrap cannot create its sandbox: user namespaces are turned off (user.max_user_namespaces = 0)"
                + said
            Remedy = Some "allow them: 'sudo sysctl -w user.max_user_namespaces=15000'"
        }
    else
        {
            Reason = "bubblewrap could not create a sandbox" + said
            Remedy =
                Some
                    "in a container, bwrap needs user namespaces (e.g. run the container with --security-opt seccomp=unconfined and --security-opt apparmor=unconfined); otherwise set the privileged variant sandbox.driver back to none (its default)"
        }

/// A command that starts bwrap's sandbox and exits 0 in it: what the probe tries.
let trial (ready: SandboxReady) : Program =
    let args = baseArgs ready NetworkAccess.Denied false @ [ "--"; Sh; "-c"; "exit 0" ]

    match ready.Files.TryFind SeccompFile with
    | Some file when List.contains Features.Seccomp ready.Features ->
        Exec(Sh, [ "-c"; SeccompLauncher; file; ready.Program; "--seccomp"; "3" ] @ args)
    | _ -> Exec(ready.Program, args)

let private run (program: Program) : Eff<ISandboxEnv, Result<Output, ShellError>> =
    Shell.run (
        Command.ofSpec
            {
                Program = program
                Cwd = None
                Env = Inherit []
            }
    )

/// Whether bubblewrap can sandbox here: bwrap on `PATH`, a sandbox it can create (the trial), then
/// `--disable-userns` (bwrap ≥ 0.8) and the seccomp filter, each kept only if the trial still
/// passes with it. Writes the seccomp program into the site's directory.
let probe (site: SandboxSite) : Eff<ISandboxEnv, Result<SandboxReady, SandboxUnavailable>> =
    eff {
        if site.Target.Os <> Os.Linux then
            return
                Error
                    {
                        Reason = $"bubblewrap runs on Linux only, not {site.Target.Os}"
                        Remedy = None
                    }
        else
            let roots =
                site.Roots
                |> List.filter (fun r -> SandboxPath.isAbsolute r && not (r.Contains '\n' || r.Contains '\t'))

            match! run (Exec(Sh, [ "-c"; discoveryScript; "sh" ] @ roots)) with
            | Error e ->
                return
                    Error
                        {
                            Reason = $"the probe could not run {Sh}: {e}"
                            Remedy = None
                        }
            | Ok output ->
                let discovery = parseDiscovery (Output.stdoutText output)

                match discovery.Bwrap with
                | None ->
                    return
                        Error
                            {
                                Reason = "bubblewrap (bwrap) is not installed"
                                Remedy =
                                    Some
                                        "install it: 'sudo apt install bubblewrap', 'sudo dnf install bubblewrap', 'sudo apk add bubblewrap' or 'sudo pacman -S bubblewrap'"
                            }
                | Some bwrap ->
                    let basic =
                        {
                            Driver = Id
                            Program = bwrap
                            Version = discovery.Version
                            Features = []
                            Files = Map.empty
                            Aliases = discovery.Aliases
                            Notes = []
                        }

                    let tries (ready: SandboxReady) : Eff<ISandboxEnv, Result<unit, string>> =
                        eff {
                            match! run (trial ready) with
                            | Error e -> return Error(string e)
                            | Ok output when output.ExitCode = 0 -> return Ok()
                            | Ok output -> return Error(Output.stderrText output)
                        }

                    match! tries basic with
                    | Error errors -> return Error(explain discovery errors)
                    | Ok() ->
                        let! withUserns =
                            eff {
                                if atLeast 0 8 discovery.Version then
                                    let candidate =
                                        { basic with
                                            Features = [ Features.DisableUserns ]
                                        }

                                    match! tries candidate with
                                    | Ok() -> return candidate
                                    | Error _ ->
                                        return
                                            { basic with
                                                Notes =
                                                    [
                                                        "bwrap refused --disable-userns: the seccomp filter alone stops new user namespaces"
                                                    ]
                                            }
                                else
                                    return
                                        { basic with
                                            Notes =
                                                [
                                                    $"bwrap {discovery.Version} has no --disable-userns (0.8 has): the seccomp filter alone stops new user namespaces"
                                                ]
                                        }
                            }

                        let file = SandboxPath.under site.Dir $"seccomp-{site.Target.Arch}.bpf"
                        let! made = Fs.createDirectory site.Dir
                        let! written = Fs.writeFile file (Seccomp.program site.Target.Arch)

                        match made, written with
                        | Ok(), Ok() ->
                            let candidate =
                                { withUserns with
                                    Features = withUserns.Features @ [ Features.Seccomp ]
                                    Files = Map.ofList [ SeccompFile, file ]
                                }

                            match! tries candidate with
                            | Ok() -> return Ok candidate
                            | Error errors ->
                                return
                                    Ok
                                        { withUserns with
                                            Notes =
                                                withUserns.Notes
                                                @ [
                                                    $"the seccomp filter did not load ({errors.Trim()}): processes run without it"
                                                ]
                                        }
                        | Error e, _
                        | _, Error e ->
                            return
                                Ok
                                    { withUserns with
                                        Notes =
                                            withUserns.Notes
                                            @ [
                                                $"the seccomp filter could not be written ({e}): processes run without it"
                                            ]
                                    }
    }

/// The driver.
let driver: SandboxDriver =
    {
        Id = Id
        Description =
            "Linux namespaces with bubblewrap (bwrap) and a seccomp filter: a read-only system, the policy's paths mounted over it, the network only when allowed."
        Platforms = [ Os.Linux ]
        Confines = true
        Probe = probe
        Wrap = wrap
    }

let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description = Some "The Linux sandbox: bubblewrap namespaces and a seccomp filter."
        Contributions = [ Declare.contribution SandboxDrivers.contributionType Id ]
    }

let extension: Extension =
    {
        Manifest = manifest
        Activate = Eff.ret (Activation.create [ Contribution.create SandboxDrivers.contributionType Id driver ] [])
    }
