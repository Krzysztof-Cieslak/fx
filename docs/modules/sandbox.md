# Module spec: sandbox drivers (`Fx.Sdk` `Sandbox.fs`; `fx.sandbox.none|bubblewrap|seatbelt`)

Spec: §9.1 (sandbox drivers: none, bubblewrap + seccomp, Seatbelt; Windows `none` with a persistent
warning in v1, AppContainer later), §7.1 (attenuation enforced twice: types and sandbox), §8.2
(protected paths are sandbox-enforced), §16.4–16.5 (built-ins, privileged contributions), §18
(sandbox drivers are the process boundary), §19.5 (the Windows "unsandboxed" banner). Task: T113.
Code: the contract in [`src/Fx.Sdk/Sandbox.fs`](../../src/Fx.Sdk/Sandbox.fs); the host's side in
[`src/Fx.Environments/Sandbox.fs`](../../src/Fx.Environments/Sandbox.fs) (choice, policy, sandboxed
shell and terminals) and `Envs.fs` (the install step); the bindings' mirror in
[`src/Fx.CodeAct/SandboxGuard.fs`](../../src/Fx.CodeAct/SandboxGuard.fs); the drivers in
[`extensions/Fx.Ext.SandboxNone`](../../extensions/Fx.Ext.SandboxNone),
[`extensions/Fx.Ext.SandboxBubblewrap`](../../extensions/Fx.Ext.SandboxBubblewrap) and
[`extensions/Fx.Ext.SandboxSeatbelt`](../../extensions/Fx.Ext.SandboxSeatbelt) (their READMEs have the
details).
Tests: `tests/Fx.Sdk.Tests/SandboxTests.fs` (the contract), `tests/Fx.Ext.SandboxBubblewrap.Tests` and
`tests/Fx.Ext.SandboxSeatbelt.Tests` (command lines, the seccomp program, profiles, probes),
`tests/Fx.Environments.Tests` (the conformance suite on every built-in driver; choosing a driver;
sandboxed local environments; tools' homes and caches, and this repository's smallest project restored
locked and built offline in a sandboxed environment), `tests/Fx.CodeAct.Tests/LiveTests.fs` (the bun-ts
kernel sandboxed, and a read-only reviewer), `tests/Fx.CodeAct.Tests/UnitTests.fs` and
`tests/Fx.Replay.Tests` (the prompt's sandbox section, recorded and replayed), `tests/Fx.Extensions.Tests` (the packages load; a project cannot contribute
drivers), and the `sandbox-*` scenarios of the JIT-vs-native diff.

A **sandbox driver** is the process boundary of an environment (spec §18): every process the agent
starts there (commands, terminals, kernels, setup) runs confined to a **policy**. Grants, content
exclusions and the bindings' checks are guardrails; the sandbox is what holds when a process ignores
them.

```
EnvSpec.sandbox + sandbox.driver (privileged) ──choose──▶ driver ──probe (once per attach)──▶ SandboxReady
                                                                                                 │
EnvSandbox.policy (layout, home, sandbox.writable/denied, network) ─▶ SandboxPolicy ─tighten─▶ agent's policy
                                                                                                 │
process (ProcSpec) ──stat the policy's paths (one fs/stat)──▶ driver.Wrap (pure) ──▶ ProcSpec ──▶ envd
```

## In the host

The host ([host.md](host.md)) lists the three drivers among its built-ins (bubblewrap and Seatbelt
before none, so `auto` takes the OS's driver), registers `SandboxSlots.all` in its slot registry
(`HostSlots.core`), and gives `EnvsHost` the drivers in effect: every session's environment is
sandboxed by the host's choice, and its kernel and commands run in it. `fx run` prints the
environment's warnings once at the start (`warning: runs unsandboxed: …`), so an unsandboxed run (on
Windows, with `sandbox.driver = none`, or where bubblewrap cannot run) says so and why.

