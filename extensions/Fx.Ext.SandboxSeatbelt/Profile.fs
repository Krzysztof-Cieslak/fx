/// The Seatbelt profile (SBPL) `fx.sandbox.seatbelt` hands `sandbox-exec`: deny by default, then
/// what a process needs to run, the policy's paths as read and write regions, and the network only
/// when allowed. Paths go in as parameters (`-D P0=<path>`, `(param "P0")`), never into the
/// profile's text, so no path needs quoting. Pure. See README.md.
module Fx.Ext.SandboxSeatbelt.Profile

open System
open Fx.Sdk

/// The program: a fixed path, never one looked up on `PATH`.
[<Literal>]
let SandboxExec = "/usr/bin/sandbox-exec"

/// macOS's top-level links (`/tmp`, `/var`, `/etc` live under `/private`): Seatbelt matches the
/// real path, so a rule on a path below them names the `/private` one too.
let private systemAliases =
    [ "/tmp", "/private/tmp"; "/var", "/private/var"; "/etc", "/private/etc" ]

/// The forms of a path a rule must match: as given, its real path (the probe's aliases), and both
/// below macOS's top-level links.
let forms (ready: SandboxReady) (path: string) : string list =
    let path = SandboxPath.trim path
    let real = Sandbox.realPath ready path

    let underPrivate (p: string) =
        systemAliases
        |> List.tryPick (fun (link, target) ->
            if SandboxPath.isUnder link p then
                Some(target + p.Substring link.Length)
            else
                None)

    [ path; real ] @ ([ path; real ] |> List.choose underPrivate) |> List.distinct

/// Every operation a process needs to run at all: start programs, signal and inspect its own
/// sandbox, the system information programs read (a list of sysctls, not all: some describe other
/// processes), user lookups, POSIX semaphores, terminals, and the devices everyone writes to.
let baseRules: string list =
    [
        "(version 1)"
        "(deny default)"
        "(allow process-exec)"
        "(allow process-fork)"
        "(allow signal (target same-sandbox))"
        "(allow process-info* (target same-sandbox))"
        "(allow sysctl-read"
        "  (sysctl-name \"hw.activecpu\") (sysctl-name \"hw.byteorder\") (sysctl-name \"hw.cachelinesize_compat\")"
        "  (sysctl-name \"hw.cpufamily\") (sysctl-name \"hw.cputype\") (sysctl-name \"hw.cpusubtype\")"
        "  (sysctl-name \"hw.l1dcachesize\") (sysctl-name \"hw.l1icachesize\") (sysctl-name \"hw.l2cachesize\") (sysctl-name \"hw.l3cachesize\")"
        "  (sysctl-name \"hw.logicalcpu\") (sysctl-name \"hw.logicalcpu_max\") (sysctl-name \"hw.machine\") (sysctl-name \"hw.model\")"
        "  (sysctl-name \"hw.memsize\") (sysctl-name \"hw.ncpu\") (sysctl-name \"hw.nperflevels\") (sysctl-name \"hw.pagesize\")"
        "  (sysctl-name \"hw.pagesize_compat\") (sysctl-name \"hw.physicalcpu\") (sysctl-name \"hw.physicalcpu_max\")"
        "  (sysctl-name \"hw.tbfrequency\") (sysctl-name \"hw.tbfrequency_compat\") (sysctl-name \"hw.vectorunit\")"
        "  (sysctl-name-prefix \"hw.optional.\") (sysctl-name-prefix \"hw.perflevel\")"
        "  (sysctl-name \"machdep.cpu.brand_string\") (sysctl-name-prefix \"machdep.cpu.\")"
        "  (sysctl-name \"kern.argmax\") (sysctl-name \"kern.hostname\") (sysctl-name \"kern.maxfilesperproc\") (sysctl-name \"kern.maxproc\")"
        "  (sysctl-name \"kern.osproductversion\") (sysctl-name \"kern.osrelease\") (sysctl-name \"kern.ostype\") (sysctl-name \"kern.osvariant_status\")"
        "  (sysctl-name \"kern.osversion\") (sysctl-name \"kern.secure_kernel\") (sysctl-name \"kern.usrstack64\") (sysctl-name \"kern.version\")"
        "  (sysctl-name \"kern.boottime\") (sysctl-name \"kern.sysv.semmns\") (sysctl-name \"kern.ngroups\") (sysctl-name \"kern.bootargs\")"
        "  (sysctl-name \"sysctl.proc_cputype\") (sysctl-name \"sysctl.proc_translated\") (sysctl-name \"vm.loadavg\") (sysctl-name \"vm.footprint_suspend\")"
        "  (sysctl-name-prefix \"kern.proc.pid.\") (sysctl-name-prefix \"kern.proc.pgrp.\") (sysctl-name-prefix \"net.routetable.\"))"
        "(allow sysctl-write (sysctl-name \"kern.grade_cputype\"))"
        "(allow iokit-open (iokit-registry-entry-class \"RootDomainUserClient\"))"
        "(allow mach-lookup"
        "  (global-name \"com.apple.system.opendirectoryd.libinfo\")"
        "  (global-name \"com.apple.system.opendirectoryd.membership\")"
        "  (global-name \"com.apple.bsd.dirhelper\")"
        "  (global-name \"com.apple.PowerManagement.control\"))"
        "(allow ipc-posix-sem)"
        "(allow pseudo-tty)"
        "(allow file-read* file-write* file-ioctl (literal \"/dev/ptmx\") (literal \"/dev/tty\"))"
        "(allow file-read* file-write* file-ioctl (regex #\"^/dev/ttys[0-9]+$\"))"
        "(allow file-write-data file-ioctl (literal \"/dev/null\") (literal \"/dev/zero\") (literal \"/dev/dtracehelper\"))"
        "(allow file-write* (literal \"/dev/stdout\") (literal \"/dev/stderr\") (regex #\"^/dev/fd/[0-9]+$\"))"
    ]

