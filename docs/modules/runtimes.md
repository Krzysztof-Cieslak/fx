# Module spec: runtimes (`Fx.Sdk` `Runtime.fs`, `InjectionVectors.fs`; `fx.runtime.bun`)

Spec: §6.2–6.3 (with §6.1, §16.2, §16.4–16.5, §18). Task: T114.
Code: the model and the launch in [`src/Fx.Sdk`](../../src/Fx.Sdk) (`InjectionVectors.fs`,
`LaunchSpec` in `Manifest.fs`, `Runtime.fs`); the Bun runtime in
[`extensions/Fx.Ext.RuntimeBun`](../../extensions/Fx.Ext.RuntimeBun) (its
[README](../../extensions/Fx.Ext.RuntimeBun/README.md) has the pins, the baseline decision, the
environment's justification and the positive controls).
Tests: `tests/Fx.Sdk.Tests/RuntimeTests.fs`, `tests/Fx.Ext.RuntimeBun.Tests` (the runtime
conformance suite, the Bun data, plan → install → launch offline, and the real Bun with
`NETWORK_TESTS=1`), the bootstrap test in `tests/Fx.Extensions.Tests`, and the `runtimes-*`
scenarios of the JIT-vs-native diff.

A **runtime** is a program that runs other programs: Bun, later .NET or Python. fx installs it as a
component (components.md) and starts it only through its **launch spec**: data that says which
flags, which working directory and exactly which environment, with every runtime injection vector
denied. Nothing in a project can configure a runtime before the project is trusted (spec §18).

## The model (in `Fx.Sdk`)

```fsharp
type RuntimeSpec =
  { Component: Component            // the component that provides it (one exact version)
    Launch: LaunchSpec              // how to start it, hardened
    Files: RuntimeFile list }       // written into the launch's owned config dir before every launch
and RuntimeFile = { Name: string; Content: string }   // e.g. Bun's bunfig.toml

type LaunchSpec =                   // shared with the process runtime's manifest `launch`
  { Exe: string; Args: string list                  // templates
    EnvAllowlist: string list                       // names (or prefixes ending in *) taken from the launcher
    EnvSet: (string * string) list                  // values are templates
    EnvDeny: string list                            // must cover InjectionVectors.all
    Cwd: LaunchCwd                                  // Owned | Workspace
    UntrustedOverrides: string list }               // flags added while the project is not trusted
```

- `RuntimeSpec` adds `Files` to spec §6.2's shape: the configuration a runtime reads through a flag
  (`--config=${file.bunfig.toml}`) is part of the runtime's data, written by the launcher, so a
  project's file never takes its place.
- A runtime's id is its component's. Extensions contribute runtimes under
  `RuntimeSpecs.contributionType` (`runtime`, data, **privileged**, in the core catalog next to
  `component`). A runtime's package contributes its component under `component` too, for the
  planner; the two values are the same definition.
- `RuntimeSpec.codec` validates while it decodes: a manifest whose runtime does not deny every
  vector, or uses an unknown placeholder, is refused when the extension loads, with paths
  (`$.contributions[0].value.launch.envDeny: does not deny "BUN_OPTIONS", …`).

### Injection vectors

`InjectionVectors.all` lists every environment variable known to make a runtime load code or options
before its program does, each with who reads it and what it does: Bun (`BUN_OPTIONS`, `BUN_INSPECT*`,
`BUN_CONFIG_*`, `BUN_JSC_*`, `BUN_FEATURE_FLAG_*`, `BUN_DEBUG*`), Node and npm (`NODE_OPTIONS`,
`NODE_PATH`, `NODE_CHANNEL_*`, …, `NPM_CONFIG_*`), .NET (`DOTNET_STARTUP_HOOKS`, profilers, …), the
dynamic loaders (`LD_PRELOAD`, `LD_AUDIT`, `LD_LIBRARY_PATH`, `DYLD_*`), Python, Perl, Ruby and
Java. The Bun and Node rows come from the T009 spike, four of them with positive controls.

- It is one list for every runtime: a runtime's children may be other runtimes (a Bun cell starting
  Node), and denying a variable a runtime ignores costs nothing.
