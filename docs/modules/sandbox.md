# Module spec: sandbox drivers (`Fx.Sdk` `Sandbox.fs`; `fx.sandbox.none|bubblewrap|seatbelt`)

Spec: §9.1 (sandbox drivers: none, bubblewrap + seccomp, Seatbelt, opt-in; Windows has only `none` in
v1, AppContainer later), §7.1 (attenuation enforced twice: types and sandbox), §8.2 (protected paths
are sandbox-enforced where a sandbox is on), §16.4–16.5 (built-ins, privileged contributions), §18
(sandbox drivers are the process boundary when a host turns one on), §19.5 (the "unsandboxed" banner
when a requested sandbox cannot run). Tasks: T113, T137 (opt-in).
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
sandboxed local environments), `tests/Fx.CodeAct.Tests/LiveTests.fs` (the bun-ts kernel sandboxed, and a
read-only reviewer), `tests/Fx.Extensions.Tests` (the packages load; a project cannot contribute
drivers), `tests/Fx.Host.Tests` (the default runs unsandboxed without a warning; `auto` from the host's
config confines or warns; a required driver that cannot run fails the run; a project cannot select
`sandbox.driver`; the sandboxed run and T135's kernel opt in), and the `sandbox-*` scenarios of the
JIT-vs-native diff. Tests that need a sandbox select `sandbox.driver` themselves: the default never
confines.

A **sandbox driver** is the process boundary of an environment (spec §18): every process the agent
starts there (commands, terminals, kernels, setup) runs confined to a **policy**. Grants, content
exclusions and the bindings' checks are guardrails; the sandbox is what holds when a process ignores
them. **The sandbox is opt-in** (T137): a host turns it on with the privileged variant
`sandbox.driver` (`auto` or a driver's id); by default (`none`) the agent's processes run as the user.

```
EnvSpec.sandbox + sandbox.driver (privileged) ──choose──▶ driver ──probe (once per attach)──▶ SandboxReady
                                                                                                 │
EnvSandbox.policy (layout, home, sandbox.writable/denied, network) ─▶ SandboxPolicy ─tighten─▶ agent's policy
                                                                                                 │
process (ProcSpec) ──stat the policy's paths (one fs/stat)──▶ driver.Wrap (pure) ──▶ ProcSpec ──▶ envd
```

## Opt-in (T137)

Dogfooding (T132) showed what a sandbox costs: ordinary tools stopped working in it. NuGet failed on
`~/.nuget/NuGet/NuGet.Config` (it holds feeds' credentials, so it is on the credential list, and NuGet
fails when the file exists but cannot be read); Cargo, Yarn Berry and the MSBuild server keep state
under `~` or read a credential-listed config. Working around each tool with variables (T136: moving
`DOTNET_CLI_HOME`, NuGet's folders, `CARGO_HOME`, `XDG_RUNTIME_DIR` into the environment) was a list
of special cases that would keep growing, and was dropped. Mainstream coding agents run without an OS
sandbox, with permission prompts as the control, and Windows v1 runs unsandboxed anyway. So:

- `sandbox.driver` defaults to **`none`**. It stays privileged: the host's config or the command line
  (`--variant sandbox.driver=auto`) turn the sandbox on, never a project's config or a session.
- The drivers stay bundled (`fx.sandbox.bubblewrap`, `fx.sandbox.seatbelt`, `fx.sandbox.none`) and
  work as before when a host chooses them.
- **Never fall back silently** still holds for a sandbox that was asked for: a driver the host names
  that cannot run fails the environment; `auto` falls back to `none` with a warning that says why.
- The default carries **no warning**: clients show `sandbox: off` (the TUI's info line, `fx run`'s
  environment line); the banner and `warning:` lines are for a requested sandbox that cannot run.
- The general fix for tools that keep state under `~`, a copy-on-write home per environment, is T512
  (below, "Known limits").

**Sandbox off (the default).** The agent's commands, terminals, kernels and setup run with the
user's rights: they can read the user's credentials (SSH keys, cloud and registry tokens, the
keyring's session bus, and the product's own credentials file in its data directory), write anywhere
the user can, and use the network. What guards them is the grants log: permission prompts for
commands, writes outside the workspace, the network and installs (T127b; with auto approvals on,
T134, commands written plainly in the workspace run unasked, with those rights), and content
exclusions at egress. "Credentials live only on the host" (spec §18) then means that the model
never receives them through the product (configuration refers to secrets, egress hooks run on every
request), not that an agent's processes cannot read files. The file bindings keep to the
environment's policy as a guardrail (`SandboxGuard`, below), but a command does not.

**Sandbox on** (`auto`, `bubblewrap`, `seatbelt`): processes run confined to the environment's policy
(next section): the workspace writable except git's config and hooks and the project's directory;
temporary files, the launches' directories and the tools' caches of the environment writable; the
product's data directory and the credential locations unreadable; the rest of the machine, the home
included, read-only; the network as the spec says. What breaks is in "Known limits".

## In the host

The host ([host.md](host.md)) lists the three drivers among its built-ins (bubblewrap and Seatbelt
before none, so `auto` takes the OS's driver), registers `SandboxSlots.all` in its slot registry
(`HostSlots.core`), and gives `EnvsHost` the drivers in effect: every session's environment gets the
host's choice, and its kernel and commands run in it. `fx run` prints the environment once at the
start (`env: default (local) · sandbox: off`, or the driver), then its warnings: a sandbox the host
asked for that cannot run says so and why (`warning: runs unsandboxed: the sandbox the host asked for
cannot run here (…)`).

**Tools' caches.** The user's home is read-only in the sandbox, and writing into the user's own
caches would let an agent reach what runs outside it later, so a confining sandbox gives every
process a cache directory of its environment (`<home>/cache`, writable) and points tools at it unless
the process sets the variable itself (`EnvSandbox.caches`): `XDG_CACHE_HOME`,
`BUN_INSTALL_CACHE_DIR`, `npm_config_cache`, `YARN_CACHE_FOLDER`, `PIP_CACHE_DIR`, `GOMODCACHE`. Tools
download again into it the first time. NuGet's global packages and Cargo's registry have no such
variable that is safe to move (it would also move their configuration or toolchains): a host that
wants them writable lists them in `sandbox.writable`, knowing the risk. git needs nothing writable
outside the workspace, and the pinned Bun's kernel writes only its owned directories. Without a
sandbox none of this applies: processes get no cache variables and use the user's own caches.

**The prompt.** When a driver confines the hands' processes, CodeAct's prefix ends with a `sandbox`
section (`Instructions.sandbox` of `Hands.Sandbox`, rendered from the policy alone, paths relative to
the workspace, so environments with the same policy share it): what stays read-only in the workspace
and how git's refusals read, what cannot be read, that credentials and the files that hold them are
hidden, that the home is read-only so tools that keep state there can fail (the user can make a
directory writable, `sandbox.writable`), and the network. The journal records the policy with the
hands (`HandsInfo.Sandbox`), so replay renders the same frame. Unsandboxed hands get no section.

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
| the launches' owned directories (`<home>/owned`), the temporary directory (`<home>/tmp`, `TMPDIR`), the tools' caches (`<home>/cache`, `XDG_CACHE_HOME` and others, below) | read-write |
| the components' install root (`<data dir>/runtimes`), interpreters' assets (`<home>/assets`) | read-only |
| the product's data directory (credentials, envd's state, other environments, the host's files) | none |
| credential locations (`SandboxPolicy.secrets`): `~/.ssh`, `~/.gnupg`, `~/.aws`, `~/.azure`, `~/.config/gcloud`, `~/.kube`, `~/.docker`, `~/.netrc`, `~/.git-credentials`, `~/.config/git/credentials`, `~/.config/gh`, `~/.config/hub`, `~/.config/glab-cli`, `~/.npmrc`, `~/.yarnrc.yml`, `~/.pypirc`, `~/.config/pip/pip.conf`, `~/.cargo/credentials[.toml]`, `~/.gem/credentials`, `~/.nuget/NuGet/NuGet.Config`, `~/.m2/settings.xml`, `~/.gradle/gradle.properties`, `~/.terraform.d/credentials.tfrc.json`, `~/.vault-token`, `~/.config/containers/auth.json`, `~/.password-store`, `~/.config/op`, `~/.local/share/keyrings`, `~/Library/Keychains`; on Linux `/run/user` (the session bus, which serves the Secret Service, and agents' sockets), `/run/docker.sock`, `/run/podman`, `/run/containerd`; on macOS `/var/run/docker.sock` | none |
| the host's `sandbox.writable` / `sandbox.denied` (absolute or under `~/`) | read-write / none |
| everything else | read-only |
| the network | `network` of the spec: `open` (default) or `none` |

The list of credential locations is a guardrail of known places (keep secrets in the OS keyring, which
is reachable only through the denied session bus on Linux, and through the keychain's own access
control on macOS). The user's home is read-only, so tools that write caches there fail unless the host
lists the cache in `sandbox.writable`; each writable cache is a way for what an agent wrote to run
outside the sandbox later. A credential location stays unreadable even inside a writable path (the
longest root decides): `sandbox.writable` cannot help a tool that reads one.

## Choosing a driver (`EnvSandbox.choose`)

The privileged variant `sandbox.driver` (default `none`, T137) and the spec's `sandbox`:

| `sandbox.driver` | spec's `sandbox` | the driver |
|---|---|---|
| `none` (default) | absent or `none` | `none`: off (`SandboxChoice.Off`), recorded without notes, no warning |
| `auto` | absent | the first contributed driver that confines on the environment's OS (bubblewrap on Linux, Seatbelt on macOS), **falling back to `none`** with notes that say why if its probe fails; `none` with a note where no driver confines (Windows) |
| `auto` | `none` | `none` if no driver confines on the OS; refused otherwise (only the host turns sandboxing off) |
| a driver | absent or the same | that driver, **required**: if its probe fails, the environment fails |
| any | another confining driver | that driver, required (a spec can always tighten), unless the host requires a different one (refused) |

- Each attach (every connection to envd) decides again: the install step resolves and probes the
  driver, builds the policy, creates `<home>/tmp` and `<home>/sandbox`, runs setup in the sandbox (with
  `setup.network`, else the spec's `network`), and records `EnvSandboxInfo {driver, confines, notes}`
  in the `readied` event (an optional member, so version 1's events read the same).
- `network: none` needs a driver that confines; with `none` (the default too) the environment fails,
  naming the variant that turns a sandbox on.
- `Envs.warnings record`: an environment that is not confined although the host asked for a sandbox
  (an unconfined sandbox with notes: `auto` fell back) carries `runs unsandboxed: the sandbox the host
  asked for cannot run here (…)`, the persistent warning clients show (the TUI's banner, T130). On
  Windows the note says that Windows has no sandbox driver yet (AppContainer, T511). The host's own
  `none` has no notes and no warning; `Envs.sandboxLabel` names it `off` for clients. (Records written
  before T137 noted a host's `none` too; the next attach records them again.)
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

Where no driver confines (the default, and Windows) only the second applies: the bindings keep to the
policy (no reading the credential locations or the data directory, no writing outside the workspace's
writable roots), but commands are not confined.

**Auto approvals** (spec §13.3, codeact.md "Approval modes"): the CodeAct guard runs commands written
plainly in the workspace unasked, sandbox or not; the sandbox does not gate them, it makes them
stronger. Where a driver confines (`EnvSandboxInfo.Confines`), what those commands do is held to this
page's policy (writes, credentials); with `none` they run with the user's rights, and clients say so
(`approvals: auto · unsandboxed`). Neither holds the network, which stays open unless the spec closes it
(and needs a driver to). The grant that turns them on names the driver (`approvals.auto`, `none` when
nothing confines), so a policy can forbid them only unsandboxed.

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
(arm64 and Intel) with `SANDBOX_TESTS=1`, and `none` everywhere; the CodeAct live tests and the host's
sandboxed runs opt in (`sandbox.driver = auto`) and run the bun-ts kernel in the sandbox. A new driver is done when it passes it on every OS it lists.

## Known limits

With the sandbox on:

- **Tools that keep state under `~`** fail where no variable moves it into the environment: NuGet's
  global packages folder (`~/.nuget/packages`), Cargo's registry (`~/.cargo`), the MSBuild server and
  .NET's named mutexes (the runtime directory, `/run/user/<uid>`, is denied on Linux), `go install`
  into `~/go/bin`, `pip install --user`. Workaround: the host lists the directory in the privileged
  `sandbox.writable` (each one is a way for what an agent wrote to run outside the sandbox later). The
  general fix is a copy-on-write home per environment (T512).
- **Tools that read a config on the credential list** fail on the unreadable file rather than ignoring
  it: NuGet (`~/.nuget/NuGet/NuGet.Config`), Yarn Berry (`~/.yarnrc.yml`: "EACCES"); Maven and Gradle
  likely too (`~/.m2/settings.xml`, `~/.gradle/gradle.properties`). npm and pip ignore theirs. A
  credential location stays unreadable inside a `sandbox.writable` path, so there is no workaround but
  turning the sandbox off for that work (T512 would show such files as absent).
- **git's config and hooks are read-only** in a writable workspace (`.git/config`, `.git/hooks`, and
  `.<product>`): what changes them fails: `git config`, `git remote add`, `git branch
  --set-upstream-to`, `git push -u`, installing hooks, and tools that keep their own state in
  `.git/config` (lane's `lane new` stores a lane's base there). Under bubblewrap the error reads "could
  not write config file .git/config: Device or resource busy" (git renames its lock file onto the
  path, a mount point); under Seatbelt "Operation not permitted". This is deliberate: both run code
  outside the sandbox the next time a person uses git (`core.hooksPath`, `core.fsmonitor`, aliases,
  hooks). The prompt says so.
- **A linked worktree as the workspace**: its git directory (`<main>/.git/worktrees/<name>`) and the
  objects are outside the workspace, so git cannot commit there. Open the main checkout.
- **Signing and pushing**: SSH and GPG keys and the agents' sockets are hidden, so signed commits
  (`commit.gpgsign`) and pushes over SSH or with a credential helper fail; leave them to the person.
- **macOS with the network denied** blocks every unix socket, the environment's own included: .NET's
  named pipes (MSBuild's nodes, the compiler server) cannot connect. With the network allowed the
  environment's writable directories and `TMPDIR` keep their sockets.
- **Protected paths that do not exist** when a process starts are not mounted by bubblewrap (it would
  create them in the workspace): a sandboxed process can create a missing `.git/hooks`.
- **One temporary directory per environment**: processes under different policies in one environment
  (an implementer and a read-only reviewer) share `TMPDIR`, so a long-lived process of one can serve
  the other over a unix socket there.

## Native AOT

Scenarios: `sandbox-policy` (rules, access, guarded directories, overrides and their JSON),
`sandbox-choose` (the host's choice for every combination, an environment's policy),
`sandbox-bubblewrap` (command lines, the seccomp program's digest and verdicts per architecture, the
probe's diagnosis), `sandbox-seatbelt` (a profile and its parameters), and with `SANDBOX_TESTS=1` on
Linux and macOS `sandbox-live` (a process confined by this OS's driver, and one that outlives the
thread that started it). CI's sandbox steps diff them
natively (`--only sandbox-`).

## Left for later

| What | Task |
|---|---|
| `fx.sandbox.appcontainer` on Windows, which `auto` then picks there | T511 |
| A copy-on-write home per environment (the real home as a read-only lower layer, a private upper layer, credential paths as whiteouts), so tools that keep state under `~` work in the sandbox without per-tool variables; bubblewrap's `--overlay` needs 0.10 (Ubuntu 24.04 has 0.9), Seatbelt cannot remap paths | T512 |
| Network allowlists (`allowlist:<name>`), credential injection, per-call attribution: the egress proxy | with the egress proxy (spec §18) |
| Agents' `sandbox:` from their definitions into `KernelPlan.Sandbox`; child sessions | T213 |
| Protected paths from goals (`protect: ["tests/**"]`): globs that name a directory become its path; others need a check of the diff | T212 |
| envd resolving the bindings' paths beneath their roots (`openat2` `RESOLVE_BENEATH`, `O_NOFOLLOW_ANY`), closing the link race of `SandboxGuard` | follow-up (T113b) |
| The user's home on other machines (home-relative denied paths there), drivers probing over the environment protocol | T501 |
| Choosing a sandbox per environment in the TUI | when people ask |
| Sandboxed `Script` installs; sandboxing extension processes | T211; with the extension host |
| Plugins: nsjail, gVisor | — |
