namespace Fx.Sdk

open System
open System.Text.RegularExpressions
open Fx
open Fx.Contracts
open Fx.Effects

/// Whether sandboxed processes may change the workspace (spec §7.1: a reviewer gets it read-only).
[<RequireQualifiedAccess>]
type WorkspaceAccess =
    | ReadWrite
    | ReadOnly

    override this.ToString() =
        match this with
        | ReadWrite -> "read-write"
        | ReadOnly -> "read-only"

/// Whether sandboxed processes may use the network. Allowlists need the egress proxy (later).
[<RequireQualifiedAccess>]
type NetworkAccess =
    | Allowed
    | Denied

    override this.ToString() =
        match this with
        | Allowed -> "allowed"
        | Denied -> "denied"

/// What a sandboxed process may do with a path, from least to most.
[<RequireQualifiedAccess>]
type PathAccess =
    /// Neither read nor written: hidden, or refused.
    | NoAccess
    | ReadOnly
    | ReadWrite

    override this.ToString() =
        match this with
        | NoAccess -> "none"
        | ReadOnly -> "read-only"
        | ReadWrite -> "read-write"

/// One root of a policy: every path under it (itself included) has its access, unless a longer
/// root of the policy contains the path too.
type SandboxRule =
    {
        Root: string
        Access: PathAccess
    }

    override this.ToString() = $"{this.Root}: {this.Access}"

/// What sandboxed processes may do (spec §9.1, §18): data, built by the host for an environment and
/// only ever tightened for an agent (`SandboxOverrides`). Paths are absolute, in the environment's
/// syntax. A path's access is the access of the longest root below that contains it
/// (`SandboxPolicy.access`); everything else is read-only.
type SandboxPolicy =
    {
        /// The workspace: its access is `WorkspaceAccess`.
        Workspace: string
        WorkspaceAccess: WorkspaceAccess
        /// Directories processes may change besides the workspace: the owned directories of
        /// runtimes' launches, the environment's temporary directory.
        Writable: string list
        /// Directories processes may read inside denied ones: the components' install root and
        /// interpreters' assets under the product's data directory.
        Readable: string list
        /// Paths that stay read-only inside writable ones (spec §8.2 protected paths; by default
        /// the workspace's git hooks and config and the project's own directory).
        Protected: string list
        /// Paths processes may neither read nor change: credentials (`SandboxPolicy.secrets`) and
        /// the product's data directory.
        Denied: string list
        /// The directory for temporary files: `TMPDIR` of sandboxed processes. Writable.
        Temp: string option
        Network: NetworkAccess
    }

    override this.ToString() =
        $"sandbox policy (workspace {this.Workspace} {this.WorkspaceAccess}, network {this.Network})"

/// What an agent's sandbox may change about its environment's (spec §7.1 `SandboxOverrides`): it
/// can only take access away. A project or an agent definition can never widen a sandbox.
type SandboxOverrides =
    {
        /// `ReadOnly` makes the workspace read-only; `ReadWrite` changes nothing.
        Workspace: WorkspaceAccess option
        /// `Denied` takes the network away; `Allowed` changes nothing.
        Network: NetworkAccess option
        /// More protected paths: absolute, or relative to the workspace (with `/`).
        Protect: string list
        /// More denied paths: absolute, or relative to the workspace (with `/`).
        Deny: string list
    }

    override this.ToString() =
        let workspace =
            this.Workspace |> Option.map string |> Option.defaultValue "unchanged"

        $"sandbox overrides (workspace {workspace})"

/// A process to start in a sandbox.
type SandboxProcess =
    {
        Program: Program
        /// The working directory (absolute); `None`: envd's (the workspace).
        Cwd: string option
        Env: EnvVars
        /// On a pseudo-terminal: it keeps its controlling terminal (no new session).
        Terminal: bool
    }

    override this.ToString() =
        let terminal = if this.Terminal then " on a terminal" else ""
        $"{this.Program}{terminal}"