- `Manifest.deniedEnvironment` is this list: the `process` runtime removes them from every extension
  process, and the installer's probes run without the exact names.
- A new vector goes into this table. Every runtime spec that does not deny it then fails validation,
  so update the built-ins' `EnvDeny` in the same change.

### Validation

`LaunchSpec.validate` (every runtime's launch):

- `EnvDeny` covers every vector, exactly or with a broader prefix (`BUN_*` covers `BUN_INSPECT*`);
- no `EnvSet` name is denied (compared without case), contains `*`, or is set twice (without case);
- no allowlist entry is `*` (that would pass the launcher's whole environment on) or a denied name;
- every untrusted override is a flag.

`RuntimeSpec.validate` adds:

- the program is in the install directory: `Exe` starts with `${bin.<command>}` or `${root}`, never
  a name looked up on `PATH`;
- `Args` are flags only, written `--name=value`: the program to run and its arguments come from the
  launch, not the spec;
- every placeholder exists;
- file names are plain names, unique without case.

## Templates

| Placeholder | Value |
|---|---|
| `${root}` | the component's install directory |
| `${bin.<name>}` | a command the component exports (absolute) |
| `${exeSuffix}` | `.exe` on Windows, empty elsewhere |
| `${owned.root}`, `${owned.cwd}`, `${owned.home}`, `${owned.config}`, `${owned.cache}`, `${owned.temp}` | the launch's owned directories |
| `${file.<name>}` | one of the runtime's files, in `${owned.config}` |

Nothing else: no host variables and no workspace paths, so a template cannot carry the launcher's
environment or the project into the runtime. Values are whole paths: a template cannot join paths
(`${owned.cache}/x`), which would mix separators on Windows. A runtime that needs a cache shared
across launches (under the product's home) gets a `${shared.*}` placeholder then.

## Launching

```fsharp
RuntimeSpec.launch : RuntimeSpec -> InstalledComponent -> LaunchSession -> program: string list -> Validation<ProcSpec, Issue>
RuntimeSpec.prepare : RuntimeSpec -> OwnedDirs -> Eff<#IFs, Result<unit, FsError>>
```

`LaunchSession` is what the caller read from the world once: the target (`TargetTriple.ofRid` of the
running binary), the owned directories, the workspace, whether the project is trusted, and the
launcher's environment. `launch` is pure, and its result does not depend on the machine that
computes it: paths are joined and judged absolute by the target's rules.

- **Program:** `Exe` expanded; it must be an absolute path on the target.
- **Arguments:** the runtime's flags; then, while the project is not trusted, `UntrustedOverrides`;
  then `program` exactly as given (an entry point and its arguments, or `--version`).
- **Working directory:** `Owned` is `OwnedDirs.Cwd`; `Workspace` is the session's workspace, an error
  if there is none.
- **Environment, built fresh:**
  1. the launcher's variables that `EnvAllowlist` names (without case on Windows);
  2. the component's exported variables;
  3. `EnvSet`. Each one replaces a variable of the same name.
  4. Every denied name is removed: `EnvDeny` and the vectors, without case everywhere.

  The result is `Exactly` those, sorted by name. The launcher's own environment is never passed on
  otherwise.
- **Refusals** (issues with paths): an invalid spec, an `InstalledComponent` of another id or
  version, a relative program, a missing workspace.

`prepare` creates the owned directories and writes the runtime's files. Run it before every launch:
the files are rewritten, so a process cannot change what the next launch reads.
`OwnedDirs.under dir` is the usual layout: `cwd`, `home`, `config`, `cache`, `tmp`. Owned
directories belong to one launch, or to one kernel or session. Never put them inside the workspace.

### Using a runtime (T115, T117: `Interpreter.prepare`, interpreters.md)

```fsharp
// 1. Install it: the extension requires { Id = "bun"; Version = "^1.4" } (manifest `requires`),
//    or the caller ensures it through IComponents with its own holder.
let! installed = Components.ensure { Holder = holder; Requires = [ { Id = "bun"; Version = "^1.4" } ] }
// 2. The runtime: from the `runtime` contributions in effect (ExtensionHost.contributions RuntimeSpecs.contributionType).
// 3. Own directories for this launch, written before it.
let owned = OwnedDirs.under (Path.Combine(launchesRoot, launchId))
let! prepared = RuntimeSpec.prepare runtime owned
// 4. The process, then start it with the shell capability (Shell.spawn) or a PTY.
match RuntimeSpec.launch runtime bun session [ kernelEntry; $"--global={Product.kernelGlobal}" ] with
| Ok proc -> Shell.spawn proc
| Error issues -> …
```

The kernel's own hardening stays the kernel's: e.g. `Bun.Transpiler` needs `macro: false`, because
`--no-macros` does not reach it, and `bun check` needs `--no-script` (docs/spikes/bun-kernel.md).

### The process runtime

A manifest's `launch` is the same `LaunchSpec`, read with two differences:

- the host removes the vectors whatever the manifest says, so a manifest need not list them;
- `UntrustedOverrides` go before the first argument that is not a flag (`LaunchSpec.arguments`),
  because a manifest's arguments hold the program too. `LaunchContext.ProjectTrusted` comes from the
  bootstrap's trust context.

The field is written into a manifest's JSON only when it has entries, so that adding it changed no
manifest's hash.

## Conformance suite v1

`RuntimeConformance.tests name spec` (`tests/Fx.Ext.RuntimeBun.Tests`) is what every runtime spec
must hold to, whoever contributes it:

- it is valid, and travels unchanged as the `runtime` contribution's JSON;
- the planner plans its component for every target it supports, each with a digest-pinned archive
  at an `https://` URL, and the component has a probe;
- on every target, its launch runs the program from the install directory, by absolute path, in the
  owned directory (if `cwd = owned`);
- the untrusted overrides come before the program until the project is trusted, and the program
  comes last, as given;
- with a hostile launcher environment (every vector, in two spellings, and secrets), the
  environment holds only names the spec allowlists, sets or exports, no vector and no secret, one
  value per name, and every owned value inside the owned root;
- removing any one vector from its deny list makes it invalid, and its launch refused.

A runtime's live behaviour is its package's to test: `LiveTests` installs the real Bun and checks its
flags exist (`bun --help`) and the positive controls (extensions/Fx.Ext.RuntimeBun/README.md).

## Native AOT

Scenarios: `runtimes-vectors` (the table, patterns, argument order), `runtimes-validation` (issues
of broken specs, decoding), `runtimes-bun` (the manifest hash, methods, assets, round trip) and
`runtimes-launch` (the Bun launch on all eight targets, trusted and not, with a hostile
environment). Their output does not depend on the operating system.

## Left for later

| What | Task |
|---|---|
| Launching a process extension on a runtime (a manifest `launch` that names the `bun` runtime and its entry point), the TypeScript extension host | T115 |
| The bun-ts kernel's owned directories per session (per environment: `EnvLayout.owned`, environments.md), killing stale kernels (T117 launches it on this runtime: interpreters.md) | T127, T129 |
| Where launches' owned directories live (under the product's home, per session), serving runtimes to envd over the environment protocol | T111, T129 |
| The installer's probe (`bun --revision`) inherits the installer's environment minus the vectors' exact names: an inherited environment cannot drop prefixes (`BUN_INSPECT*`). The composition root should set `InstallerSettings.ProbeEnv` to `Exactly` a small environment | T129 |
| `ReleaseIndex` with `SHASUMS256.txt` and its signature as the vendor index | T211 |
| `--no-addons` and other optional hardening as variants | when packages come (interpreters.md) |
| A `${shared.*}` placeholder for caches shared across launches | when a runtime needs one |