/// What the network needs when it is allowed: sockets, the services that resolve names and check
/// certificates, the routing table. Unix sockets under denied paths and the user's SSH agent stay
/// closed (the profile's last rules).
let networkRules: string list =
    [
        "(allow network-outbound)"
        "(allow network-inbound)"
        "(allow network-bind)"
        "(allow system-socket)"
        "(allow mach-lookup"
        "  (global-name \"com.apple.SecurityServer\")"
        "  (global-name \"com.apple.networkd\")"
        "  (global-name \"com.apple.ocspd\")"
        "  (global-name \"com.apple.trustd.agent\")"
        "  (global-name \"com.apple.SystemConfiguration.DNSConfiguration\")"
        "  (global-name \"com.apple.SystemConfiguration.configd\")"
        "  (global-name \"com.apple.dnssd.service\"))"
    ]

/// A profile with its parameters.
type Rendered =
    {
        Rules: string list
        /// `(name, path)`: passed as `-D name=path`.
        Parameters: (string * string) list
    }

    override this.ToString() =
        $"seatbelt profile ({this.Rules.Length} lines, {this.Parameters.Length} parameters)"

/// The profile of a request. A path's region is the subpath of its root minus the roots inside it,
/// which have their own rules: the longest root containing a path decides, as `SandboxPolicy.access`
/// says. The last rules refuse to rename or delete the directories a writable root guards
/// (`SandboxPolicy.guarded`) and the paths kept read-only in it: Seatbelt matches paths, and a
/// rename would carry what is inside past its rules.
let render (request: SandboxRequest) : Rendered =
    let ready = request.Ready
    let policy = request.Policy
    let rules = SandboxPolicy.rules policy
    let parameters = ResizeArray<string * string>()

    let param (path: string) : string =
        match parameters |> Seq.tryFind (fun (_, p) -> p = path) with
        | Some(name, _) -> name
        | None ->
            let name = $"P{parameters.Count}"
            parameters.Add((name, path))
            name

    let subpaths (path: string) : string list =
        forms ready path |> List.map (fun form -> $"(subpath (param \"{param form}\"))")

    let literals (path: string) : string list =
        forms ready path |> List.map (fun form -> $"(literal (param \"{param form}\"))")

    let anyOf (filters: string list) =
        match filters with
        | [ one ] -> one
        | many -> "(require-any " + String.Join(" ", many) + ")"

    /// The region of a root: its subpath minus the roots directly inside it.
    let region (root: string) : string =
        let inner =
            rules
            |> List.filter (fun r -> r.Root <> root && SandboxPath.isUnder root r.Root)
            |> List.filter (fun r ->
                // Directly inside: no other inner root between them.
                not (
                    rules
                    |> List.exists (fun m ->
                        m.Root <> root
                        && m.Root <> r.Root
                        && SandboxPath.isUnder root m.Root
                        && SandboxPath.isUnder m.Root r.Root)
                ))

        let within =
            if root = "/" then
                "(subpath \"/\")"
            else
                anyOf (subpaths root)

        match inner with
        | [] -> within
        | inner ->
            let exclusions =
                inner
                |> List.collect (fun r -> subpaths r.Root)
                |> List.map (fun s -> $"(require-not {s})")

            "(require-all " + within + " " + String.Join(" ", exclusions) + ")"

    let topLevel =
        {
            Root = "/"
            Access = PathAccess.ReadOnly
        }

    let all =
        if rules |> List.exists (fun r -> r.Root = "/") then
            rules
        else
            topLevel :: rules

    let reads =
        all
        |> List.filter (fun r -> r.Access <> PathAccess.NoAccess)
        |> List.map (fun r -> $"(allow file-read* {region r.Root})")

    let writes =
        all
        |> List.filter (fun r -> r.Access = PathAccess.ReadWrite)
        |> List.map (fun r -> $"(allow file-write* {region r.Root})")

    // The denied directories above a readable root inside them (the data directory above an
    // environment's owned directories): their own metadata, not their content, so that programs
    // that look at each directory on the way (`mkdir -p`, `realpath`) reach the root, as they do
    // under bubblewrap, where those directories are empty mounts.
    let lookups =
        rules
        |> List.filter (fun r -> r.Access <> PathAccess.NoAccess)
        |> List.collect (fun r ->
            SandboxPath.between "/" r.Root
            |> List.filter (fun dir -> dir <> r.Root && SandboxPolicy.access policy dir = PathAccess.NoAccess))
        |> List.distinct
        |> List.map (fun dir -> $"(allow file-read-metadata {anyOf (literals dir)})")

    let keptInPlace =
        let readOnlyInside =
            rules
            |> List.filter (fun inner ->
                inner.Access <> PathAccess.ReadWrite
                && rules
                   |> List.exists (fun outer ->
                       outer.Access = PathAccess.ReadWrite
                       && outer.Root <> inner.Root
                       && SandboxPath.isUnder outer.Root inner.Root))
            |> List.map _.Root

        SandboxPolicy.guarded policy @ readOnlyInside
        |> List.distinct
        |> List.map (fun dir -> $"(deny file-write-unlink {anyOf (literals dir)})")

    let closing =
        if policy.Network = NetworkAccess.Allowed then
            [
                yield!
                    policy.Denied
                    |> List.map (fun denied ->
                        $"(deny network-outbound (remote unix-socket {anyOf (subpaths denied)}))")
                // The user's SSH agent (`/private/tmp/com.apple.launchd.*/Listeners`) and other
                // programs' sockets in the shared temporary directory.
                "(deny network-outbound (remote unix-socket (subpath \"/private/tmp\")))"
                // The environment's own sockets, though its directories are inside the denied data
                // directory: .NET's named pipes are in TMPDIR (MSBuild's nodes talk over them).
                yield!
                    policy.Writable @ Option.toList policy.Temp
                    |> List.map (fun own -> $"(allow network-outbound (remote unix-socket {anyOf (subpaths own)}))")
            ]
        else
            []

    let rendered =
        baseRules
        @ reads
        @ lookups
        @ writes
        @ (if policy.Network = NetworkAccess.Allowed then
               networkRules
           else
               [])
        @ keptInPlace
        @ closing

    {
        Rules = rendered
        Parameters = List.ofSeq parameters
    }

/// The process that runs the request under `sandbox-exec`. Pure.
let wrap (request: SandboxRequest) : Result<Fx.Effects.ProcSpec, string> =
    let proc = request.Process
    let rendered = render request
    let program, args = Sandbox.commandLine request.Shell proc.Program

    let arguments =
        [
            "-p"
            String.Join("\n", rendered.Rules)
            yield! rendered.Parameters |> List.map (fun (name, path) -> $"-D{name}={path}")
            "--"
            program
            yield! args
        ]

    Ok
        {
            Program = Fx.Effects.Exec(SandboxExec, arguments)
            Cwd = proc.Cwd
            Env = Sandbox.withTemp request.Policy proc.Env
        }
