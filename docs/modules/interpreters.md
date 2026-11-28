# Module spec: interpreters (`InterpreterProvider`, `src/Fx.Interpreters`, `fx.interpreter.bun-ts`)

Spec: §6.2–6.6 (interpreters, the Bun stack, kernel behaviour, values), §3 (kernels run in the
environment as separate processes), §4.5 (turn results), §6.9–6.10 (action surfaces, example cells),
§16.4 (built-ins embedded in the binary, extracted with hash checks), §18. Task: T117.
Code: `InterpreterProvider` and its parts in [`src/Fx.Sdk/Interpreter.fs`](../../src/Fx.Sdk/Interpreter.fs)
(public SDK); the host's side in [`src/Fx.Interpreters`](../../src/Fx.Interpreters) (the bridge, the
core bindings, assets, launching); the built-in in
[`extensions/Fx.Ext.InterpreterBunTs`](../../extensions/Fx.Ext.InterpreterBunTs) with the kernel's
sources in [`packages/kernel-bun-ts`](../../packages/kernel-bun-ts).
Tests: `tests/Fx.Interpreters.Tests` (over fakes, and the provider conformance suite),
`tests/Fx.Ext.InterpreterBunTs.Tests` (the package, golden declarations and prompt, end to end on the
pinned Bun), the kernel conformance suite in `tests/Fx.Kernel.Tests` (the bun-ts kernel launched as
the product launches it), `bun test` in `packages/kernel-bun-ts`, and the `interpreters-*` scenarios of
the JIT-vs-native diff.

An **interpreter** is a language for the model's cells plus the kernel that runs them (spec §6.2). The
kernel is a separate process in the environment, speaking the kernel protocol
([kernel-protocol.md](kernel-protocol.md)) on its stdio. The host owns what the kernel can call (the
**host bridge**), validates how a turn ends (`finish`), and generates the declarations and prompt the
model reads from the same codecs it decodes calls with.

```
 host                                                   environment (envd, or the host in embedded mode)
 ─────────────────────────────────────────────          ───────────────────────────────────────────────────
 Bridge: bindings (codecs) + finish (codec, Ensure)
   │ Bridge.surface ──► provider.Bindings ──► <global>.d.ts, prompt (PromptPack.render)
   │ Interpreter.prepare (IFs of the environment) ───►  assets extracted + checked, owned dirs, binding files
   │ RuntimeSpec.launch ──► KernelLaunch                 
   │ Kernel.start (IShell of the environment) ───────►  bun --config=… --no-install … main.ts --bindings=…
   ▼                                                        │ kernel protocol on stdio
 KernelHost: host/call ──► HostBinding.dispatch ──► capabilities (Shell, Fs, FsOps: envd's or local)
             finish    ──► Finishing (codec, Ensure, accept)
```

## The contract (`Fx.Sdk`)

```fsharp
type InterpreterProvider =
  { Id: string; Language: string; Description: string
    Requires: ComponentRef list                  // installed in the environment before a launch
    Launch: InterpreterLaunch                    // { Runtime = "bun"; Program = ["${asset.main.ts}"; "--bindings=${bindings}"] }
    Assets: RuntimeFile list                     // the kernel's own files, carried in the binary
    Bindings: BindingSurface -> BindingFiles     // e.g. <global>.d.ts, and the declarations for the prompt
    PromptPack: PromptPack                       // instructions and examples
    Check: CheckSpec option                      // Kernel: the kernel handles cell/check
    Caps: InterpreterCaps }                      // what its kernels announce in initialize
```

- **Differences from spec §6.2's sketch** (spec.md updated): the launch is not a `LaunchSpec` of its
  own but a **runtime** plus a program, because runtimes (T114) own the hardened launch: the kernel's
  process is `RuntimeSpec.launch runtime installed session program`, so its flags, owned directories,
  environment allowlist and injection-vector denial are the runtime's, never the interpreter's. And the
  kernel's files are `Assets`, carried in the binary (spec §16.4) rather than downloaded.