**Tools' homes and caches** (T136). The user's home is read-only in the sandbox, writing into the
user's own caches would let an agent reach what runs outside it later, and some tools fail outright
when a credential file they read is denied (NuGet). So a confining sandbox gives every process its
environment's cache directory (`<home>/cache`, writable) and points tools at it, unless the process
sets the variable itself (`EnvSandbox.defaults`; see [Build tools](#build-tools-t136)):

| Variable | Value | Why |
|---|---|---|
| `XDG_CACHE_HOME`, `BUN_INSTALL_CACHE_DIR`, `npm_config_cache`, `YARN_CACHE_FOLDER`, `PIP_CACHE_DIR`, `GOMODCACHE` | `<home>/cache`, `<home>/cache/<tool>` | caches (`EnvSandbox.caches`) |
| `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH`, `NUGET_PLUGINS_CACHE_PATH` | `<home>/cache/nuget/{packages,http-cache,plugins-cache}` | NuGet's caches |
| `NUGET_FALLBACK_PACKAGES` | the user's packages folder (the host's `NUGET_PACKAGES`, else `~/.nuget/packages`), if it exists | read in place, read-only: no download again |
| `DOTNET_CLI_HOME` | `<home>/cache/dotnet` | the .NET CLI's home: its first-run files and tools, and **NuGet's user config**, which NuGet writes afresh with nuget.org only (`EnvSandbox.homes`) |
| `CARGO_HOME` | `<home>/cache/cargo` | Cargo's registry cache (it writes under its home) without the user's `credentials.toml` |
| `XDG_RUNTIME_DIR` | `<home>/tmp` (also `TMPDIR`) | the user's `/run/user/<uid>` is denied; .NET keeps its named mutexes there (the MSBuild server's) |

Where the host lists the user's packages folder in `sandbox.writable`, NuGet uses it directly (shared,
no fallback); one it denies is not a fallback. git needs nothing writable outside the workspace, and the
pinned Bun's kernel writes only its owned directories.

## Where the sandbox applies

**The host wraps each process it starts for an agent; envd itself is not sandboxed.**

- envd runs in the host's process in embedded mode, where nothing could confine it;
- envd writes its own state (blobs, spill files, the shadow git repository of snapshots) outside every
  path an agent may write;
