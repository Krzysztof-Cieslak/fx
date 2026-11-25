# Spike: Bun kernel (T009)

Spec: §6.2–6.6, §18. Code: [`spikes/bun-kernel/`](../../spikes/bun-kernel/). CI: the **Bun kernel spike** step of the Lint job (Linux), when the spike changes and in the full tier.

**Question.** Can the `bun-ts` CodeAct kernel run on a pinned Bun with the hardened launch from §6.3, keep state across cells with `node:vm` + `Bun.Transpiler` replMode, support top-level await, and type-check cells against a generated `.d.ts`?

**Answer.** Yes, on all three operating systems, with Bun **1.4.5**. Bun 1.4 also brings two things that change the design for the better: a built-in type checker (`bun check`, TypeScript 7.0.2, no `typescript` package needed) and `Bun.ModuleGraph`, which gives each cell ownership of the timers, sockets and processes it starts. The gaps are source maps (replMode emits none), interrupting a busy loop after an `await` (only a kill works), and on macOS `--no-orphans` cannot see the host die while the kernel is busy.

## What was built

| Piece | File | Grows into |
|---|---|---|
| Pinned Bun: version, revision, 12 per-target archives with SHA-256, download → verify → extract → probe | `src/pinned-bun.ts`, `src/zip.ts` | T114 `Component` + `Archive` method |
| Hardened `LaunchSpec` as data, pure resolution into argv/env/cwd | `src/launch-spec.ts` | T114 `LaunchSpec` |
| Kernel process: NDJSON JSON-RPC 2.0 over stdio | `src/kernel/` | T116 protocol, T117 kernel |
| Host stand-in: owned dirs, bundling, launch, protocol client, stub `host/call` | `src/host.ts` | F# host / envd |
| Stand-in for the generated `<global>.d.ts` | `src/typings.ts` | T102 |
| 60 tests, each hardening measure with a positive control; benchmark | `test/`, `bench/` | T116 conformance suite |

The product name is never hardcoded: the harness reads `FxProductName` from `Directory.Build.props`, and the kernel learns the global's name only from `--global=<name>` (a test launches it with a different name).

## Pinned runtime

- **Bun 1.4.5** (`1.4.5+a7733435c`), released 2026-11-19, the latest at the time.
- Assets are `bun-<os>-<arch>[-musl][-baseline].zip` at `https://github.com/oven-sh/bun/releases/download/bun-v1.4.5/`, each holding `bun-<target>/bun[.exe]` (strip 1). Twelve desktop/server targets are pinned, including musl and the `-baseline` builds for x64 CPUs without AVX2.
- The digests come from the release's `SHASUMS256.txt`. They match GitHub's asset digests, and the file is GPG-signed (`SHASUMS256.txt.asc`).
- The installer re-verifies a cached install against the recorded digest. The probe compares `bun --revision` exactly.
- The kernel is bundled into one 29 KiB file with the pinned Bun (`bun build --target=bun`) and launched from there. That is the shape of a bundled package (§16.4): extracted from the binary, hash-checked, and never affected by a project's `package.json` or `tsconfig.json`.

## Hardened launch: what each measure does in 1.4.5

Every row was verified against the pinned binary on Linux, macOS and Windows. Rows marked "positive control" have a test that shows the injection works on a plain `bun` launch and fails on the hardened one.

| Measure | Status in 1.4.5 | Evidence |
|---|---|---|
| `--config=<owned bunfig>` | Works. Replaces `./bunfig.toml`. A project bunfig with `preload` runs code otherwise | positive control |
| `--no-env-file` | Works. `.env` in cwd is loaded otherwise. The owned bunfig also sets `env = false` | positive control |
| `--no-install` | Works. Also `[install] auto = "disable"` in the owned bunfig | unresolvable imports fail at once; no positive control, because the tests avoid the network |
| `--no-orphans` | Works on Linux, macOS and Windows when the kernel is idle. **On macOS it does not fire while the kernel is in a busy loop** (it uses kqueue on the event loop). Linux uses `PR_SET_PDEATHSIG`, Windows a thread-pool wait plus a kill-on-close Job Object. On POSIX Bun exports `BUN_FEATURE_FLAG_NO_ORPHANS=1` so Bun children inherit it | positive control (POSIX); busy-loop test |
| `--no-macros` | Works for files Bun loads. **Does not reach the `Bun.Transpiler` API**: the kernel must pass `macro: false` (undocumented; `macro?: MacroMap` in the types), or `import … with { type: "macro" }` in a cell runs code at transpile time in the kernel's main realm | test |
| `--tsconfig-override=<owned>` | Works and is global: workspace modules a cell imports also stop using their own `tsconfig.json` `paths` | positive control (cwd); checked by hand (workspace modules) |
| `--disallow-code-generation-from-strings` | Exists, but `=strict` refuses `vm.Script`, which the kernel needs. Not usable | docs |
| `--no-addons` | Exists (refuses `process.dlopen`). Not used; a candidate variant once packages are allowed | — |

