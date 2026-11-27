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
- Inside the builders (`src/Fx.Effects/Builders.fs`), pass the state machine (`&sm`) only to a resumable code's `Invoke` and to the method builder's generic methods. Give any other helper the data it needs (`EffMachine.IsError sm.Data.Result`).
  - A static machine is a struct the compiler generates. A method that gets it as a `ResumableStateMachine` reads its fields at the wrong offsets.
  - Release builds inline such helpers, which hides the bug; Debug builds call them. An `effResult` loop once stopped after its first asynchronous iteration in Debug only.
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
- **Test doubles**: the fakes in `Fx.Effects.Fakes` (`FakeClock`, `FakeIds`, `InMemoryFs`, `ScriptedShell`, `InMemorySecrets`, `InMemoryHttp`, `InMemoryKeyring`, `InMemoryPrivateFiles`, `InMemoryVariables`, `TraceCollector`).
  - Drive time with `FakeClock.Advance`.
  - Keep effects under a fake clock on the advancing thread (no `Task.Yield`, no real I/O); otherwise the order of events depends on thread-pool timing.

**Machines and actors** (module spec: [`docs/modules/machines.md`](docs/modules/machines.md))
- A machine is a `Machine` value (`Initial` / `Evolve` / `Handle` / `React`) whose events and state use only FSharp.Core, so clients can share `Evolve` (Fable).
  - `Handle` gets everything from the world (time, ids, results) in the command.
  - Events are facts kept forever: add event cases, never change what an old one means.
- Run machines with `Actor.start` or `Actor.run`, or with an `ActorRegistry` for one actor per stream. Send with `Actor.send`; the control path is `Actor.interrupt`.
  - Long streams start from a snapshot (`Actor.startFrom`, `ActorSnapshots` in Fx.Store); the actor then looks up keys it has not seen with `EventLog.FindCommit`.
- Every command has an idempotency key. Derive the keys of commands the host makes from the event that caused them, never from the clock or random ids, so retries deduplicate.
- Interpreters turn expected failures into result commands. A throw stops the actor.
- Intents are not durable: record work in flight in the state and pick it up in `ActorSpec.Recover`.
- Actors continue on the sender's thread. Never block a thread on an actor's reply inside a continuation.
- Test with `InMemoryEventLog` and `FakeClock`: the same inputs must give the same log. A new event store passes `EventLogConformance` (including `FindCommit`).

**Sessions** (module spec: [`docs/modules/sessions.md`](docs/modules/sessions.md))
- The session machine (`src/Fx.Sessions`) is shared with clients: it references only FSharp.Core, Fx.Machines and Fx.Contracts. Its commands carry their sender and time (`SessionCommand`), so events record them; send commands with `SessionHost.send`, which builds both from one attribution and the clock.
  - Session events are kept forever: add cases, never change an old one's JSON without a new schema version and an upcaster. `tests/Fx.Sessions.Tests/golden/events-v1.jsonl` is what version 1 wrote; never regenerate it.
  - After every accepted command the session must not sit idle with work it could start: a command that adds inputs or ends a wait ends with `settle`.
- An interrupt goes on the control path (`SessionHost.send` routes `Command.Interrupt` to `Actor.interrupt`); sent as a normal command the running turn would keep running.
- Turns run behind `TurnRunner` (`TurnContext`: `Boundary` at each step, `Open`/`Await` for requests). A turn's commands are keyed by its start event and what they ask; request ids derive from what the opener does.
  - A turn the host stopped during goes to the runner's `Recover` (`Intent.RecoverTurn`): `None` abandons it (held: nothing auto-resumes); a result ends it as `Run`'s would. Recovery must not run anything a person has not decided on.
- Pending requests have a kind (`RequestKind` with payload and answer codecs, a check and the client capabilities that answer it). Owners register theirs; the machine checks payloads and answers against the kind.
- Tests: `ScriptedTurnRunner` on `FakeClock` with an in-memory payload log (`SessionLog.typed`), so every event goes through its JSON.

