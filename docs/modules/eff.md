# Effects with typed environments (`Fx.Effects`)

Spec: §4.2 (with §1 principles 2–3, §5 journaling, §15 record and replay, §18). Task: T103.
Code: [`src/Fx.Effects`](../../src/Fx.Effects) (pure) and [`src/Fx.Effects.Live`](../../src/Fx.Effects.Live) (the real interpreters).
Tests: `tests/Fx.Effects.Tests`, `tests/Fx.Effects.Live.Tests`, and the `eff-*` scenarios of the JIT-vs-native diff.

## `Eff`

```fsharp
type Eff<'R, 'T> = 'R -> Task<'T>
```

- `'R` is the environment. Its constraints (`'R :> IClock`, `'R :> IShell`, …) state what the effect may use, and the environment value it is handed is its authority.
- **Defects** are exceptions, so the task faults. **Expected failures** are a `Result` inside `'T`; write those effects with `effResult { }`.
- Invoking an effect starts it. It runs synchronously up to its first incomplete await, like a `task`.
  - Use `Eff.run env effect` at the edges. It turns a synchronous throw into a faulted task.
  - Inside the builders this happens anyway.
- An effect is a value. The same `Eff` can be invoked any number of times, also concurrently, and the runs share no state.

## The builders

`eff { }` and `effResult { }` are F# resumable state machines, like `task { }`. A block compiles to one state machine that carries the environment, and each `let!` passes the environment to the effect it binds.

| Construct | `eff` | `effResult` |
|---|---|---|
| `let!`, `do!`, `match!`, `return!` | binds an `Eff<'R, 'T>` | binds an `Eff<'R, Result<'T, 'E>>`, a plain `Eff<'R, 'T>` or a pure `Result<'T, 'E>`; the first `Error` ends the block |
| `return` | the value | `Ok` value |
| `if`, `match`, `let mutable` across awaits | yes | yes |
| `while`, `for … in` | yes, without growing the stack | yes; an `Error` stops the loop, and its condition is not evaluated again |
| `try … with` | catches exceptions, synchronous or not | catches exceptions; an `Error` is not an exception |
| `try … finally`, `use` (`IDisposable`) | runs on success, exception and cancellation | same, and after an `Error` |
| `and!` | no | no |

- **Only effects can be bound.** There is deliberately no overload for a raw `Task`, `ValueTask` or `Async`, so every effect shows up in the environment type.
  - Capability modules and interpreters lift tasks with `Eff.ofTask` or `Eff.ofCancellableTask`. Application code never needs them.
- `effResult` resolves its `let!` overloads by priority. An effect that returns a `Result` is bound as one, and a plain effect is wrapped in `Ok`. If an effect's result type is still generic at that point, annotate it.
- **Loops, not recursion.** `return!` of a recursive call is not a tail call, so it builds a chain of tasks that grows with every iteration. Write loops with `while`.
- **FS3511.** Some constructs inside a block, such as a `let rec`, make the compiler fall back to a slower dynamic implementation and warn with FS3511. Warnings are errors, so production code never takes that path; `DynamicBuilderTests` tests it anyway.

## Scope and cancellation

```fsharp
type Scope = { Cancellation: CancellationToken; Span: SpanId option }
type IScope = abstract Scope: Scope
type IScopeWith<'R> = inherit IScope; abstract WithScope: Scope -> 'R
```

- Every environment carries a `Scope`: the cancellation of everything running in it, and the current trace span.
- Capability functions take the scope's cancellation token as their last argument. Cancellation fails them with an `OperationCanceledException`, which is a defect, not an expected failure.
- `IScopeWith<'R>` is implemented on the concrete environment type itself (`'R :> IScopeWith<'R>`). It lets combinators run children in a narrower scope:
  - `Eff.all`, `both`, `race` and `timeout` give their children a linked child scope, so they can cancel the losers;
  - `Trace.span` opens a span;
  - `Eff.shield` runs clean-up that must finish after a cancellation.