**Environment.** The launcher replaces the environment rather than filtering the parent's: the allowlist below, plus the launcher's own variables, minus the deny list (deny always wins, matched case-insensitively because Windows names are case-insensitive).

- Allowlist: `PATH LANG LC_ALL LC_CTYPE TZ` and, for Windows, `SystemRoot SystemDrive windir ComSpec PATHEXT NUMBER_OF_PROCESSORS PROCESSOR_ARCHITECTURE OS`.
- Set: `DO_NOT_TRACK=1` (crash-report upload is on by default on macOS and Windows), `NO_COLOR=1`; `HOME`, `USERPROFILE`, `XDG_CONFIG_HOME`, `XDG_CACHE_HOME`, `TMPDIR`, `TEMP`, `TMP`, `BUN_RUNTIME_TRANSPILER_CACHE_PATH` and `BUN_INSTALL_CACHE_DIR` all point into owned directories. With an owned `HOME`, the global `~/.bunfig.toml` and `~/.npmrc` are out of reach too.
- Deny: `BUN_OPTIONS BUN_INSPECT* BUN_CONFIG_* BUN_JSC_* BUN_FEATURE_FLAG_* BUN_DEBUG* NODE_OPTIONS NODE_PATH NODE_CHANNEL_* NODE_REPL_EXTERNAL_MODULE NODE_TLS_REJECT_UNAUTHORIZED NPM_CONFIG_* LD_PRELOAD LD_LIBRARY_PATH LD_AUDIT DYLD_*`.

| Injection vector | Effective against plain Bun 1.4.5? |
|---|---|
| `BUN_OPTIONS=--preload=…` | **Yes**, runs code before the entry point (positive control) |
| `BUN_INSPECT=host:port` | **Yes**, opens a debugger listener, i.e. remote code execution (positive control) |
| `BUN_JSC_*` | **Yes**, sets JavaScriptCore options (`BUN_JSC_dumpOptions=1` positive control) |
| `NODE_PATH` | **Yes**, adds module resolution roots (positive control) |
| `NODE_OPTIONS=--require/--import` | No for Bun itself, but inherited by Node children; denied anyway |
| `JSC_*` (without the `BUN_` prefix) | No effect; not listed |
| `.env`, `bunfig.toml`, `tsconfig.json` in cwd | **Yes** without the flags (positive controls); the kernel's cwd is owned anyway |

## Kernel

**Persistence and await.** `new Bun.Transpiler({ loader: "tsx", replMode: true, macro: false })` + one `vm.createContext`:

- replMode hoists every top-level declaration to a `var` on the context and wraps the cell in an (async) IIFE returning `{ value: <last expression> }`. `const`/`let`/functions/classes/enums/destructuring and imports all persist, and top-level `await` works.
- `import x from "m"` becomes `x = (await import("m")).x`. Dynamic import in a `vm.Script` needs `importModuleDynamically: vm.constants.USE_MAIN_CONTEXT_DEFAULT_LOADER` (otherwise `ERR_VM_DYNAMIC_IMPORT_CALLBACK_MISSING`). Relative specifiers resolve against the script's `filename`, so cells get file names in a module base directory (the workspace in T117).
- Semantics to know:
  - `const` becomes reassignable and redeclarable.
  - Only a trailing expression statement is the value, so the completion value of `try {…}` is lost.
  - `{ a: 1 }` alone is treated as an object literal.
  - Enum members are inlined.
  - Imported modules evaluate in the main realm.

**Realms.** The context is a separate realm, so main-realm arrays fail `instanceof Array` in cells. The kernel creates host results with the context's own `JSON.parse`.

