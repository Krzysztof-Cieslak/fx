/// `fx.sandbox.seatbelt` (spec §9.1, §16.4): the sandbox driver of macOS. Every process starts as
/// `/usr/bin/sandbox-exec -p <profile> -D … -- <program>`, confined by a Seatbelt profile built from
/// the policy (`Profile`). `sandbox-exec` is deprecated but present on every macOS release, and it
/// is what Chromium's and other agents' sandboxes use; see README.md for the risk. A native
/// extension built only on the public SDK. See docs/modules/sandbox.md.
module Fx.Ext.SandboxSeatbelt.Seatbelt

open System
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Sdk

/// The extension's id: `fx.sandbox.seatbelt`.
let id = Product.extensionId "sandbox.seatbelt"

/// What specs and the `sandbox.driver` variant name.
[<Literal>]
let Id = "seatbelt"

/// Finds the real paths of the roots (`readlink -f`, or `cd -P` for directories where `readlink`
/// has no `-f`) and the macOS version, one `key=value` line each; the roots come as arguments.
let discoveryScript =
    String.Join(
        "\n",
        [
            "printf 'version=%s\\n' \"$(sw_vers -productVersion 2>/dev/null)\""
            "for p in \"$@\"; do"
            "  if [ -e \"$p\" ] || [ -L \"$p\" ]; then"
            "    r=$(readlink -f -- \"$p\" 2>/dev/null) || r="
            "    if [ -z \"$r\" ] && [ -d \"$p\" ]; then r=$(cd -P -- \"$p\" 2>/dev/null && pwd -P); fi"
            "    [ -n \"$r\" ] && [ \"$r\" != \"$p\" ] && printf 'alias=%s\\t%s\\n' \"$p\" \"$r\""
            "  fi"
            "done"
            "exit 0"
        ]
    )

/// The version and aliases the discovery script printed.
let parseDiscovery (output: string) : string * (string * string) list =
    let lines =
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun line -> line.TrimEnd '\r')
        |> List.ofArray

    let version =
        lines
        |> List.tryPick (fun line ->
            if line.StartsWith("version=", StringComparison.Ordinal) then
                Some(line.Substring("version=".Length).Trim())
            else
                None)
        |> Option.defaultValue ""

    let aliases =
        lines
        |> List.choose (fun line ->
            if line.StartsWith("alias=", StringComparison.Ordinal) then
                match line.Substring("alias=".Length).Split('\t') with
                | [| given; real |] when SandboxPath.isAbsolute given && SandboxPath.isAbsolute real ->
                    Some(SandboxPath.trim given, SandboxPath.trim real)
                | _ -> None
            else
                None)

    version, aliases

let private run (program: Program) : Eff<ISandboxEnv, Result<Output, ShellError>> =
    Shell.run (
        Command.ofSpec
            {
                Program = program
                Cwd = None
                Env = Inherit []
            }
    )

/// The policy the probe tries: the driver's own directory, read-write, no network.
let trialPolicy (site: SandboxSite) : SandboxPolicy =
    {
        Workspace = site.Dir
        WorkspaceAccess = WorkspaceAccess.ReadWrite
        Writable = []
        Readable = []
        Protected = []
        Denied = []
        Temp = None
        Network = NetworkAccess.Denied
    }

/// Whether Seatbelt can sandbox here: macOS, `/usr/bin/sandbox-exec`, and a profile of this driver
/// that runs `/usr/bin/true`.
let probe (site: SandboxSite) : Eff<ISandboxEnv, Result<SandboxReady, SandboxUnavailable>> =
    eff {
        if site.Target.Os <> Os.MacOs then
            return
                Error
                    {
                        Reason = $"Seatbelt runs on macOS only, not {site.Target.Os}"
                        Remedy = None
                    }
        else
            match! Fs.stat Profile.SandboxExec with
            | Error _ ->
                return
                    Error
                        {
                            Reason = $"{Profile.SandboxExec} is missing"
                            Remedy =
                                Some
                                    "Seatbelt's sandbox-exec ships with macOS; set the privileged variant sandbox.driver to none to run unsandboxed"
                        }
            | Ok _ ->
                let roots =
                    site.Roots
                    |> List.filter (fun r -> SandboxPath.isAbsolute r && not (r.Contains '\n' || r.Contains '\t'))

                let! discovered = run (Exec("/bin/sh", [ "-c"; discoveryScript; "sh" ] @ roots))

                let version, aliases =
                    match discovered with
                    | Ok output -> parseDiscovery (Output.stdoutText output)
                    | Error _ -> "", []

                let ready =
                    {
                        Driver = Id
                        Program = Profile.SandboxExec
                        Version = version
                        Features = []
                        Files = Map.empty
                        Aliases = aliases
                        Notes = []
                    }

                let! _ = Fs.createDirectory site.Dir

                let request =
                    {
                        Ready = ready
                        Policy = trialPolicy site
                        Paths = Map.empty
                        Shell = site.Shell
                        Process =
                            {
                                Program = Exec("/usr/bin/true", [])
                                Cwd = None
                                Env = Inherit []
                                Terminal = false
                            }
                    }

                match Profile.wrap request with
                | Error e ->
                    return
                        Error
                            {
                                Reason = $"the trial profile could not be built: {e}"
                                Remedy = None
                            }
                | Ok spec ->
                    match! run spec.Program with
                    | Ok output when output.ExitCode = 0 -> return Ok ready
                    | Ok output ->
                        return
                            Error
                                {
                                    Reason =
                                        $"sandbox-exec refused this driver's profile (exit {output.ExitCode}): {(Output.stderrText output).Trim()}"
                                    Remedy = Some "set the privileged variant sandbox.driver to none to run unsandboxed"
                                }
                    | Error e ->
                        return
                            Error
                                {
                                    Reason = $"sandbox-exec did not start: {e}"
                                    Remedy = None
                                }
    }

/// The driver.
let driver: SandboxDriver =
    {
        Id = Id
        Description =
            "macOS Seatbelt through sandbox-exec: deny by default, the policy's paths as read and write regions, the network only when allowed."
        Platforms = [ Os.MacOs ]
        Confines = true
        Probe = probe
        Wrap = Profile.wrap
    }

let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description = Some "The macOS sandbox: Seatbelt profiles through sandbox-exec."
        Contributions = [ Declare.contribution SandboxDrivers.contributionType Id ]
    }

let extension: Extension =
    {
        Manifest = manifest
        Activate = Eff.ret (Activation.create [ Contribution.create SandboxDrivers.contributionType Id driver ] [])
    }