- one environment serves processes with different policies (a reviewer's read-only workspace beside an
  implementer's), so the policy belongs to the process, not to envd.

`EnvHandle.Client` (what `IEnvs.Open` returns) has a shell and terminals that start every process in
the sandbox: kernels (`Interpreter.prepare` + `Kernel.start` on the hands' shell), `shell.run`, the
`bash` tool, terminals, and setup commands. envd's own processes (git for snapshots, the probe) and
its file operations are not wrapped. `EnvClient.start` (the protocol's `exec/start`) bypasses the
sandbox: host code uses the handle's records.

The wrap is computed on the host because it is pure (`SandboxDriver.Wrap`): what it needs from the
environment (which of the policy's paths exist, envd's shell, the probe's findings) comes in the
request. The same works for environments on other machines (T501): the probe runs there through envd.

## The contract (`Fx.Sdk`)

```fsharp
type SandboxPolicy =
  { Workspace: string; WorkspaceAccess: ReadWrite | ReadOnly
    Writable: string list; Readable: string list; Protected: string list; Denied: string list
    Temp: string option                       // TMPDIR, writable
    Network: Allowed | Denied }

type SandboxDriver =
  { Id; Description; Platforms: Os list
    Confines: bool                            // false only for `none`
    Probe: SandboxSite -> Eff<ISandboxEnv, Result<SandboxReady, SandboxUnavailable>>
    Wrap: SandboxRequest -> Result<ProcSpec, string> }   // pure
```

- **A path's access** is that of the longest root of the policy containing it
  (`SandboxPolicy.access`): the workspace (its access), writable roots and `Temp` (read-write), readable
  and protected roots (read-only), denied roots (none); everything else is read-only. A root listed
  twice keeps its most restrictive access. `SandboxPolicy.rules` lists the roots shortest first;
  drivers apply them in that order.
- **`SandboxPolicy.guarded`**: the directories between a writable root and a read-only or denied root
  inside it. Drivers keep them in place: a rename would carry the inner root's path past its rule
  (bubblewrap binds them onto themselves; Seatbelt refuses to unlink them).
- **`SandboxOverrides`** (spec §7.1, an agent's `sandbox:` frontmatter): `workspace: read-only`,
  `network: denied`, more `protect` and `deny` paths (absolute, or relative to the workspace without
  `.`/`..`). `SandboxPolicy.tighten` only takes access away: `read-write` and `allowed` change nothing.
- **The probe** runs once per connection to envd, in the driver's extension environment over envd's own
  (unsandboxed) files and processes (`ISandboxEnv`): whether the driver works here (`SandboxReady`:
  its program, version, optional features, files it wrote, the real paths of the site's roots), or why
  not and what would fix it (`SandboxUnavailable`: a reason and a remedy).
- **The request**: the probe's findings, the policy, what each path of the policy and its real path is
  right now (`Sandbox.pathsToStat`, looked up with one `fs/stat` per start: a denied directory created
  since the last start is hidden too), envd's shell (for `Script` programs) and the process
  (`Terminal = true` keeps its controlling terminal).
- **Symbolic links**: a mount over a link follows it, and Seatbelt matches real paths, so drivers use
  real paths (`Sandbox.realPath`, from the probe's `readlink -f` of every path of the environment's
  policy). A denied or protected path that is a link the probe did not resolve refuses the start
  (`Sandbox.target`); a writable or readable one is simply not granted.
- `SandboxDrivers.contributionType` (`sandbox-driver`): code in the host's process (native
  extensions), **privileged** (a project never chooses how it is confined), in the core catalog, the
  id as contribution id. `SandboxDrivers.validate`: the id's form (`auto` is reserved), platforms, and
  only `none` confines nothing.

## An environment's policy (`EnvSandbox.policy`)

| Path | Access |
|---|---|
| the workspace | read-write |
| `.git/hooks`, `.git/config`, `.<product>` in the workspace (`SandboxPolicy.protectedInWorkspace`) | read-only: they run code later, outside the sandbox (the next `git` a person runs; the project's agents and extensions once it is trusted) |
| the launches' owned directories (`<home>/owned`), the temporary directory (`<home>/tmp`, `TMPDIR` and `XDG_RUNTIME_DIR`), the tools' caches and homes (`<home>/cache`, `XDG_CACHE_HOME`, `DOTNET_CLI_HOME` and others, above) | read-write |
| the components' install root (`<data dir>/runtimes`), interpreters' assets (`<home>/assets`) | read-only |
| the product's data directory (credentials, envd's state, other environments, the host's files) | none |
| credential locations (`SandboxPolicy.secrets`): `~/.ssh`, `~/.gnupg`, `~/.aws`, `~/.azure`, `~/.config/gcloud`, `~/.kube`, `~/.docker`, `~/.netrc`, `~/.git-credentials`, `~/.config/git/credentials`, `~/.config/gh`, `~/.config/hub`, `~/.config/glab-cli`, `~/.npmrc`, `~/.yarnrc.yml`, `~/.pypirc`, `~/.config/pip/pip.conf`, `~/.cargo/credentials[.toml]`, `~/.gem/credentials`, `~/.nuget/NuGet/NuGet.Config`, `~/.m2/settings.xml`, `~/.gradle/gradle.properties`, `~/.terraform.d/credentials.tfrc.json`, `~/.vault-token`, `~/.config/containers/auth.json`, `~/.password-store`, `~/.config/op`, `~/.local/share/keyrings`, `~/Library/Keychains`; on Linux `/run/user` (the session bus, which serves the Secret Service, and agents' sockets), `/run/docker.sock`, `/run/podman`, `/run/containerd`; on macOS `/var/run/docker.sock` | none |
| the host's `sandbox.writable` / `sandbox.denied` (absolute or under `~/`) | read-write / none |
| everything else | read-only |
| the network | `network` of the spec: `open` (default) or `none` |

The list of credential locations is a guardrail of known places (keep secrets in the OS keyring, which
is reachable only through the denied session bus on Linux, and through the keychain's own access
control on macOS). Nothing removes an entry to make a tool work: tools get homes of their own instead
(above). A denied file reads as an error (`EACCES` under bubblewrap, whose mount of `/dev/null` over it
is `nodev`; `EPERM` under Seatbelt), not as a missing file, and a denied directory as an empty one. The
user's home is read-only, so tools that write elsewhere in it fail unless the host lists the path in
`sandbox.writable`; each writable path is a way for what an agent wrote to run outside the sandbox
later.

## Build tools (T136)

Dogfooding (T132) found that fx could not build fx in its own sandbox: `dotnet restore` failed with
"Failed to read NuGet.Config due to unauthorized access" because NuGet reads the user's config,
`~/.nuget/NuGet/NuGet.Config`, a denied credential location (`packageSourceCredentials`, API keys), and
fails when it exists but cannot be read.

**NuGet and the .NET CLI.** What NuGet 7 (SDK 11.0.100) honours, checked with `dotnet nuget config
paths` and `dotnet nuget locals all --list`: `DOTNET_CLI_HOME` moves its home (the user config to
`$DOTNET_CLI_HOME/.nuget/NuGet/NuGet.Config`, the default packages folder, and the HTTP and plugins
caches under `.local/share/NuGet`, unless `XDG_DATA_HOME` is set), and the CLI's own `.dotnet` (first-run
sentinels, tools' resolver cache); `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH`, `NUGET_PLUGINS_CACHE_PATH`
and `NUGET_FALLBACK_PACKAGES` set each folder; `NUGET_SCRATCH` defaults under `TMPDIR`. No variable
moves only the user config. So:

- **The user config** is the environment's own: NuGet writes it the first time, with nuget.org only.
  The user's config is never copied or sanitized into the sandbox: credentials hide in forms a
  sanitizer misses (a token in a feed's URL, `%VAR%` references, a proxy password), and the file stays
  denied. **Sources**: a user's own feeds (in `~/.nuget/NuGet/NuGet.Config`) do not apply in the
  sandbox, even those without credentials; feeds come from the repository's `nuget.config` (the usual
  place for a project's feeds) or the machine-wide config (readable). Feeds that need credentials need
  the egress proxy's credential injection (later).
- **Packages**: the environment's own folder (`<home>/cache/nuget/packages`, writable), with the user's
  global packages folder as a read-only fallback folder: NuGet uses a package found there in place, so
  restoring this repository (about 700 MB of packages) downloads nothing, offline too (the runtime packs
  and ILCompiler, which the SDK gets with `PackageDownload`, are found there as well), and an agent
  cannot change what the user's own builds read. What is not there is downloaded into the
  environment's folder, once per environment, and goes with it. The fallback is set only when the folder
  exists: NuGet fails every restore on a missing one. A host that wants one shared, writable folder
  lists it in `sandbox.writable` (then it is `NUGET_PACKAGES` itself), accepting that an agent can
  change packages the user's builds run (MSBuild targets in packages are code).
- A restore in the sandbox and one outside it see different folders, so each restores again after the
  other (NuGet's no-op check hashes the folders): `obj/` refers to the folders of the last restore.
  What a person builds from the workspace after an agent worked there runs what the agent wrote,
  caches or not: `obj/` is in the workspace.

**Toolchains surveyed** (bubblewrap on Linux, a scratch home holding every denied credential file;
Cargo in a container with a read-only `CARGO_HOME`):

| Tool | In the sandbox before T136 | Now |
|---|---|---|
| .NET (`dotnet restore`, `build`, `test`, `tool restore`) | restore fails: the user's NuGet config cannot be read; the MSBuild server cannot start (its named mutex lives in the denied `/run/user/<uid>`) | works (`DOTNET_CLI_HOME`, NuGet's variables, `XDG_RUNTIME_DIR`); this repository restores locked and builds |
| Cargo | builds read no credentials, but write the registry cache under `CARGO_HOME` (`~/.cargo`, read-only): builds with dependencies fail | works (`CARGO_HOME`): toolchains stay where `PATH` finds them (rustup's proxies, `RUSTUP_HOME` read-only); the user's `~/.cargo/config.toml` still applies to workspaces under the home (Cargo looks in every parent directory), not to others |
| npm, Bun | work: they ignore an unreadable `~/.npmrc` | unchanged |
| pip | works: it ignores an unreadable `pip.conf`; `--user` installs fail (read-only home), virtual environments in the workspace work | unchanged |
| Go | works (`GOMODCACHE`, the build cache under `XDG_CACHE_HOME`); `go install` into `~/go/bin` fails | unchanged |
| git | commits, branches and worktrees inside the workspace work; see [Known limits](#known-limits) | the prompt describes the sandbox |
| Yarn Berry | fails: "Internal Error: EACCES … ~/.yarnrc.yml" | not handled: no variable moves only the user's rc file (`YARN_RC_FILENAME` renames the project's too). Keep registry settings in the project's `.yarnrc.yml` and tokens out of `~/.yarnrc.yml` (or delete it) |
| Maven, Gradle, Terraform | not tried (not installed): their user settings (`~/.m2/settings.xml`, `~/.gradle/gradle.properties`, `credentials.tfrc.json`) are denied files and their caches are in the read-only home | not handled: `GRADLE_USER_HOME` would move Gradle's whole home like `CARGO_HOME`; Maven needs `-s` and `-Dmaven.repo.local` |
| `gh`, cloud CLIs | not signed in (their configs are denied directories, which read as empty) | intended |

**The prompt.** When a driver confines the hands' processes, CodeAct's prefix ends with a `sandbox`
section (`Instructions.sandbox`, rendered from the policy alone, paths relative to the workspace): what
stays read-only in the workspace and how git's refusals read, what cannot be read, that credentials are
hidden, where the tools' caches are, and the network. The journal records the policy with the hands, so
replay renders the same frame.

## Choosing a driver (`EnvSandbox.choose`)

The privileged variant `sandbox.driver` (default `auto`) and the spec's `sandbox`:

| `sandbox.driver` | spec's `sandbox` | the driver |
|---|---|---|
| `auto` | absent | the first contributed driver that confines on the environment's OS (bubblewrap on Linux, Seatbelt on macOS), **falling back to `none`** with notes that say why if its probe fails; `none` where no driver confines (Windows) |
| `auto` | `none` | `none` if no driver confines on the OS; refused otherwise (only the host turns sandboxing off) |
| `none` | absent or `none` | `none` |
| a driver | absent or the same | that driver, **required**: if its probe fails, the environment fails |
| any | another confining driver | that driver, required (a spec can always tighten), unless the host requires a different one (refused) |

- Each attach (every connection to envd) decides again: the install step resolves and probes the
  driver, builds the policy, creates `<home>/tmp` and `<home>/sandbox`, runs setup in the sandbox (with
  `setup.network`, else the spec's `network`), and records `EnvSandboxInfo {driver, confines, notes}`
  in the `readied` event (an optional member, so version 1's events read the same).
- `network: none` needs a driver that confines; with `none` the environment fails.
- `Envs.warnings record`: an environment that is not confined (or whose record predates sandboxes)
  carries `runs unsandboxed: …` with the notes, the persistent warning clients show (the TUI's banner,
  T130). On Windows it says that Windows has no sandbox driver yet (AppContainer, T511).
- `EnvSpecs.check` checks the form of `sandbox`; `EnvSpecs.checkSandbox` (at `Create`) that it names a
  driver in effect; `network` and `setup.network` are `open` or `none` (allowlists need the egress
  proxy).

## Agents: overrides and the bindings (spec §7.1)

`KernelPlan.Sandbox` holds the session's agent's overrides (T213 sets them from the agent's definition).
`EnvHands.ofHandle` applies them twice:

1. **Processes and kernels** start under the tightened policy (`EnvHandle.attenuate`).
2. **The bindings' file operations** run in envd, outside the sandbox: `SandboxGuard.check` runs before
   every host call (before the grants guard) and refuses what the policy refuses a process (`fs.read`,
   `fs.list`, `fs.stat` need read access; `fs.write` and `fs.patch` write access), as
   `HostCallError.Denied`. Paths through a symbolic link inside a writable root are refused too (a
   sandboxed process can make one to a denied path; read it with the shell, which runs in the sandbox).
   A process that swaps a directory for a link between the check and envd's operation is not stopped:
   resolving paths beneath their roots in envd (`openat2` `RESOLVE_BENEATH`, `O_NOFOLLOW_ANY`) is
   follow-up work.

On Windows (no driver confines yet) only the second applies, with the unsandboxed warning.

## The drivers

| Driver | OS | How | Details |
|---|---|---|---|
| `none` (`fx.sandbox.none`) | all | the process as it is | [README](../../extensions/Fx.Ext.SandboxNone/README.md) |
| `bubblewrap` (`fx.sandbox.bubblewrap`) | Linux | `bwrap` (all namespaces, the network only when allowed, the system read-only with the policy's paths mounted over it, a private `/tmp`, `--die-with-parent`, `--disable-userns`) and a seccomp filter built as data per architecture | [README](../../extensions/Fx.Ext.SandboxBubblewrap/README.md) |
| `seatbelt` (`fx.sandbox.seatbelt`) | macOS | `/usr/bin/sandbox-exec` with a deny-by-default profile built from the policy (paths as parameters) | [README](../../extensions/Fx.Ext.SandboxSeatbelt/README.md) |

**Making bubblewrap work** (what the probe says when it cannot): install it (`apt install bubblewrap`,
…); on Ubuntu 23.10 and later let it create user namespaces with an AppArmor profile
(`/etc/apparmor.d/bwrap`: `abi <abi/4.0>, include <tunables/global> profile bwrap /usr/bin/bwrap
flags=(unconfined) { userns, }`, then `sudo apparmor_parser -r /etc/apparmor.d/bwrap`) or turn the
restriction off (`sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0`, as CI does); in a
container, allow user namespaces or set `sandbox.driver = "none"`.

**Containment** (envd.md): with bubblewrap, a killed bwrap or envd takes every process of the sandbox
with it (`--die-with-parent`, a pid namespace per process). Seatbelt has no such tie: processes a
sandboxed process left behind outlive envd, as unsandboxed ones do. `--die-with-parent` is
`PR_SET_PDEATHSIG`, whose parent is the thread that started bwrap: envd starts every process on a thread
that lives as long as it does (envd.md, T135), and the suite checks that a sandboxed process outlives
the thread that asked for it.

## Conformance suite v1

`SandboxConformance.tests subject` (`tests/Fx.Environments.Tests/Conformance`) is a function from a
driver (with its extension environment, and whether it must be available here) to tests. It builds a
scratch machine (a workspace with protected paths, a home with a key, the product's data directory
with an environment's home and a component, a directory outside), probes the driver there, and runs
real processes through its wrap on this machine's shell and terminals:

- the driver's data is valid; its probe says it can sandbox here (or the live tests skip, unless
  `SANDBOX_TESTS=1` requires it);
- commands run, with their exit codes and output, in the workspace;
- writes land in the workspace, the owned and temporary directories (`TMPDIR`), and nowhere else (the
  home, the data directory, the environment's home, a component, a directory outside);
- denied paths cannot be read or listed; readable paths inside denied ones can, and programs that
  look at each directory on the way to them (`mkdir -p` of an owned directory under the data
  directory) reach them;
- protected paths stay unchanged inside a writable workspace, and cannot be renamed away;
- a read-only workspace (overrides) blocks writes, not reads; overrides deny and protect more;
- a denied network refuses a connection to a listener on the loopback, an allowed one makes it;
- a process on a terminal keeps it;
- a sandboxed process outlives the thread that asked for it (T135: `--die-with-parent` fires when the
  thread that started bwrap ends);
- git works in the workspace;
- a driver that confines nothing leaves processes as they are (and says so: `Confines = false`).

Every built-in passes it on its OS: CI runs bubblewrap on Linux (x64 and arm64) and Seatbelt on macOS
(arm64 and Intel) with `SANDBOX_TESTS=1`, and `none` everywhere; the CodeAct live tests run the bun-ts
kernel in the sandbox. A new driver is done when it passes it on every OS it lists.

## Native AOT

Scenarios: `sandbox-policy` (rules, access, guarded directories, overrides and their JSON),
`sandbox-choose` (the host's choice for every combination, an environment's policy),
`sandbox-bubblewrap` (command lines, the seccomp program's digest and verdicts per architecture, the
probe's diagnosis), `sandbox-seatbelt` (a profile and its parameters), and with `SANDBOX_TESTS=1` on
Linux and macOS `sandbox-live` (a process confined by this OS's driver, and one that outlives the
thread that started it) and `sandbox-build-tools` (the
process start the host does for an agent: an environment's policy and variables with the user's real
home, the driver's probe and wrap, then `dotnet build` of a small project and the program it built).
CI's sandbox steps diff them
natively (`--only sandbox-`).

## Known limits

- **git's config and hooks are read-only** in a writable workspace (`.git/config`, `.git/hooks`, and
  `.<product>`): what changes them fails: `git config`, `git remote add`, `git branch
  --set-upstream-to`, `git push -u`, installing hooks. Under bubblewrap the error reads "could not write
  config file .git/config: Device or resource busy" (git renames its lock file onto the path, a mount
  point); under Seatbelt "Operation not permitted". This is deliberate: both run code outside the
  sandbox the next time a person uses git (`core.hooksPath`, `core.fsmonitor`, aliases, hooks). The
  prompt says so (above). Tools that keep their own state in `.git/config` fail in the sandbox: lane
  stores a lane's base as `git config lane.<name>.base`, so `lane new` fails after creating the
  worktree; lane could keep it in its own `.lane/` directory (writable) instead.
- **A linked worktree as the workspace**: its git directory (`<main>/.git/worktrees/<name>`) and the
  objects are outside the workspace, so git cannot commit there. Open the main checkout (lane's trees
  are inside it).
- **Signing and pushing**: SSH and GPG keys and the agents' sockets are hidden, so signed commits
  (`commit.gpgsign`) and pushes over SSH or with a credential helper fail; leave them to the person.
- **Protected paths that do not exist** when a process starts are not mounted by bubblewrap (it would
  create them in the workspace): a sandboxed process can create a missing `.git/hooks`. git's
  `config.worktree` (with `extensions.worktreeConfig`) and submodules' `.git/modules/*/{config,hooks}`
  are not protected either.
- **One temporary directory per environment**: processes under different policies in one environment
  (an implementer and a read-only reviewer) share `TMPDIR`, so a long-lived process of one can serve
  the other over a unix socket there. Under Seatbelt build servers (MSBuild's nodes and server, the
  compiler server) outlive their command and are reused that way; under bubblewrap each command's pid
  namespace ends them, but a kernel lives on. A temporary directory per policy would close it.

## Left for later

| What | Task |
|---|---|
| `fx.sandbox.appcontainer` on Windows; then the unsandboxed banner goes | T511 |
| Network allowlists (`allowlist:<name>`), credential injection, per-call attribution: the egress proxy | with the egress proxy (spec §18) |
| Agents' `sandbox:` from their definitions into `KernelPlan.Sandbox`; child sessions | T213 |
| Protected paths from goals (`protect: ["tests/**"]`): globs that name a directory become its path; others need a check of the diff | T212 |
| envd resolving the bindings' paths beneath their roots (`openat2` `RESOLVE_BENEATH`, `O_NOFOLLOW_ANY`), closing the link race of `SandboxGuard` | follow-up (T113b) |
| The user's home on other machines (home-relative denied paths there), drivers probing over the environment protocol | T501 |
| Choosing a sandbox per environment in the TUI | T130 |
| Protecting missing protected paths, `config.worktree` and submodules' config and hooks; Yarn Berry's, Maven's and Gradle's user settings | follow-up |
| Showing `Envs.warnings` persistently (the Windows banner) | T130 |
| Sandboxed `Script` installs; sandboxing extension processes | T211; with the extension host |
| Plugins: nsjail, gVisor | — |