- `Bun.inspect` prints `Object.prototype`'s methods for objects from another realm (a 1.4.5 bug); `node:util` `inspect` is correct and is used for previews.
- A fresh context contains only the ECMAScript builtins and `console`. The kernel adds a curated set of web globals: timers, `fetch`, `URL`, encoders, `AbortController`, `crypto`, `performance`, …
- It does not add `process`, `Bun` or `require`. Cells import them explicitly (`await import("node:process")`).

**Cell lifecycle.** Each cell runs inside its own `Bun.ModuleGraph` (experimental in 1.4):

- Timers, sockets, fetches and child processes the cell starts belong to the graph. Uncaught errors and unhandled rejections from the cell's async work go to the graph's `onError`, so they are attributed to the right cell even after it returned.
- `cell/cancel` and timeouts call `graph.dispose()`: intervals stop and child processes are killed (both tested). Values the cell stored in the context survive.
- Without `ModuleGraph` the kernel still runs, but cancellation can only abandon the cell's promise.

**Interrupts.**

| Situation | What stops it |
|---|---|
| Synchronous code (`while (true) {}`) | `runInContext(…, { timeout })`, the JSC watchdog. The kernel survives |
| Awaiting forever | `cell/cancel` or the timeout disposes the cell's graph |
| Busy loop **after** an `await` | Nothing in-process: the event loop is blocked and never reads `cell/cancel`. The host must kill the kernel (tested) |

**Output and stdout.**

- The protocol owns fd 1. The kernel keeps the original `process.stdout.write` for frames.
- It redirects `process.stdout/stderr.write` and the main realm's `console` (which imported modules use) into the current cell's output.
- Output is bounded: head and tail, with an omitted-character count, 16 000 characters by default.
- Writes that bypass all of that (`Bun.write(Bun.stdout, …)`, children with inherited stdio) still reach fd 1. Every frame therefore starts with the literal `{"jsonrpc":"2.0",` and is written on its own line (a leading `\n` ends any partial line). The host recovers frames after stray bytes and reports the rest as stray output (tested).

**Errors.**

- Syntax errors come from the transpiler with the cell's own line, column and line text.
- Runtime stack frames point into the transpiled text, because replMode reprints the code and **`Bun.Transpiler` emits no source map** (the `sourcemap` option is accepted and ignored in 1.4.5).
- The kernel maps frames back with a token heuristic: it matches the tokens around the reported column in the cell source, uses statement order for repeats, and returns nothing rather than guess.
- It works for the tested cases (throws in later lines, in methods, across cells), and the stack shows only cell frames: `at explode (cell c15:2:34)`.

**Host bridge.** The global is built from the binding names the host sends in `initialize` (`shell.run` becomes `<global>.shell.run(...)`). Each call is a `host/call` request carrying the calling cell's id. Host errors reject in the cell with `message`, `code` and `data`. `<global>.finish(result)` is a `finish` request that the host validates; a rejection throws in the cell, and an accepted result is returned in the cell's `finished` field.

## Type-checking: `bun check`

**`bun check` exists in Bun 1.4.** It reports what TypeScript 7 (`process.versions.typescript`, 7.0.2) reports, with TypeScript's `lib.*.d.ts` built in. No `typescript`/`tsgo` package is needed, so the recommendation is to use it.

**How cells are checked.**

- The kernel runs `<pinned bun> --no-orphans check --no-script --no-pretty -p <owned tsconfig> <cell file>` in an owned check directory.
- The directory holds the generated `<global>.d.ts`, a small `kernel-globals.d.ts` (exactly the globals the kernel provides; no DOM) and one module per executed cell that exports the cell's top-level names.
- A cell being checked starts with one line that re-declares the earlier names it mentions, e.g. `declare let items: typeof import("./cell-c3.ts").items; type Item = import("./cell-c3.ts").Item;`.
- `declare let` keeps earlier names assignable, as they are at runtime, and a later declaration of a name wins.
- Diagnostics are mapped back to cell lines.
- `cell/run { check: true }` refuses to run a cell with type errors.

**Gotchas found.**