**Prompt render** (module spec: [`docs/modules/prompt.md`](docs/modules/prompt.md))
- The prompt is a pure render of the log (`src/Fx.Prompt`): `Transcript` folds the session's events and the context events turns append (`ContextEvent`: frames, replies, receipts, pins, collapses), `Render.render` gives the prefix, history and suffix, `Render.request` the canonical request with cache breakpoints and key. Never build model messages for a session by hand.
- Keep it append-only (providers check a reasoning block's prefix; caches hit only on an unchanged one): record the frame with `Transcript.reconcile` before each call, never change the prefix; record each reply with the suffix its prompt ended with (`Shown`); answer every call before the next call; bound a receipt when it is made and never re-render it. Collapses, new frames and newly excluded content are the only rewrites.
  - Context events are kept forever: `tests/Fx.Prompt.Tests/golden/context-events-v1.jsonl` is what version 1 wrote; never regenerate it.
- Record context events through `ContextEvent.withhold exclusions`; only `Piece.File` pieces can be withheld and counted in `SeenRanges`, so a receipt that shows a file must say so. Text that may quote files without showing them (a cell's output after it read them) is a `Piece.Quote` with their sources: withheld when one is excluded, never counted as seen.
- Views are pure and show only what changed since the visible history last showed them (`Transcript.lastShown`); what they need from the world comes in `Live`.
- The render's slots are `PromptSlots.all` (core slots outside `Fx.Variants`): the host registers them with `Slots.all`.
- Tests: the property tests (`tests/Fx.Prompt.Tests/PropertyTests.fs`) must keep passing; `UPDATE_GOLDEN=1` rewrites the golden renders.

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

**Kernel protocol** (module spec: [`docs/modules/kernel-protocol.md`](docs/modules/kernel-protocol.md))
- Kernels speak `src/Fx.Kernel`'s protocol over Fx.Rpc on stdio; every message is defined once in `KernelProtocol` with codecs. TypeScript kernels import the generated `src/protocol.d.ts` (`KernelDeclarations`); never edit it, change the codecs and run the tests with `UPDATE_GOLDEN=1`.
  - A kernel writes each message as `{"jsonrpc":"2.0",…}` (`jsonrpc` first) on a line of its own, so the host recovers messages after stray bytes on fd 1. No binary channels, no batches.
  - A change an old kernel or host would misread is a new protocol version (`KernelProtocol.versions`), documented in the module spec.
- Values cross the boundary as `Value` (`Inline | Blob | Handle`). Issue handles only from the session's `HandleTable` and resolve them there; decode values only into declared types (`Value.decode`, `Kernel.encodeAs`); validate `finish` with `KernelProtocol.Finish.validate`.
- Start kernels with `Kernel.start` and a `KernelLaunch` whose environment is `Exactly` (the hygiene check refuses inherited environments and injection vectors).
- A kernel is done when it passes `KernelConformance.tests` (tests/Fx.Kernel.Tests) on Linux, macOS and Windows: add it as a subject there, with its `Snippets`. Keep the suite language-neutral: check through the protocol, never a language's previews or completion values.

**envd** (module spec: [`docs/modules/envd.md`](docs/modules/envd.md))
- envd is a mode of the binary (`fx envd --stdio`) that serves an environment's capabilities over the environment protocol. Host code uses the same capability records either way: `EnvClient` implements `FsRead`, `Fs`, `FsOps`, `Shell`, `Terminal`, `Blobs` and `Snapshots` over a connection.
- Adding an operation: a method in `EnvProtocol` (codecs), a handler in `EnvdServer`, the proxy in `EnvClient`, and a case in the capability's conformance suite, which runs directly, in process and over an `fx envd --stdio` child.
- Bulk data goes over a channel the caller opens and names in the request; the handler accepts it before anything else, and a caller whose call fails resets it (`Transfer`).
- Processes belong to the `Envd`, not to a connection: a host resumes their output from byte offsets (`EnvClient.attach`). Watches end with their connection.
- In envd mode standard output is the protocol: messages go to standard error.

**Environments** (module spec: [`docs/modules/environments.md`](docs/modules/environments.md))
- A provider is an `EnvProvider` (Fx.Sdk) contributed by a native extension under `EnvProviders.contributionType` (privileged, in process): effects over `IProviderEnv` (the host's files and processes, `HostInfo`), expected failures as `ProviderError`. `fx.env.local` (`extensions/Fx.Ext.EnvLocal`) is the reference.
  - `Lease.Id` is the environment's id the host passed to `Create`: creating again is idempotent, `List` finds it, and `Destroy` releases only what the lease created (never an `existing` workspace) and succeeds when it is gone.
  - A provider is done when it passes `ProviderConformance.tests` (tests/Fx.Environments.Tests) for every transport it offers, on Linux, macOS and Windows.
- Host code reaches environments only through `IEnvs` (`EnvsHost`): specs are checked against their provider (`EnvSpecs.check`) before anything is created; use the capability records `Envs.Open` returns (`EnvClient`) and the paths of `EnvLayout` (envd's state, assets and owned directories under the lease's home, components in the machine's install root), never paths you built.
- The lifecycle (`Lifecycle.machine`, spec §9.5) is one actor per host on `environments/<host>`; its events are kept forever (`tests/Fx.Environments.Tests/golden/events-v1.jsonl`, never regenerated). Results carry their attempt: a result of another attempt is stale.
- envd on the host's machine gets the host's variables only through `HostEnvironment.scrub` (no credentials, no injection vectors). A newly found credential name goes into `HostEnvironment.credentialPatterns`.

**Sandboxes** (module spec: [`docs/modules/sandbox.md`](docs/modules/sandbox.md))
- The sandbox is opt-in (T137, spec §9.1): `sandbox.driver` defaults to `none` (privileged: host config or the command line turn it on with `auto` or a driver's id, never a project). The default carries no warning (clients show `sandbox: off`, `Envs.sandboxLabel`); only a sandbox the host asked for that cannot run warns. Tests that need a sandbox select `sandbox.driver` themselves (`auto`, required with `SANDBOX_TESTS=1`): never rely on the default to confine.
- A sandbox driver is a `SandboxDriver` (Fx.Sdk) contributed by a native extension under `SandboxDrivers.contributionType` (privileged): a probe (an effect over the environment's own processes and files, once per connection: ready, or a reason and a remedy) and a **pure** `Wrap` from a `SandboxRequest` to the `ProcSpec` that runs the process confined. Everything from the world comes in the request; keep it pure so the host computes it per start and the JIT-vs-native diff checks it.
  - A driver is done when it passes `SandboxConformance.tests` (tests/Fx.Environments.Tests) on every OS it lists, with `SANDBOX_TESTS=1` in CI.
- The host wraps each process it starts for an agent (`EnvHandle.Client`'s shell and terminals); envd is never sandboxed. Start an agent's processes, kernels and setup only through the handle's records, never `EnvClient.start` or the unsandboxed client (`EnvSandbox.Unsandboxed` is for the host's own work: probes).
- Policies only tighten below the host: loosening is privileged variants (`sandbox.driver`, `sandbox.writable`, `sandbox.denied`); a spec, a project or an agent (`SandboxOverrides`, `SandboxPolicy.tighten`) can only take access away. Never fall back silently: a required driver that cannot run fails the environment; only `auto` falls back to `none`, with notes that `Envs.warnings` shows.
- The file bindings run in envd, outside the sandbox: `SandboxGuard` checks them against the same policy (`EnvHands.ofHandle`), sandbox or not. A new binding that touches paths goes into `SandboxGuard.paths`.
- Never make a credential location readable (or copy, sanitize or proxy its content) to make a tool work in the sandbox, and add no per-tool variables (T136 was dropped as a growing list of special cases): tools that keep state under `~` or read a credential-listed file are T512's (a copy-on-write home); until then they are sandbox.md's "Known limits". `Hands.Sandbox` (the policy when a driver confines, else `None`) is what the prompt's `sandbox` section (`Instructions.sandbox`) and approval modes read.
- Never write code that assumes a sandbox exists: it is off by default, Windows v1 has only `none`, and `auto` falls back to it.

**Extensions** (module spec: [`docs/modules/extensions.md`](docs/modules/extensions.md))
- A built-in is a package under `extensions/<Name>/` (project `Fx.Ext.<Name>`, id `Product.extensionId "<local id>"`). It references `src/Fx.Sdk` and nothing else; `ArchitectureTests` (tests/Fx.Extensions.Tests) checks the `.fsproj` and the compiled assembly. Reference every new package from `tests/Fx.Extensions.Tests` so the check sees it. `extensions/Fx.Ext.Sample` is the reference.
- Write it as a value: a `Manifest` (built with `Manifest.create` and the `Declare` helpers) and an `Activate` effect that returns an `Activation`: the contributions and methods the manifest declares, exactly. Native extensions are registered from a static list in the composition root (`Builtins.candidates` in `src/Fx.Host`), never loaded from assemblies.
- Contribute under typed keys (`ContributionType<'T>`): `ContributionType.data` (concatenated), `Hook.gate`, `Hook.transform`, `Hook.observe` (all serve over the extension protocol) or `Hook.interpret` (in process only). Define a hook point next to the code that runs it, and run it with `Hooks.decide`, `transform`, `collect`, `observe`, `interpret` over `ExtensionHost.contributions`. Mark the types spec §16.5 lists `ContributionType.privileged`.
- Extension code runs in `IExtensionEnv`, its extension's environment (attenuated to the manifest's `capabilities`) with the caller's scope. Keep per-activation state inside `Activate`.
- Failures carry no exception messages: a failing gate denies, a failing transform fails the pipeline, a failing observer is reported, a throwing method is `MethodFailure.Crashed`.
- Behaviour must be identical in process and out: the parity suite (`ParitySuite.tests`) runs the sample natively, as a child process (`Fx.Aot.Scenarios ext-peer`), built from the SDK packages, and in TypeScript on the pinned Bun (`packages/sample-extension`); the server conformance suite (`ServerConformance.tests`) runs every extension protocol server. Add cases there when you add a hook kind or a protocol method, and mirror the change in the TypeScript sample.

**Public SDKs** (module spec: [`docs/modules/sdk.md`](docs/modules/sdk.md))
- `Fx.Sdk` (and what it exposes: Prelude, Contracts, Contracts.TypeScript, Effects, Variants, Models, Grants, Identity) is all a built-in sees; an F# extension that runs as its own process references `Fx.Sdk.Process` (the protocol's server, `ExtensionProcess.run`) and nothing else. Widening either surface is a deliberate edit of `sdkSurface`/`processSdkSurface` in `ArchitectureTests` and of sdk.md.
- A breaking change to an SDK (a removed member, a changed codec or JSON form, changed hook semantics) bumps `Versions.Sdk`'s minor version before 1.0; a change an old process or host would misread is a new `Versions.Protocol`.
- Package ids come from the naming constant (`FxPascalName` in `Directory.Build.targets`; `packages/sdk/package.json` is checked against `Product.npmPackage "sdk"`): never set one by hand. Mark a project that joins the SDK `<IsPackable>true</IsPackable>` with a `Description` and `GenerateDocumentationFile`.
- `@fx/sdk` mirrors Fx.Contracts' combinators and the contracts it decodes at run time: when you change an F# codec it mirrors (`Codecs.schema`, the manifest, the extension protocol's messages), change the TypeScript side and run `UPDATE_GOLDEN=1 dotnet test --project tests/Fx.Sdk.Process.Tests`, which rewrites `packages/sdk/src/generated` and the cross-language fixtures that `bun test` checks.

**Components** (module spec: [`docs/modules/components.md`](docs/modules/components.md))
- fx installs the programs it runs (the pinned Bun first) as components, in its own install root, never through `PATH` or user-global installs. The model (`Component`, `InstallMethod`, `SemVer`, `VersionRange`, `TargetTriple`, `Digest`) is in `Fx.Sdk`; extensions contribute definitions under `ComponentDefinitions.contributionType` (privileged).
  - A definition is one exact version. Paths in it are relative to the install directory, `/`-separated (`Component.isRelativePath`), and may use `${exeSuffix}`; URLs are `https://` and may use `${version}`.
  - Only the `Archive` method is implemented; the others are typed, and the planner and installer refuse them with an error naming T211.
- Plan with `Planner.plan` (pure, host side) into a `Lockfile`. Render it only with `Lockfile.render` (canonical, byte-identical for the same inputs in any order) and hash it only with `Lockfile.hash` (what `Profile.withLockfile` records).
- Install with the installer machine (`ComponentInstaller.start`, one actor per install root and process) and serve `IComponents` from it (`ComponentInstaller.components`); in the extension bootstrap, `Components.installFor` is `HostSettings.InstallComponents`.
  - Every step must stay safe to run again, and an install directory must only ever appear whole: unpack and probe in staging, then activate with one rename under the root lock. Never write into an install directory after activation.
  - Unpack archives only through `Archive.extract`, which holds every safety check (traversal, links, devices, Windows names, case collisions, limits). An interpreter writes what it is told and nothing else.
  - The first install of a component is a grant: `InstallerSettings.Approve` (`Approvals.viaGrants` over the grants log), never skipped. Its request is keyed by the attempt that asks (`Approvals.key`: the install, the holder, the attempt), so an answer never stands for a later run's install.
- Test an installer environment with `InstallerConformance.tests` (in-memory: `FakeFetch`, `InMemoryInstallFiles`; live: `HttpFetch`, `LocalInstallFiles`). Build test archives with `Support.tar`, `tarGz` and `zip` (fixed times, so digests can be pinned), and never pin the digest of compressed bytes in golden files or scenarios: zlib builds compress differently.

**Runtimes** (module spec: [`docs/modules/runtimes.md`](docs/modules/runtimes.md))
- Start a runtime (Bun, …) only through its `RuntimeSpec` (contributed under `RuntimeSpecs.contributionType`, privileged): install the component with `IComponents`, run `RuntimeSpec.prepare` on fresh owned directories (`OwnedDirs.under`, never inside the workspace), then start the `ProcSpec` that `RuntimeSpec.launch` gives. Never start a `bun` from `PATH` or with an environment you built yourself; append the program and its arguments, never edit the spec's flags.
- `RuntimeSpec.launch` is pure and must stay so: the launcher's environment, the target and trust come in the `LaunchSession`.
- A runtime's `EnvDeny` covers every pattern in `InjectionVectors` (validated). A newly found vector goes into that table, and the built-ins' deny lists in the same change.
- Bun ignores unknown command-line flags silently: a hardening flag must appear in the pinned `bun --help` and have a positive control (`tests/Fx.Ext.RuntimeBun.Tests/LiveTests.fs`). Bumping Bun follows `extensions/Fx.Ext.RuntimeBun/README.md`.

**Models** (module spec: [`docs/modules/models.md`](docs/modules/models.md))
- Everything but wire adapters speaks the canonical model (`Fx.Models`: `ChatRequest`, typed `Part`s, `ChatEvent`, `ChatError`). Never let a provider's JSON shape leak past its adapter.
  - Fold streams only with `ChatAggregate` (pure, total: broken stream rules become `InvalidResponse`); compare streams with `ChatEvents.coalesce`.
  - `ProviderState.Data` is opaque and replayed byte for byte: never parse, trim or re-encode it outside its adapter. Which state may go to which model is `ReplayPolicy` data, never ad-hoc code.
  - Expected failures are `ChatError` cases, as a `Failed` event (adapters never throw for them); the caller's cancellation is an `OperationCanceledException`.
- Call models only through `ILlm`, which the host builds with `ModelAccess` (`Fx.Sdk`): it fits the request, applies the replay rules and runs the model-egress hooks before any wire sees it. Never call `ChatWire.Stream` yourself; `WireRequest` cannot be made outside `ModelAccess`.
- A wire adapter is a native extension contributing a `ChatWire` (`ChatWires.contributionType`), usually `SseWire.create` over pure functions; it gets only a `WireCtx` whose `Http` is confined to its provider's endpoints and adds credentials itself. Providers are data (`ProviderDefs.contributionType`).
- An adapter is done when it passes `WireConformance.tests` (link `tests/Fx.Models.Tests/Conformance/WireConformance.fs`) on cassettes of its provider's documented format, on Linux, macOS and Windows. Cassettes never hold credentials: record with `CassetteRecorder` (it redacts) and review the file.
  - Follow `extensions/Fx.Ext.ModelAnthropic`'s layout: `Provider.fs` (the provider and catalog as data, the quirks the wire reads), `Request.fs`, `Decoder.fs`, `Errors.fs` (pure), and the adapter, wire and extension in one file. Write the cases' expected provider requests and responses by hand from the provider's docs (`tests/<Package>.Tests/sources/`), never from the adapter's own output; the case files built from them are golden.
  - Provider options the canonical model lacks (beta headers, API versions) are quirks, data in the `ProviderDef`; never hardcode a beta in the adapter.
- HTTP goes through `IHttp` (`SystemHttp` live, `InMemoryHttp` fake, `CassetteHttp` replay); never follow redirects.

**Interpreters** (module spec: [`docs/modules/interpreters.md`](docs/modules/interpreters.md))
- An interpreter is an `InterpreterProvider` (Fx.Sdk) contributed by a native extension under `InterpreterProviders.contributionType` (privileged): its kernel's files as `Assets`, a launch on a runtime (never a launch of its own), `Bindings` (a binding surface to its language's files and prompt declarations, pure), a `PromptPack`, `Check` and `Caps`. Its kernel passes `KernelConformance` and the provider `InterpreterConformance.tests` (tests/Fx.Interpreters.Tests).
- Define host bindings once, as data with codecs (`HostBinding.create0|1|2` with `Arg`s), and run them only through a `Bridge` (`Bridge.surface` for declarations and prompt, `Bridge.setup`, `Bridge.host`): arguments decode only into their declared types, failures are `HostCallError`s (`HostBinding.failed` keeps a capability's kind as the code), `finish` goes through `Finishing.create codec ensure accept`. Never write the global's name; it is `Product.kernelGlobal`.
- Start kernels with `Interpreter.prepare` (assets checked or extracted, the runtime's owned directories, binding files) on the environment's filesystem and `Kernel.start` on its shell: never launch a kernel from a path you built.
- The bun-ts kernel's sources ship as `extensions/Fx.Ext.InterpreterBunTs/KernelFiles.fs`, generated from `packages/kernel-bun-ts/src`: after changing the kernel, run `UPDATE_GOLDEN=1 dotnet test --project tests/Fx.Ext.InterpreterBunTs.Tests`, rebuild, and run the kernel conformance suite. Keep the kernel flat and without dependencies; pass `macro: false` to `Bun.Transpiler` and `--no-script` to `bun check`.

**CodeAct** (module spec: [`docs/modules/codeact.md`](docs/modules/codeact.md))
- The CodeAct strategy (`src/Fx.CodeAct`) is the `TurnRunner` sessions run turns with. A turn ends only with a `TurnResult` a cell's `finish` handed over (the session's `Codecs.turnResult` plus `Ensure`), with `NeedInput` on an `agent.question` request the strategy opened, or the `Error` of a policy. Register `Questions.kinds` (which has `decision.outcomeUnknown` too) with the session host and `CodeActSlots.read` with the host's registry.
- Record context events only through the strategy's `record` (`ContextLog.record` through `ContextEvent.withhold`): they live in `context/<session>`, placed after the session event their envelope's `Cause` names; never append them elsewhere or with another cause. Answer every tool call of a reply with a receipt before the next model call.
- Both action surfaces act through the same bridge: direct tools (`read`, `edit`, `bash`) are host calls of the bridge's bindings through `Hands.Guard`, never capability calls of their own. A conversation keeps the surface its frame recorded.
- Kernels live in the hands' environment (`Hands.Env`, the host's scope), never a turn's; stop cells with `Kernel.interrupt`, not by cancelling `Kernel.run`. Every policy, budget and bound is a `CodeActSlots` slot read once per turn from its profile.
  - A kernel lives for the session unless it is killed, dies or the host stops. Journal how each kernel ended (`kernelEnded`, through `KernelJournal.Ended`) before the launch that replaces it, and tell the model and people why (the receipt's first piece and `Receipt.Restarted`, which clients show as a line of its own).
- Compaction (codeact.md "Compaction"): collapse history only with `Compaction` (Fx.Prompt: cuts, `Compaction.request`, `Compaction.collapse`) recorded through the strategy's `record`; never touch the frame. A summary request is the conversation's own request up to the cut plus the instruction (same system, tools, tool choice and reasoning, so the provider's cache hits), through the turn's `ILlm`. Prepare summaries in the background only with `Eff.both` beside the step's actions, joined before the next call, and never let a failed summary fail the cell beside it.
- Journal before side effects (crash recovery, [`codeact.md`](docs/modules/codeact.md#crash-recovery-spec-5-65-121-191)): run every action through `runAction`, which appends its `callStarted` to `steps/<session>` before it does anything; its receipt is its end (never add a second end record). Kernel launches are journaled before they start, so launch numbers (and owned directories) go on after a restart. Step events are kept forever: `tests/Fx.CodeAct.Tests/golden/step-events-v1.jsonl` is what version 1 wrote; never regenerate it.
  - A call with a start and no receipt is in doubt: recovery pauses the turn on a `decision.outcomeUnknown` request with evidence and goes on only with a person's answer. Never run a call again unless they chose `rerun`; derive the request id from the call and attempt, so recovering twice opens one decision.
- Grants (codeact.md "Grants"): the composition root attaches `GrantGuard.attach` to each turn's hands; a kernel's host calls go through the running turn's guard (`SessionKernel.Guard`), never the guard of the turn that launched it. A binding that runs programs, writes, reads outside the workspace or connects needs a `BindingRule` (`Permissions.core`, `Permissions.connects`). Split commands only with `ShellSyntax.parse`: what it does not follow must stay `Opaque` (the whole line asks). Keep `codeact.grants.mode`, `.allowCommands`, `.askCommands` and `.protectedPaths` privileged, and list in `allowCommands` only commands that read and start nothing else.
  - Auto approvals (codeact.md "Approval modes", spec §13.3) run unasked only `InWorkspace` needs (written plainly in the workspace) that nothing but "no grant applies" stands in the way of, sandbox or not: never gate them on a sandbox (it is off by default) and never widen them beyond the workspace. `GrantSetup.Sandbox` is the driver they are checked on (the composition root's, from the environment's sandbox; `none` when nothing confines), so policy can forbid them only unsandboxed. Downloads, installs and inline shell lines go into `codeact.grants.askCommands`; writes outside the workspace, reads outside it, network bindings, installs and opaque lines always ask. A person turns them on only with a grant of `approvals.auto` (the TUI's switch, `session/setApprovals`, owner), never a session override; clients and `fx run` say when they run unsandboxed.
  - The guard says how a call ran (`Approval`s: auto, or a person by request key): keep it flowing to the step journal, the receipt (`Receipt.Approvals`, written only when not empty, never rendered) and the replay journal (`null` when empty), so nothing runs unasked without a record.
- Tests: the script kernel in process and a scripted model on the real session host (`tests/Fx.CodeAct.Tests/Support.fs`; `restart` stops the host process as a crash would and starts another on the same stores); replay tests render the recorded logs into the requests that were sent.

**Host** (module spec: [`docs/modules/host.md`](docs/modules/host.md))
- `src/Fx.Host` is the host's only composition root: it puts the stores, the built-ins, the variant registry, grants, the installer, identity, environments, `ModelAccess` and the session host together (`Host.start`, undone in reverse by `Host.stop`). Code elsewhere gets the parts it declares, never the `Host`.
  - Built-ins are `Builtins.candidates`, a static list: add a new bundled package there (its test checks the list against `extensions/`). The host's catalog is `Builtins.catalog`.
  - Core slots outside `Fx.Variants` (the render's, CodeAct's, identity's, the sandbox's, recording's, the host's) are registered through `HostSlots.core`; the host's own choices (store durability, session snapshots, the interpreter) are `HostSlots` read from the host's resolution.
  - Config files are read strictly (`ConfigFile.ofJson`: unknown members are issues); a project's config only once the project is trusted (`TrustStore`, kept in the data directory, never in the project).
  - Model credentials reach `ModelAccess.Auth` only through `AuthService.modelAuth` over the service `Setup.auth` starts (with `HostOptions`' keyring, private files and variables); `fx auth` (`Commands.auth`) starts the same service under the data directory's lock.
  - `fx run` puts the run's grant requests (its session's and those of no session, shown with `GrantPrompts`) and decisions about calls in doubt (crash recovery) to the person through `RunIo`; without a terminal it withdraws the requests (the host's closure, never an answer on the person's behalf), saying so and naming the `fx grants allow …` that allows them beforehand, and ends the run on an open decision, never waits for one. A new session's model and credentials are checked (`Headless.modelReady`) before its environment installs anything.
  - `fx grants` (`Commands.grants`) is a person's direct grant: the OS user signed in through `local-os-user` (owner only), under the data directory's lock. Never give an agent, an extension or a policy a way to reach it.
  - Each turn's hands get the grants guard in `Host.start` (`GrantGuard.attach`, settings from the turn's profile); never hand a session hands without it.
  - Sessions are recorded in `Host.start` (`Recording.codeact` over the host's stores; `session.record`): its `Wraps` replace the turn environment's clock and model (`ModelAccess`'s `ILlm`) and the hands' clock, files and sandboxed shell. A capability a turn or its hands newly reach gets a tap there in the same change. `fx replay` (`Commands.replay`) replays a recorded session under the data directory's lock, as `fx auth` does, and exits 1 with the first divergence.
- `Cli.parse` stays pure; `Program` is the shell: it reads the process once (`ProcessFacts`: variables, current directory, binary), writes to the console (one lock), asks at the terminal, and handles Ctrl+C (the first interrupts, the second exits).
- Sessions the host creates go in the session index (`host/sessions`, kept forever: never regenerate `tests/Fx.Host.Tests/golden/index-events-v1.jsonl`); a started host starts every indexed session's actor so recovery runs.
- The client protocol (`ClientProtocol`, spec §17) is one value per method with codecs; handlers (`ClientServer`) ask `IdentityServer.caller` with the method's least role (`ClientProtocol.leastRole`) before anything else, key clients' commands `client/<client id>/<key>`, and never answer a request for a client that did not offer its capability. A new method gets its least role, a handler, a case in `tests/Fx.Host.Tests/ProtocolTests.fs`, and `UPDATE_GOLDEN=1` for `golden/client.d.ts`. Clients connect with `HostClient.embedded` (in process) or `HostClient.locate` + `connect` (the listener `fx serve` opened). A session's context log reaches clients through `session/context`, and what running turns' models write through its `session/streaming` notices (`Streams`, not durable).
- Tests: `tests/Fx.Host.Tests/Support.fs` starts hosts on scratch directories with the live interpreters, a scripted model in the Claude API's stream format and downloads cached in `artifacts/host-tests/downloads`; the live tests run the bun-ts kernel (skipped offline, required in CI).

**Record and replay** (module spec: [`docs/modules/replay.md`](docs/modules/replay.md))
- Sessions are recorded by building the strategy with `Recording.codeact recorder wraps config` (`src/Fx.Replay`; the host does it in `Host.start`): every capability call of a turn, its hands, its kernel launches (`Interpreter.Launches`) and its kernel processes goes to `journal/<session>` (inputs as digests, values inline or by blob hash). `Wraps` must replace every capability of the composition root's environments; register `RecordingSlots.all` (`session.record`).
- The journal tells concurrent calls apart by the structured child that made them (`Flow`, set by `Eff.all`, `both` and `race`): start concurrent work only through those combinators, and never branch on `Flow.path`.
- **Every capability is recordable before a session uses it**: a `Record.x`/`Replay.x` pair in `src/Fx.Replay` (its operations in `Ops`, its values' codecs in `Values`), and a recorded session in `tests/Fx.Replay.Tests` that replays it. A new effect a strategy makes needs replay coverage there (`Harness.recordsAndReplays`), not only unit tests.
- **Never journal a secret**: inputs are digests, a process's variables count by name only (`Values.procInput`), `Secrets` by reference and outcome; recorders sit above `ModelAccess`. Keep `SecretTests` passing.
- Code replays only if its path depends on what capabilities answered, never on timing: decide from data (what a race's branches did), not from whether a task or connection has finished yet. A call that acts on the world (a write to a process) is journaled when it starts (`RunActing`); one that brings something from the world when it completes.
- `golden/session-v1.jsonl` must keep replaying: when it diverges, the change alters what fx does; re-record deliberately with `RERECORD_GOLDEN=1` and say why. `golden/journal-events-v1.jsonl` is what version 1 wrote; never regenerate it.

**TUI** (module spec: [`docs/modules/tui.md`](docs/modules/tui.md))
- The TUI is a client of the client protocol (embedded mode in process, `fx attach` over the listener): it never reads the host's stores or calls its services. What it shows is a fold of the protocol's events (`Timeline`: the session machine's own `evolve`, context events placed by their cause); events arrive once, so a replay changes nothing.
- Elm-style: `App.update` and `View.view` are pure (`src/Fx.Tui`); what the person does leaves as `Cmd` values that `TuiClient` sends; keys of commands start with the run's prefix (prompts numbered, an interrupt keyed by its turn, an answer by its request). A new screen or card gets golden renders in `tests/Fx.Tui.Tests` (`UPDATE_GOLDEN=1`) at a narrow width too; measure text only with `Text` (grapheme clusters, wide characters), never `String.Length`.
- The approval mode the footer shows is the host's answer (`session/approvals`), never computed by the TUI: policy and grants decide it on the host, which also says whether a sandbox confines the commands (`approvals: auto · unsandboxed` when none does). Switching it (`Shift+Tab`, `/approvals`) is `session/setApprovals`; ask again when the environment is known and when grants of `approvals.auto` change.
- Only `LocalTty` touches the terminal (raw mode, VT, the Windows console's modes): nothing may write to the console while the TUI runs (`TuiMain` silences .NET's writers), and every exit path gives the terminal back. Tests use `FakeTty` (a `VtScreen`); the real one is tested on a PTY (`tests/Fx.Tui.Live.Tests`, ConPTY on Windows).

**Grants** (module spec: [`docs/modules/grants.md`](docs/modules/grants.md))
- Permissions go to the grants log, choices to the decision ledger. Ask with `IGrants` (`Grants.authorize`, `Grants.check`, `Grants.gate`) under an action of the vocabulary (`Actions.builtIn`, or contribute an `ActionDef` under `GrantActions.contributionType`), on a canonical resource of the action's kind (`Resource.validate`), with a key derived from what asks (a tool call, with its session), never from time or random ids: a key is one request, and its answer is final for it.
  - Split compound shell commands and check each part (`ShellSyntax.parse` in Fx.CodeAct, for sh and PowerShell); give paths canonical (no `..`) and `/`-separated.
- Never hand an agent or extension anything that answers, grants or revokes: only people do, through the client protocol (`GrantsProtocol`) with the connection's principal. Policy (`GrantPolicy.rules`, `GrantPolicy.gates`, privileged) can only ask or deny.
- `Fx.Grants` is folded by clients (Fable): FSharp.Core, Fx.Contracts and Fx.Identity only, no `Array.*` functions or culture-sensitive string APIs (its `ArchitectureTests` lists what may be referenced). Its events are version 1 of an event schema: change their JSON only through a new version and an upcaster.
- A new grant store passes `GrantStoreConformance` (`tests/Fx.Grants.Host.Tests/Conformance`).

**Identity** (module spec: [`docs/modules/identity.md`](docs/modules/identity.md))
- Who acts is a principal id, `<kind>:<provider>:<subject>` (`PrincipalId.create`): put it in `Attribution.Principal` and a grant's `By`, never a name or an account. Ids are recorded for ever: never change their form; the grants machine reads the kind from the id (only `user:` principals answer, grant and revoke).
- Only the host signs clients in and issues session tokens: `Authenticator.signIn` asks the provider the client names in `initialize`, with the `Connection` the transport established (never what the client says about itself), then `SessionTokens.issue`. Serve a connection with `IdentityServer.withIdentity` and one `ClientConnection`; handlers get who is calling from `IdentityServer.caller auth least connection` (a revoked token or a missing role refuses).
- Session tokens are secrets: never log, store or show them (the host keeps SHA-256 hashes; `ToString` hides them). Their lifetime is the privileged slot `identity.sessionTokenTtl`; revocation also ends the connections a token opened (`SessionTokens.Revocation`).
- An identity provider is an `IdentityProvider` contributed by a native extension under `IdentityProviders.contributionType` (privileged); it never mints tokens and refuses with an `AuthError`, never an exception. It passes `IdentityProviderConformance.tests` (link `tests/Fx.Identity.Host.Tests/Conformance/IdentityProviderConformance.fs`). `extensions/Fx.Ext.IdentityLocalOsUser` is the reference.
- Peer accounts come only from the OS (`PeerCredentials`: socket options on Unix, the client process's token on Windows); the local listener's token lives in an owner-only file (`TokenFile.write`), and a session token is a valid bearer there too (`AccessPolicy.Sessions = SessionTokens.lookup`).

**Service credentials** (module spec: [`docs/modules/auth.md`](docs/modules/auth.md))
- How the host authenticates to providers is service credentials, not identity: auth methods (`AuthMethod`, `AuthMethods.contributionType`), credential stores (`CredentialStoreDef`, `CredentialStores.contributionType`) and secret sources (`SecretSource`, `SecretSources.contributionType`), all privileged and in process (`Fx.Sdk/Auth.fs`, `Fx.Sdk/Credentials.fs`). The host's side is `src/Fx.Auth` (`AuthHost.start`, `AuthService`).
  - Give `ModelAccess` its credentials only as `AuthService.modelAuth service` (the `Auth` seam); never read keys for a wire yourself.
  - Methods fail with `CredentialError` (`Missing` lets the provider's next method try), never exceptions; they never get the store: the host reads and writes credentials for them (it is the single refresher, `Resolution.Updated`).
  - Where a key goes is the provider's settings (`header` + `scheme`, or `query`), never the method's guess; unknown settings are errors.
- Secrets travel only as `SecretText` (it never prints; equal by text) and are stored only as `StoredCredential` JSON in a store: never in events, logs, traces, forms (`FormField`s are never secrets; secrets are pasted) or command output.
- Never fall back silently from the store `credentials.store` names (privileged): `StoreChoice.choose` errors with the reason, and `auto`'s downgrade to the file is a notice. `auto` never picks `ephemeral`.
- Configuration names secrets as `secret:<name>` references (case-insensitive), resolved by `Secrets` over the sources in `secrets.sources` order; the `env` source reads only `<PREFIX>SECRET_<NAME>` variables.
- A credential store is done when it passes `CredentialStoreConformance.tests` (`tests/Fx.Auth.Tests/Conformance`) on Linux, macOS and Windows; a host capability it needs goes into `IStoreEnv` with its own conformance suite (`PrivateFilesConformance`, `KeyringConformance`).

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
  - No `SetLastError = true` under `DisableRuntimeMarshalling`: the call throws at run time ("Setting SetLastError to 'true' is not supported"; found on Windows in T122's `PeerCredentials` and T007's keyring). Leave it out and read `Marshal.GetLastSystemError()` right after the call that failed, as `src/Fx.Keyring/Native/Windows.fs` does, or signal failure through return values.
  - Never call a variadic C function (e.g. `sqlite3_db_config`) through `DllImport`: variadic arguments go on the stack on Apple arm64. Add a fixed-arity C shim next to the library's sources, or declare the variadic arguments exactly as the platform passes them (see `Posix.ioctl` in `src/Fx.Pty`).
  - Pin arrays with a `GCHandle` (`Native.pinned` in `src/Fx.Sqlite`), not `use p = fixed array`: the analyzers' typed-tree walk throws on `fixed`.
- Native interop and processes (details in [`docs/spikes/pty.md`](docs/spikes/pty.md)); `src/Fx.Pty` follows these rules:
  - never run managed code between `fork` and `exec`. Create processes that `Process.Start` cannot express with `posix_spawn` from .NET 11's `UnixProcessStartArguments.Start` callback (`WindowsProcessStartArguments.Start` and `CreateProcessW` on Windows): .NET still resolves the program, builds `argv`/`envp` and reaps the child;
  - start processes only on the starter thread (`ProcessStarter.run` in `Fx.Effects.Live` and `Fx.Pty`), which lives as long as the process: `PR_SET_PDEATHSIG` (bubblewrap's `--die-with-parent`) kills a child when the *thread* that forked it ends, and thread-pool threads end after 20 s idle (T135: sessions' kernels died between turns). The shell, terminal and sandbox suites check that a process outlives the thread that asked for it;
  - P/Invoke signatures use only `int`, `int16`, `nativeint`, `unativeint` and pointers as `nativeint` (`BOOL` as `int`); write structures into zeroed native memory at fixed offsets. Keep the constants that differ per OS in one table per OS;
  - Apple frameworks (details in [`docs/spikes/keychains.md`](docs/spikes/keychains.md)): import them by absolute path (`/System/Library/Frameworks/<Name>.framework/<Name>`), read exported constants (`kSecClass`) with `NativeLibrary.GetExport`, declare CoreFoundation's `Boolean` as `byte` (never `int`), release every CF object you create and never the framework's constants;
  - open descriptors and handles non-inheritable at creation (`O_CLOEXEC`), never "then mark them": another thread may start a process in between.
- Code that touches the AOT surface is only done once the AOT publish succeeds **and** the JIT-vs-native diff passes. Cover it with a scenario (see "JIT-vs-native diff" below).
- The AOT publish fails on any trim or AOT warning (IL2xxx, IL3xxx), including the single IL2104 that summarizes a library's warnings. Known cases:
  - `Seq.init` and `Seq.initInfinite` trigger IL2091 inside FSharp.Core. Use `Seq.unfold` or a `seq { }` expression.
  - IL3054 ("generic recursion"): ILC sees a generic cycle in FSharp.Core's function machinery (`FSharpFunc`, the `OptimizedClosures` classes that curried lambdas derive from, the closures of partial applications). It aborts any instantiation of it whose type arguments hold 10 or more of its types in total, or one generic type nested more than 4 deep. Curried lambdas, partial applications and curried calls of function values (`f a b`) instantiate it over all their parameter and result types; single-parameter lambdas do not. Details in [`docs/modules/contracts.md`](docs/modules/contracts.md#native-aot-no-limit-on-fields-or-cases).
    - Never write a curried function over one function per case or item, such as a match function taking one encoder per union case (7 cases failed). Pass them as one tuple to a single-parameter function, as `Codec.union "kind" { let! … and! … return … }` does.
    - Never let a curried function see a type that nests one generic type 5 deep (`int list list list list list`, `'A * ('B * ('C * ('D * ('E * 'F))))`); every codec's preview is curried over its type. Wrap a level in a record. Applicative `and!` builders get `MergeSources3` … `MergeSources12`, as `Codec.record` does, and must hand the tuple only to `return`'s single-parameter function.
- Behaviour that differs under Native AOT, found by the diff:
  - `InvalidCastException` messages lose the type names ("Specified cast is not valid."). Never show or match on runtime exception messages; use the exception type.
  - On linux-musl, the main thread of a Native AOT binary does not know its stack size: `RuntimeHelpers.TryEnsureSufficientExecutionStack()` is false there, so task continuations never run inline on it. Code that relies on inline continuations, such as driving a `FakeClock`, must run on a thread-pool thread (`FakeClock.Advance` throws otherwise).
- A Native AOT executable sets `FxNativeAot` and sets `PublishAot` only when a runtime identifier is given. Copy the two properties from `src/Fx.Cli/Fx.Cli.fsproj`; the restore section of Commands explains why.

The bans on `%A`, printf format specifiers, runtime reflection and I/O outside `Eff` are enforced by our analyzers; see [Analyzers](#analyzers).

**Cross-platform**
- Build paths with `Path` APIs; never hardcode `/` or `\`. Handle CRLF, case-insensitive filesystems and Windows path-length limits.
- The default shell on Windows is PowerShell 7 (`pwsh`); never assume bash there. PTYs use ConPTY on Windows.
- The sandbox is opt-in everywhere, and Windows v1 has **no confining driver** (only `fx.sandbox.none`; `auto` falls back to it with a persistent warning). Never write code that assumes a sandbox is present.

## TypeScript rules

- Kernel and workflow-worker code must stay deterministic. In workflow mode, `Date` and `Math.random` must not be used directly; every effect goes through `fx.*`.
- Types for `@fx/sdk` and `fx.d.ts` are **generated from the F# codecs**. Never edit generated files by hand.
  - Generate them with `DeclarationFile.render` (`src/Fx.Contracts.TypeScript`, module spec [`docs/modules/typescript.md`](docs/modules/typescript.md)): `Layout.Module` for the SDK and clients, `Layout.Global(Product.kernelGlobal, …)` for a kernel's host bridge.
  - Name a codec with `Codec.named` to make it a declared type, and describe it before naming it, so that its declaration carries the description.
  - Type-check TypeScript with Bun's `bun check`, always with `--no-script` (otherwise it runs a `check` script from a `package.json` above the project).
- TypeScript extensions are written against `@fx/sdk` (`packages/sdk`): its codecs (values are the JSON shapes), its generated contract types, `Hook`/`ExtMethod`/`contribution`, `runStdio`. Inside this repository import it by relative path, so that no source names the product; get product-derived names from its generated `product.ts`.

## Naming

`fx` is a working name, and a rename is expected. The product name, the binary name, the `.fx/` and `~/.fx` directories, the kernel global `fx`, the SDK package names, the `FX_*` environment-variable prefix and the protocol namespaces all come from **one build-time constant**. Never hardcode `"fx"` anywhere else.

## Security rules (spec §18)

- Treat model providers as outside the trust boundary. Every payload sent to a model must pass through the model-egress hooks.
  - Never send model-bound data except through `ModelAccess` (the `ILlm` it makes): no HTTP client, credentials or `ChatWire` of a provider anywhere else, for the main loop, `fx.llm`, agents, summaries or judgments alike. The architecture tests check that only `ModelEgress.clear` makes a `WireRequest`.
  - When you put a file's content into a request part, set the part's `Sources` (`ContentSource`); a transform that rewrites a part keeps them. Content exclusions match parts by them ([`docs/modules/egress.md`](docs/modules/egress.md)); content without sources cannot be excluded at egress.
  - Match paths against content exclusions only with `Fx.Models.Exclusions` (one matcher for render, bindings, activity and egress) and build it from settings with `ContentExclusions.compile`. Patterns only add up (no negations, case ignored): never let one origin's list remove another's.
- Credentials live only on the host. Config refers to secrets by reference (`secret:<name>`), never inline; a reader that takes a credential takes a `SecretRef` and refuses a literal (as `api_key`'s `key` does).
  - That means the model never receives them through fx, not that an agent's processes cannot read files: with the sandbox off (the default, T137) commands run with the user's rights and can read credential files, fx's own credentials file in the data directory included. Permission prompts (grants) are the control then; never describe the default as confined.
- Nothing project-local may execute before the project is trusted.
- Runtime launch specs must deny injection vectors such as `BUN_OPTIONS`, `NODE_OPTIONS` and `DOTNET_STARTUP_HOOKS`.
- Never add telemetry.

## Testing and definition of done

- Every module or contribution type has a **spec**, and its **conformance suite** defines what "done" means. Built-ins must pass the same suites as third-party extensions.
- **Transport parity**: in-process F# built-ins must also pass their tests over the out-of-process JSON-RPC transport.
- Changes to machines, strategies or workflows need **replay tests**: replaying recorded events or effects must give identical results.
- **Debug behaves as Release.** Without optimization, F# 11 calls `inline` functions instead of inlining them, and it emits no tail calls. Never let correctness depend on either.
  - A project whose tests are about tail calls sets `<Tailcalls>true</Tailcalls>`, as `Fx.Aot.Scenarios` does.
  - CI's `lint` job builds in Debug and runs `Fx.Effects.Tests` and `Fx.Aot.Tests` there. After changing the builders, run the whole suite with `-c Debug` too.
- CI gates (spec §20):
  - analyzers;
  - AOT publish on Linux, macOS and Windows;
  - JIT-vs-native diff;
  - `Eff` determinism lint;
  - replay, architecture, parity and conformance tests;
  - lockfile reproducibility;
  - workflow IR validation;
  - ledger merge and history-rewrite tests.
- CI publishes `src/Fx.Cli` with Native AOT for `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64` (in an Alpine container), `osx-arm64`, `osx-x64`, `win-x64` and `win-arm64`, each on a native runner (the five beyond `linux-x64`, `osx-arm64` and `win-x64` in the full tier only). Each smoke-tests the binary (`.github/scripts/aot-smoke.sh`), reports the binary sizes in the job summary, runs the JIT-vs-native diff and its self-test, and uploads the binary as a workflow artifact.
- CI (`.github/workflows/ci.yml`) has two tiers, decided by its `plan` job (`scripts/ci-plan.ts`, tested by `scripts/ci-plan.test.ts`; the run's summary shows the plan):
  - **fast**: every pull request, five jobs. `plan`; `lint` (formatting, the analyzers, the TypeScript packages, the Debug build with the builders' tests and the scenarios in Debug; only its Bun steps for a Markdown-only pull request); and one `os` job per OS (`linux-x64`, `osx-arm64`, `win-x64`) that builds, tests, publishes with Native AOT and runs the JIT-vs-native diff, then the steps of each area whose files changed: keyring, sandbox, the network Bun test off Linux (the Bun kernel spike runs in `lint`). A Markdown-only pull request skips the `os` jobs.
  - **full**: the same jobs with every area's steps, plus the `targets` job for the other five targets (`TARGETS` in `scripts/ci-plan.ts`; the musl ones also run the pinned Bun's musl build and the bun-ts kernel on it, and some run an area the `os` jobs cannot: bubblewrap on arm64, the keyring in Alpine, the Keychain and Seatbelt on Intel). Pushes to `main`, the nightly schedule, manual runs and pull requests labelled `ci:full`. Request it for a pull request with `gh pr edit <n> --add-label ci:full`, or run it on a branch with `gh workflow run CI --ref <branch>`; label every pull request that changes platform-sensitive files (native code and P/Invoke projects, build and package settings, the workflows, the pinned runtime) before merging: the plan's summary and a warning say when one does.
  - Changed paths never pick the tier. When you add code whose behaviour may differ per architecture (P/Invoke, native libraries, per-target data), add its paths to `PLATFORM_PATHS` in `scripts/ci-plan.ts`. To test an area of its own, add a step to the `os` job (and to `targets` for a target that needs it) gated on a plan output, with the area's paths and the output in `scripts/ci-plan.ts` and a case in its tests; never add a job for it.
  - The probes and benchmarks (`kestrel-probe.yml`, `pty-probe.yml`, `store-bench.yml`, `contracts-bench.yml`) run weekly and by hand (`gh workflow run "<name>" --ref <branch>`), never on pull requests.

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
| Codec benchmark | `dotnet run -c Release --project tools/Fx.Contracts.Bench` (results in `docs/modules/contracts.md`) |
| Task graph | `bun scripts/tasks.ts check` |
| Bun kernel spike | In `spikes/bun-kernel`: `bun install --frozen-lockfile`, `bun check --no-script`, `bun test`, `bun bench/bench.ts` |
| bun-ts kernel | In `packages/kernel-bun-ts`: `bun install --frozen-lockfile`, `bun check --no-script`, `bun test`. Its conformance runs in `dotnet test` (tests/Fx.Kernel.Tests, launched through `fx.interpreter.bun-ts`), which downloads the pinned Bun into `artifacts/kernel-bun-ts/` (required in CI, skipped offline); then regenerate the kernel's files in the binary: `UPDATE_GOLDEN=1 dotnet test --project tests/Fx.Ext.InterpreterBunTs.Tests` and rebuild |
| Kestrel size and startup probe | `docs/spikes/kestrel-probe/measure.sh <rid>`; CI runs it on every target weekly and by hand (`kestrel-probe.yml`) |
| OpenAI live smoke test | `OPENAI_API_KEY=… dotnet test --project tests/Fx.Ext.ModelOpenAI.Tests`: calls the OpenAI API (`OPENAI_MODEL` picks the model) and records the exchanges, credentials redacted, as conformance cases in `artifacts/cassettes/openai.responses/` (skipped without the key; review a recording before copying it into the suite) |
| Update golden files | `UPDATE_GOLDEN=1 dotnet test --solution Fx.slnx`, then review the diff |
| Re-record the golden recorded session | `RERECORD_GOLDEN=1 dotnet test --project tests/Fx.Replay.Tests` (only when a change deliberately alters what a session does; review the diff). `REPLAY_DUMP=<dir>` writes a diverging replay test's journal; `REPLAY_MEASURE=<file>` measures what recording costs (docs/modules/replay.md) |
| Anthropic live smoke test | `ANTHROPIC_API_KEY=… dotnet test --project tests/Fx.Ext.ModelAnthropic.Tests`: calls the Claude API (`ANTHROPIC_MODEL` picks the model) and records the exchanges, credentials redacted, as conformance cases in `artifacts/cassettes/anthropic.messages/` (skipped without the key; review a recording before copying it into the suite) |
| Install and launch the pinned Bun over the network | `NETWORK_TESTS=1 dotnet test --project tests/Fx.Ext.RuntimeBun.Tests`: downloads Bun for this machine through the planner and installer, launches it hardened and runs the positive controls (skipped without the variable; CI runs it as its own step in the `os` jobs, and in the musl targets' Alpine containers) |
| TypeScript SDK and sample | In `packages/sdk` and `packages/sample-extension`: `bun install --frozen-lockfile`, `bun check --no-script`, `bun test`. The sample's parity and server conformance suites run in `dotnet test` (tests/Fx.Extensions.Tests) on the pinned Bun |
| SDK packages | After a Release build: `bun scripts/packages.ts` packs the SDKs into `artifacts/packages` (ids from the constant, dependencies checked), packs `@fx/sdk`, builds every package under `extensions/` against the SDK package alone, and builds the F# sample as a process from the packages alone into `artifacts/package-consumer/out`; `PACKAGE_TESTS=1 dotnet test --project tests/Fx.Extensions.Tests` then requires it in the suites (CI does both) |
| Type-check the generated TypeScript | `bun scripts/declarations.ts`: the golden declarations in `tests/Fx.Contracts.TypeScript.Tests/golden` with the shared fixtures, and the misuses that must fail (needs Bun 1.4.5, as CI) |
| Native libraries | Built by `dotnet build` and `dotnet publish` (no separate step) into `artifacts/native/`. Needs a C compiler, as Native AOT does: clang or gcc on Linux and macOS (`FxSqliteCC` overrides), Visual Studio or the Build Tools with the C++ workload on Windows. The first build downloads the pinned sources; offline, pass `-p:FxSqliteArchive=<zip>` |
| envd | `fx envd --stdio [--workspace <dir>] [--state <dir>] [--shell pwsh\|powershell\|sh] [--retain <bytes>]` serves the environment protocol on stdin/stdout (hidden; hosts start it) |
| PTY probe | `dotnet build src/Fx.Pty -c Release`, then `dotnet fsi docs/spikes/pty-probe/probe.fsx`; CI runs it on every target weekly and by hand (`pty-probe.yml`; run it after changing `src/Fx.Pty` or the probe) |
| Run the TUI | `dotnet run --project src/Fx.Cli` (or the published binary with no arguments) on a terminal: a host in the current directory and the TUI; `--session <id>`, `--continue`, `--model`, `--surface`, `--variant`; `fx attach [--session <id>]` shows the host `fx serve` runs. Without a terminal it prints the usage |
| Run a prompt headless | `dotnet run --project src/Fx.Cli -- run -p "<prompt>" --model anthropic/<model>` (or the published binary): an embedded host in the current directory; needs `ANTHROPIC_API_KEY` or `OPENAI_API_KEY`; `FX_HOME` (the product's home variable) moves the data directory, e.g. to a scratch one. `--json`, `--session <id>`, `--surface`, `--variant slot=value`; `fx trust [<dir>]` trusts a project, `fx variants [list\|explain\|schema]`, `fx auth login\|list\|logout\|status`, `fx serve [--tcp [<port>]]` serves clients on the local listener, `fx replay <session> [--json]` replays a recorded session exactly (exit 1 with the first divergence), `fx grants list\|allow\|deny\|revoke` grants beforehand as the OS user. Without a terminal nobody answers grant requests (they are withdrawn and the run names the command): to run unattended, `fx grants allow component.install bun@1.4.5` once per data directory, grant the commands a run needs (`fx grants allow shell.exec 'git **'`) or pass `--variant codeact.grants.mode=deny` (privileged: the command line or host config, never a project); `--variant codeact.grants.mode=auto` runs unasked commands written plainly in the workspace, sandbox or not (downloads, installs and writes outside the workspace still ask; unsandboxed, the commands run with your rights), and `fx grants allow approvals.auto '*'` turns auto approvals on for every session; docs/modules/host.md "Running unattended" |
| Sandbox tests | `SANDBOX_TESTS=1 dotnet test --project tests/Fx.Environments.Tests` requires this OS's confining driver (bubblewrap on Linux: install it and, on Ubuntu 24.04, `sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0`; Seatbelt on macOS) instead of skipping where it cannot run; the same variable makes the CodeAct live tests and the host's sandboxed runs check that the bun-ts kernel ran sandboxed, and enables the `sandbox-live` scenario (`dotnet run --project tests/aot/Fx.Aot.Diff -c Release -- --no-publish --only sandbox-`, CI's sandbox steps). The sandbox is opt-in (default `none`): those tests select `sandbox.driver = auto` themselves; to try fx sandboxed, pass `--variant sandbox.driver=auto` or set it in the host's config |
| Live keyring tests | `KEYRING_TESTS=1 dotnet test --project tests/Fx.Keyring.Tests` against this machine's keyring (always on Windows); on Linux under `dbus-run-session` with `gnome-keyring-daemon --unlock`, adding `KEYRING_TEST_PASSWORD` for the lock tests. `KEYRING_TESTS=1 dotnet test --project tests/Fx.Auth.Tests` runs the credential-store conformance suite on the keyring store the same way. `KEYRING_TESTS=1` also enables the `keyring-roundtrip` and `keyring-credential-store` scenarios; `dotnet run --project tests/aot/Fx.Aot.Diff -c Release -- --no-publish --only keyring-` diffs only those (CI's keyring steps, `.github/scripts/keyring-*.sh`, docs/spikes/keychains.md) |

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
| `src/Fx.Cli` | The single `fx` executable (`AssemblyName` comes from the constant; Native AOT when published with `-r <rid>`): `Cli.parse` (pure) and the entry point (`Program`) for the TUI (no command), `attach`, `run`, `serve`, `trust`, `variants`, `auth`, `replay`, `grants` and `envd` |
| `src/Fx.Effects` | `Eff`, the `eff`/`effResult` builders, the core capabilities (`IHttp`, and for service credentials `IKeyring`, `IPrivateFiles`, `IHostVariables` and `SecretText`) and their deterministic fakes (pure; spec in [`docs/modules/eff.md`](docs/modules/eff.md)) |
| `src/Fx.Effects.Live` | The real interpreters of the core capabilities (the imperative shell); referenced only by composition roots |
| `src/Fx.Pty` | Processes on pseudo-terminals (PTY on Linux and macOS, ConPTY on Windows) behind the `Terminal` capability shape; findings in [`docs/spikes/pty.md`](docs/spikes/pty.md) |
| `src/Fx.Keyring` | The OS keyrings interpreting the `Keyring` capability (in `Fx.Effects`, with its in-memory fake): the macOS Keychain (Security.framework), the Windows Credential Manager and DPAPI, the Secret Service on Linux over our own D-Bus client; the headless decisions; `LocalPrivateFiles` (owner-only files, DPAPI on Windows) for the file credential store; findings in [`docs/spikes/keychains.md`](docs/spikes/keychains.md) |
| `src/Fx.Transport.Http` | Local Kestrel listener (loopback, Unix socket, named pipe) with HTTP, WebSocket and SSE behind the token, `Origin` and `Host` checks (spec §18; the listener's token or a host-issued session token), the OS account of each local peer (`PeerCredentials`), the listener's owner-only token file (`TokenFile`), and `LocalClient`, the WebSocket client that dials it |
| `src/Fx.Machines` | The `Machine` type, its pure helpers, and what is sent to and from actors (Fable-compatible: FSharp.Core only, checked by a test; spec in [`docs/modules/machines.md`](docs/modules/machines.md)) |
| `src/Fx.Machines.Host` | The machine host: the event-log abstraction and the in-memory log, actors, the actor registry and process managers |
| `src/Fx.Rpc` | Our JSON-RPC 2.0 (spec §17): typed methods, the connection engine, binary channels with credit flow control, the `initialize` handshake, and the in-process, stream (stdio) and WebSocket transports (spec in [`docs/modules/rpc.md`](docs/modules/rpc.md)) |
| `src/Fx.Sqlite` | The SQLite binding: blittable DllImports, connections, cached prepared statements, transactions, WAL, errors as results. An imperative shell for storage interpreters and composition roots |
| `src/Fx.Store` | Typed events (`Payload`, `EventSchema`, upcasters, `EventLog.typed`), snapshots and projections, the `IBlobs` capability, envelope codecs, and the in-memory fakes (pure; spec in [`docs/modules/store.md`](docs/modules/store.md)) |
| `src/Fx.Store.Live` | The real stores: `SqliteEventStore` (event log and snapshots in SQLite) and `FileBlobStore` (content-addressed files with a SQLite index); referenced only by composition roots |
| `src/Fx.Variants` | Variant slots, the slot registry, resolution of config layers under policies, profiles and their ids, the `IVariants` capability with recording and replay, eval matrices, the core's slots and `FakeVariants` (pure; references only Contracts and Effects, so the SDK can expose it; spec in [`docs/modules/variants.md`](docs/modules/variants.md)) |
| `src/Fx.Sdk` | The public F# SDK for extensions (spec §16): manifests, contribution types, the hook algebra, extension methods, `Extension`, `ExtensionRuntime`, `IExtensionEnv`, `BasicExtensionEnv`, variant slots, the environment provider contract (`EnvProvider`, `IProviderEnv`, specs and leases), the sandbox driver contract (`SandboxDriver`, `SandboxPolicy`, `SandboxOverrides`); in `Models/`, the wire contract, the model-egress hook points and `ModelAccess`; in `Grants.fs`, `IGrants`, `GrantStore` and the grants' contribution types. Extensions see this and nothing else of the host (specs in [`docs/modules/extensions.md`](docs/modules/extensions.md), [`docs/modules/sdk.md`](docs/modules/sdk.md)) |
| `src/Fx.Sdk.Process` | The SDK for F# extension processes: the extension protocol's messages (shared with the host), its F# server (`ExtensionServer`) and the stdio entry point (`ExtensionProcess.run`) |
| `src/Fx.Envd` | envd core (spec §3, §9, §17): `FsOps` and workspace `Snapshots` (shadow git over the shell capability), output logs with byte sequence numbers and spill files, the environment protocol (`EnvProtocol`), envd's state and handlers (`Envd`, `EnvdServer`), the host's proxies (`EnvClient`) and blob cache (pure; spec in [`docs/modules/envd.md`](docs/modules/envd.md)) |
| `src/Fx.Envd.Live` | envd's imperative shell: `LocalFsOps`, `LocalWatch`, `LocalSpill`, `LocalPlatform` and the composition root `EnvdMain` (`fx envd --stdio`, and `EnvdMain.embed` for embedded mode); referenced only by composition roots |
| `src/Fx.Environments` | Environments on the host's side (spec §9): specs from config and capability negotiation, the layout, the host's variables scrubbed, the lifecycle machine and its events, `EnvsHost` (envd's bootstrap through providers, connections, components, the sandbox and setup, reconciliation of leases), the sandbox's choice, policy and sandboxed shell and terminals (`EnvSandbox`, `SandboxSlots`) and the `IEnvs` capability (pure; the provider and sandbox driver contracts are in `Fx.Sdk`; specs in [`docs/modules/environments.md`](docs/modules/environments.md), [`docs/modules/sandbox.md`](docs/modules/sandbox.md)) |
| `src/Fx.Extensions` | The extension system's core: the registry machine, trust checks, the catalog, the declarative, native and process runtimes, the host's side of the extension protocol, and the bootstrap (`ExtensionHost`) |
| `src/Fx.Components` | The component planner and lockfile, safe archive unpacking, the installer machine and its pipeline, the `IFetch`/`IInstallFiles`/`IComponents` capabilities and their fakes (pure; the model is in `Fx.Sdk`; spec in [`docs/modules/components.md`](docs/modules/components.md)) |
| `src/Fx.Components.Live` | The installer's real interpreters: `HttpFetch` (downloads) and `LocalInstallFiles` (the disk); referenced only by composition roots |
| `src/Fx.Models` | The canonical model (spec §10.2–10.3): model metadata and provider definitions, typed parts with opaque provider state and provenance, requests, streamed events and their fold, typed errors, `ChatStream` and `ILlm`, event-stream (SSE, NDJSON) parsers, HTTP cassettes, replay rules and fitting, and the content-exclusion matcher and request redaction (`Exclusions`) (pure; exposed by `Fx.Sdk`; specs in [`docs/modules/models.md`](docs/modules/models.md), [`docs/modules/egress.md`](docs/modules/egress.md)). The wire contract, the model-egress hooks, `ModelAccess` and `ContentExclusions` are in `src/Fx.Sdk/Models` |
| `src/Fx.Sessions` | The session machine (spec §5): commands, events, state and intents, steer/queue, turns, durable pending requests and their kinds, forks, profile bindings, and the codecs of all of them (Fable-compatible: FSharp.Core, Fx.Machines and Fx.Contracts only; spec in [`docs/modules/sessions.md`](docs/modules/sessions.md)) |
| `src/Fx.Sessions.Host` | The session host: one actor per session on the event store (started from snapshots), request timers, forks, the boundary to the strategies that run turns (`TurnRunner`), profile binding for overrides (`ProfileBinder`), `subscribe(afterSeq)` with snapshot + tail, and `ScriptedTurnRunner` for tests |
| `src/Fx.Prompt` | The prompt render (spec §5): context events and their JSON, the transcript (a fold of the session's log and the context events), the pure render into prefix, history and suffix, the canonical request with cache breakpoints and key, stable receipts, `SeenRanges`, withholding excluded files (`Withhold`, over the shared matcher), the files each part quotes (`Sources`), views and the render's variant slots (pure; spec in [`docs/modules/prompt.md`](docs/modules/prompt.md)) |
| `src/Fx.Grants` | The grants log's pure core (spec §13): actions, resource patterns, grants, requests, answers, events, the effective rules, the machine's functions and every codec, the `grants/*` messages (Fable-compatible: FSharp.Core, Fx.Contracts and Fx.Identity only; exposed by `Fx.Sdk`, whose `Grants.fs` has `IGrants`, `GrantStore` and the contribution types; spec in [`docs/modules/grants.md`](docs/modules/grants.md)) |
| `src/Fx.Grants.Host` | The grants log on the host: the machine as an actor, the host-local store on the event store, `GrantsService` (the `IGrants` capability, answers, timers) and the `grants/*` client protocol |
| `src/Fx.Identity` | Principals (spec §11.2): principal ids, principals, roles, OS accounts, the connection facts and credentials providers verify, identity failures, and `initialize`'s identity members, with every codec (Fable-compatible: FSharp.Core and Fx.Contracts only; exposed by `Fx.Sdk`, whose `Identity.fs` has the `IdentityProvider` contract and its contribution type; spec in [`docs/modules/identity.md`](docs/modules/identity.md)) |
| `src/Fx.Identity.Host` | Principals on the host: session tokens (`SessionTokens`, kept hashed, expired by the clock, revocable), the authenticator that asks the contributed identity providers, and the identity side of `initialize` (`IdentityServer`, `IdentityClient`) |
| `src/Fx.Kernel` | The kernel protocol (spec §6.4–6.6): messages and codecs, the `Value` wire type and session-scoped handles, the generated TypeScript declarations, the kernel framing and transport, and the host's `Kernel` client (spec in [`docs/modules/kernel-protocol.md`](docs/modules/kernel-protocol.md)) |
| `src/Fx.Interpreters` | Interpreters on the host's side (spec §6.2–6.6): the host bridge (bindings as data with codecs, dispatch, `finish`), the core bindings (`shell.run`, `fs.*`) over the environment's capabilities, interpreters' assets extracted with hash checks, and preparing a kernel's launch on its runtime (pure; spec in [`docs/modules/interpreters.md`](docs/modules/interpreters.md)) |
| `src/Fx.CodeAct` | The CodeAct strategy (spec §4.5, §5, §6.5, §6.9): the turn runner (render, model calls through `ILlm`, cells and the hybrid tools, receipts), the context log and the step journal beside each session's stream, crash recovery (decisions about calls in doubt), cells' values kept for `recall`, the action surfaces as tools from codecs, kernels per session with staged interrupts and restarts, `Ensure`, the text-only/length/empty/error policies, the loop guard and budgets as variant slots, the agent's questions, hands in environments opened through `IEnvs`, and the grants guard (command lines split for sh and PowerShell, host calls as grants, `GrantGuard`) (pure; spec in [`docs/modules/codeact.md`](docs/modules/codeact.md)) |
| `src/Fx.Auth` | Service credentials on the host (spec §11.3–11.5): the store choice (`credentials.store`), the `secret:` resolver (`secrets.sources`), `AuthService` (resolving providers' credentials, the `ModelAccess.Auth` seam, logins, logout, list, status), `AuthHost.start` from the contributions, and the `auth` commands (pure; the contracts are in `Fx.Sdk`; spec in [`docs/modules/auth.md`](docs/modules/auth.md)) |
| `src/Fx.Tui` | The TUI's pure core (spec §19, §20: Elm-style): model, `update`, `view` over the client protocol's events (`Timeline`), cards for permissions, decisions and questions, text width and wrapping, rows and frames, VT output as frame differences, key decoding, the prompt editor, the `Tty` capability with `FakeTty` and `VtScreen`, and the loop (`Runtime`) (spec in [`docs/modules/tui.md`](docs/modules/tui.md)) |
| `src/Fx.Tui.Live` | The TUI's imperative shell: the terminal in raw mode (`LocalTty`: termios, the Windows console's VT modes and UTF-8 code pages, given back on every exit), the TUI as a client of the client protocol (`TuiClient`), and the composition roots of `fx` (a host in this process) and `fx attach` (`TuiMain`) |
| `src/Fx.Host` | The host's composition root (spec §3, §16.1): the data directory and its lock, config, policy and project trust, the static list of built-ins, the variant registry, the SQLite stores, grants, the component installer, identity, environments, `ModelAccess`, the session host with CodeAct, the session index, embedded mode and `fx run -p` (headless), the client protocol (`session/*` with `session/context` and streaming, `grants/*`, `identity/*`, `env/*`, `ext/<id>/*`) in process and on the local listener (`fx serve`), `fx trust`, `fx variants`, `fx auth`, `fx grants`; `Live.fs` builds the live interpreters (spec in [`docs/modules/host.md`](docs/modules/host.md)) |
| `src/Fx.Replay` | Record and replay (spec §15): the journal of a session's capability calls (`journal/<session>`, values by blob hash), the recording interpreters and the recorded CodeAct strategy (`Recording.codeact`, `session.record`), `ReplayEnv` and exact replay of recorded sessions (`SessionReplay.run`), and `fx replay` (`ReplayCommand`) (pure; spec in [`docs/modules/replay.md`](docs/modules/replay.md)) |
| `extensions/` | Extension packages, one per directory, using only `src/Fx.Sdk`: `Fx.Ext.Sample` is the reference and the subject of the transport-parity tests (not bundled); `Fx.Ext.RuntimeBun` is `fx.runtime.bun`, the pinned Bun and its hardened launch (declarative); `Fx.Ext.ModelCanonical` is the reference chat wire (`canonical`) and the subject of the wire-conformance suite's own tests (not bundled); `Fx.Ext.ModelAnthropic` is `fx.model.anthropic`, the `anthropic.messages` wire and the Claude API as a provider (native) ; `Fx.Ext.ModelOpenAI` is `fx.model.openai`, the `openai.responses` wire and the OpenAI API as a provider (native); `Fx.Ext.EgressExclusions` is `fx.egress.exclusions`, content exclusions on the model-egress hooks (native); `Fx.Ext.EnvLocal` is `fx.env.local`, environments on the host's own machine with envd in process or piped (native); `Fx.Ext.IdentityLocalOsUser` is `fx.identity.local-os-user`, the identity provider of embedded mode and local clients (native); `Fx.Ext.AuthStandard` is `fx.auth.standard`, the `api_key` and `env` auth methods and the `store` and `env` secret sources (native); `Fx.Ext.StoreKeyring`, `Fx.Ext.StoreFile` and `Fx.Ext.StoreEphemeral` are `fx.store.keyring`, `fx.store.file` and `fx.store.ephemeral`, the credential stores (native; their tests are `tests/Fx.Auth.Tests`); `Fx.Ext.SandboxNone`, `Fx.Ext.SandboxBubblewrap` and `Fx.Ext.SandboxSeatbelt` are `fx.sandbox.none`, `fx.sandbox.bubblewrap` (bwrap and a seccomp program) and `fx.sandbox.seatbelt` (sandbox-exec), the sandbox drivers (native; their conformance runs in `tests/Fx.Environments.Tests`) |
| `packages/` | TypeScript packages: `sdk` (`@fx/sdk`, the TypeScript SDK: codecs, generated contract types in `src/generated`, the extension API and the protocol's server), `sample-extension` (the F# sample's contract in TypeScript, the subject of the parity tests on Bun), `kernel-bun-ts`, the reference kernel of the kernel protocol (TypeScript cells on the pinned Bun), which T117 grows into `fx.interpreter.bun-ts` |
| `native/` | Native libraries built from pinned sources: `native/sqlite` (the pin in `Sqlite.props`, compile options in `library.c`, the build in `Sqlite.targets`) |
| `tests/<Project>.Tests` | Expecto tests for `src/<Project>`, run through Microsoft Testing Platform. `tests/Directory.Build.props` makes them JIT-only |
| `tests/aot/Fx.Aot.Scenarios` | Native AOT scenarios for the JIT-vs-native diff; also the JSON-RPC test service and its child-process peer (`Fx.Aot.Scenarios rpc-peer`), which `tests/Fx.Rpc.Tests` uses, the sample extension's host and child process (`Fx.Aot.Scenarios ext-peer`), which `tests/Fx.Extensions.Tests` uses, and the script kernel and its process (`Fx.Aot.Scenarios kernel-peer`), which `tests/Fx.Kernel.Tests` uses, and envd on stdio (`Fx.Aot.Scenarios envd-peer <workspace> <state>`) for the `envd-stdio-child` scenario, and the binary's envd mode (`Fx.Aot.Scenarios envd --stdio …`) for `environments-local-piped`; the CodeAct tests use its script kernel in process, and the replay tests `ScriptProc`, the script kernel as a process of a shell |
| `tests/Fx.Envd.Live.Tests` | Besides its own tests, runs the capability suites against `fx envd --stdio`: it references `src/Fx.Cli` to get the binary next to its output |
| `tests/aot/Fx.Aot.Diff` | The JIT-vs-native diff driver, which runs on the JIT |
| `tests/support` | Programs that tests start as child processes (not test projects), e.g. `Fx.Store.Writer`, which the crash tests kill mid-append. A test project references one to get its apphost next to its own output |
| `tests/Fx.Aot.Tests` | Tests for the diff harness in `tests/aot` |
| `scripts/` | Repository tooling (Bun) |
| `.github/` | CI: `workflows/ci.yml` (the `plan`, `lint`, `os` and `targets` jobs), the weekly probes and benchmarks, `scripts/` (what CI's steps run: the native binary's smoke tests, the live keyrings) and `actionlint.yaml` |
| `tools/` | Repository tooling in F#: JIT-only, never shipped. Tested by `tests/<Tool>.Tests` |
| `tools/Fx.Analyzers` | Our F# analyzers ([Analyzers](#analyzers)) |
| `tools/Fx.Effects.Bench` | Micro-benchmark of the `eff` builder (a measurement, so no test project) |
| `tools/Fx.Store.Bench` | Throughput and latency of the event and blob stores (a measurement, so no test project) |
| `tools/Fx.Contracts.Bench` | Encode, decode and preview throughput of codecs (a measurement, so no test project) |
| `analyzers.json` | Analyzer policy: where each rule applies, and the reviewed allowlist |
| `spikes/` | Throwaway prototypes from the spike tasks; findings live in `docs/spikes/` |
| `docs/spikes/` | Spike findings and recommendations for the tasks that build on them, with any measurement tools they need |
| `docs/plans/` | Plans a person decided for a task (`t<id>-<topic>.md`): what to build and why, a code map, tests and done-when; the task's lane implements one and moves what stays true into `spec.md`, the module docs and AGENTS.md |
| `docs/research/` | Research proposals: findings from outside fx and a suggested implementation path; not decided until `spec.md` adopts them |
| `docs/modules/` | Module specs, one per module, e.g. [`contracts.md`](docs/modules/contracts.md), [`eff.md`](docs/modules/eff.md), [`machines.md`](docs/modules/machines.md), [`rpc.md`](docs/modules/rpc.md), [`extensions.md`](docs/modules/extensions.md), [`sdk.md`](docs/modules/sdk.md), [`components.md`](docs/modules/components.md), [`kernel-protocol.md`](docs/modules/kernel-protocol.md), [`runtimes.md`](docs/modules/runtimes.md), [`envd.md`](docs/modules/envd.md), [`models.md`](docs/modules/models.md), [`interpreters.md`](docs/modules/interpreters.md), [`sessions.md`](docs/modules/sessions.md), [`prompt.md`](docs/modules/prompt.md), [`grants.md`](docs/modules/grants.md), [`egress.md`](docs/modules/egress.md), [`environments.md`](docs/modules/environments.md), [`identity.md`](docs/modules/identity.md), [`codeact.md`](docs/modules/codeact.md), [`auth.md`](docs/modules/auth.md), [`host.md`](docs/modules/host.md), [`sandbox.md`](docs/modules/sandbox.md), [`replay.md`](docs/modules/replay.md), [`tui.md`](docs/modules/tui.md) |

- Add new F# projects under `src/`, and give each one a matching test project under `tests/`. F# repository tooling goes under `tools/`. Extension packages go under `extensions/`; their tests are the conformance and parity suites in `tests/Fx.Extensions.Tests`, plus `tests/Fx.Ext.<Name>.Tests` for a package with data or behaviour of its own (e.g. `Fx.Ext.RuntimeBun`).
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
