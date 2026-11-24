# AGENTS

Guidance for AI coding agents working on **fx**. fx is built entirely by coding agents, and fx itself takes over that work once it is usable. Read this file before making changes.

## Source of truth

- **[`spec.md`](spec.md)** is the design specification. Every change must be consistent with it.
- If a change requires a design change, update `spec.md` in the same change and explain why.
- The project is in the **exploration stage**. Only the scaffold exists so far. The spec's roadmap (§21) gives the build order.

## Picking work: the task graph

- **[`tasks.md`](tasks.md)** is the implementation task graph. Each task is one lane. Dependencies, not phases, decide what can start.
- Run `bun scripts/tasks.ts ready` to list tasks you can start now. Tasks on the critical path are marked `*`.
- The lane for a task is named after its id, e.g. `lane new t101-codecs`.
- Status updates:
  - set the task to `in-progress` in the lane's first commit;
  - set it to `done` in the lane's last commit, so the task becomes done when its PR merges.
- Splitting a task:
  - split it in the tables, e.g. `T110a` and `T110b`, and give each part explicit dependencies;
  - then run `bun scripts/tasks.ts` to regenerate the waves and the graph. CI can run `bun scripts/tasks.ts check`.

## Development workflow: lane

We use [lane](https://github.com/lukeed/lane) for isolated worktrees and durable code memory.

- Do every task in its own lane: `lane new <task-name>`. Work in the printed worktree path, not in the parent checkout.
- Land with `lane merge`, or `lane push` to open a PR. Run `lane check` before landing, and resolve every stale note (`confirm`, `replace` or `retire`).
- If `lane` is not set up in this repository yet, run `lane init` once. Then run `lane install skill` and `lane install hooks`.

### One PR per task, stacked

- Every task lands as its own pull request made of focused commits. Do not batch tasks into one PR.
- If a task depends on a task whose PR is still open, stack it:
  - `lane new <task> --base <parent-lane-branch>`;
  - open the PR against that branch with `gh pr create --base <parent-lane-branch>`.
- A lane has one base. If a task has several unmerged parents, stack it on one of them, and stack those parents on each other first.
- Before opening a PR, run `lane check` and the CI commands below. Then run `lane push` and open the PR with `gh pr create`.
- Once the parent PR merges:
  - retarget the child PR with `gh pr edit <n> --base main`;
  - update the child with `lane push --base main`;
  - run `lane prune`.

<!-- lane:protocol -->
## Context memory

- Before editing a file, read `.lane/memory/<path>/` if it exists, or run `lane why <path>`.
- Record non-obvious findings with `lane note add <path> -a <anchor> "..."`.
- Do not edit `.lane/` by hand; landing manages it.
- Land with `lane merge`, or `lane push` where trunk is protected, then `lane prune` once it merges.
- Detailed workflow lives in `.agents/skills/lane/SKILL.md`; run `lane install skill` if it is absent.
<!-- /lane:protocol -->

Notes record **constraints that must stay true**, not summaries of changes. With hooks installed, add them as commit trailers: `Why: <path>[#<anchor>] | <text>`.

## Tech stack

| Area | Choice |
|---|---|
| Core language | **F# 11 on .NET 11**, compiled with **Native AOT** into a single `fx` binary. SDK version pinned in `global.json` |
| Second language | **TypeScript**: the `@fx/sdk` package, the CodeAct kernel and Bun-hosted extensions |
| Script runtime | **Bun**, pinned and managed by fx itself (`fx.runtime.bun`). Never rely on a user-installed Bun |
| Targets | Linux (glibc + musl), macOS, **Windows** (x64 and arm64). All three are first-class from v1 |
| Clients | F# TUI first. Later: web (Fable), desktop (Tauri), IDE (ACP) |
| License / telemetry | MIT, no telemetry of any kind |

## Architecture in one screen (details in spec §3)

- **Host** ("brain"): sessions, the agent loop, the run kernel, the ledger, grants and model access. It holds all credentials.
- **envd** ("hands"): runs inside an environment. It handles exec/PTY, the filesystem, repo navigation, snapshots, the component installer and CodeAct kernels. It is the same binary as the host.
- **Clients** attach over JSON-RPC 2.0 on WebSocket.
- **The core is minimal**: event store, machine host, run kernel, protocols, extension loader, component installer, envd bootstrapper, principals and the variant registry. **Everything else is an extension.**
- **Built-in features are bundled extension packages** that use **only the public SDKs** (`Fx.Sdk` and `@fx/sdk`). Never give a built-in access to core internals. Architecture tests enforce this.

## F# coding rules

**Style**
- **Functional core, imperative shell.** State is a fold over events, built from `Machine`s (`Initial` / `Evolve` / `Handle` / `React`). `Evolve` and `Handle` must be pure.
- **Effects** are written as `Eff<'R,'T> = 'R -> Task<'T>`, where the environment `'R` is typed with "has" interfaces (`#IShell`, `#ILlm`, …).
  - **Do no I/O outside `Eff`.** That includes the clock, random numbers, IDs, the filesystem, the network and processes. Go through the corresponding capabilities (`IClock`, `IIds`, …), otherwise replay and determinism break.
- **Errors**:
  - expected failures → `Result` inside `'T`, using the `effResult { }` builder;
  - defects → exceptions;
  - never use exceptions for control flow.
- **Values, not magic**: build things from records, discriminated unions and combinators. No metaclass-style tricks, no runtime reflection.
- **Codecs**: write them by hand with combinators (`Codec.record`, `Codec.union`, …). A single codec yields the schema, encoding, validated decoding and the preview.
- **Configurable behaviour** must be read through `IVariants`. Never read configuration ad hoc, because it has to be recorded and replayable.

**Native AOT (failures are often silent, so follow these strictly)**
- No `%A`, and no printf format specifiers (`sprintf "%d"`, `failwithf "%s"`, …). Use string interpolation without format specifiers.
- Override `ToString` on every union and on any record that contains unions, tuples or maps.
- No runtime reflection: no `FSharp.Reflection`, `Reflection.Emit`, `DispatchProxy` or dynamic assembly loading.
- No reflection-based serializers (FSharp.SystemTextJson, Newtonsoft). Use our codecs.
- F# cannot run C# source generators (System.Text.Json source generation, Request Delegate Generator, LibraryImport, StreamJsonRpc). For Kestrel use `RequestDelegate` endpoints, and for interop use `DllImport` with blittable signatures.
- Avoid libraries that are not AOT-ready. Use the git CLI instead of LibGit2Sharp. Use SQLite only through the AOT-verified path.
- Code that touches the AOT surface is only done once the AOT publish succeeds **and** the JIT-vs-native diff tests pass.

**Cross-platform**
- Build paths with `Path` APIs; never hardcode `/` or `\`. Handle CRLF, case-insensitive filesystems and Windows path-length limits.
- The default shell on Windows is PowerShell 7 (`pwsh`); never assume bash there. PTYs use ConPTY on Windows.
- Windows v1 has **no sandbox**. Never write code that assumes a sandbox is present.

## TypeScript rules

- Kernel and workflow-worker code must stay deterministic. In workflow mode, `Date` and `Math.random` must not be used directly; every effect goes through `fx.*`.
- Types for `@fx/sdk` and `fx.d.ts` are **generated from the F# codecs**. Never edit generated files by hand.

## Naming

`fx` is a working name, and a rename is expected. The product name, the binary name, the `.fx/` and `~/.fx` directories, the kernel global `fx`, the SDK package names, the `FX_*` environment-variable prefix and the protocol namespaces all come from **one build-time constant**. Never hardcode `"fx"` anywhere else.

## Security rules (spec §18)

- Treat model providers as outside the trust boundary. Every payload sent to a model must pass through the model-egress hooks.
- Credentials live only on the host. Config refers to secrets by reference (`secret:<name>`), never inline.
- Nothing project-local may execute before the project is trusted.
- Runtime launch specs must deny injection vectors such as `BUN_OPTIONS`, `NODE_OPTIONS` and `DOTNET_STARTUP_HOOKS`.
- Never add telemetry.

## Testing and definition of done

- Every module or contribution type has a **spec**, and its **conformance suite** defines what "done" means. Built-ins must pass the same suites as third-party extensions.
- **Transport parity**: in-process F# built-ins must also pass their tests over the out-of-process JSON-RPC transport.
- Changes to machines, strategies or workflows need **replay tests**: replaying recorded events or effects must give identical results.
- CI gates (spec §20):
  - analyzers;
  - AOT publish on Linux, macOS and Windows;
  - JIT-vs-native diff;
  - `Eff` determinism lint;
  - replay, architecture, parity and conformance tests;
  - lockfile reproducibility;
  - workflow IR validation;
  - ledger merge and history-rewrite tests.

## Commands

Run from the repository root. The SDK is pinned in `global.json`; all output goes to `artifacts/`.

| What | Command |
|---|---|
| Restore (as CI does) | `dotnet restore Fx.slnx --locked-mode` |
| Build | `dotnet build Fx.slnx` |
| Test | `dotnet test --solution Fx.slnx` |
| Format | `dotnet tool restore` once, then `dotnet fantomas src tests` (CI runs `--check`) |
| AOT publish | `dotnet publish src/Fx.Cli -c Release -r <rid>`, e.g. `linux-x64`; binary in `artifacts/publish/Fx.Cli/release_<rid>/` |
| Task graph | `bun scripts/tasks.ts check` |

- After adding or upgrading a package, run `dotnet restore Fx.slnx` and commit the updated `packages.lock.json` files. Package versions live only in `Directory.Packages.props`.
- `TreatWarningsAsErrors` is on everywhere. Production code is compiled with `--reflectionfree`, so `%A` is a compile error.

## Repository layout

| Path | Contents |
|---|---|
| `Fx.slnx` | The solution |
| `Directory.Build.props` | Shared build settings and **`FxProductName`, the naming constant** |
| `Directory.Packages.props` | Central package versions |
| `src/Fx.Prelude` | Bottom layer with no dependencies (Fable-compatible). `Fx.Product` derives every user-visible name from the constant |
| `src/Fx.Cli` | The single `fx` executable (`AssemblyName` comes from the constant, `PublishAot` is on) |
| `tests/<Project>.Tests` | Expecto tests for `src/<Project>`, run through Microsoft Testing Platform. `tests/Directory.Build.props` makes them JIT-only |
| `scripts/` | Repository tooling (Bun) |

- Add new F# projects under `src/`, and give each one a matching test project under `tests/`.
- Get names such as `.fx/`, `FX_*`, `@fx/sdk` and `fx.env.local` from `Fx.Product`. F# namespaces and project names (`Fx.*`) are code identifiers and are not derived.