- **Concurrency is structured.**
  - `all` (fail-fast), `both`, `race` (the first to finish with a value or an exception wins) and `timeout` cancel their child scope once the outcome is decided.
  - They complete only after every child has finished. An effect that ignores cancellation therefore delays its parent, so honour the token.
  - Children start inline, on the calling thread, and run up to their first incomplete await. So a fake clock sees their delays in a fixed order. CPU-bound work does not run in parallel.
- `Eff.bracket acquire release use` always runs `release`, shielded from cancellation. `Shell.withProcess` uses it to kill processes.

## Combinators

| Group | Functions |
|---|---|
| Values | `Eff.ret`, `Eff.unit`, `Eff.delay`, `Eff.ofTask`, `Eff.ofCancellableTask` |
| Composition | `Eff.map`, `bind`, `ignore`, `traverse`, `sequence` (sequential) |
| Environment | `Eff.ask`, `asks`, `local` (derive or attenuate), `provide` (fix the environment) |
| Scope | `Eff.scope`, `cancellation`, `withScope`, `shield` |
| Failures | `Eff.catch` (any exception, cancellation included, becomes an `Error`; use it only at boundaries), `bracket` |
| Concurrency | `Eff.all`, `both`, `race`, `timeout` (on the clock) |
| Expected failures | `EffResult.ok`, `error`, `ofResult`, `ofEff`, `map`, `mapError`, `bind`, `defaultWith`, `traverse`, `sequence` |

## Capabilities

A capability is three things in one file:

```fsharp
/// 1. A record of functions: data in, data out, the cancellation token last.
type Clock =
    { Now: unit -> Instant
      Monotonic: unit -> MonoTime
      Delay: TimeSpan -> CancellationToken -> Task<unit> }

/// 2. The "has" interface, with one member named after the record.
type IClock = abstract Clock: Clock

/// 3. A module of effects that read the capability (and the scope) from the environment.
module Clock =
    let sleep<'R when 'R :> IClock and 'R :> IScope> (duration: TimeSpan) : Eff<'R, unit> =
        fun env -> env.Clock.Delay duration env.Scope.Cancellation
```

- **Data in, data out.** No streams, callbacks or mutable objects cross a capability: a running process is a record of functions too (`Proc`). This is what makes every call recordable and replayable (T131), and transportable over JSON-RPC to envd.
- Expected failures are `Result`s with a capability-specific error union, e.g. `FsError` or `ShellError`. Override `ToString` on every union.
- Interpreters are values of the record type. To wrap one (recording, tracing, attenuation), write a function from the record to the record; this is the `Interpret` hook of §16.3.

| Interface | Record | Operations | Interpreters |
|---|---|---|---|
| `IClock` | `Clock` | now (UTC `Instant`), monotonic (`MonoTime`), delay; module: `sleep`, `sleepUntil`, `timed`, `every` | `SystemClock`; `FakeClock`; `Record.clock` / `Replay.clock` |
| `IIds` | `Ids` | new sortable `Id`, random bytes | `SystemIds` (OS CSPRNG); `FakeIds` (seeded); `Record.ids` / `Replay.ids` |
| `IFsRead` / `IFs` | `FsRead` / `Fs` | read, stat, list / write, create directory, delete, move; module: `readText`, `writeText`, `exists`, `confine` | `LocalFs.read` / `LocalFs.write`; `InMemoryFs` |
| `IShell` | `Shell` | run a command to completion; spawn a `Proc` (write, close stdin, read chunks, wait, kill) | `LocalShell`; `ScriptedShell` |
| `ISecrets` | `Secrets` | resolve a `secret:<name>` reference to a redacted `Secret` | `SecretResolver` over the secret sources (`src/Fx.Auth`, [auth.md](auth.md)); `InMemorySecrets` |
| `IKeyring` | `Keyring` | probe; read, write, delete a `SecretText` under a `KeyringKey`; list a service's accounts | `LocalKeyring` (`src/Fx.Keyring`: Keychain, Credential Manager, Secret Service; [keychains.md](../spikes/keychains.md)); `InMemoryKeyring` |
| `IPrivateFiles` | `PrivateFiles` | read, write (atomic), delete named files only this OS user can read | `LocalPrivateFiles` (`src/Fx.Keyring`: `0600` in `0700`, an owner-only ACL and DPAPI on Windows); `InMemoryPrivateFiles` |
| `IHostVariables` | `HostVariables` | read one of the host's environment variables as a `SecretText` (credentials only) | `SystemVariables`; `InMemoryVariables` |
| `ITrace` | `Trace` | start and end spans, record events (local only) | `TraceCollector` (in memory), `Trace.none` |
| `IHttp` | `Http` | send a request; the response body streams (`HttpBody`: read, close); per-request timeouts; no redirects | `SystemHttp`; `InMemoryHttp`; `CassetteHttp` and `CassetteRecorder` (Fx.Models) |