- **`bun check` runs a `check` script from any `package.json` in or above its directory instead of checking**, even with `-p`. That runs arbitrary code. `--no-script` is mandatory (positive-control test).
- The online docs describe flags that 1.4.5 does not have (`--no-follow-imports`, `--aot`). Use the docs shipped in `bun-types@1.4.5`, and verify flags against the pinned binary.
- `AGENT=1`/`CLAUDECODE=1` change the output format. `--no-pretty` pins the one-line `tsc --pretty false` format.
- `lib: ["DOM"]` declares globals cells do not have, and costs about 10 ms per run to parse.
- Module imports (`node:*`, packages) need declarations. The spike ships none, so they report TS2591.
- With `bun-types@1.4.5` (1.2 MB without its docs) + `@types/node` (2.8 MB) and `types: ["bun"]`, a cell importing `node:path` checks in about 20 ms.

## Performance

`bun bench/bench.ts` in CI (GitHub-hosted runners), medians; p95 is in the job summaries. The kernel bundle is 29 KiB.

| Measure | Linux x64 (4 CPU) | macOS arm64 (3 CPU) | Windows x64 (4 CPU) |
|---|---|---|---|
| `bun -e 0` with the hardened flags and environment | 2.7 ms | 10.7 ms | 14.9 ms |
| kernel spawn → `initialize` result | 15.7 ms | 24.5 ms | 32.2 ms |
| spawn → first cell result | 17.2 ms | 26.4 ms | 34.3 ms |
| cell `1 + 1`, round trip (in kernel) | 0.10 (0.05) ms | 0.26 (0.15) ms | 0.31 (0.21) ms |
| cell with a `host/call`, round trip | 0.16 ms | 0.30 ms | 0.41 ms |
| cell with `await setTimeout(0)` | 1.3 ms | 2.0 ms | 1.9 ms |
| cell printing 100 lines | 0.20 ms | 0.35 ms | 0.42 ms |
| `cell/check` (spawns `bun check`), one earlier cell | 9.6 ms | 12.5 ms | 21.3 ms |
| `cell/check` after 1000 cells of history | 10.6 ms | 14.5 ms | 22.8 ms |
| kernel RSS after start / after ~1000 cells | 49 / 70 MiB | 8 / 20 MiB (as macOS reports it) | 25 / 44 MiB |

- Cell overhead is negligible next to model latency.
- Starting a kernel is cheap enough to restart one after a kill and replay the journal.
- Type-checking every cell costs about 10–25 ms.
- Before the kernel carried over only the names a cell mentions, `cell/check` grew with history (about 110 ms after 1000 cells on the dev VM).

## Windows notes

- Everything above works on Windows x64 with the same code. Paths come from `node:path`, the reader accepts CRLF, and the allowlist carries `SystemRoot` and friends.
- Environment variable names keep their case (`Path`), so matching must be case-insensitive.
- `Bun.spawn` (libuv) adds `HOMEDRIVE HOMEPATH LOGONSERVER USERDOMAIN USERNAME` to a child environment that lacks them, and puts children in a kill-on-close Job Object. A .NET host gets neither, so T114 must not rely on the second either.
- `--no-orphans` uses a thread-pool wait on the parent handle, so it works even when the kernel is busy. Bun does not export `BUN_FEATURE_FLAG_NO_ORPHANS` there.
- Test pitfall: bun test's `expect(promise).rejects/.resolves` waits in a nested event loop that, on Windows, does not read subprocess pipes. A test awaiting a kernel response that way deadlocks. Use plain `await` (bisected in CI).
- Windows runs unsandboxed (spec §18). Nothing here assumes a sandbox.

## Risks

- **`Bun.ModuleGraph` is experimental.** It is the best way to clean up after a cell and attribute background errors to it. Keep the fallback path, and pin the Bun version (we do).
- **No source maps from replMode.** The position heuristic can fail on heavily rewritten code (enum inlining, destructuring). An upstream `sourcemap` for `Bun.Transpiler` would remove it.
- **Busy loops after an `await`** need a host-side kill. On macOS, `--no-orphans` cannot help either while the kernel is busy, so a dead host can leave a spinning kernel behind.
- **Bun moves fast.** 1.4 rewrote large parts of the runtime (the transpiler binding is now Rust) and the online docs run ahead of releases. Every flag and behaviour this design depends on is covered by a test against the pinned binary; keep it that way when bumping.
- **The interpreter is not a security boundary** (spec non-goal). Hardening keeps project files and the environment from configuring the *runtime*. Cells can still import `node:fs`, spawn processes or open sockets. That is for sandbox drivers and grants.
- **Protocol on stdout.** Children that inherit fd 1 can interleave bytes with frames longer than the pipe's atomic write size (4 KiB on Linux).

