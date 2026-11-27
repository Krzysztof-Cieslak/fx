namespace Fx.Environments

open System
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects
open Fx.Envd
open Fx.Pty
open Fx.Sdk
open Fx.Variants

/// The host's sandbox variants (spec §9.1, §15): privileged, so only the host's config, the command
/// line and policy set them; a project's config, an environment spec or an agent can only tighten a
/// sandbox. Core slots outside `Fx.Variants`: the host registers `SandboxSlots.all` with `Slots.all`.
[<RequireQualifiedAccess>]
module SandboxSlots =
    /// What `sandbox.driver` says to pick a driver: the first that confines and runs on the
    /// environment's OS, or `none` with a warning that says why where none can.
    [<Literal>]
    let Auto = "auto"

    /// `sandbox.driver`: `none` (the default: the sandbox is opt-in, T137), `auto`, or the id of the
    /// driver every environment must use. Off by default because the sandbox keeps ordinary tools
    /// from working (they keep state under `~` or read configs on the credential list) while
    /// permission prompts (grants) are the control people rely on.
    let driver: Slot<string> =
        Slot.create
            "sandbox.driver"
            "The sandbox driver of environments, off by default: none (commands run with the user's rights; permission prompts are the guard), auto (the first driver that confines processes on the environment's OS, else none with a warning), or a driver's id that every environment must use (an environment fails where it cannot run)."
            (Codec.string
             |> Codec.refine (fun value ->
                 if
                     value = Auto
                     || Text.RegularExpressions.Regex.IsMatch(value, SandboxDrivers.IdPattern)
                 then
                     Ok()
                 else
                     Error $"expected \"auto\" or a sandbox driver's id, got {Json.quote value}"))
            SandboxDrivers.NoSandbox
        |> Slot.privileged

    let private paths (description: string) =
        Codec.list (
            Codec.string
            |> Codec.refine (fun path ->
                if path = "~" || path.StartsWith "~/" || SandboxPath.isAbsolute path then
                    Ok()
                else
                    Error $"expected an absolute path or one under ~/, got {Json.quote path}")
        )
        |> Codec.describe description

    /// `sandbox.writable`: more paths sandboxed processes may change (absolute or under `~/`), e.g.
    /// a package cache. Each one is a way out of the sandbox for what reads it later.
    let writable: Slot<string list> =
        Slot.create
            "sandbox.writable"
            "More paths sandboxed processes may change besides the workspace (absolute or under ~/), e.g. a package cache; whatever later reads them trusts what the agent wrote."
            (paths "Absolute paths, or paths under ~/.")
            []
        |> Slot.privileged

    /// `sandbox.denied`: more paths sandboxed processes may not read, besides the built-in list
    /// (`SandboxPolicy.secrets`) that nothing removes.
    let denied: Slot<string list> =
        Slot.create
            "sandbox.denied"
            "More paths sandboxed processes may not read or change (absolute or under ~/), besides the built-in list of credential locations."
            (paths "Absolute paths, or paths under ~/.")
            []
        |> Slot.privileged

    /// The slots, for the host's registry.
    let all: SlotInfo list = [ Slot.info driver; Slot.info writable; Slot.info denied ]

/// How an environment is to be sandboxed, before the driver is probed.
[<RequireQualifiedAccess>]
type SandboxChoice =
    /// This driver, or the environment fails.
    | Required of SandboxDriver
    /// This driver if it can run here; else `none`, with why.
    | Preferred of SandboxDriver
    /// No sandbox, as the host chose (`sandbox.driver = none`, the default): nothing to warn about.
    | Off
    /// No sandbox although the host asked for one (`auto`), because none can confine here: this
    /// reason, which the environment's warning shows.
    | Unconfined of reason: string

    override this.ToString() =
        match this with
        | Required driver -> $"required {driver.Id}"
        | Preferred driver -> $"preferred {driver.Id}"
        | Off -> "off"
        | Unconfined reason -> $"unconfined: {reason}"

/// The sandbox of a connected environment: its driver, what the driver's probe found, the
/// environment's policy (an agent's may be tighter, `EnvHandle.attenuate`), and envd's own client,
/// whose processes are not sandboxed (the host's own work: probes, nothing an agent asks for).
[<NoEquality; NoComparison>]
type EnvSandbox =
    {
        Driver: SandboxDriver
        Ready: SandboxReady
        Policy: SandboxPolicy
        Info: EnvSandboxInfo
        /// Variables every sandboxed process gets unless it sets them itself: the tools' caches in the
        /// environment's cache directory (`EnvSandbox.caches`). Empty when the driver confines nothing.
        Defaults: (string * string) list
        /// envd's client, unsandboxed.
        Unsandboxed: EnvClient
    }

    override this.ToString() = $"{this.Info}"