/// Whether a path a policy names exists when a process starts, and what it is.
[<RequireQualifiedAccess>]
type PathKind =
    | Directory
    | File
    /// A symbolic link (not followed): drivers use the real path the probe found, if it found one.
    | Symlink
    | Missing

    override this.ToString() =
        match this with
        | Directory -> "directory"
        | File -> "file"
        | Symlink -> "symlink"
        | Missing -> "missing"

/// What a driver's probe gets to know about an environment.
type SandboxSite =
    {
        /// The environment's machine.
        Target: TargetTriple
        /// envd's shell: how `Script` programs run (`/bin/sh -c`).
        Shell: ShellSpec
        /// A directory of the environment for the driver's own files (e.g. a seccomp program):
        /// outside the workspace, never writable by sandboxed processes.
        Dir: string
        /// Paths whose symbolic links the driver may resolve once: the workspace, the user's home,
        /// the product's data directory, the environment's home.
        Roots: string list
    }

    override this.ToString() = $"sandbox site ({this.Target})"

/// What a driver's probe found: it can sandbox processes here, and how.
type SandboxReady =
    {
        /// The driver's id.
        Driver: string
        /// The program that sandboxes (absolute), e.g. `/usr/bin/bwrap`; empty when nothing does.
        Program: string
        Version: string
        /// Optional abilities found, e.g. `seccomp`, `disable-userns`.
        Features: string list
        /// Files the probe wrote, by name (e.g. `seccomp`): absolute paths in the environment.
        Files: Map<string, string>
        /// Roots whose real path differs, `(as given, real)`: rules name both.
        Aliases: (string * string) list
        /// What a person may want to know (a missing optional feature).
        Notes: string list
    }

    override this.ToString() =
        let features = String.Join(", ", this.Features)
        $"{this.Driver} {this.Version} ({features})"

/// Why a driver cannot sandbox here, and what would change that.
type SandboxUnavailable =
    {
        Reason: string
        /// What a person can do about it, e.g. install the program or allow user namespaces.
        Remedy: string option
    }

    override this.ToString() =
        match this.Remedy with
        | Some remedy -> $"{this.Reason} ({remedy})"
        | None -> this.Reason

/// One process start for a driver to wrap: everything comes in, so wrapping is pure.
type SandboxRequest =
    {
        Ready: SandboxReady
        Policy: SandboxPolicy
        /// What every path of the policy and its real path (`Sandbox.pathsToStat`) is right now.
        Paths: Map<string, PathKind>
        /// envd's shell: how `Script` programs run.
        Shell: ShellSpec
        Process: SandboxProcess
    }

    override this.ToString() = $"sandbox {this.Process}"

/// The environment a sandbox driver's probe runs in: its extension's environment and the
/// environment's own files and processes, unsandboxed (drivers are privileged contributions).
type ISandboxEnv =
    inherit IExtensionEnv
    inherit IFsRead
    inherit IFs
    inherit IShell

/// A sandbox driver (spec §9.1, §18): the process boundary of an environment. It wraps every
/// process the agent starts there (commands, terminals, kernels) into one that the OS confines to a
/// `SandboxPolicy`. Contributed by native extensions under `SandboxDrivers.contributionType`
/// (privileged), with the id as the contribution id. Module spec: docs/modules/sandbox.md.
[<NoEquality; NoComparison>]
type SandboxDriver =
    {
        /// What specs and the `sandbox.driver` variant name, e.g. `bubblewrap`.
        Id: string
        Description: string
        /// The operating systems it runs on.
        Platforms: Os list
        /// Whether it confines processes at all: only `none` does not, and environments using it
        /// carry a persistent warning (spec §9.1).
        Confines: bool
        /// Whether it can sandbox in this environment (run once per connection to envd, through
        /// envd's own, unsandboxed processes): what it found, or why not and what would fix it.
        Probe: SandboxSite -> Eff<ISandboxEnv, Result<SandboxReady, SandboxUnavailable>>
        /// The process that runs the requested one in the sandbox. Pure.
        Wrap: SandboxRequest -> Result<ProcSpec, string>
    }

    override this.ToString() = $"sandbox driver {this.Id}"

