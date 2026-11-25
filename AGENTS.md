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

### One PR per task, in a native GitHub stack

Every task lands as its own pull request made of focused commits. Do not batch tasks into one PR. Unmerged task PRs form one **native GitHub stack**, managed with [`gh stack`](https://gh.io/stacks).

- **Roles**:
  - lane owns the worktrees, branches and notes;
  - `gh stack link` owns the PRs and the stack on GitHub. It is gh-stack's mode for externally managed branches, so do not also use `gh stack init`/`add`, which track branches locally.
- **Order**:
  - GitHub stacks are linear, so the stack is the open tasks in dependency order;
  - start each new lane on top of the current top of the stack: `lane new <task> --base <top-branch>`, or with no `--base` when nothing is open;
  - a task with several parents is fine as long as they are all below it in the stack.
- **Publishing a task**:
  1. Run `lane check` and the CI commands below.
  2. Run `lane push`.
  3. Link the stack, listing branches from bottom to top: `gh stack link --open <bottom-branch> … <task-branch>`, or `gh stack link --open <stack-number> <task-branch>` to append. This creates the PR with the right base and adds it to the stack.
  4. Edit the generated PR title and body with `gh pr edit`.
- **Changing a lower task**:
  - commit in its lane and `lane push`;
  - then, bottom-up, `lane push` each lane above it; each one rebases onto its recorded base.
- **Landing**:
  - merge with `gh stack merge <pr-number>`. It merges everything up to and including that PR atomically;
  - then `git pull` on `main`, `lane push --base main` the lowest remaining lane, and `lane prune`.

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
- **Codecs**: write them by hand with combinators (`Codec.record`, `Codec.union`, …). A single codec yields the schema, encoding, validated decoding and the preview. Use our JSON (`Fx.Contracts`), never System.Text.Json; hash only `Json.serializeCanonical` output. See [docs/modules/contracts.md](docs/modules/contracts.md).
- **Configurable behaviour** must be read through `IVariants`. Never read configuration ad hoc, because it has to be recorded and replayable. Module spec: [`docs/modules/variants.md`](docs/modules/variants.md).
  - Make each choice a slot next to the code that reads it (`Slot.enum` or `Slot.create`: a dotted id that never changes, a description, a codec, a default, choices if enumerated, `Slot.privileged` if a project must not set it), register it (core slots in `Slots.all`, an extension's as contributions), and read it with `Variants.get slot`.
  - Test with `FakeVariants`. Adding a core slot changes the golden files in `tests/Fx.Variants.Tests/golden`, `variants.d.ts` and `fixtures.ts` in `tests/Fx.Contracts.TypeScript.Tests/golden`, and every profile id.

**Effects and capabilities** (module spec: [`docs/modules/eff.md`](docs/modules/eff.md))
- Write effects with `eff { }`, or `effResult { }` when they have expected failures. Only `Eff` values can be bound. Lift a task with `Eff.ofTask` only inside capability modules and interpreters.
- Loop with `while` or `for`, never with a recursive `return!`: that builds an ever longer chain of tasks.
- Never suppress FS3511. Restructure the block (e.g. move a `let rec` out of it) so that it compiles to a state machine.
- Every environment carries a `Scope`: a cancellation token and the current trace span.
  - Capability calls pass `env.Scope.Cancellation`. Honour it: `Eff.all`, `race` and `timeout` wait for every child.
  - Run effects concurrently with `Eff.all`, `both`, `race`, `timeout` and `bracket`, never with `Task.Run` or `Task.WhenAll`.
- **Adding a capability** (one file, in `src/Fx.Effects` or in the project that owns the capability):
  1. A record of functions with data in and out, and the `CancellationToken` last. No streams or callbacks, so calls can be recorded, replayed and sent over JSON-RPC. Expected failures are a `Result` with the capability's own error union.
  2. A "has" interface with one member named after the record: `type IClock = abstract Clock: Clock`.
  3. A module of effects, e.g. `let sleep<'R when 'R :> IClock and 'R :> IScope> d : Eff<'R, unit> = fun env -> env.Clock.Delay d env.Scope.Cancellation`.
  4. A deterministic fake, and a conformance suite (a function from an interpreter to a `Test`) that the fake and the real interpreter both pass.
  5. The real interpreter in its own small file (`src/Fx.Effects.Live` for the core capabilities), with an `analyzers.json` entry for exactly its EFF code, and a JIT-vs-native scenario.
- **Building an environment**: a record of capability values that implements the "has" interfaces and `IScopeWith` of itself. `WithScope` returns a copy with the new scope. See `tests/Fx.Effects.Tests/TestEnv.fs`. Only composition roots (the host, envd, tests) reference `Fx.Effects.Live`.
- **Attenuating**: hand an effect a narrower environment with `Eff.local` (e.g. a view that implements only `IFsRead`), or wrap a capability (`Fs.confine root`). These are guardrails; the sandbox is the boundary.
- **Test doubles**: the fakes in `Fx.Effects.Fakes` (`FakeClock`, `FakeIds`, `InMemoryFs`, `ScriptedShell`, `InMemorySecrets`, `TraceCollector`).
  - Drive time with `FakeClock.Advance`.
  - Keep effects under a fake clock on the advancing thread (no `Task.Yield`, no real I/O); otherwise the order of events depends on thread-pool timing.

**Machines and actors** (module spec: [`docs/modules/machines.md`](docs/modules/machines.md))
- A machine is a `Machine` value (`Initial` / `Evolve` / `Handle` / `React`) whose events and state use only FSharp.Core, so clients can share `Evolve` (Fable).
  - `Handle` gets everything from the world (time, ids, results) in the command.
  - Events are facts kept forever: add event cases, never change what an old one means.
- Run machines with `Actor.start` or `Actor.run`, or with an `ActorRegistry` for one actor per stream. Send with `Actor.send`; the control path is `Actor.interrupt`.
- Every command has an idempotency key. Derive the keys of commands the host makes from the event that caused them, never from the clock or random ids, so retries deduplicate.
- Interpreters turn expected failures into result commands. A throw stops the actor.
- Intents are not durable: record work in flight in the state and pick it up in `ActorSpec.Recover`.
- Actors continue on the sender's thread. Never block a thread on an actor's reply inside a continuation.
- Test with `InMemoryEventLog` and `FakeClock`: the same inputs must give the same log. A new event store passes `EventLogConformance`.

**Event and blob stores** (module spec: [`docs/modules/store.md`](docs/modules/store.md))
- Stores keep `Payload`s (canonical JSON plus a schema version); type a log with `EventLog.typed schema`. An `EventSchema` is a codec plus upcasters from every older version.
  - Committed events are never rewritten: change an event's JSON only through a new schema version and a pure upcaster, and keep a test that reads what the old version wrote.
  - A stream never holds two commits with one idempotency key: a retried append gets `Duplicate` with the first commit.
- Snapshots are caches of a fold, versioned by their `SnapshotSchema`. Bump the version when the state's shape or `Evolve` changes; old snapshots are then ignored and rebuilt from the log.
- Events stay small (at most 1 MiB of JSON): put large content in the blob store and keep the `BlobRef` in the event.
- Composition roots open `SqliteEventStore` and `FileBlobStore` (Fx.Store.Live) with absolute paths and a `Durability`; everything else gets `EventLog`, `SnapshotStore` or `IBlobs` values. Tests use `InMemoryEventLog`, `InMemorySnapshots` and `InMemoryBlobs`; a new store passes the conformance suites in `tests/Fx.Store.Tests/Conformance`.

**JSON-RPC** (module spec: [`docs/modules/rpc.md`](docs/modules/rpc.md))
- Define each method once, as a value with codecs (`Method.define`, `Method.withErrors`, `Notification.define`), and build both the handler (`Handler.request`) and the call (`Rpc.call`) from it.
  - A method's typed errors are its expected failures (`Error` in the handler, `CallError.Failed` at the caller). An exception is a defect: the peer gets `InternalError` with the exception's type, never its message.
  - Method names follow `Protocol.checkMethodName`: `rpc.` and `$/` are reserved. Extension methods are `Protocol.ownExtensionMethod "group/name"` and `_meta` keys are `Protocol.metaKey "name"`; never write the namespace by hand.
- Transports wrap streams and sockets that a composition root opened (`StreamTransport`, `WebSocketTransport`, `LocalClient`); `src/Fx.Rpc` does no I/O of its own. Nothing else may write to a stream that carries the protocol: no logging on stdout.
- A notification handler blocks the ones after it: never wait in it for a call to the same peer.
- Bulk data goes over binary channels, not in JSON messages.
- A new transport passes the conformance suite (`RpcConformance.tests` in `tests/Fx.Rpc.Tests`).

**Extensions** (module spec: [`docs/modules/extensions.md`](docs/modules/extensions.md))
- A built-in is a package under `extensions/<Name>/` (project `Fx.Ext.<Name>`, id `Product.extensionId "<local id>"`). It references `src/Fx.Sdk` and nothing else; `ArchitectureTests` (tests/Fx.Extensions.Tests) checks the `.fsproj` and the compiled assembly. Reference every new package from `tests/Fx.Extensions.Tests` so the check sees it. `extensions/Fx.Ext.Sample` is the reference.
- Write it as a value: a `Manifest` (built with `Manifest.create` and the `Declare` helpers) and an `Activate` effect that returns an `Activation`: the contributions and methods the manifest declares, exactly. Native extensions are registered from a static list in the composition root, never loaded from assemblies.
- Contribute under typed keys (`ContributionType<'T>`): `ContributionType.data` (concatenated), `Hook.gate`, `Hook.transform`, `Hook.observe` (all serve over the extension protocol) or `Hook.interpret` (in process only). Define a hook point next to the code that runs it, and run it with `Hooks.decide`, `transform`, `collect`, `observe`, `interpret` over `ExtensionHost.contributions`. Mark the types spec §16.5 lists `ContributionType.privileged`.
- Extension code runs in `IExtensionEnv`, its extension's environment (attenuated to the manifest's `capabilities`) with the caller's scope. Keep per-activation state inside `Activate`.
- Failures carry no exception messages: a failing gate denies, a failing transform fails the pipeline, a failing observer is reported, a throwing method is `MethodFailure.Crashed`.
- Behaviour must be identical in process and out: the parity suite (`ParitySuite.tests`) runs the sample natively and as a child process (`Fx.Aot.Scenarios ext-peer`). Add cases there when you add a hook kind or a protocol method.

**Native AOT (failures are often silent, so follow these strictly)**
- No `%A`, and no printf format specifiers (`sprintf "%d"`, `failwithf "%s"`, …). Use string interpolation without format specifiers.
- Override `ToString` on every union and on any record that contains unions, tuples or maps.
- No runtime reflection: no `FSharp.Reflection`, `Reflection.Emit`, `DispatchProxy` or dynamic assembly loading.
- No reflection-based serializers (FSharp.SystemTextJson, Newtonsoft). Use our codecs.
- F# cannot run C# source generators (System.Text.Json source generation, Request Delegate Generator, LibraryImport, StreamJsonRpc). For Kestrel use `RequestDelegate` endpoints, and for interop use `DllImport` with blittable signatures.
- Kestrel (details in [`docs/spikes/kestrel.md`](docs/spikes/kestrel.md)); `src/Fx.Transport.Http` follows these rules:
  - build the host with `WebApplication.CreateEmptyBuilder` + `UseKestrelCore`. `CreateSlimBuilder` and `CreateBuilder` read `appsettings.json` from the working directory, whose `Kestrel:Endpoints` add listeners;
  - pass `RequestDelegate` values only (wrap lambdas: `RequestDelegate(fun ctx -> ...)`). Overloads taking a `Delegate` (`MapGet("/", fun () -> ...)`) go through `RequestDelegateFactory`;
  - never use the JSON helpers (`Results.Json`, `TypedResults.Ok<T>`, `WriteAsJsonAsync`, `ReadFromJsonAsync`) or configuration binding; use our codecs;
  - replace the default `IHostLifetime` (`ConsoleLifetime`, registered even by the empty builder) with a no-op one: it takes over Ctrl+C and SIGTERM, which belong to the entry point.
- Avoid libraries that are not AOT-ready. Use the git CLI instead of LibGit2Sharp. Use SQLite only through `src/Fx.Sqlite` (details in [`docs/spikes/sqlite.md`](docs/spikes/sqlite.md)), and only in storage interpreters (`EFF009`).
- Native libraries (SQLite today) are built from pinned, digest-checked sources under `native/<library>/` by MSBuild, never taken from the system. `native/sqlite/Sqlite.targets` is the pattern:
  - one source, two outputs: a static library that a Native AOT publish links in (`NativeLibrary` + `DirectPInvoke`, so the binary stays a single file) and a shared library copied next to every project that references the binding, for the JIT (tests and the JIT side of the diff);
  - give the library a name no system library has (`Fx.Sqlite.Native`): `DllImport` falls back to system paths;
  - builds are cached in `artifacts/native/` under a key that hashes their inputs, never compared by timestamp.
- P/Invoke: blittable `DllImport` signatures only (pointers, `nativeint`, numbers; text as UTF-8 bytes you encode), with `[<assembly: DisableRuntimeMarshalling>]` so a signature that needs marshalling fails on the JIT too.
  - Never call a variadic C function (e.g. `sqlite3_db_config`) through `DllImport`: variadic arguments go on the stack on Apple arm64. Add a fixed-arity C shim next to the library's sources, or declare the variadic arguments exactly as the platform passes them (see `Posix.ioctl` in `src/Fx.Pty`).
  - Pin arrays with a `GCHandle` (`Native.pinned` in `src/Fx.Sqlite`), not `use p = fixed array`: the analyzers' typed-tree walk throws on `fixed`.
- Native interop and processes (details in [`docs/spikes/pty.md`](docs/spikes/pty.md)); `src/Fx.Pty` follows these rules:
  - never run managed code between `fork` and `exec`. Create processes that `Process.Start` cannot express with `posix_spawn` from .NET 11's `UnixProcessStartArguments.Start` callback (`WindowsProcessStartArguments.Start` and `CreateProcessW` on Windows): .NET still resolves the program, builds `argv`/`envp` and reaps the child;
  - P/Invoke signatures use only `int`, `int16`, `nativeint`, `unativeint` and pointers as `nativeint` (`BOOL` as `int`); write structures into zeroed native memory at fixed offsets. Keep the constants that differ per OS in one table per OS;
  - open descriptors and handles non-inheritable at creation (`O_CLOEXEC`), never "then mark them": another thread may start a process in between.
- Code that touches the AOT surface is only done once the AOT publish succeeds **and** the JIT-vs-native diff passes. Cover it with a scenario (see "JIT-vs-native diff" below).
- The AOT publish fails on any trim or AOT warning (IL2xxx, IL3xxx), including the single IL2104 that summarizes a library's warnings. Known cases:
  - `Seq.init` and `Seq.initInfinite` trigger IL2091 inside FSharp.Core. Use `Seq.unfold` or a `seq { }` expression.
  - ILC aborts generic instantiations nested deeper than 4 levels inside FSharp.Core's closure machinery (IL3054, "generic recursion"), e.g. a curried call or a generic closure over `'A * ('B * ('C * …))`. Applicative `and!` builders nest one tuple per binding: give them `MergeSources3`, `MergeSources4`, … as `Codec.record` does.
  - The same limit holds for `Codec.union`: its match function takes one encoder per case, and with more than four cases the curried closure nests too deep (IL3054, found with a nine-case union). Keep a union codec to four cases or fewer; group cases (e.g. one `unknown` case with a description) until `Codec.union` is restructured.
- Behaviour that differs under Native AOT, found by the diff:
  - `InvalidCastException` messages lose the type names ("Specified cast is not valid."). Never show or match on runtime exception messages; use the exception type.
  - On linux-musl, the main thread of a Native AOT binary does not know its stack size: `RuntimeHelpers.TryEnsureSufficientExecutionStack()` is false there, so task continuations never run inline on it. Code that relies on inline continuations, such as driving a `FakeClock`, must run on a thread-pool thread (`FakeClock.Advance` throws otherwise).
- A Native AOT executable sets `FxNativeAot` and sets `PublishAot` only when a runtime identifier is given. Copy the two properties from `src/Fx.Cli/Fx.Cli.fsproj`; the restore section of Commands explains why.

The bans on `%A`, printf format specifiers, runtime reflection and I/O outside `Eff` are enforced by our analyzers; see [Analyzers](#analyzers).

**Cross-platform**
- Build paths with `Path` APIs; never hardcode `/` or `\`. Handle CRLF, case-insensitive filesystems and Windows path-length limits.
- The default shell on Windows is PowerShell 7 (`pwsh`); never assume bash there. PTYs use ConPTY on Windows.
- Windows v1 has **no sandbox**. Never write code that assumes a sandbox is present.

## TypeScript rules

- Kernel and workflow-worker code must stay deterministic. In workflow mode, `Date` and `Math.random` must not be used directly; every effect goes through `fx.*`.
- Types for `@fx/sdk` and `fx.d.ts` are **generated from the F# codecs**. Never edit generated files by hand.
  - Generate them with `DeclarationFile.render` (`src/Fx.Contracts.TypeScript`, module spec [`docs/modules/typescript.md`](docs/modules/typescript.md)): `Layout.Module` for the SDK and clients, `Layout.Global(Product.kernelGlobal, …)` for a kernel's host bridge.
  - Name a codec with `Codec.named` to make it a declared type, and describe it before naming it, so that its declaration carries the description.
  - Type-check TypeScript with Bun's `bun check`, always with `--no-script` (otherwise it runs a `check` script from a `package.json` above the project).

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
- CI publishes `src/Fx.Cli` with Native AOT for `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64` (in an Alpine container), `osx-arm64`, `osx-x64`, `win-x64` and `win-arm64`, each on a native runner. Each job smoke-tests the binary, reports the binary sizes in the job summary, runs the JIT-vs-native diff and its self-test, and uploads the binary as a workflow artifact.

**JIT-vs-native diff** (`tests/aot`)
- `Fx.Aot.Scenarios` is a Native AOT executable. Its scenarios are values: a name and a function that produces deterministic output lines.
- `Fx.Aot.Diff` runs every case two ways and fails on any difference in stdout, stderr or exit code:
  - on the JIT, as `dotnet <assembly>.dll`. This is the same IL from the same RID-specific build, with the same AOT feature switches;
  - as the native binary.
- The cases are every scenario that runs on the current platform, plus a few `fx` command lines.
- Add a scenario:
  1. Write it with `Scenario.lines` or `Scenario.task` in a module under `Scenarios/`, and add it to that module's `scenarios` list.
  2. If the module is new, add it to `Registry.all` and to the `.fsproj`.
- Gate a scenario per platform with `Scenario.onlyOn [ Os.Windows ]`, `Scenario.except [ Os.Windows ]` or `Scenario.where (fun p -> not p.Musl)`. Ungated scenarios run everywhere.
- Scenario output must be deterministic. Do not print times, process ids, temporary paths, ports or string hash codes (they are randomized per process). `Fx.Aot.Tests` runs every scenario twice on the JIT, in parallel with the others.
- Bound every wait in a scenario (e.g. `Task.WaitAsync`), so that a hang fails the case instead of the job timeout. The Kestrel scenarios (`Scenarios/Kestrel/`) show how to test servers: ephemeral ports, in-process acknowledgements instead of sleeps, and logs printed in a fixed order.
- To diff another Native AOT executable, add a `Subject` to `Subjects.all` in `Fx.Aot.Diff`.
- `--self-test` runs the canaries in `Registry.canaries`. They must differ, which proves that the diff detects a divergence. Never put a diverging scenario in `Registry.all`: work around the divergence and document it under "Native AOT" above.

## Commands

Run from the repository root. The SDK is pinned in `global.json`; all output goes to `artifacts/`.

| What | Command |
|---|---|
| Restore (as CI does) | `dotnet restore Fx.slnx --locked-mode` |
| Build | `dotnet build Fx.slnx` |
| Test | `dotnet test --solution Fx.slnx` |
| Format | `dotnet tool restore` once, then `dotnet fantomas src tests tools extensions` (CI runs `--check`) |
| Analyzers | `dotnet tool restore` once, then `bun scripts/analyzers.ts`; `bun scripts/analyzers.ts samples` proves they fail on the samples |
| AOT publish | `dotnet publish src/Fx.Cli -c Release -r <rid>`, e.g. `linux-x64`; binary in `artifacts/publish/Fx.Cli/release_<rid>/` |
| JIT-vs-native diff | `dotnet run --project tests/aot/Fx.Aot.Diff -c Release`: publishes for this machine's RID, then diffs. `-- --no-publish` reuses `artifacts/`, `-- --self-test` runs the canaries, `-- --rid <rid>` picks the RID |
| Eff benchmark | `dotnet run -c Release --project tools/Fx.Effects.Bench` (results in `docs/modules/eff.md`) |
| Store benchmark | `dotnet run -c Release --project tools/Fx.Store.Bench` (results in `docs/modules/store.md`) |
| Task graph | `bun scripts/tasks.ts check` |
| Bun kernel spike | In `spikes/bun-kernel`: `bun install --frozen-lockfile`, `bun check --no-script`, `bun test`, `bun bench/bench.ts` |
| Kestrel size and startup probe | `docs/spikes/kestrel-probe/measure.sh <rid>`; CI runs it on every target when the probe changes (`kestrel-probe.yml`) |
| Update golden files | `UPDATE_GOLDEN=1 dotnet test --solution Fx.slnx`, then review the diff |
| Type-check the generated TypeScript | `bun scripts/declarations.ts`: the golden declarations in `tests/Fx.Contracts.TypeScript.Tests/golden` with the shared fixtures, and the misuses that must fail (needs Bun 1.4.5, as CI) |
| Native libraries | Built by `dotnet build` and `dotnet publish` (no separate step) into `artifacts/native/`. Needs a C compiler, as Native AOT does: clang or gcc on Linux and macOS (`FxSqliteCC` overrides), Visual Studio or the Build Tools with the C++ workload on Windows. The first build downloads the pinned sources; offline, pass `-p:FxSqliteArchive=<zip>` |
| PTY probe | `dotnet build src/Fx.Pty -c Release`, then `dotnet fsi docs/spikes/pty-probe/probe.fsx`; CI runs it on every target when `src/Fx.Pty` or the probe changes (`pty-probe.yml`) |

- After adding or upgrading a package, run `dotnet restore Fx.slnx` and commit the updated `packages.lock.json` files.
  - Package versions live only in `Directory.Packages.props`.
  - Packages come only from nuget.org (`nuget.config`).
  - Native AOT executables set `PublishAot` only for RID-specific builds. Otherwise even their RID-less restore adds the host's RID, and the lockfile would differ per OS.
  - RID-specific restores (`dotnet publish -r`) are not locked. They write git-ignored `packages.<rid>.lock.json` files, because their content depends on the host (the ILCompiler host package). They add only SDK-implicit packages, which `global.json` pins. CI runs the locked RID-less restore before publishing.
- `TreatWarningsAsErrors` is on everywhere. Production code is compiled with `--reflectionfree`, so `%A` is a compile error.
- Bumping SQLite: in `native/sqlite/Sqlite.props`, set the version, the zip's name and URL (with the release year) and its digests. Take the SHA3-256 from the CSV comment in the source of sqlite.org/download.html and check the zip against it; record the zip's SHA-256 (what MSBuild verifies), and `SQLITE_VERSION_NUMBER` and `SQLITE_SOURCE_ID` from its `sqlite3.h`. The tests fail if the library differs from the pin. Read the release notes for changed defaults and compile options.

## Repository layout

| Path | Contents |
|---|---|
| `Fx.slnx` | The solution |
| `Directory.Build.props` | Shared build settings and **`FxProductName`, the naming constant** |
| `Directory.Build.targets` | Rejects a Native AOT publish without a runtime identifier; imports the native library builds |
| `Directory.Packages.props` | Central package versions |
| `src/Fx.Prelude` | Bottom layer with no dependencies (Fable-compatible). `Fx.Product` derives every user-visible name from the constant |
| `src/Fx.Contracts` | JSON, `Schema`, `Validation`, `Preview` and the `Codec` combinators (spec §4.1; Fable-compatible) |
| `src/Fx.Contracts.TypeScript` | TypeScript declarations (`.d.ts`) generated from schemas: types for `@fx/sdk` and clients, and a kernel's host bridge (pure, Native AOT-safe; spec in [`docs/modules/typescript.md`](docs/modules/typescript.md)) |
| `src/Fx.Cli` | The single `fx` executable (`AssemblyName` comes from the constant; Native AOT when published with `-r <rid>`) |
| `src/Fx.Effects` | `Eff`, the `eff`/`effResult` builders, the core capabilities and their deterministic fakes (pure; spec in [`docs/modules/eff.md`](docs/modules/eff.md)) |
| `src/Fx.Effects.Live` | The real interpreters of the core capabilities (the imperative shell); referenced only by composition roots |
| `src/Fx.Pty` | Processes on pseudo-terminals (PTY on Linux and macOS, ConPTY on Windows) behind the `Terminal` capability shape; findings in [`docs/spikes/pty.md`](docs/spikes/pty.md) |
| `src/Fx.Transport.Http` | Local Kestrel listener (loopback, Unix socket, named pipe) with HTTP, WebSocket and SSE behind the token, `Origin` and `Host` checks (spec §18), and `LocalClient`, the WebSocket client that dials it |
| `src/Fx.Machines` | The `Machine` type, its pure helpers, and what is sent to and from actors (Fable-compatible: FSharp.Core only, checked by a test; spec in [`docs/modules/machines.md`](docs/modules/machines.md)) |
| `src/Fx.Machines.Host` | The machine host: the event-log abstraction and the in-memory log, actors, the actor registry and process managers |
| `src/Fx.Rpc` | Our JSON-RPC 2.0 (spec §17): typed methods, the connection engine, binary channels with credit flow control, the `initialize` handshake, and the in-process, stream (stdio) and WebSocket transports (spec in [`docs/modules/rpc.md`](docs/modules/rpc.md)) |
| `src/Fx.Sqlite` | The SQLite binding: blittable DllImports, connections, cached prepared statements, transactions, WAL, errors as results. An imperative shell for storage interpreters and composition roots |
| `src/Fx.Store` | Typed events (`Payload`, `EventSchema`, upcasters, `EventLog.typed`), snapshots and projections, the `IBlobs` capability, envelope codecs, and the in-memory fakes (pure; spec in [`docs/modules/store.md`](docs/modules/store.md)) |
| `src/Fx.Store.Live` | The real stores: `SqliteEventStore` (event log and snapshots in SQLite) and `FileBlobStore` (content-addressed files with a SQLite index); referenced only by composition roots |
| `src/Fx.Variants` | Variant slots, the slot registry, resolution of config layers under policies, profiles and their ids, the `IVariants` capability with recording and replay, eval matrices, the core's slots and `FakeVariants` (pure; references only Contracts and Effects, so the SDK can expose it; spec in [`docs/modules/variants.md`](docs/modules/variants.md)) |
| `src/Fx.Sdk` | The public F# SDK for extensions (spec §16): manifests, contribution types, the hook algebra, extension methods, `Extension`, `ExtensionRuntime`, `IExtensionEnv`. Extensions see this and nothing else of the host (spec in [`docs/modules/extensions.md`](docs/modules/extensions.md)) |
| `src/Fx.Extensions` | The extension system's core: the registry machine, trust checks, the catalog, the declarative, native and process runtimes, the extension protocol and its F# server (`ExtensionServer`), and the bootstrap (`ExtensionHost`) |
| `extensions/` | Extension packages, one per directory, using only `src/Fx.Sdk`: `Fx.Ext.Sample` is the reference and the subject of the transport-parity tests (not bundled) |
| `native/` | Native libraries built from pinned sources: `native/sqlite` (the pin in `Sqlite.props`, compile options in `library.c`, the build in `Sqlite.targets`) |
| `tests/<Project>.Tests` | Expecto tests for `src/<Project>`, run through Microsoft Testing Platform. `tests/Directory.Build.props` makes them JIT-only |
| `tests/aot/Fx.Aot.Scenarios` | Native AOT scenarios for the JIT-vs-native diff; also the JSON-RPC test service and its child-process peer (`Fx.Aot.Scenarios rpc-peer`), which `tests/Fx.Rpc.Tests` uses, and the sample extension's host and child process (`Fx.Aot.Scenarios ext-peer`), which `tests/Fx.Extensions.Tests` uses |
| `tests/aot/Fx.Aot.Diff` | The JIT-vs-native diff driver, which runs on the JIT |
| `tests/support` | Programs that tests start as child processes (not test projects), e.g. `Fx.Store.Writer`, which the crash tests kill mid-append. A test project references one to get its apphost next to its own output |
| `tests/Fx.Aot.Tests` | Tests for the diff harness in `tests/aot` |
| `scripts/` | Repository tooling (Bun) |
| `tools/` | Repository tooling in F#: JIT-only, never shipped. Tested by `tests/<Tool>.Tests` |
| `tools/Fx.Analyzers` | Our F# analyzers ([Analyzers](#analyzers)) |
| `tools/Fx.Effects.Bench` | Micro-benchmark of the `eff` builder (a measurement, so no test project) |
| `tools/Fx.Store.Bench` | Throughput and latency of the event and blob stores (a measurement, so no test project) |
| `analyzers.json` | Analyzer policy: where each rule applies, and the reviewed allowlist |
| `spikes/` | Throwaway prototypes from the spike tasks; findings live in `docs/spikes/` |
| `docs/spikes/` | Spike findings and recommendations for the tasks that build on them, with any measurement tools they need |
| `docs/modules/` | Module specs, one per module, e.g. [`contracts.md`](docs/modules/contracts.md), [`eff.md`](docs/modules/eff.md), [`machines.md`](docs/modules/machines.md), [`rpc.md`](docs/modules/rpc.md), [`extensions.md`](docs/modules/extensions.md) |

- Add new F# projects under `src/`, and give each one a matching test project under `tests/`. F# repository tooling goes under `tools/`. Extension packages go under `extensions/`; their tests are the conformance and parity suites in `tests/Fx.Extensions.Tests`.
- Get names such as `.fx/`, `FX_*`, `@fx/sdk` and `fx.env.local` from `Fx.Product`. F# namespaces and project names (`Fx.*`) are code identifiers and are not derived.

## Analyzers

`bun scripts/analyzers.ts` runs the analyzers in `tools/Fx.Analyzers` over every project in `Fx.slnx`, and CI fails on any finding. They are built on [FSharp.Analyzers.SDK](https://github.com/ionide/FSharp.Analyzers.SDK) and work on the typed tree, so they see through `open`s, aliases and operators. Each message says what to do instead.

| Code | Bans | Applies to |
|---|---|---|
| `AOT001` | `%A` in any format string, including `$"%A{x}"` | `src/`, `extensions/`, `tests/`, `tools/` |
| `AOT002` | printf format specifiers (`sprintf "%d"`, `failwithf "%s"`, `$"%d{x}"`), interpolated strings passed to printf functions (`failwithf $"..."`), composite formats (`String.Format("{0}", x)`) | same |
| `AOT003` | runtime reflection: `System.Reflection` (`Type.GetMethods`, `Assembly.Load`, `MethodInfo.Invoke`, …), `FSharp.Reflection`, `Activator`, `Reflection.Emit`, `DispatchProxy`, `AssemblyLoadContext`, quotations and `query { }`, expression trees, reflection-based serializers | same |
| `EFF001` | clock and timers: `DateTime.Now`, `Stopwatch`, `Environment.TickCount`, `Thread.Sleep`, `Task.Delay`, `Async.Sleep`, timers | `src/`, `extensions/` |
| `EFF002` | randomness and ids: `Random`, `Guid.NewGuid`, `RandomNumberGenerator` | `src/`, `extensions/` |
| `EFF003` | the process environment: `Environment.*` (except `NewLine`), `AppContext`, the registry, `TimeZoneInfo.Local` | `src/`, `extensions/` |
| `EFF004` | the filesystem: `File`, `Directory`, `FileInfo`, `FileStream`, `new StreamReader(path)`, `Path.GetTempPath`, `Path.GetFullPath(path)`, … | `src/`, `extensions/` |
| `EFF005` | the console: `Console`, `stdin`/`stdout`/`stderr`, `printf`/`printfn`/`eprintfn` | `src/`, `extensions/` |
| `EFF006` | processes: `Process`, `SafeProcessHandle`, custom process creation (`UnixProcessStartArguments`, `WindowsProcessStartArguments`) | `src/`, `extensions/` |
| `EFF007` | network and IPC: `HttpClient`, sockets, `Dns`, `ClientWebSocket`, pipes, … | `src/`, `extensions/` |
| `EFF008` | native code: calls to P/Invoke functions (`extern` with `[<DllImport>]`), which can do any I/O out of sight of the other rules | `src/`, `extensions/` |
| `EFF009` | SQLite: any use of `Fx.Sqlite` (its file I/O happens in native code, which `EFF004` cannot see) | `src/`, `extensions/` |

- **Allowed**:
  - interpolation without printf specifiers, including .NET formats and alignment (`{x:N2}`, `{x,5}`);
  - printf calls without specifiers (`sprintf "text"`) and `%%`;
  - `nameof`, `typeof<T>`, `typedefof<T>`, `GetType()`, and `Name`/`FullName` on a `Type`;
  - `System.Reflection.Metadata`, which reads metadata from files;
  - pure `Path` functions, `Environment.NewLine`, `OperatingSystem.Is*()`;
  - building request values such as `HttpRequestMessage` and `ProcessStartInfo`.
- **Scope**: the AOT rules apply to all our F# code, so code can move freely between tests, tools and the binary. The EFF rules are the initial "no I/O outside `Eff`" lint and apply only to production code under `src/` and `extensions/`. Tests and tools are imperative shells.
- **Opting in (the imperative shell)**: `analyzers.json` is the only escape hatch, and every entry is reviewed like code. An `allow` entry lists `codes`, `files` (globs relative to the repository root) and a mandatory `reason`. Inline `fsharpanalyzer: ignore` comments are rejected.
  - Capability interpreters live in their own files and get an entry for exactly the codes they need, e.g. `{ "codes": ["EFF001"], "files": ["src/Fx.Effects.Live/SystemClock.fs"], "reason": "IClock interpreter: …" }`.
  - Keep shell files small, so that the pure core stays analyzed.
  - SQLite's file I/O happens in native code that `EFF004` cannot see, so `EFF009` bans the SQLite binding as a whole. Its allow entries are the binding itself (with `EFF008` for its P/Invokes), the storage interpreters (`src/Fx.Store.Live`, one entry per store) and `fx --build-info`. A new store gets its own entry; the pure core never references `Fx.Sqlite`.
  - Known gap: `EFF007` does not see server listeners (`WebApplication.Create*`, `KestrelServerOptions.Listen*`), so `src/Fx.Transport.Http/LocalServer.fs` needs no entry. It is a shell all the same. Banning them needs a sample that references ASP.NET Core, which the script samples cannot do. Outgoing connections are covered: `src/Fx.Transport.Http/LocalClient.fs` is the one allowed dialer.
  - Tests check that every glob in `analyzers.json` still matches a file.
  - The analyzers cannot read code that uses F# `fixed` (the compiler service fails on it, and the run fails). Pin with a pinned-object-heap array (`GC.AllocateArray(n, pinned = true)`) or a `GCHandle` instead.
- **Changing the rules**: bans are tables in `tools/Fx.Analyzers/Apis.fs` and `Formats.fs`. Every rule needs a failing line in `tests/Fx.Analyzers.Tests/Samples/`, marked `// expect: <code>`. A sample that needs types from our own projects `#load`s a stand-in from `Samples/Support/` that calls nothing (scripts cannot declare namespaces, and the projects may not be built), as `Eff009Storage.fsx` does. `Clean.fsx` lists what must stay allowed. The tests and the CI samples step check that the findings match the markers exactly.
- **Upgrading**: the analyzers run inside the `fsharp-analyzers` tool, which loads only analyzers built against its own SDK version and shares its FSharp.Core. Upgrade `FSharp.Analyzers.SDK` and `AnalyzersFSharpCoreVersion` (both in `Directory.Packages.props`) together with the tool in `dotnet-tools.json`.