`IFs` inherits `IFsRead`. An environment that implements only `IFsRead` is the read-only attenuation of one that implements `IFs`.

`ILlm`, the model capability, lives with the canonical model in `Fx.Models` ([models.md](models.md)). The content-addressed blob store, `IBlobs`, lives with the stores in `Fx.Store` ([store.md](store.md)), `IVariants` with the variant registry in `Fx.Variants` ([variants.md](variants.md)), and `IComponents` (with the installer's `IFetch` and `IInstallFiles`) with the component installer in `Fx.Components` ([components.md](components.md)).

### Decisions

- **Time.**
  - `Instant` is whole microseconds since the Unix epoch, in UTC. It prints as fixed-width RFC 3339 (`2026-11-24T21:05:00.123456Z`), so the text sorts like the instant.
  - Microseconds are the finest resolution every OS clock gives, so precision is the same everywhere, and they fit a JavaScript number exactly.
  - Durations are `TimeSpan`.
  - `MonoTime` is time since an origin fixed for one clock. Use it for durations and deadlines (delays run on it), and never persist it.
- **Ids.**
  - `Id` is 128 bits laid out like a ULID: a 48-bit millisecond timestamp, then 80 random bits. It is written as 26 Crockford base32 characters, and the text sorts like the id.
  - `IdGenerator` keeps ids strictly increasing within a millisecond and when the clock goes back.
  - Typed ids wrap it, e.g. `type SessionId = SessionId of Id`.
- **Randomness** belongs to `IIds` (`RandomBytes`). It is journaled like every output, so protect journals like the data they record. Mint credentials only in host code (session tokens: `SessionTokens.issue`, [identity.md](identity.md)).
- **Shell.**
  - `Exec` passes arguments verbatim, with no shell. `Script` runs a command line with the interpreter's shell: `/bin/sh -c` (present on every Linux, including Alpine, and on macOS), or `pwsh -Command` on Windows. Never assume bash.
  - Which shell to use is a variant (spec §15; on Windows the `shell.windows` slot, [variants.md](variants.md)). `LocalShell.defaultShell` is only the bootstrap default.
  - Standard input is always closed after `Run` writes it, so programs that read it never hang.
  - Output is pumped into one bounded channel, so reading one stream never blocks the other.
  - Cancelling kills the process tree. A process whose parent already exited cannot be found by its tree, so sandboxes (T113) use process groups or job objects.
  - Pipes only. Terminals (PTY, ConPTY) have their own capability, `Terminal` in `src/Fx.Pty` ([`docs/spikes/pty.md`](../spikes/pty.md)), with a conformance suite in `tests/Fx.Pty.Tests` and a proxy over the environment protocol ([envd.md](envd.md)).
- **Files.** `WriteFile` replaces a file in place; it is not atomic. Atomic writes, ranged reads, patches, search and watching are envd's `FsOps` ([envd.md](envd.md)). Reads and writes here are whole files.
- **Paths** are absolute, in the syntax of the machine the interpreter runs on. That is the environment's machine, not necessarily the host's, so the host never applies its own path rules to them. Relative paths fail with `InvalidPath`, because a relative path depends on ambient state.
- **Secrets.** `Secret.ToString` and string interpolation show only the reference. Call `Reveal()` at the one place that needs the value. A recording interpreter journals references and outcomes, never values.
- **HTTP.** A request's own timeouts (`Response`, `Idle`) fail it with `HttpError.TimedOut`; the scope's cancellation is an `OperationCanceledException`. Any status is a response. Response header names are lower case. Interpreters never follow redirects, so a payload goes only where its sender named. Failures name their kind, never an exception's message.
- **Tracing.** Spec §4.2 names this capability `ITelemetry`; it is `ITrace` because "telemetry" suggests data leaving the machine, and fx has none of that (§18). Traces stay local. Exporting them to a local file or viewer is future work (spec §15, `fx trace`).
- **Environment variables and process information are not configuration.** Configurable behaviour goes through `IVariants` ([variants.md](variants.md)), which records it. The composition root reads bootstrap values (home directory, `FX_*` variables) once, as values. It is an imperative shell with its own analyzers.json entry, like `src/Fx.Cli/Program.fs`. The one exception is credentials: `IHostVariables` reads a variable that holds a key (`ANTHROPIC_API_KEY` for the `env` auth method, `FX_SECRET_<NAME>` for the `env` secret source) when a request needs it, as a `SecretText` that is never journaled, and only service credentials' code gets it ([auth.md](auth.md)).
- **Secret storage.** `IKeyring`, `IPrivateFiles` and `IHostVariables` are host-only (spec §18) and exist for service credentials: the credential stores and auth methods get them through `IStoreEnv`, `ISecretSourceEnv` and `IAuthEnv` ([auth.md](auth.md)). `SecretText` is a secret's text: it never prints, and two are equal when their texts are.

### Capabilities still to come

| Capability | Task |
|---|---|
| `ISessionLog`, `IUser` (durable pending requests) | T125 |
| `IPackages`; reading extension contributions from `IExtensionEnv` (the host reads them with `ExtensionHost.contributions`, T108) | T109b |
| `IInterpreter` (kernels are started and driven with `Kernel` and `Interpreter`, [interpreters.md](interpreters.md); a capability for strategies comes with them) | T127 |
| `IGrants` | T123 |
| `IContext` (the prompt render itself is pure: [prompt.md](prompt.md)) | T201 |
| `ISnapshots`: per-step snapshots, retention, two-phase undo (the capability and `GitSnapshots` are in `src/Fx.Envd`, [envd.md](envd.md)) | T205 |
| `ITerminal`: a deterministic fake and journaling (the shape and the local interpreter are in `src/Fx.Pty`, the conformance suite in `tests/Fx.Pty.Tests`, the proxy in `src/Fx.Envd`) | T131 |
| `IRepo` | T202 |
| `IRuns` | T212 |
| `IAgents` | T213 |
| `ILedger` | T301 |
| `IJudge` | T402 |
| `ISafeOutputs` | T405 |
| The `command` secret source, plugin sources | with their first user ([auth.md](auth.md)) |

## Environments

A concrete environment is a record of capability values that implements the "has" interfaces and `IScopeWith` (see `tests/Fx.Effects.Tests/TestEnv.fs`):

```fsharp
type SessionEnv =
    { Clock: Clock; Shell: Shell; Scope: Scope }
    interface IClock with member env.Clock = env.Clock
    interface IShell with member env.Shell = env.Shell
    interface IScopeWith<SessionEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }
```

- **Build it** in the composition root from interpreters, e.g. `{ Clock = SystemClock.clock; Shell = LocalShell.create shell cwd; Scope = Scope.ofCancellation turn.Token }`. Only composition roots (the host, envd, tests) reference `Fx.Effects.Live`.
- **Attenuate it** in one of two ways, and use both where it matters:
  - give an effect a narrower environment type with `Eff.local`, e.g. a view that implements only `IFsRead`;
  - wrap a capability, e.g. `Fs.confine root`.

  Types are not a security boundary, and `Fs.confine` does not resolve symlinks: the sandbox (T113) is the boundary (§18).
- **Environments can be structs** (no boxing: the combinators are generic in `'R`).

## Fakes and conformance

- The fakes in `Fx.Effects.Fakes` are deterministic, and they are production code, so `fx test` (§15) can use them too.
  - `FakeClock` moves only when a test advances it. `Advance` fires due delays one at a time in deadline order, with the clock set to each deadline, and runs what waited on them up to their next await. `SetNow` simulates a wall-clock adjustment.
  - `FakeIds` takes its timestamps from a clock and its random bits from a seeded SplitMix64.
  - `InMemoryFs` uses the host's path syntax and has a case-insensitive mode.
  - `ScriptedShell` answers commands from a function, records them, and throws on a command it does not expect.
  - `InMemorySecrets` and `TraceCollector` keep everything in memory.
- Keep effects that a fake clock drives on the advancing thread: no `Task.Yield` or real I/O. A thread-pool hop races with the next `Advance`, and the JIT-vs-native diff caught exactly that.
- `Advance` relies on .NET running continuations inline, which it does only on threads with a known stack size. The main thread of a musl Native AOT binary does not have one, so `Advance` throws there. Test runners use thread-pool threads, and the `eff-concurrency` scenario runs on one.
- **Conformance suites are functions from an interpreter to a `Test`** (`tests/Fx.Effects.Tests/Conformance`). `FsConformance` and `IdsConformance` run against the fakes and against the real interpreters on Linux, macOS and Windows; `Fx.Effects.Live.Tests` compiles the same files. A new interpreter (an envd-backed `Fs`, say) passes the same suite.

## Record and replay

`Fx.Effects.Replay` proves the shape that T131 builds on, for `Clock` and `Ids`:

- `Record.clock journal clock` wraps a real interpreter and journals every call with its inputs and output.
- `Replay.clock cursor` answers from the journal without touching the world. It throws `ReplayDivergedException` when the program asks something else, or the same things in another order.
- A replayed run returns exactly the recorded result, and `ReplayTests` checks this.

What T131 has to add:

- **Concurrency.** The journal here is one sequence, so it replays only sequential effects. Concurrent children interleave their calls in a nondeterministic order, so key entries by scope or span instead.
- **Delay outcomes.** Replayed delays complete at once, so a race between a delay and other work (`timeout`) can be decided differently. Journal each delay's outcome, not only its start.
- **Process output.** Streaming output is a sequence of `Read` results, and is journaled like any other output.
- **Secrets.** Journal references and outcomes, never values.

## Performance

`dotnet run -c Release --project tools/Fx.Effects.Bench` compares three ways to write the same code:

- `task`: a hand-written `task { }` that passes the environment explicitly;
- `eff`: this builder;
- `compose`: the obvious alternative design, closure composition (`Bind(m, f) = fun env -> …`, with a fast path for completed tasks).

Medians on a 2-core Linux x64 VM, .NET 11:

| Case | `task` | `eff` | `compose` |
|---|---|---|---|
| 10 sequential binds, all synchronous | 47 ns, 72 B | 46 ns, 72 B | 68 ns, 152 B |
| Same, building the effect value on every run | — | 90 ns, 240 B | 74 ns, 176 B |
| 10 sequential binds, each awaiting `Task.Yield` | 10.2 µs, 1342 B | 13.3 µs, 1342 B | 15.8 µs, 2675 B |
| `while` loop, 1000 synchronous iterations | 2.8 µs, 96 B | 2.8 µs, 96 B | 9.2 µs, 184 B |

- `eff` costs what hand-written `task` code costs. Composition allocates twice as much on the asynchronous path and is 3.3 times slower in loops. That is why the builders are resumable state machines.
- Building an `eff` value allocates one closure over the block's initial state machine, about 170 B here. Hoist effects that run in hot loops out of the loop.
- The asynchronous row is dominated by thread-pool scheduling. On the agent loop's scale (model calls take seconds, a process spawn takes milliseconds), none of these differences matter. Rerun the benchmark when the builders change, to catch regressions.

## Native AOT

- The builders use no reflection. The state machines are generic structs, compiled for each environment type.
- The `eff-*` scenarios run the static and the dynamic builder paths, generic and struct environments, the combinators on the fake clock, and every real interpreter, both on the JIT and as a native binary on every target.
