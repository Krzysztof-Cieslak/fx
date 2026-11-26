# Module spec: environments (`Fx.Sdk` contract, `src/Fx.Environments`, `fx.env.local`)

Spec: §9 (9.1 contribution types, 9.2 provider contract and bootstrap, 9.3 workspaces, 9.4 specs, 9.5
lifecycle), §3 (embedded mode), §6.1 (components in the environment), §16.4–16.5 (built-ins,
privileged contributions), §18 (credentials, trust). Task: T112.
Code: the contract in [`src/Fx.Sdk/Environment.fs`](../../src/Fx.Sdk/Environment.fs) (public: extensions
contribute providers); the host's side in [`src/Fx.Environments`](../../src/Fx.Environments) (pure apart
from the capabilities it is handed); the local provider in
[`extensions/Fx.Ext.EnvLocal`](../../extensions/Fx.Ext.EnvLocal); embedded envd in `EnvdMain.embed`
([`src/Fx.Envd.Live`](../../src/Fx.Envd.Live)).
Tests: `tests/Fx.Environments.Tests` (specs, the lifecycle machine and its replay, the provider
conformance suite on `fx.env.local` in process and piped, sandboxed environments), `tests/Fx.Extensions.Tests` (the package
loads, a project cannot contribute providers, architecture), and the `environments-*` scenarios of the
JIT-vs-native diff.

An **environment** is where an agent's hands are: a workspace and the machine around it, with envd
running there ([envd.md](envd.md)). A **provider** (an extension) creates environments and starts
processes in them; the **host** turns a spec into a ready environment (provider, envd, components,
setup), keeps its lease in the event store, and hands out envd's capability records (`EnvClient`).

```
spec (config) ──check──▶ EnvsHost (lifecycle actor) ──Create/Platform/Exec──▶ EnvProvider (extension)
                              │ bootstrap: envd embedded (in process) or piped (provider's Exec)
                              ▼
                         EnvClient: Fs, FsOps, Shell, Terminal, Blobs, Snapshots  ──▶ host code (IEnvs.Open)
```

## The provider contract (`Fx.Sdk`)

```fsharp
type TransportKind = InProcess | Piped | Direct | Dialback
type ProviderCaps = { Transports; Suspend; Fork; Snapshots: SnapshotKind list
                      Materialize: MaterializeKind list; Prebaked; Gpu }
type EnvProvider =
  { Kind; Description; Caps; Config: Codec<Json>
    Create   : EnvRequest -> Eff<IProviderEnv, Result<Lease, ProviderError>>
    Platform : Lease -> Eff<…, Result<EnvMachine, …>>
    Exec     : Lease * ProcSpec -> Eff<…, Result<Proc, …>>          // pipes; envd's stdio when piped
    Upload   : Lease * Upload -> Eff<…, Result<unit, …>>              // a host file into the environment
    Connect  : (Lease -> Eff<…, Result<Endpoint, …>>) option         // Direct
    Suspend, Resume : (Lease -> …) option;  Snapshot : (Lease -> Eff<…, ProviderSnapshot>) option
    Fork     : (Lease * int -> Eff<…, Lease list>) option
    Destroy  : Lease -> Eff<…, Result<unit, …>>
    List     : Eff<…, Result<Lease list, …>> }
```