[<RequireQualifiedAccess>]
module EnvSandbox =
    let private named (id: string) (drivers: SandboxDriver list) =
        drivers |> List.tryFind (fun d -> d.Id = id)

    let private unconfinedOn (os: Os) =
        match os with
        | Os.Windows -> "Windows has no sandbox driver yet (AppContainer: T511)"
        | os -> $"no sandbox driver that confines processes runs on {os}"

    /// Which driver sandboxes an environment on `os`: the host's `sandbox.driver` and the spec's
    /// `sandbox`. A spec may ask for a confining driver, never for less than the host gives:
    ///
    /// | `sandbox.driver` | spec | choice |
    /// |---|---|---|
    /// | `none` (the default) | none given, `none` | off |
    /// | `auto` | none given | the first confining driver for the OS, falling back to `none` |
    /// | `auto` | `none` | `none` if no driver confines on the OS, else refused |
    /// | a driver | none given, the same | that driver, required |
    /// | any | another driver | that driver, required (unless the host requires a different one) |
    let choose
        (hostChoice: string)
        (spec: EnvSpec)
        (os: Os)
        (drivers: SandboxDriver list)
        : Result<SandboxChoice, string> =
        let confining =
            drivers |> List.filter (fun d -> d.Confines && List.contains os d.Platforms)

        let required (id: string) =
            match named id drivers with
            | None -> Error $"no sandbox driver {Json.quote id} is in effect"
            | Some driver when not (List.contains os driver.Platforms) ->
                Error $"the sandbox driver {Json.quote id} does not run on {os}"
            | Some driver when not driver.Confines -> Ok SandboxChoice.Off
            | Some driver -> Ok(SandboxChoice.Required driver)

        match hostChoice, spec.Sandbox with
        | SandboxSlots.Auto, None ->
            match confining with
            | first :: _ -> Ok(SandboxChoice.Preferred first)
            | [] -> Ok(SandboxChoice.Unconfined(unconfinedOn os))
        | SandboxSlots.Auto, Some SandboxDrivers.NoSandbox ->
            match confining with
            | [] -> Ok(SandboxChoice.Unconfined(unconfinedOn os))
            | first :: _ ->
                Error
                    $"the spec asks for no sandbox, but this host sandboxes environments on {os} ({first.Id}): only the host's privileged variant sandbox.driver can turn sandboxing off"
        | SandboxSlots.Auto, Some id -> required id
        | SandboxDrivers.NoSandbox, None
        | SandboxDrivers.NoSandbox, Some SandboxDrivers.NoSandbox -> Ok SandboxChoice.Off
        | SandboxDrivers.NoSandbox, Some id -> required id
        | host, None -> required host
        | host, Some id when id = host -> required host
        | host, Some other ->
            Error
                $"this host requires the sandbox driver {Json.quote host} (sandbox.driver); the spec asks for {Json.quote other}"

    /// A path from a variant, `~` expanded against the user's home; `None` if it needs a home and
    /// there is none.
    let expand (home: string option) (path: string) : string option =
        if path = "~" then
            home
        elif path.StartsWith "~/" then
            home |> Option.map (fun home -> SandboxPath.under home (path.Substring 2))
        else
            Some(SandboxPath.trim path)

    /// Where common tools keep their caches, in the environment's cache directory: the user's own
    /// caches (`~/.cache`, `~/.bun`, `~/.npm`, …) are read-only in the sandbox, and an agent writing
    /// into them would reach what runs outside it later. XDG-following tools (pip on Linux, Go's
    /// build cache, many CLIs) follow `XDG_CACHE_HOME`; Bun, npm, Yarn, pip on macOS and Go's modules
    /// get their own variables. Tools without such a variable (NuGet's packages, Cargo) need their
    /// cache in the host's `sandbox.writable`.
    let caches (layout: EnvLayout) : (string * string) list =
        let under = EnvLayout.join layout.Os layout.Cache

        [
            "XDG_CACHE_HOME", layout.Cache
            "BUN_INSTALL_CACHE_DIR", under "bun"
            "npm_config_cache", under "npm"
            "YARN_CACHE_FOLDER", under "yarn"
            "PIP_CACHE_DIR", under "pip"
            "GOMODCACHE", under "go-mod"
        ]

    /// An environment's policy: the workspace, the launches' owned directories and the tools' cache
    /// directory writable, its temporary directory as `TMPDIR`, the components and assets readable,
    /// git's hooks and config and the project's directory protected, and the product's data
    /// directory and the credential locations denied; plus the host's `sandbox.writable` and
    /// `sandbox.denied`.
    let policy
        (layout: EnvLayout)
        (machine: EnvMachine)
        (home: string option)
        (writable: string list)
        (denied: string list)
        (network: NetworkAccess)
        : SandboxPolicy =
        let expandAll = List.choose (expand home)

        {
            Workspace = layout.Workspace
            WorkspaceAccess = WorkspaceAccess.ReadWrite
            Writable = layout.Owned :: layout.Cache :: expandAll writable
            Readable = [ layout.Components; layout.Assets ]
            Protected =
                SandboxPolicy.protectedInWorkspace
                |> List.map (SandboxPath.under layout.Workspace)
            Denied =
                machine.DataDir :: SandboxPolicy.secrets machine.Target.Os home
                @ expandAll denied
                |> List.distinct
            Temp = Some layout.Temp
            Network = network
        }

    /// What a driver's probe gets: the machine, envd's shell, the driver's directory, and every
    /// path of the policy (and the user's home) to resolve.
    let site
        (layout: EnvLayout)
        (machine: EnvMachine)
        (shell: ShellSpec)
        (home: string option)
        (policy: SandboxPolicy)
        =
        {
            Target = machine.Target
            Shell = shell
            Dir = layout.Sandbox
            Roots = SandboxPolicy.paths policy @ Option.toList home |> List.distinct
        }

    let private kindOf (kind: EntryKind) : PathKind =
        match kind with
        | EntryKind.Directory -> PathKind.Directory
        | EntryKind.Symlink -> PathKind.Symlink
        | EntryKind.File
        | EntryKind.Other -> PathKind.File

    /// The process that runs `proc` in the sandbox under a policy: the paths the driver needs are
    /// looked up (one `fs/stat` call), then the driver wraps it.
    let wrap
        (sandbox: EnvSandbox)
        (policy: SandboxPolicy)
        (proc: SandboxProcess)
        (cancellation: CancellationToken)
        : Task<Result<ProcSpec, string>> =
        task {
            let! paths =
                if sandbox.Driver.Confines then
                    task {
                        let paths = Sandbox.pathsToStat sandbox.Ready policy
                        let! entries = sandbox.Unsandboxed.FsOps.StatMany paths cancellation

                        return
                            List.zip paths entries
                            |> List.map (fun (path, entry) ->
                                SandboxPath.trim path,
                                match entry with
                                | Ok entry -> kindOf entry.Kind
                                | Error _ -> PathKind.Missing)
                            |> Map.ofList
                    }
                else
                    Task.FromResult Map.empty

            let proc =
                { proc with
                    Env =
                        sandbox.Defaults
                        |> List.fold (fun env (name, value) -> Sandbox.withDefault name value env) proc.Env
                }

            let request =
                {
                    Ready = sandbox.Ready
                    Policy = policy
                    Paths = paths
                    Shell = sandbox.Unsandboxed.Platform.Shell
                    Process = proc
                }

            try
                return
                    sandbox.Driver.Wrap request
                    |> Result.mapError (fun e -> $"the sandbox ({sandbox.Driver.Id}) refused it: {e}")
            with e when not (e :? OperationCanceledException) ->
                return Error $"the sandbox driver {sandbox.Driver.Id} failed ({e.GetType().Name})"
        }

    /// envd's shell with every process sandboxed under a policy.
    let shell (sandbox: EnvSandbox) (policy: SandboxPolicy) : Shell =
        let inner = sandbox.Unsandboxed.Shell

        let wrapped (spec: ProcSpec) (cancellation: CancellationToken) =
            wrap
                sandbox
                policy
                {
                    Program = spec.Program
                    Cwd = spec.Cwd
                    Env = spec.Env
                    Terminal = false
                }
                cancellation

        {
            Run =
                fun command cancellation ->
                    task {
                        match! wrapped command.Spec cancellation with
                        | Error e -> return Error(ShellError.NotStarted(string command.Spec.Program, e))
                        | Ok spec -> return! inner.Run { command with Spec = spec } cancellation
                    }
            Spawn =
                fun spec cancellation ->
                    task {
                        match! wrapped spec cancellation with
                        | Error e -> return Error(ShellError.NotStarted(string spec.Program, e))
                        | Ok wrappedSpec -> return! inner.Spawn wrappedSpec cancellation
                    }
        }

    /// envd's terminals with every process sandboxed under a policy (keeping its controlling
    /// terminal).
    let terminal (sandbox: EnvSandbox) (policy: SandboxPolicy) : Terminal =
        let inner = sandbox.Unsandboxed.Terminal

        {
            Spawn =
                fun spec cancellation ->
                    task {
                        let! wrapped =
                            wrap
                                sandbox
                                policy
                                {
                                    Program = spec.Program
                                    Cwd = spec.Cwd
                                    Env = spec.Env
                                    Terminal = true
                                }
                                cancellation

                        match wrapped with
                        | Error e -> return Error(PtyError.NotStarted(string spec.Program, e))
                        | Ok wrappedSpec ->
                            return!
                                inner.Spawn
                                    { spec with
                                        Program = wrappedSpec.Program
                                        Cwd = wrappedSpec.Cwd
                                        Env = wrappedSpec.Env
                                    }
                                    cancellation
                    }
        }

    /// envd's capability records with its processes and terminals sandboxed under a policy; files,
    /// blobs and snapshots are envd's own (the host's bindings attenuate them, spec §7.1).
    let client (sandbox: EnvSandbox) (policy: SandboxPolicy) : EnvClient =
        { sandbox.Unsandboxed with
            Shell = shell sandbox policy
            Terminal = terminal sandbox policy
        }