/// The environment a driver's probe runs in, built by the host for one driver.
type SandboxEnv =
    {
        Extension: IExtensionEnv
        FsRead: FsRead
        Fs: Fs
        Shell: Shell
    }

    interface ISandboxEnv

    interface IExtensionEnv with
        member env.Extension = env.Extension.Extension

    interface IClock with
        member env.Clock = env.Extension.Clock

    interface IScopeWith<IExtensionEnv> with
        member env.Scope = env.Extension.Scope

        member env.WithScope scope =
            { env with
                Extension = env.Extension.WithScope scope
            }
            :> IExtensionEnv

    interface IFsRead with
        member env.FsRead = env.FsRead

    interface IFs with
        member env.Fs = env.Fs

    interface IShell with
        member env.Shell = env.Shell

    override env.ToString() =
        $"SandboxEnv({env.Extension.Extension})"

[<RequireQualifiedAccess>]
module SandboxEnv =
    /// Runs driver code in the driver's environment with the caller's scope.
    let within (sandboxEnv: SandboxEnv) (effect: Eff<ISandboxEnv, 'T>) : Eff<#IScope, 'T> =
        fun caller ->
            effect
                { sandboxEnv with
                    Extension = sandboxEnv.Extension.WithScope caller.Scope
                }

/// Paths in an environment's syntax, compared as sandboxes see them.
[<RequireQualifiedAccess>]
module SandboxPath =
    /// Whether a path is absolute: `/…` (Linux, macOS), `C:\…` or `\\…` (Windows).
    let isAbsolute (path: string) : bool =
        path.StartsWith '/'
        || path.StartsWith @"\\"
        || Regex.IsMatch(path, @"^[A-Za-z]:[\\/]")

    let private separatorOf (root: string) : char =
        if root.StartsWith '/' then '/' else '\\'

    /// The path without trailing separators (a root stays a root).
    let trim (path: string) : string =
        let trimmed = path.TrimEnd([| '/'; '\\' |])

        if trimmed = "" then
            path.Substring(0, min 1 path.Length)
        elif trimmed.Length = 2 && trimmed.EndsWith ':' then
            path.Substring(0, 3)
        else
            trimmed

    /// Whether `path` is `root` or inside it.
    let isUnder (root: string) (path: string) : bool =
        let root = trim root
        let path = trim path

        if root = "/" then
            path.StartsWith '/'
        else
            path = root
            || (path.Length > root.Length
                && path.StartsWith(root, StringComparison.Ordinal)
                && (path[root.Length] = '/' || path[root.Length] = '\\'))

    /// A relative path (with `/`) under a directory, in the directory's syntax.
    let under (dir: string) (relative: string) : string =
        let separator = separatorOf dir
        let parts = relative.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
        let dir = trim dir
        let joined = String.Join(string separator, parts)

        if joined = "" then dir
        elif dir.EndsWith separator then dir + joined
        else dir + string separator + joined

    /// The path itself if absolute, else under the workspace.
    let resolve (workspace: string) (path: string) : string =
        if isAbsolute path then trim path else under workspace path

    /// An absolute path with `.` and `..` segments resolved lexically (a `..` at the root stays at
    /// the root), and no doubled or trailing separators; a relative path as it is.
    let normalize (path: string) : string =
        if not (isAbsolute path) then
            path
        else
            let separator = separatorOf path

            let root, rest =
                if path.StartsWith '/' then "/", path.Substring 1
                elif path.StartsWith @"\\" then @"\\", path.Substring 2
                else path.Substring(0, 3), path.Substring 3

            let segments =
                rest.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.fold
                    (fun (kept: string list) segment ->
                        match segment, kept with
                        | ".", _ -> kept
                        | "..", _ :: rest -> rest
                        | "..", [] -> []
                        | _ -> segment :: kept)
                    []
                |> List.rev

            root + String.Join(string separator, segments)

    /// A relative path that stays under where it is resolved: no `..` or `.` segments, not empty.
    let isPlainRelative (path: string) : bool =
        let parts = path.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

        not (isAbsolute path)
        && parts.Length > 0
        && parts |> Array.forall (fun p -> p <> ".." && p <> ".")

    /// The directories from `root` (exclusive) down to `path` (inclusive), outermost first; empty
    /// when `path` is not strictly under `root`.
    let between (root: string) (path: string) : string list =
        let root = trim root
        let path = trim path

        if path = root || not (isUnder root path) then
            []
        else
            let separator = separatorOf root

            let rest =
                path.Substring(root.Length).Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

            rest
            |> Array.scan (fun (dir: string) (part: string) -> dir.TrimEnd separator + string separator + part) root
            |> Array.skip 1
            |> List.ofArray

[<RequireQualifiedAccess>]
module SandboxPolicy =
    /// Credentials under a user's home that sandboxed processes never read (spec §18: credentials
    /// live on the host), relative to the home with `/`: SSH and GPG keys, cloud and cluster
    /// credentials, registry and forge tokens, password stores and keyrings. A guardrail of known
    /// places, not a boundary: keep secrets in the OS keyring.
    let homeSecrets: string list =
        [
            ".ssh"
            ".gnupg"
            ".aws"
            ".azure"
            ".config/gcloud"
            ".kube"
            ".docker"
            ".netrc"
            ".git-credentials"
            ".config/git/credentials"
            ".config/gh"
            ".config/hub"
            ".config/glab-cli"
            ".npmrc"
            ".yarnrc.yml"
            ".pypirc"
            ".config/pip/pip.conf"
            ".cargo/credentials"
            ".cargo/credentials.toml"
            ".gem/credentials"
            ".nuget/NuGet/NuGet.Config"
            ".m2/settings.xml"
            ".gradle/gradle.properties"
            ".terraform.d/credentials.tfrc.json"
            ".vault-token"
            ".config/containers/auth.json"
            ".password-store"
            ".config/op"
            ".local/share/keyrings"
            "Library/Keychains"
        ]

    /// Paths outside the home whose sockets reach more than the sandbox should: the user's
    /// runtime directories (the D-Bus session bus, which serves the Secret Service; agents) and the
    /// container engines' sockets.
    let systemSecrets (os: Os) : string list =
        match os with
        | Os.Linux -> [ "/run/user"; "/run/docker.sock"; "/run/podman"; "/run/containerd" ]
        | Os.MacOs -> [ "/var/run/docker.sock" ]
        | Os.Windows -> []

    /// The default denied paths of an environment on `os` whose user's home is `home`.
    let secrets (os: Os) (home: string option) : string list =
        [
            match home with
            | Some home -> yield! homeSecrets |> List.map (SandboxPath.under home)
            | None -> ()
            yield! systemSecrets os
        ]

    /// What stays read-only in a writable workspace by default: git's hooks and config (they run
    /// code the next time a person uses git there) and the project's own directory (`.<product>`:
    /// its agents, workflows and extensions run once the project is trusted).
    let protectedInWorkspace: string list =
        [ ".git/hooks"; ".git/config"; Product.projectDirName ]

    let private rank (access: PathAccess) =
        match access with
        | PathAccess.NoAccess -> 0
        | PathAccess.ReadOnly -> 1
        | PathAccess.ReadWrite -> 2

    /// Every root of the policy with its access, shortest first (ties by name). A root listed
    /// twice keeps its most restrictive access.
    let rules (policy: SandboxPolicy) : SandboxRule list =
        let workspace =
            match policy.WorkspaceAccess with
            | WorkspaceAccess.ReadWrite -> PathAccess.ReadWrite
            | WorkspaceAccess.ReadOnly -> PathAccess.ReadOnly

        [
            policy.Workspace, workspace
            yield! policy.Writable |> List.map (fun p -> p, PathAccess.ReadWrite)
            match policy.Temp with
            | Some temp -> temp, PathAccess.ReadWrite
            | None -> ()
            yield! policy.Readable |> List.map (fun p -> p, PathAccess.ReadOnly)
            yield! policy.Protected |> List.map (fun p -> p, PathAccess.ReadOnly)
            yield! policy.Denied |> List.map (fun p -> p, PathAccess.NoAccess)
        ]
        |> List.map (fun (root, access) -> SandboxPath.trim root, access)
        |> List.groupBy fst
        |> List.map (fun (root, entries) ->
            {
                Root = root
                Access = entries |> List.map snd |> List.minBy rank
            })
        |> List.sortBy (fun r -> r.Root.Length, r.Root)

    /// The access of a path: that of the longest root containing it, read-only outside every root.
    let access (policy: SandboxPolicy) (path: string) : PathAccess =
        rules policy
        |> List.filter (fun r -> SandboxPath.isUnder r.Root path)
        |> List.tryLast
        |> Option.map _.Access
        |> Option.defaultValue PathAccess.ReadOnly

    /// Every path the policy names: what a driver needs to know exists before a process starts.
    let paths (policy: SandboxPolicy) : string list = rules policy |> List.map _.Root

    /// The directories between each writable root and a read-only or denied root inside it (not
    /// roots themselves), outermost first: renaming one would carry the inner root's path away,
    /// so drivers keep them in place (bubblewrap binds them onto themselves; Seatbelt refuses to
    /// unlink them).
    let guarded (policy: SandboxPolicy) : string list =
        let rules = rules policy

        rules
        |> List.filter (fun rule -> rule.Access <> PathAccess.ReadWrite)
        |> List.collect (fun inner ->
            rules
            |> List.filter (fun outer ->
                outer.Access = PathAccess.ReadWrite
                && outer.Root.Length < inner.Root.Length
                && SandboxPath.isUnder outer.Root inner.Root)
            |> List.tryLast
            |> Option.map (fun outer ->
                SandboxPath.between outer.Root inner.Root
                |> List.filter (fun dir -> dir <> inner.Root && not (rules |> List.exists (fun r -> r.Root = dir))))
            |> Option.defaultValue [])
        |> List.distinct
        |> List.sortBy (fun dir -> dir.Length, dir)

    /// The policy with an agent's overrides (spec §7.1): only ever less access.
    let tighten (overrides: SandboxOverrides) (policy: SandboxPolicy) : SandboxPolicy =
        let resolve = List.map (SandboxPath.resolve policy.Workspace)

        { policy with
            WorkspaceAccess =
                match overrides.Workspace with
                | Some WorkspaceAccess.ReadOnly -> WorkspaceAccess.ReadOnly
                | _ -> policy.WorkspaceAccess
            Network =
                match overrides.Network with
                | Some NetworkAccess.Denied -> NetworkAccess.Denied
                | _ -> policy.Network
            Protected = policy.Protected @ resolve overrides.Protect |> List.distinct
            Denied = policy.Denied @ resolve overrides.Deny |> List.distinct
        }

    let workspaceAccess: Codec<WorkspaceAccess> =
        Codec.enum
            [
                WorkspaceAccess.ReadWrite, "read-write"
                WorkspaceAccess.ReadOnly, "read-only"
            ]
        |> Codec.describe "Whether sandboxed processes may change the workspace."
        |> Codec.named "WorkspaceAccess"

    let networkAccess: Codec<NetworkAccess> =
        Codec.enum [ NetworkAccess.Allowed, "allowed"; NetworkAccess.Denied, "denied" ]
        |> Codec.describe "Whether sandboxed processes may use the network."
        |> Codec.named "NetworkAccess"

    let private absolutePath =
        Codec.string
        |> Codec.refine (fun path ->
            if SandboxPath.isAbsolute path then
                Ok()
            else
                Error $"expected an absolute path, got {Json.quote path}")

    let private paths' = Codec.list absolutePath

    /// A policy's JSON (diagnostics, the `env/*` protocol).
    let codec: Codec<SandboxPolicy> =
        Codec.record {
            let! workspace =
                Codec.field "workspace" (fun (p: SandboxPolicy) -> p.Workspace) absolutePath

            and! access =
                Codec.field "workspaceAccess" (fun p -> p.WorkspaceAccess) workspaceAccess

            and! writable = Codec.defaultField "writable" (fun p -> p.Writable) [] paths'
            and! readable = Codec.defaultField "readable" (fun p -> p.Readable) [] paths'
            and! protect = Codec.defaultField "protected" (fun p -> p.Protected) [] paths'
            and! denied = Codec.defaultField "denied" (fun p -> p.Denied) [] paths'
            and! temp = Codec.optionalField "temp" (fun p -> p.Temp) absolutePath
            and! network = Codec.field "network" (fun p -> p.Network) networkAccess

            return
                {
                    Workspace = workspace
                    WorkspaceAccess = access
                    Writable = writable
                    Readable = readable
                    Protected = protect
                    Denied = denied
                    Temp = temp
                    Network = network
                }
        }
        |> Codec.strict
        |> Codec.describe "What sandboxed processes may do: paths by access, and the network."
        |> Codec.named "SandboxPolicy"

[<RequireQualifiedAccess>]
module SandboxOverrides =
    /// No overrides: the environment's sandbox as it is.
    let none: SandboxOverrides =
        {
            Workspace = None
            Network = None
            Protect = []
            Deny = []
        }

    /// A read-only workspace (spec §7.2: `sandbox: { workspace: read-only }`, the reviewer).
    let readOnly: SandboxOverrides =
        { none with
            Workspace = Some WorkspaceAccess.ReadOnly
        }

    let private path =
        Codec.string
        |> Codec.refine (fun path ->
            if SandboxPath.isAbsolute path || SandboxPath.isPlainRelative path then
                Ok()
            else
                Error
                    $"expected an absolute path or one relative to the workspace without . or .., got {Json.quote path}")

    /// `{"workspace": "read-only", "network": "denied", "protect": ["tests"], "deny": [".env"]}`,
    /// every member optional (agents' `sandbox` frontmatter, spec §7.2).
    let codec: Codec<SandboxOverrides> =
        Codec.record {
            let! workspace =
                Codec.optionalField "workspace" (fun (o: SandboxOverrides) -> o.Workspace) SandboxPolicy.workspaceAccess

            and! network =
                Codec.optionalField "network" (fun o -> o.Network) SandboxPolicy.networkAccess

            and! protect = Codec.defaultField "protect" (fun o -> o.Protect) [] (Codec.list path)
            and! deny = Codec.defaultField "deny" (fun o -> o.Deny) [] (Codec.list path)

            return
                {
                    Workspace = workspace
                    Network = network
                    Protect = protect
                    Deny = deny
                }
        }
        |> Codec.strict
        |> Codec.describe
            "What an agent's sandbox takes away from its environment's: a read-only workspace, no network, more protected or denied paths."
        |> Codec.named "SandboxOverrides"

/// What drivers share: turning a process into a command line, temporary files, real paths.
[<RequireQualifiedAccess>]
module Sandbox =
    /// The program and arguments that run a process: `Script`s through envd's shell.
    let commandLine (shell: ShellSpec) (program: Program) : string * string list =
        match program with
        | Exec(file, args) -> file, args
        | Script script -> ShellSpec.invocation shell script

    /// The variables with `name` set to `value` (replacing one of the same name).
    let setVariable (name: string) (value: string) (env: EnvVars) : EnvVars =
        match env with
        | Inherit changes -> Inherit(changes @ [ name, Some value ])
        | Exactly vars -> Exactly((vars |> List.filter (fun (n, _) -> n <> name)) @ [ name, value ])

    /// The process's variables with `TMPDIR` at the policy's temporary directory, if it has one.
    let withTemp (policy: SandboxPolicy) (env: EnvVars) : EnvVars =
        match policy.Temp with
        | Some temp -> setVariable "TMPDIR" temp env
        | None -> env

    /// The real path of a path, by the probe's aliases (the longest alias that contains it).
    let realPath (ready: SandboxReady) (path: string) : string =
        ready.Aliases
        |> List.filter (fun (given, _) -> SandboxPath.isUnder given path)
        |> List.sortBy (fun (given, _) -> -given.Length)
        |> List.tryHead
        |> Option.map (fun (given, real) ->
            let given = SandboxPath.trim given
            let path = SandboxPath.trim path
            SandboxPath.trim real + path.Substring given.Length)
        |> Option.defaultValue (SandboxPath.trim path)

    /// What the host stats before each start: every path of the policy and the directories it
    /// guards (`SandboxPolicy.guarded`), and their real paths.
    let pathsToStat (ready: SandboxReady) (policy: SandboxPolicy) : string list =
        SandboxPolicy.paths policy @ SandboxPolicy.guarded policy
        |> List.collect (fun path -> [ path; realPath ready path ])
        |> List.distinct

    /// The kind of a path in a request (`Missing` if the host did not say).
    let kindOf (request: SandboxRequest) (path: string) : PathKind =
        request.Paths.TryFind(SandboxPath.trim path)
        |> Option.defaultValue PathKind.Missing

    /// A path of the policy as a driver mounts or names it: its real path and what that is;
    /// `None` if it does not exist; `Error` for a symbolic link the probe did not resolve (a mount
    /// over it would follow it, and a rule on it would not match what it points to).
    let target (request: SandboxRequest) (path: string) : Result<(string * PathKind) option, string> =
        let real = realPath request.Ready path

        match kindOf request real with
        | PathKind.Missing -> Ok None
        | PathKind.Symlink ->
            Error
                $"{path} is a symbolic link that the sandbox's probe did not resolve: open the environment again to resolve it"
        | kind -> Ok(Some(real, kind))

/// The `sandbox-driver` contribution type and checks of drivers.
[<RequireQualifiedAccess>]
module SandboxDrivers =
    /// Driver ids: lowercase letters, digits and `-`, starting with a letter.
    [<Literal>]
    let IdPattern = "^[a-z][a-z0-9-]*$"

    /// The driver that confines nothing (`fx.sandbox.none`).
    [<Literal>]
    let NoSandbox = "none"

    /// Sandbox drivers (spec §9.1), by id (the contribution id). Code that runs in the host's
    /// process: native extensions contribute them. Privileged (spec §16.5): a project never chooses
    /// how it is confined.
    let contributionType: ContributionType<SandboxDriver> =
        ContributionType.inProcess
            "sandbox-driver"
            "A sandbox driver: confines the processes of an environment to a policy."
            ContributionKind.Code
        |> ContributionType.privileged

    /// Every issue with a driver's data. Empty for a valid driver.
    let validate (driver: SandboxDriver) : Issue list =
        let at (field: string) (message: string) =
            Issue.at [ PathSegment.Key field ] message

        [
            if not (Regex.IsMatch(driver.Id, IdPattern)) then
                at "id" $"expected lowercase letters, digits and \"-\", got {Json.quote driver.Id}"
            if driver.Id = "auto" then
                at "id" "\"auto\" is what the sandbox.driver variant says to pick a driver"
            if driver.Platforms.IsEmpty then
                at "platforms" "a driver runs on at least one operating system"
            if List.distinct driver.Platforms <> driver.Platforms then
                at "platforms" "operating systems are listed once each"
            if (driver.Id = NoSandbox) = driver.Confines then
                at "confines" "only the none driver confines nothing"
        ]