- **Values, not tasks.** Every operation is an effect over `IProviderEnv`: the provider's extension
  environment (its id, clock, the caller's scope), the host machine's files (`IFsRead`, `IFs`) and
  processes (`IShell`), and `HostInfo` (the host's target, the product's data directory, the user's
  home). Providers reach their environments through those (a directory here, `ssh` or `docker`
  later). Multi-argument operations take tuples (Native AOT: no curried function fields over many
  types, see AGENTS.md).
- **Expected failures** are `ProviderError`: `Invalid` (issues with paths into the spec),
  `Unsupported` (names what and the task that brings it), `NotFound`, `Failed` (a message). A
  provider that throws fails its step, not the host (the host records the exception's type only).
- **Leases.** `Lease = { Id; Provider; Workspace; Home; Data }`. `Id` is the environment's id the host
  passed to `Create`: providers name what they create after it, so **creating again is idempotent**
  and the host finds its leases in `List`. `Home` is a directory of the environment alone, outside the
  workspace. `Data` holds provider facts (container ids, host names) as strings.
- **`Platform`** reports `EnvMachine = { Target; DataDir; SameHost }` before envd runs: what envd and
  components must be built for, the product's per-user data directory there, and whether the
  environment *is* the host's machine (then the host's binary and installs are usable as they are).
  envd reports the rest (`Platform` in `Fx.Envd`: shell, features, case sensitivity) at the handshake.
- **`Destroy`** releases what the lease holds and nothing else: an `existing` workspace stays.
  Destroying what is gone succeeds.
- **`Config: Codec<Json>`** checks the spec's `config` member; strict (unknown settings are refused).
- `EnvProviders.contributionType` (`environment-provider`) is **code that runs in the host's process**
  (native extensions only) and **privileged** (spec §16.5): only the binary, host config and policy
  may contribute providers. It is in the core catalog. The contribution id is the kind.
  `EnvProviders.validate` checks that the caps match the operations provided (Connect ⇔ Direct,
  Suspend/Resume ⇔ caps.suspend, Fork ⇔ caps.fork, Snapshot ⇔ caps.snapshots).

### Decisions

- **`InProcess` is a transport** (spec §9.2 lists `Piped | Direct | Dialback`). Embedded mode (spec
  §3) needs envd in the host's process over the in-process transport; it is offered only for
  `SameHost` machines, and the provider takes no part in it beyond `Create`/`Platform`: the composition
  root runs envd (`EnvsSettings.Embed`, `EnvdMain.embed`).
- **`Upload` takes a host path**, not a stream: copying a file is what `scp` and `docker cp` do, and
  the host's binaries are files. **`Exec` has pipes only**: terminals are envd's once it runs.
- **Providers are in-process code.** A provider in an extension process or on Bun (E2B, T510) needs
  the operations over the extension protocol (`Exec`'s byte streams over channels); that is a
  remote form of the contribution type for later.

## Environment specs (spec §9.4)

Config files are JSON (as for variants, [variants.md](variants.md)); `EnvSpecs.ofConfig` reads the
`environments` member, a spec per name (the key):

```json
{ "environments": {
    "dev":   { "provider": "local", "workspace": { "path": "~/src/app" }, "transport": "piped" },
    "cloud": { "provider": "e2b", "config": { "template": "node22", "apiKey": "secret:e2b" },
               "workspace": { "source": "git", "ref": "HEAD", "export": "branch" },
               "components": { "placement": "prebaked", "fallback": "host-push" },
               "setup": { "run": ["bun install"], "network": "open", "secrets": ["npm"] },
               "policy": { "network": "allowlist:dev", "idle": "suspend:15m", "ttl": "8h", "budget": "$5/day" } } } }
```

- `EnvSpec.codec` is strict at every level; issues have paths into the file
  (`$.environments.dev.workspace.source: expected one of …`). Names are `^[a-z][a-z0-9_-]*$`.
- Members: `provider`, `config` (default `{}`), `transport` (default: the provider's first),
  `sandbox`, `network`, `workspace` (`source` default `existing`, `path`, `ref`, `export`),
  `components` (`placement` default `env-fetch`, `fallback`), `setup` (`run`, `network`, `secrets`),
  `policy` (`network`, `idle`, `ttl`, `budget`: recorded, enforced by later tasks).
- `EnvSpecs.merge` layers specs (host, then project once trusted): a later layer's spec replaces one
  of the same name. Reading config files and project trust are the composition root's (T129):
  **setup commands from a project's config run only after the project is trusted** (spec §18).
- **Capability negotiation** (`EnvSpecs.check provider spec`, before anything is created): the
  provider is the one named; `config` decodes with its codec; the transport is one it offers and one
  this host implements (`Direct`, `Dialback`: T501, T504); the workspace's source is one it
  materializes; no `export` yet (T503); `sandbox` is a driver's id, one in effect
  (`EnvSpecs.checkSandbox`; which driver applies is decided when the environment connects:
  [sandbox.md](sandbox.md)); `network` and `setup.network` are absent, `open` or `none` (`none` needs a
  sandbox driver that confines; allowlists need the egress proxy); no `setup.secrets` (credential
  injection); `components.placement` is `env-fetch` (T503 for the rest).

## The lifecycle (spec §9.5)

`Lifecycle.machine` runs as **one actor per host** on the stream `environments/<host>`, so listing and
reconciling environments is a fold. Its events are kept forever (`EnvLog.schema`, version 1; golden
file `tests/Fx.Environments.Tests/golden/events-v1.jsonl`).

| Phase | Meaning | Step in flight |
|---|---|---|
| `requested` | accepted | `provision` (`Create`) |
| `provisioning` | the lease is held; envd is starting, or is to be started again (detached) | `bootstrap`, or none |
| `bootstrapped` | envd runs | `connect` |
| `connected` | the host speaks to envd | `install` (components, the sandbox, then setup) |
| `ready` | ready | — |
| `suspended` | the provider suspended it | — |
| `lost` | the provider no longer has it | — |
| `destroyed` | destroyed | — |
| `failed` | a step failed (`reason`) | — |

| Command | Events | Rejections |
|---|---|---|
| `Request` | `requested` | `environment.exists` |
| `Attach id` (no connection) | `attaching` (attempt + 1), or nothing while an attach runs | `environment.state`, `environment.busy` |
| `Suspend`, `Resume` | `started` | `environment.state`, `environment.busy` |
| `Destroy` | `started` (from any phase, over any step), nothing if destroyed | — |
| results: `Provisioned`, `Bootstrapped`, `Connected`, `Installed`, `Suspended`, `Resumed`, `Destroyed` | the next phase, or `failed` (a failed suspend or resume keeps the phase) | `environment.stale`: another step or attempt |
| `Disconnected(id, attempt)` | `detached` | `environment.stale` |
| `Reconciled(present, missing)` | `detached` (the host restarted; only the attempt recovery saw) / `lost` | — |

- **Attempts.** Every provisioning or attach is an attempt; results of another attempt are stale, so a
  late result changes nothing. Connections (`Live`) are per attempt; releasing an attempt also refuses
  its connections that register later, and a new attempt waits until the previous envd stopped (one
  envd per state directory).
- **Restart** (`Lifecycle.recover`, intents are not durable): steps safe to run again run again
  (`provision`, since `Create` is idempotent; `destroy`; `suspend`; `resume`), and every other lease
  is reconciled with its provider's `List`: held ones are `detached`, missing ones `lost`. A detached
  environment starts envd again when it is next opened. The answer carries the attempt each lease had
  at recovery: an environment a session opened again meanwhile (a new attempt) is not detached.
- A lost connection (envd ended, the host did not close it) detaches the environment the same way.
- Keys: the host's commands get keys from new ids; results from the actor (`<event>/<i>.<j>`);
  disconnections `disconnected:<id>:<attempt>`.

## The bootstrap (spec §9.2)

`Platform` → envd → handshake → components → setup:

1. `provider.Platform lease` gives the machine; the layout follows (`EnvLayout`: `<home>/envd` for
   envd's state, `<home>/assets` for interpreters' assets, `<home>/owned/<launch>` for runtimes' owned
   directories, `<home>/tmp` and `<home>/cache` for sandboxed processes' temporary files (`TMPDIR`,
   `XDG_RUNTIME_DIR`) and tools' caches and homes (`XDG_CACHE_HOME`, `DOTNET_CLI_HOME`, NuGet's
   folders, `CARGO_HOME`, …: `EnvSandbox.defaults`, [sandbox.md](sandbox.md) "Build tools"),
   `<home>/sandbox` for the sandbox driver's files, `<data dir>/runtimes` for components; paths in the
   environment's syntax).
2. envd starts for the transport:
   - `InProcess`: `EnvsSettings.Embed` (the composition root's `EnvdMain.embed`) in the host's process;
   - `Piped`: the host's own binary (`EnvsSettings.Binary`, `BinaryArgs` before `envd` on the JIT)
     through `provider.Exec` with `envd --stdio --workspace <w> --state <s> [--shell <shell>]`; its
     standard output is the protocol (`ProcTransport`), the end of its standard error is kept for the
     error message. Another machine needs envd for its target, uploaded (T501).
   - On Windows the shell is the **`shell.windows` variant** (`pwsh` or `powershell`), read by the host.
3. `EnvClient.connect` (the handshake checks the protocol version and reads envd's platform). The
   connection lives as long as the host, not the intent.
4. **Components** of the lockfile (`EnvComponents.Requires`, held by `environment:<id>`), through the
   installer that serves the machine (`EnvComponents.For`: on the host's machine, the host's
   `ComponentInstaller.components`; elsewhere over the environment protocol, T501). Then the **sandbox**
   ([sandbox.md](sandbox.md)): the driver the host's `sandbox.driver` and the spec choose, probed in the
   environment, and the environment's policy; a required driver that cannot run fails the step, `auto`
   falls back to `none` with notes. Then `setup.run`, each command with the environment's shell in the
   workspace, **in the sandbox** (with `setup.network`); a non-zero exit fails the step. The `readied`
   event records the sandbox (`EnvSandboxInfo`).

**Credentials stay on the host** (spec §18). On the host's machine, envd and everything it starts get
the host's variables **scrubbed** (`HostEnvironment.scrub`): runtime injection vectors
(`InjectionVectors`) and names that look like credentials (`*_API_KEY*`, `*_TOKEN`, `*SECRET*`,
`*PASSWORD*`, `*_PRIVATE_KEY*`, `*_CREDENTIALS*`, …, without case). Piped envd starts with exactly those;
embedded envd uses them as the base of every process it starts (`EnvdOptions.Environment`,
`BaseEnvironment` in `Fx.Envd`). The list is a guardrail, not a boundary: sandboxes are
([sandbox.md](sandbox.md): credential files are denied paths).

## The capability (`IEnvs`)

```fsharp
type Envs = { Create: EnvSpec -> …Result<EnvRecord, EnvError>    // waits until ready (or not)
              Get; List; Open: string -> …Result<EnvHandle, EnvError>
              Suspend; Resume; Destroy }
type EnvHandle = { Id; Spec; Client: EnvClient; Machine: EnvMachine; Layout: EnvLayout; Installed
                   Sandbox: EnvSandbox }        // Client's processes and terminals start in the sandbox
```

`EnvsHost.start settings log` serves it (`EnvsHost.envs`). `Open` returns the capability records of a
ready environment, attaching it again first if the host has no connection (after a restart or a lost
connection); its shell and terminals start every process in the environment's sandbox, and
`EnvHandle.attenuate overrides` gives an agent's tighter one ([sandbox.md](sandbox.md)).
`Envs.warnings` gives what a person must be told: an environment no driver confines runs unsandboxed
(spec §9.1: a persistent warning; T130 shows it). `EnvsHost.stop` closes every connection (envds
stop) and keeps the leases for the next host. `EnvsSettings.Sandboxes` are the drivers in effect
(built-ins first: `auto` takes the first that confines on an environment's OS), and the host registers
`SandboxSlots.all` with `Slots.all`.

`Create`, `Destroy`, `Suspend` and `Resume` wait for the outcome in the log, which the actor appends
before it publishes the state the events make; reads (`Get`, `List`, `Open`) take the newer of the
actor's state and the one a wait returned, so they never show less than a result did.

Continuations: actors reply, and the in-memory log wakes subscribers, on the appending thread; the
host hands those results on asynchronously (`later`), so code that reads the log or sends again never
runs inside an append.

## `fx.env.local`

A native extension (`Product.extensionId "env.local"`) contributing the `local` provider: the host's
own machine. Caps: transports `in-process` (default) and `piped`; materializes `existing`; no
suspend, fork or snapshots; config `{}`.

- `Create`: the workspace is the spec's `path` (absolute, or under `~`), normalized, and must be an
  existing directory. The home is `<data dir>/environments/<id>`, with the lease in `lease.json` (what
  `List` reads after a restart). Ids must be plain names.
- `Platform`: the host's target and data directory, `SameHost = true`. `Exec`: the host's shell.
  `Upload`: unsupported (the host's machine has every file).
- `Destroy`: deletes the home (envd's state with it), depth first, never following links, and only if
  it is the provider's home for that id; the workspace is never touched.
- Its processes are sandboxed like every environment's ([sandbox.md](sandbox.md)); its home holds the
  sandbox's temporary directory and the driver's files.

## Conformance suite v1

`ProviderConformance.tests subject` (`tests/Fx.Environments.Tests/Conformance`) is a function from a
provider and how a host starts with it (`Subject`: the provider, a transport, specs for fresh
workspaces holding `hello.txt`, a failing spec, `Start`, the provider's environment, a way to lose a
lease) to tests. Everything goes through `EnvsHost` and envd's capability records:

- the provider's data is valid and offers the subject's transport;
- create → ready, leased (`Lease.Id` = the environment's id), listed by the host and the provider;
- the machine, envd's platform (OS, architecture, workspace) and the layout under the home;
- processes run in the workspace with the environment's shell;
- on the host's machine, the host's plain variables pass, credentials and injection vectors do not;
- files: read, write, batch stat; blobs; workspace snapshots where envd has them (take, diff, restore);
- destroy: destroyed, the provider let it go, destroying again succeeds, it cannot be opened, an
  existing workspace stays;
- lease reconciliation after a restart: a held lease is detached and opens again (same workspace,
  a new attempt), a lost one is `lost` and can be destroyed;
- attaching again after the connection to envd is lost;
- capability negotiation: transports and materializations not offered, unknown settings, a sandbox
  driver that does not exist, an unknown provider; nothing is created;
- the record says what sandboxes the environment, and an unsandboxed one carries one warning;
- a failed creation is `failed` with a reason and can be destroyed;
- `Create` and `Destroy` are idempotent per id at the provider.

`fx.env.local` passes it in process and piped (the `fx` binary next to the tests), on Linux, macOS and
Windows. A new provider is done when it passes it for every transport it offers.

## Native AOT

Scenarios: `environments-specs` (config, issues, negotiation, scrubbing, layout, codecs),
`environments-lifecycle` (a script of commands with every rejection, the events' JSON, recovery),
`environments-local-in-process` and `environments-local-piped` (a session through the host: create,
files, a command with the scrubbed variables, a lost connection and a new attempt, a restart that
reconciles, destroy; piped runs the scenarios program's own `envd --stdio` mode).

## Left to later tasks

| What | Task |
|---|---|
| Network allowlists (the egress proxy); agents' overrides from their definitions | with the egress proxy; T213 ([sandbox.md](sandbox.md)) |
| Choosing an environment per session (the host's composition and `env/*` are [host.md](host.md)) | T130 |
| Showing `Envs.warnings` (unsandboxed) persistently | T130 |
| envd for other targets (upload), installing components over the environment protocol, `fx.env.ssh`, `Direct` over WebSocket | T501 |
| `Dialback` through a relay; reconnecting transports | T504 |
| Materialize `mount`, `git`, `upload`, `snapshot`; export after diff review; `host-push` and `prebaked` placement | T503 |
| `fx.env.docker`, benchmark containers; suspend, fork and provider snapshots | T216, T502 |
| Providers in extension processes and on Bun (a remote form of the contribution type) | T510 |
| Policy: idle suspend, TTL, budgets; cost tracking | with the run kernel's budgets (T212) |
| Kernels per session on an opened environment (`EnvHands.ofHandle`: assets at `EnvLayout.Assets`, owned directories per launch under `EnvLayout.Owned`) are T127's ([codeact.md](codeact.md)); serving runtimes and kernels to a remote envd | T129, T501 |