- `InterpreterCaps` (with `AsyncHostCalls`, `PackageSupport` and their codecs) moved from `Fx.Kernel`
  into the SDK: interpreters declare them; `KernelProtocol.Codecs` refers to the same codecs, so the
  protocol's JSON and TypeScript declarations did not change.
- **Bindings as the interpreter sees them:** `BindingSurface` is the global's name, the bindings
  (`BindingDecl`: a dotted path, a description, parameters and result as `Schema`s), descriptions of
  their groups, and the schema `finish` takes. It is language-neutral; the interpreter turns it into
  its language's files. `BindingFiles.Declarations` is the text the prompt shows.
- **Program templates:** `${asset.<name>}` (an asset's absolute path once extracted) and `${bindings}`
  (the directory with the binding files); nothing else, joined by the target's rules.
- `PromptPack.render pack files`: the instructions, the declarations in a code block, then the
  examples; deterministic, `\n` line ends. The prompt render places it in the
  prefix (`Sections.interpreter`, [prompt.md](prompt.md)).
- **Contribution type:** `InterpreterProviders.contributionType` (`interpreter`), code
  (`ContributionType.inProcess`: `Bindings` is a function), privileged (spec §16.5), in the core
  catalog. So interpreters are native extensions; a data form for declarative interpreters (spec
  §6.11's `fsi` sketch: `bindings = "fsi"`) comes with the first one that needs it (T602).
- `InterpreterProvider.validate`: the id (`^[a-z][a-z0-9]*([.-][a-z0-9]+)*$`), a language, a runtime
  and a program whose templates expand, plain unique asset names, version ranges, and `Bindings`
  working on an empty surface (checked when it is registered, so generating a session's declarations
  cannot fail later). `assetsDigest` is the SHA-256 of the canonical JSON of the assets' names and
  contents' SHA-256, in name order.

## The host bridge (`Fx.Interpreters`)

A session's bridge is a value: the global (`Product.kernelGlobal`, never written by hand), the bindings,
their groups' descriptions, and `finish`.

```fsharp
let bridge =
    Bridge.create (Finishing.create TurnResult.codec ensures (fun turn -> record turn))
    |> Bridge.add CoreBindings.groups (CoreBindings.all settings)
// Bridge.surface → provider.Bindings (declarations, prompt); Bridge.setup → initialize; Bridge.host → KernelHost
```

- **A binding** (`HostBinding<'R>`) is its declaration and an effect from the call's `Value`s:
  `HostBinding.create0|1|2 path description args returns run`. Each argument is an `Arg`
  (`Arg.required`, `optional`, `withDefault`) with its codec: the host decodes arguments only into
  their declared types (`Value.decode`: a blob or a handle never decodes into data), and every issue
  is reported at the parameter's name (`InvalidArguments`); nothing runs then. Extra arguments are an
  issue. Results are encoded with the result codec (inline JSON).
- **Dispatch** (`HostBinding.dispatch`) maps a `host/call`'s binding to its effect, or
  `UnknownBinding`.
- **Failures**: a capability's typed failure becomes `HostCallError.Failed(kind, message, data)`
  (`HostBinding.failed codec error`): its `kind` is the code, its JSON the data. In a cell the error's
  `code` is the protocol error's kind (`failed`, `invalidArguments`, …; kernel-protocol.md), so a
  capability's own code is `error.data.code` (`notFound`, `stale`, …) and in the message. A binding
  that throws is a defect: the kernel gets an internal error.
- **`finish`** (spec §4.5, §6.5): `Finishing.create codec ensure accept` decodes the result with its
  codec (issues under `result`), runs the checks (`Ensure`), then hands the typed result to `accept`
  (the strategy records it). Evidence stays the values the cell passed. The result type is the
  session's: T125's `TurnResult` codec, wired by T127; the tests use one of the same shape.
- `Bridge.validate`: paths are dotted identifiers, unique, never both a binding and a group, and never
  under the kernel's own names (`finish`, `activity`).
- `Cell.intent code`: the cell's `// intent:` line (its first line that is not blank), for `cell/run`'s
  `intent` and the transcript (spec §19.2). It does not change what the cell does.

### The core bindings

`CoreBindings.all settings` (spec §6.5 "env-side"), over the environment's capabilities: `IShell`,
`IFs`, `IFsOps` (envd's) and `IClock`. In embedded mode they are the local interpreters; with a remote
environment, `EnvClient`'s proxies.

| Binding | Does | Returns |
|---|---|---|
| `shell.run(command, options?)` | runs a command line with the environment's shell (`/bin/sh -c`; PowerShell 7 on Windows) in the workspace or `options.cwd`, with `options.stdin`, killed after `options.timeoutMs` | `RunResult {command, exitCode?, stdout, stderr, truncated, timedOut}`; a non-zero exit is data |
| `fs.read(path)` | a UTF-8 text file, whole, up to `ReadBytes` (1 MiB; else `tooLarge`); a byte order mark dropped; not UTF-8: `notText`; a file content exclusions keep from models: `excluded` (origin and pattern as data; nothing is read, [egress.md](egress.md)) | `FileText {path, text, lines, size, digest}` |
| `fs.write(path, text)` | creates the directories, then writes atomically (`FsOps.WriteAtomic`) | `Written {path, digest, lines}` |
| `fs.list(path = ".")` | a directory's entries, by name | `FileEntry[] {path, name, kind, size}` |
| `fs.stat(path)` | one entry; `notFound` if none | `FileEntry` |
| `fs.patch(patches)` | envd's apply-patch: `create`, `edit` (line ranges, with the digest from `fs.read`: `stale` if the file changed), `delete`; all checked, then all written | `Written[]` |

- Paths are relative to the workspace (`/` or `\`; `.` and `..` resolved) or absolute in the
  environment's syntax; results show them relative, with `/`, when inside the workspace.
- Output is bounded (spec §6.6): each stream of a command keeps `OutputChars` (16 000) characters, the
  head and the tail with a marker line (`… [n chars omitted] …`), never half a surrogate pair. Overflow
  into a blob with a grep-able path is T201's.
- `CoreSettings.CommandEnv` is the environment commands get: envd's own (`Inherit []`) by default. On
  the host's machine that is the host's variables without credentials and injection vectors
  (`HostEnvironment.scrub`, [environments.md](environments.md)); it never holds the host's credentials
  (§18).
- The settings' values are variants (spec §6.6); T127 reads them through `IVariants`.

## Launching

`Interpreter.prepare session surface` (on the environment's filesystem), then `Kernel.start` (on the
environment's shell): `Interpreter.start` does both with one environment, `Interpreter.startBridge`
from a bridge.

1. The provider is validated and must name the runtime given; the runtime's component must be among
   the installed ones (`Components.ensure` with `Provider.Requires`, in the environment).
2. **Assets** (`Assets.ensure`): the directory `<AssetsRoot>/<id>-<first 16 hex digits of the digest>`
   must hold every asset with the SHA-256 the binary carries. If not, the set is written to a staging
   directory of this launch, checked, and renamed into place (a damaged set is removed first); if
   another launch renamed its own first, that one is checked and used. A set is never rewritten in
   place, versions live side by side, and every launch checks again, so a changed or deleted file is
   restored. `AssetsRoot` is under the product's home, never in the workspace.
3. The runtime's owned directories are prepared (`RuntimeSpec.prepare`: bunfig, tsconfig), and the
   binding files written into `<owned root>/bindings`.
4. The program expands, and `RuntimeSpec.launch` gives the process: the runtime's hardened flags, the
   untrusted overrides until the project is trusted, then the program; a fresh environment.
5. `KernelLaunch` carries the runtime's deny list; `Kernel.start` refuses an unsafe launch, starts the
   process with the shell capability, and connects (kernel-protocol.md).

Where `AssetsRoot` and the owned directories live per environment is `EnvLayout` (`Assets`, and
`EnvLayout.owned` per launch, under the environment's home: [environments.md](environments.md));
serving runtimes and kernels to a remote envd is the composition root's (T129).

## `fx.interpreter.bun-ts`

A native extension (`Product.extensionId "interpreter.bun-ts"`, manifest `1.0.0`) contributing the
interpreter `bun-ts`: TypeScript cells on the pinned Bun (`fx.runtime.bun`, `bun 1.4.5`).

| Part | Value |
|---|---|
| Launch | runtime `bun`; program `${asset.main.ts} --bindings=${bindings}` |
| Caps | no snapshot (Jupyter semantics plus the journal, spec §6.5), host calls awaited, no packages, not deterministic |
| Check | `CheckSpec.Kernel`: `cell/check`, and `cell/run` with `check` |
| Bindings | `<global>.d.ts`: `DeclarationFile.render` with `Layout.Global(global, description)`; every binding as `<global>.<group>.<name>(…): Promise<Result>`, plus `finish(result): Promise<null>`, and every named type they use; groups without bindings left out |
| Prompt | instructions (TypeScript, `// intent:` lines, the bridge, values by name and bounded output, imports, `finish`), the declarations, three examples |

### How the kernel ships (spec §16.4)

The kernel is the TypeScript sources of `packages/kernel-bun-ts/src` (not the generated
`protocol.d.ts`, which is only imported for types). `extensions/Fx.Ext.InterpreterBunTs/KernelFiles.fs`
carries them as F# string literals, one per line, **generated** by the extension's tests from the
sources (`UPDATE_GOLDEN=1` rewrites it; the test fails when it is stale), and the provider's `Assets`
are those files. Bun runs TypeScript itself, so nothing is bundled.

- Why generated F# source: it is plain data in the assembly, so Native AOT keeps it with no reflection
  (no `GetManifestResourceStream`, which the analyzers ban as `System.Reflection`), no build step needs
  a Bun, and a change to the kernel shows as a reviewable diff. Bundling (`bun build`) would make a
  build depend on the pinned Bun and the output on its bundler.
- **Versions:** an asset set is named by its digest, so kernels of different binaries never mix; the
  extension's version is its manifest's. The kernel announces `peer.name = "bun-ts"` and its Bun and
  TypeScript versions in `initialize`'s `details`.
- Bun looks for a `tsconfig.json` above a file it runs: assets live under the product's home, and the
  runtime's `--tsconfig-override` applies until the project is trusted.

### The kernel

The design of the Bun kernel spike (docs/spikes/bun-kernel.md), grown into the reference kernel of the
protocol (T116) and finished here:

- **Cells**: `Bun.Transpiler({ loader: "tsx", replMode: true, macro: false })` (`--no-macros` does not
  reach the transpiler) into one persistent `node:vm` context: top-level declarations persist,
  top-level `await` works; each cell runs in its own `Bun.ModuleGraph` (timers, sockets and processes
  it starts belong to it); the completion value is kept as `Out[cellId]` and previewed within the
  limits; output is captured per cell, streamed and bounded; runtime errors map back to cell lines.
- **Globals** (`globals.ts`): one table gives the context's globals and their declarations
  (`kernel-globals.d.ts`): timers, `fetch`, URL, encoders, `crypto`, `performance`, … and `Out`; not
  `process`, `Bun` or `require` (cells import them). A unit test keeps the two in step.
- **Host bridge**: the global is built from `initialize`'s bindings; a call is a `host/call` with the
  cell's id. Arguments are strict JSON except that `undefined` members of objects and trailing
  `undefined` arguments are left out (as optional fields are written in TypeScript); `finish`'s result
  likewise. `value/encode` stays strict.
- **Type-checking** (`checker.ts`): `bun --no-orphans check --no-script --no-pretty -p <tsconfig>
  <cell>` in a check directory under the owned temporary directory, against the bridge's declarations
  (read from `--bindings`) and the globals. Each cell that ran is kept as a module exporting its
  top-level names; a checked cell starts with one line that re-declares the earlier names it mentions
  (`declare let x: typeof import("./cell-c3.ts").x`), so diagnostics are at the cell's own lines (line 0
  is the carried-over line). `--no-script` is mandatory: otherwise a `check` script in a `package.json`
  above the directory runs. Modules have no declarations in the binary (bun-types would add
  megabytes): `node:*` and `bun` imports type-check as `any`. Without a bindings directory the global
  is `any`. `cell/run` with `check` runs nothing that has type errors (`typeError`).
- **Cancellation** in three stages, `recall`, previews and values: kernel-protocol.md.

## Conformance

- **Interpreter providers** (`InterpreterConformance.tests name provider`,
  `tests/Fx.Interpreters.Tests/Conformance`): valid; requires its runtime's component; assets with a
  stable digest; the program absolute on every target; binding files deterministic and independent of
  the bindings' order; declarations naming the global given and every binding; surfaces that leave
  groups out; the prompt holding instructions, declarations and examples; privileged in-process code;
  caps that round-trip. bun-ts and a test provider pass it.
- **Kernels**: the bun-ts kernel passes the kernel conformance suite (tests/Fx.Kernel.Tests) on Linux,
  macOS and Windows (and musl in CI's Alpine containers, in the full tier), launched through `Interpreter.prepare` with
  `fx.runtime.bun`'s runtime from the provider's assets, including `cell/check` and a launcher
  environment full of injection vectors that must not reach the kernel.
- **End to end** (`tests/Fx.Ext.InterpreterBunTs.Tests/LiveTests.fs`, embedded mode): the core bindings
  over the local filesystem and shell, failures and their codes, `finish` accepted and rejected,
  `cell/check` against the generated declarations and earlier cells, `check: true`, and the prompt's
  examples type-checking.

## Native AOT

`interpreters-bridge` (the bun-ts declarations of the core bridge, a script of calls over fakes with
results, typed failures and invalid arguments, `finish` accepted and rejected, the intent line),
`interpreters-provider` (the package's manifest hash and checks, caps, programs on two targets,
issues of a broken provider, the binding files and the prompt) and `interpreters-assets` (extraction
into an in-memory filesystem, a tampered file repaired, a launch prepared with a hostile launcher
environment, a missing runtime).

## Left to later tasks

| What | Task |
|---|---|
| A session's bridge and kernel: the strategy builds the bridge (its `TurnResult` codec and `Ensure`), reads the core settings and whether type errors block (`check`) through variants, numbers cells, passes intents, restarts killed kernels and restores kept values with `recall` (done: [codeact.md](codeact.md)) | T127, T128 |
| Brokered host-side bindings (`ledger`, `grants`, `llm`, `agents`, `runs`, `context`, `history`, …; `recall` is the CodeAct strategy's, T128) | their tasks (T201, T301, T213, …) |
| `repo.*` bindings, anchored edits (`fs.edit`), readable results and smart previews | T202, T204a, T203a, T203b |
| Blobs and handles for large values and output overflow | T201 |
| Activity wrappers around bindings and per-cell diffs | T205 |
| Provenance from kernel-side activity (`CoreSettings.Exclusions` from the session's variants and the provenance of `fs.read` calls are T127's, done) | T205 |
| Killing stale kernels when envd starts (a pid file in the owned directory). Installing `Requires` in the environment is the host's ([host.md](host.md)); launching over envd's capability records is `EnvHands.ofHandle` (T127) | T128 |
| Per-session packages through the egress proxy (`caps.packages`), `process`/`Bun` as globals (a variant), cells on a `Worker` so busy loops can be stopped without a kill | later |
| A data form of `InterpreterProvider` for declarative interpreters | T602 |