## Recommendations

**T114 `fx.runtime.bun`**

- Carry the pin table as data: version, revision, `{target → asset, sha256}`. Use `Archive` with strip 1, and probe `bun --revision` exactly.
- Select `-baseline` with `System.Runtime.Intrinsics.X86.Avx2.IsSupported` and musl by probing the loader. Optionally use `SHASUMS256.txt` + `.asc` for the vendor-index method.
- Ship the `LaunchSpec` above as data: args, allowlist, owned-dir variables and the deny list.
- Validate specs: `EnvDeny` must be non-empty, and `EnvSet`/the allowlist must not name a denied variable. Match names case-insensitively, and build the child environment from scratch (with .NET: clear `ProcessStartInfo.Environment`, then add).
- Write the owned `bunfig.toml` (`telemetry = false`, `env = false`, `[install] auto = "disable"`, `[run] noOrphans = true`) so either the flag or the file alone suffices.
- Keep `--tsconfig-override` in `UntrustedOverrides`.
- Do not include the `BUN_FEATURE_FLAG_*` that Bun exports itself in leak checks.

**T116 kernel protocol**

- Use newline-delimited JSON-RPC 2.0 on stdio.
  - Frames start with `{"jsonrpc":"2.0",` and are written on their own line.
  - Readers accept CRLF, ignore empty lines, recover a frame after stray bytes, and report the rest as stray output.
- Methods proven here:

  | Direction | Methods |
  |---|---|
  | host → kernel | `initialize { protocolVersion, bindings, moduleBase, limits, check }`, `cell/run { cellId, code, check?, timeoutMs? }`, `cell/check`, `cell/cancel { cellId }`, `value/preview { name }`, `shutdown` |
  | kernel → host | `host/call { cellId, method, args }`, `finish { cellId, result } → { accepted, errors? }` |
  | notifications | `cell/late { cellId, kind, text }` for output and errors after a cell returned |

- `cell/run` returns `{ ok, value: { type, text, truncated }, error: syntax|type|runtime|timeout|cancelled, output: { text, truncated, totalChars }, declared, finished?, durationMs }`. A second `cell/run` while one runs is refused with a busy error (−32001).
- Not covered by the spike, so T116 designs them: `value/encode|decode`, `recall`, `snapshot`/`restore`, and streaming activity events while a cell runs.
- Cancellation is three-staged:
  1. `cell/cancel`, which disposes the cell's resources;
  2. the VM timeout for synchronous code;
  3. a host-side kill after a grace period.
- Conformance must cover a busy loop after `await` (kill + restart + journal replay) and launch hygiene with positive controls like `test/hardening.test.ts`.

**T117 `fx.interpreter.bun-ts`**

- Start from `src/kernel/`.
  - Bundle it with the pinned Bun into one file at build time and embed it with a hash.
  - Create the transpiler with `macro: false`.
  - Give each cell its own `ModuleGraph`, and attribute output by `ModuleGraph.current`.
  - Create host values in the context realm, and preview with `node:util`.
- Generate the global's typings (T102) and `kernel-globals.d.ts` from the same list of globals the kernel installs.
- Ship `bun-types` at the pinned version (without its docs) in the kernel package, so cells that import `node:*` type-check.
- Check with `bun check --no-script --no-pretty`, carrying earlier cells as `declare let … typeof import(…)`. Treat type errors as blocking or as warnings through a variant.
- Decide, as a variant, whether `process`/`Bun` are globals in cells.
- Consider (not tried here) running cells on a `Worker`, so the protocol thread stays responsive and `worker.terminate()` can stop busy loops, at the cost of the worker's state. Today only a process kill does it.
- Record each kernel's pid in its owned directory and kill stale kernels when envd starts. That covers the macOS case where a busy kernel outlives a crashed host.
- Ask upstream for `Bun.Transpiler` source maps in replMode; until then keep the position heuristic and its tests.

**Spec.** No change is needed. §6.3's flag list holds for Bun 1.4.5. "`bun check`" in §6.3/6.4 is now literally Bun's built-in checker.
