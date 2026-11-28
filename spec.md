# fx — design specification

> Status: exploration. `fx` is a working name; it will change if the project gets traction (see §20 "Naming").

---

## 0. Summary

**fx** is an AI coding harness (in the space of OpenCode / Pi) written in **F#** and compiled to a single native binary (.NET Native AOT). The agent acts through **CodeAct**: it writes **TypeScript cells** executed in a persistent kernel on a **pinned, isolated, fx-managed Bun runtime** — provided by **built-in extensions** installed via a general **component installer** and implemented with the same public API as third-party extensions.

It adopts **NOOA's concepts** (not its OO design) — typed contracts, pass-by-reference with bounded previews, code as action, explicit state, cache-valid prompt layout, validated termination — plus **Chopin's** idea of human choices as a long-lived, **git-native decision ledger** (per repository). **Permissions are kept separately as grants.** Calls to a Jev-like decision model (**judgments**) are an **optional** research capability.

First-class: **specialized subagents**, **goal loops** and **graph workflows** (one durable run kernel, one Runs UI), authored in Markdown, TypeScript, or F#.

Topology: **host** (brain: agent loop, sessions, model keys), **environments** (hands: shell, files, kernel), **clients** (TUI first; web, desktop, IDE). Everything beyond a minimal core is an extension. Every behaviour is a named, recorded, replayable variant; defaults come from evals.

**Project stance**
- Open source (**MIT**) from day one, **no telemetry**.
- Goal is a good package of features, not competing.
- **v1 audience: TUI + evals.**
- **Built 100% by coding agents**, and **fx builds fx from phase 1** (Anthropic + OpenAI APIs). No timelines.
- **Windows is a full native target in v1.**

**Goals**
- Excellent end-to-end coding agent: legible, verifiable, steerable, never loses work.
- Choices remembered in the repo, following branches, reviewable in PRs.
- Specialized agents, goal loops and workflows as first-class.
- Run anywhere, attach from anywhere.
- Any model through any sanctioned or practical route.
- Small core; everything else (incl. built-ins) is an extension.
- Measurable and reproducible; results published continuously.

**Non-goals (for now)**
- Porting NOOA or building a general agent framework.
- Choosing defaults before eval data.
- Treating the interpreter as a security boundary.
- Live two-way workspace sync.
- User- or org-scoped decisions.
- An ownership model for decision scopes (deferred).
- GitHub-triggered runs.
- A delegated `claude`-binary adapter.
- Coding-plan subscription routes (Z.ai, Kimi, …).

---

## 1. Principles

1. **Values, not magic.** Agents, tools, strategies, workflows, policies and extensions are values built from combinators. No metaclasses, no runtime reflection.
2. **Pure core, imperative shell.** State is a fold over events; effects are described as data and run by interpreters.
3. **Typed environments.** Signatures state the capabilities a function may use; authority = the values you were handed.
4. **The log is the single source of truth.** Every client view and the prompt itself are projections.
5. **Humans decide; models judge.** Decisions are authoritative and human-owned; judgments are advisory and optional.
6. **Choices ≠ permissions.** Decision ledger = choices among alternatives with rationale; grants log = permissions.
7. **Measured, not assumed.** Behaviour is selected through variants that are recorded, compared and published.
8. **A workspace lives in one place.** Data moves only by explicit, recorded, one-way transfers.
9. **Extensions all the way down, from day one.** Built-ins are bundled extension packages using **only the public SDKs**, enforced by architecture tests, transport-parity tests and conformance suites.
10. **Reference, don't copy.** Values stay where they were produced; the model sees bounded previews and passes values by name.
11. **Verification ends work.** Turns, goals and workflows end through typed, harness-checked results.
12. **Reproducible toolchains.** Pinned components + lockfile.
13. **Agent-buildable.** Every module has a spec, an agent style guide, analyzers and tests; fx builds fx.
14. **UX first.** The internals exist to make the agent legible, steerable and trustworthy.

---

## 2. NOOA concepts → fx

| Concept | NOOA (Python, OO) | fx |
|---|---|---|
| Typed contracts | Method signature + Pydantic | `Codec<'T>`: schema, encode, validated decode, bounded preview |
| LLM-implemented functions | `...`-bodied methods | `Fn<'R,'I,'O>` with code or strategy implementation; **agents** are `Fn`s + profile |
| Tools = generation | Public methods on `self` | Everything is an `Fn`; agents are callable by the model |
| Explicit state | Fields on `self` | Fold over events; prompt suffix is a view |
| Code as action | Python REPL | Cells run by an interpreter extension (TypeScript on Bun first) |
| Pass-by-reference | Live REPL objects | Values stay in the kernel; inline / blob / handle across processes, with previews |
| Programmable loop | Python + `@strategy` | Goals and workflows on one run kernel; Markdown / TypeScript / F# authoring |
| Model-callable context APIs | `self.context`, `self.events` | Model actions become events (`Pinned`, `Collapsed`) |
| Cache-friendly transcript | Context blocks, stable receipts | Pure `render`: stable prefix + append-only history + dynamic suffix |
| Validated termination | `return_result` + postconditions | `fx.finish(TurnResult)` + `Ensure`; goals verified by harness-run checks |

---

## 3. Architecture

```
 Clients ─ TUI (F#, v1) · Web (Fable) · Desktop (Tauri + web) · IDE (ACP adapter, VS Code) · mobile web
    │  Client protocol: JSON-RPC 2.0 over WebSocket (+binary channels); HTTP/SSE fallback
    │  Principal auth via identity-provider extensions → host-issued session token
    ▼
 HOST — "brain"  (fx binary)
    • one actor per session/run = single writer of its event log (inputs serialized + attributed)
    • agent runtime: strategies, render (seen-range tracking, content exclusions), agents registry
    • run kernel: goals + workflows; deterministic workflow worker (Bun); triggers; safe outputs
    • ledger (LedgerStore: git-native + host working store) + grants log (GrantStore: host-local default)
    • extension loader + registry; extension runtimes: declarative, native, process (core); bun (extension)
    • component planner (resolve → lockfile); host-side fetch for host-push installs
    • model registry with model-egress hooks; identity, credentials, secrets; judgments (all extensions)
    • environment providers + envd bootstrapper; blob cache
    • policy engine; durable pending requests (decision cards, grant requests, gates, forms, auth flows)
    │  Environment protocol: JSON-RPC control + multiplexed streams (PTY, blobs, ports)
    │  Transport: Piped | Direct | Dialback (via relay)
    ▼
 ENVIRONMENT — "hands"  (fx envd, same binary; Linux, macOS, Windows)
    • exec/PTY (ConPTY on Windows) with sequence-numbered, resumable output
    • fs: batch stat, search, apply-patch, watch   • repo navigation (tree-sitter, LSP proxy)
    • content-addressed blob store   • workspace snapshots   • git, port forwarding
    • component installer   • sandbox drivers (opt-in; none on Windows initially)
    • CodeAct kernels from interpreter extensions (bun-ts by default)
    • egress proxy with policy + credential injection
```

- **Core contains only:** event store and machine host, run kernel, protocols/transports, extension loader/registry (+ `declarative`, `native`, `process` runtimes), component planner/installer, `envd` bootstrapper, principals/session tokens, variant registry.
- **Embedded mode:** host + `envd` + TUI in one process with `fx.env.local`, `fx.identity.local-os-user` and in-process transports.
- **Connectivity:** `Dialback` makes hosts/environments dial out to a relay; Noise end-to-end encryption inside.

---

## 4. Core model (F#)

### 4.1 Contracts

```fsharp
type Codec<'T> =
    { Schema  : Schema                          // → JSON Schema, language bindings, docs
      Encode  : 'T -> Json
      Decode  : Json -> Validation<'T, Issue>   // accumulates model-readable issues
      Preview : Limits -> 'T -> Preview }       // bounded; type-aware (§6.7)
```

- **Combinators first:** codecs are hand-written with combinators (`Codec.record`, `Codec.union`, `Codec.list`, refinements); a build-time generator is optional later.
- The same codecs produce the wire schema, interpreter bindings, client types and SDK types. TypeScript authors use `@fx/sdk` codecs producing the same `Schema`.
- **Own JSON:** exact numbers (no float rounding of int64/decimal), strict reader (duplicate keys rejected, depth-limited); content hashes (events, ledger files, profiles) are taken over one **canonical form**: RFC 8785 key order and escaping, with numbers normalized exactly instead of through doubles. Module spec: `docs/modules/contracts.md`.
- Core result types: `FileText`, `Match`, `RunResult`, `Evidence`, `BlobRef`, `EditResult`, `TaskResult`.

### 4.2 Effects with typed environments

```fsharp
type Eff<'R, 'T> = 'R -> Task<'T>
// defects → exceptions; expected failures → Result inside 'T (effResult { })

type Shell  = { Run: Command -> Task<Output>; Spawn: ProcSpec -> Task<ProcHandle> }
type IShell = abstract Shell : Shell
module Shell = let run cmd : Eff<#IShell, Output> = fun env -> env.Shell.Run cmd

let fixTest test = eff {
    let! out  = Shell.run (Command.test test)
    let! decs = Ledger.relevant (Query.forTest test)
    return! Llm.ask (Prompt.fix out decs) }
// Eff<'R, Patch> when 'R :> IShell and 'R :> ILedger and 'R :> ILlm
```

- **Core capabilities:** `IClock`, `IIds`, `IVariants`, `ILlm`, `IJudge`, `ISessionLog`, `IContext`, `IInterpreter`, `IShell`, `ITerminal`, `IFs`/`IFsRead`, `IFsOps` (envd's batch stat, ranged reads, atomic writes, patches, search, watch), `IRepo`, `IBlobs`, `ISnapshots`, `ILedger`, `IGrants`, `IUser`, `IAgents`, `IRuns`, `ISafeOutputs`, `IPackages`, `IEnvs`, `ISecrets`, `IComponents`, `ITrace` (local traces only; named so because "telemetry" suggests data leaving the machine).
- Concrete environments are assembled per session/agent/run from interpreters (`SessionEnv`, `ReplayEnv`, attenuated agent envs).
- **Scope:** every environment carries a `Scope` (cancellation + current trace span). Capability functions take its cancellation token; structured combinators (`Eff.all`, `race`, `timeout`, `bracket`) run children in a narrower scope and wait for all of them. Capability calls are data in, data out (no streams or callbacks), so they can be journaled and replayed (§15). Details: `docs/modules/eff.md`.
- **No I/O outside `Eff`** (lint + replay tests). Types are not a security boundary.

### 4.3 Functions

```fsharp
type Impl<'R,'I,'O> = Code of ('I -> Eff<'R,'O>) | Strategy of StrategyRef

type Fn<'R,'I,'O> =
    { Name: string; Instructions: Template<'I>
      Input: Codec<'I>; Output: Codec<'O>; Impl: Impl<'R,'I,'O>
      Ensure: ('I -> 'O -> Result<unit, Issue>) list }

type AnyFn = { Name: string; Doc: string; Input: Schema; Output: Schema
               Invoke: Value -> Task<Result<Value, Issue list>> }   // Value: §6.6

module Fn = let bind (env: 'R) (f: Fn<'R,'I,'O>) : AnyFn = …   // erasure once, at session build
```

### 4.4 Machines

```fsharp
type Machine<'Cmd,'Evt,'S,'Intent> =
    { Initial : 'S
      Evolve  : 'S -> 'Evt -> 'S                            // pure; shared with Fable clients
      Handle  : 'S -> 'Cmd -> Result<'Evt list, Rejection>
      React   : 'S -> 'Evt -> 'Intent list }
```

- Shell loop: command → `Handle` → append → `Evolve` → `React` → interpreters → result commands.
- `Rejection` is `{ Code; Message }`, an expected outcome. `React` sees the state after the event. Intents are not durable: `React` runs when an event is appended, never on replay, so an actor's `Recover` (state → intents) picks up work in flight when it starts.
- Each actor is the single writer of its stream; process managers carry events across streams through their own stream and an outbox (at-least-once delivery, deduplicated by idempotency keys). Details: `docs/modules/machines.md`.
- Machines: sessions, runs, strategies, ledger, grants, environment leases, auth flows, component installs, extension state.
- Strategies: `CodeAct`, `Predict`, `Template`, `Delegate`, `Decide`.

### 4.5 Turn results

```fsharp
type TurnResult =
    | Done      of summary: string * evidence: Evidence list * verify: string option
    | NeedInput of DecisionId list
    | Waiting   of on: JobId list * note: string
```

- A turn ends **only** with a valid `TurnResult`; inside goals, `Done` is a *proposal* verified by the harness (§8).
- Explicit policies for text-only, length-cut and empty replies; loop guard fingerprints repeated actions. When the strategy itself stops a turn (a reply in plain text taken as a question, the loop guard, a budget), it ends `NeedInput` on a question request it opened, whose answer is the next turn's input; failures that no retry fixes (a context overflow that compaction cannot relieve, a refused request) fail the turn (`docs/modules/codeact.md`).

---

## 5. Sessions, history and context

- **Event log:** typed durable events with sequence numbers; session tree (forks append); ephemeral deltas vs authoritative items; content-addressed blob payloads. Events keep their schema version and are upcast on read, never rewritten; snapshots are versioned caches of folds. Details: `docs/modules/store.md`.
- **Commands, not mutations:** prompt (steer/queue), interrupt, approve, fork, revert, move — idempotency keys. **Concurrent inputs from multiple clients are serialized and attributed to their principal.** Interrupt uses a control path that bypasses queues.
  - Every event records its command's key, sender and cause, so deduplication survives restarts: a command takes effect at most once.
  - The control path cancels work in flight at once and goes before queued commands.
- **Durable pending requests:** decision cards, grant requests, workflow gates, forms, auth flows, installs — re-offered to capable clients, expiry policy.
- **Reconnect:** `subscribe(id, afterSeq)` replays then goes live; large gaps get snapshot + tail.
- **Crash recovery:** step results journaled; **in-flight tool calls become `OutcomeUnknown` → the run pauses and opens a session-scoped decision with evidence**; nothing auto-resumes. `fx.recall(callId)` / `Out[n]` restore values. A call's start is journaled before it acts and its receipt is its end; the decision's answer (completed, failed, run again, inspect) becomes the call's receipt. Details: `docs/modules/codeact.md`.
- **Prompt = pure render:**

```fsharp
type Prompt = { Prefix: Block list; History: Message list; Suffix: Block list }
let render cfg (s: SessionState) =
    { Prefix  = cfg.Static
      History = History.project s.Log            // append-only; honours Collapsed ranges
      Suffix  = cfg.Views |> List.collect (fun v -> v s) }
```

  Stable receipts keep the prefix cacheable (mapped to provider cache breakpoints). **Content exclusions** filter render inputs. `SeenRanges` records which file ranges/versions entered context.
  - **Append-only:** providers check that what came before a reasoning block is unchanged, and caches hit only on an unchanged prefix. So the prefix is recorded once per conversation (`Framed`) and later changes are stated as updates at the end of the history; the suffix a call showed is recorded with the model's reply and stays before it, so every request is a prefix of the next. Views show only what changed since they last showed it.
  - Collapses, a new frame and content excluded after it was recorded are the documented rewrite boundaries: replies after one lose their reasoning. Details: `docs/modules/prompt.md`.
- **Compaction:** `Collapsed(range, summary)` with originals addressable; cache-sharing summary request; background preparation; model-driven via `fx.context.*` / `fx.history.*`. The CodeAct strategy compacts (when the prompt nears the model's window, on overflow, when the model asks), with the render's pure cuts and summary requests; it moves to the bundled `fx.compaction` (§16.4) once strategies are extension contributions. Details: `docs/modules/codeact.md`, `docs/modules/prompt.md`.
- **Snapshots:** `SnapshotRef` per step; two-phase undo reverting only agent-attributed changes.

---

## 6. CodeAct: components, runtimes, interpreters, kernels

### 6.1 Components and installation

```fsharp
type Component =
  { Id: string; Version: SemVer                     // exact: one definition per version
    Methods: (TargetTriple list * InstallMethod) list   // per target (empty: any), in order of preference
    DependsOn: ComponentRef list                    // id + version range
    Layout: InstallLayout
    Exports: Exports
    Probe: (Argv * expect: string option) option
    Platforms: TargetTriple list; Size: SizeHint }

type InstallMethod =
  | Archive      of url: UrlTemplate * digest: Digest * format: ArchiveFormat * strip: int
  | ReleaseIndex of index: Uri * select: Selector * trust: IndexTrust   // default trust: vendor-index
  | Script       of url: Uri * digest: Digest * runner: string * args: string list
  | Package      of manager: ComponentRef * spec: string
  | System       of probe: Argv * constraint: VersionRange
  | Custom       of ExtensionRef
```

- **Planner (host):** resolves components (highest version in every range, dependencies included; a method per target) and writes `fx.components.lock`, canonical and reproducible; its hash is part of the profile.
- **Installer (`envd`):** resolve → fetch → verify → stage → probe → activate (one atomic rename of the probed staging directory, so an install is never half active); repair/upgrade/prune; locks + refcounts (holders, published per installer in the install root); installs into `~/.fx/runtimes/…` (`%LOCALAPPDATA%\fx\…` on Windows). Never touches `PATH` or user-global installs. Details: `docs/modules/components.md`.
- **Placement:** `env-fetch`, `host-push`, `prebaked`. Mirrors; `fx runtime bundle|import` for offline.
- **Trust:** **vendor-index digests accepted by default** (policy can require pinned digests); `Script` sandboxed and disable-able; `System` read-only; first install is a **grant**.
- Installer conformance suite.

### 6.2 Runtimes and interpreters (extensions)

```fsharp
type RuntimeSpec = { Component: Component; Launch: LaunchSpec
                     Files: RuntimeFile list }      // written to the launch's owned config dir first (e.g. bunfig.toml)
and LaunchSpec =
  { Exe: Template; Args: string list
    EnvAllowlist: string list; EnvSet: Map<string,string>
    EnvDeny: string list                            // runtime injection vectors (required)
    CwdPolicy: OwnedDir | Workspace
    UntrustedOverrides: string list }

type InterpreterProvider =
  { Id: string; Language: string
    Requires: ComponentRef list
    Launch: InterpreterLaunch                       // a runtime (its RuntimeSpec's hardened launch) + the program
    Assets: RuntimeFile list                        // the kernel's files, embedded; extracted with hash checks
    Bindings: BindingSurface -> BindingFiles        // bindings' schemas → e.g. <global>.d.ts, prompt declarations
    PromptPack: PromptPack; Check: CheckSpec option
    Caps: InterpreterCaps }
and InterpreterCaps =
  { Snapshot: bool; AsyncHostCalls: Await | Transparent
    Packages: PackageSupport option; Deterministic: bool }
```

- **Launch:** a pure function of the spec, the installed component and the session (target, owned dirs, trust, the launcher's environment) gives the process: the runtime's flags, the untrusted overrides, then the program; a fresh environment (allowlist, exports, `EnvSet`; denied names removed); an absolute program from the install directory. `EnvDeny` must cover every known injection vector (one list for all runtimes). Details: `docs/modules/runtimes.md`.
- Bindings/prompt packs generated host-side; kernels run in the environment as **separate processes**. An interpreter launches on a runtime, so its process always gets the runtime's hardened launch; its `Bindings` is a function, so interpreters are contributed by native extensions (a data form for declarative ones later). Details: `docs/modules/interpreters.md`.
- **Interpreter is a variant slot.** Plugin candidates: `python`, `monty`, `deno-ts`, `node-ts`, `fsi`, `fsharp-subset`, `luau`.

### 6.3 Built-in Bun stack (bundled extension packages)

| Package | Extension runtime | Contributes |
|---|---|---|
| `fx.runtime.bun` | declarative | `Component` + `RuntimeSpec` (pinned digests, all targets incl. Windows x64/arm64; x64 takes the `-baseline` builds, the same executable as the default ones in 1.4.5) + `bun` package method (with T211's `Package`) |
| `fx.exthost.bun` | process | `ExtensionRuntime "bun"` + **deterministic workflow worker** |
| `fx.interpreter.bun-ts` | native | TypeScript `InterpreterProvider` (kernel sources embedded, `fx.d.ts`, prompt pack, `bun check`); the kernel itself is TypeScript on `fx.runtime.bun` |

- **Hardened launch (data):** `--config=<fx bunfig> --no-env-file --no-install --no-orphans --no-macros` (+ `--tsconfig-override` until trusted); owned cwd; env allowlist (fx-owned cache/temp/npm config, `DO_NOT_TRACK=1`, crash reports off), injection vectors denied (`BUN_OPTIONS`, `NODE_OPTIONS`, `BUN_INSPECT*`, `BUN_CONFIG_*`, `NPM_CONFIG_*`, …).
- **Two Buns:** fx's Bun runs kernels, extensions and the workflow worker; shell commands use the **user's toolchain** from `PATH`.
- **bun-ts kernel:** `Bun.Transpiler({ loader: "tsx", replMode: true })` + persistent `node:vm` context; optional `bun check`; per-session packages via egress proxy (recorded); the project's `.env` only via a **grant**.

### 6.4 Kernel protocol and conformance

- **Kernel protocol** (language-neutral JSON-RPC over stdio): `cell/run|cancel`, `value/preview|encode|decode`, `host/call`, `finish`, `recall`, optional `snapshot`/`restore` and `cell/check`; output and activity stream as notifications while a cell runs. Details: `docs/modules/kernel-protocol.md`.
- **Kernel conformance suite** required for selection, on Linux, macOS and Windows: persistent state, previews, `finish`, marshalling, cancellation, truncation, activity, content exclusions, launch hygiene.

### 6.5 Kernel behaviour (all interpreters)

- **Host bridge:** generated `fx` module. Env-side: `fx.shell`, `fx.fs`, `fx.repo`, `fx.blobs`. Host-side (brokered): `fx.ledger`, `fx.grants` (request only), `fx.llm`, `fx.judge`, `fx.agents`, `fx.runs`, `fx.workflow`, `fx.goal`, `fx.ask`, `fx.context`, `fx.history`, `fx.envs`, `fx.recall`.
- **Every model-bound payload passes the model-egress hooks.**
- **Finishing:** validated by codecs + `Ensure`; evidence from values, never retyped.
- **Activity:** semantic events (Read, Refs, Edit, Run, Test, Review, Agent, Applies D-n), best-effort wrappers, per-cell diff, content exclusions.
- **Durability:** snapshot if supported; otherwise Jupyter semantics + journal.
- **Lifetime:** one kernel per session, kept across cells and turns; it ends only when it is killed (an interrupt's escalation), dies, or the host stops. Every restart is journaled with how the kernel before it ended, and the model and people are told that names from earlier cells are gone, and why.
- **Cell timeout:** counts only the cell's own time: kernels stop its clock while its host calls are in flight, so a person's wait for a grant never times a cell out; the host bounds what those calls do (a cell's commands share its timeout, measured after the grants guard). An interrupt still stops a cell at once. A cell that stopped early reports the names it declared but never assigned, and the calls it stopped while they waited for approval.
- **Windows shell:** `fx.shell.run` defaults to **PowerShell 7** (`pwsh`), falling back to Windows PowerShell; configurable (variant).

### 6.6 Values and references

- Values live in kernel variables; the model sees bounded, type-aware previews and passes variables by name. Bounded output everywhere; overflow → blob with a grep-able path; prefill cell binds task inputs.
- Wire type:

```fsharp
type Value =
  | Inline of Json                          // small: copied
  | Blob   of BlobRef * Preview             // large: content-addressed in envd, lazy, host-cached
  | Handle of HandleId * TypeRef * Preview  // host-resident live object (history, ledger results, agent transcripts, runs)
```

  Handles are unforgeable and session-scoped; values decode only into declared types; thresholds are variants.
- **What the model sees of a cell's value** (the last expression): a string as its text, a binding's result the way that binding's direct tool shows it (a command's output and exit code, a file's numbered lines, which count as seen), anything else as a preview (`values.previewLimits`). The model ends a cell with what it wants to see instead of printing it again.
- **Host-call arguments** (a file's text, a command's input) are copied inline up to their own limit, `values.argumentMaxBytes` (8 MiB; at most 12 MiB, under the JSON-RPC message limit), beyond `values.inlineMaxBytes`, which bounds the values a kernel keeps and `finish`. Journals keep only bounded previews and digests of arguments. Arguments as blobs (content that need not travel to the host, for remote environments) are T201's.
- Deliberate prompt inclusion: `fx.context.pin(name, value)`, `fx.context.pinFile(path, range)` (live view); visible in `/context` with cost.

### 6.7 Reading and navigation

- **Smart previews:** small files shown in full, larger files as a structural outline (line ranges, path, line count, digest); logs/runs head+tail+summary; data shape+samples.

```
FileText(path="src/billing/Retry.fs", lines=412, digest="sha256:9f…", outline=[
  type RetryConfig = {…}                    14–31
  let execute (cfg) (op: unit -> Task<_>)   60–141
  let executeWithClock (clock) (cfg) (op)   143–230
  module Http = let retry n f               232–260   [deprecated]  … ])
```

- **Navigation mirrors familiar habits:** `fx.repo.grep` (ripgrep semantics), `fx.fs.read(path, { lines: [a, b] })`, symbols/refs/definitions/callers (tree-sitter + LSP proxy), plain shell `rg`/`sed`.
- Optional repo map in the cached prefix; ledger anchors as hints.

### 6.8 Anchored edits and the edit guard

- **Ranged reads:** `fx.fs.read(path, { lines: [first, last] })` gives those lines as a `FileText` whose `start` and `end` say where they are (lines past the end are left out); `lines`, `size` and `digest` stay the whole file's. So every `FileText` is also a `FileRange {path, start, end, text, digest, lines}`, the anchor that edits take.
- **Edits by text:** `fx.fs.edit(path, change | change[])`, `change = {old, new, all?}`. Changes apply in order, each to the text the ones before it left; each `old` must occur exactly once unless `all` is set (else `missing` or `ambiguous`; `unchanged` when nothing would change). The lines that differ become one patch, checked against the digest read (**stale** if the file changed meanwhile). Text matches with `\n` line endings; new lines get the file's own, so CRLF files stay CRLF.
- **Edits by range:** `fx.fs.editRange(range, text)` replaces exactly the range's lines if they still hold its text, whatever else changed in the file; otherwise it fails **stale**, with the lines there now as a range. An empty range (`end = start - 1`) inserts; empty text removes the lines.
- **Fresh anchors:** both return `Edited {path, digest, lines, replacements, ranges}`, a fresh range for the lines each change left, so the same lines can be edited again.
- **Two bindings, not one `fx.fs.edit(string | FileRange, …)`:** with one, the second argument would be `change | change[] | string`, and the declaration could not say which goes with which; with two, each signature says exactly what it takes, and the type check catches a mismatch before the cell runs. Search matches (T142) are ranges for `fx.fs.editRange`.
- **One implementation:** hybrid's `edit` tool is a host call of `fx.fs.edit` (§6.9), and a cell ending with an `Edited` shows as `edit`'s receipt does: the changed lines, as seen file lines. Grants and the sandbox see an edit as a write of the file, and outside the workspace also as a read (its result shows the file's lines); content exclusions refuse it as they refuse a read. Module spec: `docs/modules/interpreters.md`.
- **Edit guard** (T204b): using `SeenRanges`, editing never-seen regions warns or errors naming the range; anchor lines count as seen. Modes `off | warn | require` (variant).

### 6.9 Action surface (variants)

- **`cells-only`**: one cell tool + `finish` (NOOA-style). `finish` is the bridge's `fx.finish`, called from a cell, so evidence is built from the values cells hold (§6.5) rather than retyped into a tool call.
- **The cell tool** is `cell {code, texts?}`. `texts` maps names (identifiers) to text: each is bound in the kernel as a string constant of that name before the code runs (`value/decode`), so file bodies, commit messages, patches and a command's input travel without escaping (backticks end TypeScript template literals). A name the kernel cannot bind fails the cell before anything runs, saying which; a cell run again after a crash binds its texts again; receipts never repeat them.
- **`hybrid`**: cells + direct `read` / `edit` / `bash` tools mapped onto the same `fx.*` capabilities (identical activity, grants, exclusions, edit guard): they are host calls of the bridge's own bindings.
- **Both ship from phase 1 with no baseline preference**; evals decide. A conversation keeps the surface and the tools its first frame recorded: tools precede the prompt prefix that providers cache and check, so a new surface, or a new version of the tools' schemas, applies to new sessions.
- Module spec: `docs/modules/codeact.md` (the CodeAct strategy: steps, receipts, kernels per session, policies, loop guard, budgets).

### 6.10 Example cells

```ts
// intent: find all call sites of the deprecated helper
const calls = await fx.repo.refs("Http.retry")            // Match[](len=37, files=14, …)
```

```ts
// intent: rewrite every call site via anchors, test, and get an independent review
for (const m of calls)                                    // one-line ranges: positions stay
  await fx.fs.editRange(m, m.text.replace(/Http\.retry (\S+) /, "RetryPolicy.exponential($1).execute "))
const run = await fx.shell.run("dotnet test tests/Billing")
const review = await fx.agents.reviewer({ diff: await fx.fs.diff(), intent: task.text })
```

```ts
fx.finish({ kind: "done", summary: "Migrated 37 call sites to RetryPolicy; preserved attempt semantics.",
            evidence: [run2, review], verify: "dotnet test tests/Billing" })
```

A new file and its commit message travel in the cell tool's `texts`, outside the code, so nothing in them needs escaping:

```json
{"code": "// intent: add the native layer and commit it\nawait fx.fs.write(\"src/Native.fs\", posix)\nawait fx.shell.run(\"git add src/Native.fs && git commit -F -\", { stdin: message })",
 "texts": {"posix": "module Native.Posix\n/// See ``Native.fs``…", "message": "Add the `Native` layer…"}}
```

### 6.11 Example: F# interpreter plugin (condensed)

```toml
# fx.runtime.dotnet
[[component]]
id = "dotnet-runtime"; version = ">=10.0 <12.0"; probe = ["${exe}", "--list-runtimes"]
exports.env = { DOTNET_ROOT = "${root}", DOTNET_MULTILEVEL_LOOKUP = "0", DOTNET_CLI_TELEMETRY_OPTOUT = "1",
                DOTNET_NOLOGO = "1", DOTNET_CLI_HOME = "${fxHome}/dotnet-home", NUGET_PACKAGES = "${fxHome}/nuget" }
methods = [
  { kind = "release-index", trust = "vendor-index",
    index = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/${channel}/releases.json",
    select = { component = "runtime", rid = "${dotnetRid}", latestPatch = true } },
  { kind = "script", url = "https://dot.net/v1/dotnet-install.sh", digest = "sha256:…",
    args = ["--runtime","dotnet","--channel","${channel}","--install-dir","${staging}","--no-path"] },
  { kind = "system", probe = ["dotnet","--list-runtimes"], constraint = ">=10.0" } ]

# fx.interpreter.fsi
[[component]]
id = "fsi-kernel"; version = "0.3.1"; dependsOn = ["dotnet-runtime >=10.0"]
methods = [ { kind = "package", manager = "nuget", spec = "Fx.Kernel.Fsi@0.3.1" },
            { kind = "archive", url = "https://<fx-downloads>/kernels/fsi/0.3.1/fx-kernel-fsi.zip", digest = "sha256:…" } ]
[interpreter]
id = "fsi"; language = "fsharp"; requires = ["fsi-kernel"]
launch = { exe = "${dotnet-runtime.exe}", args = ["${fsi-kernel.root}/FxKernel.Fsi.dll","--stdio"], cwd = "owned",
           envDeny = ["DOTNET_STARTUP_HOOKS","DOTNET_ADDITIONAL_DEPS","CORECLR_PROFILER*"] }
bindings = "fsi"; check = "kernel"; caps = { snapshot = false, asyncHostCalls = "await", packages = "nuget-session" }
```

---

## 7. Agents (specialized subagents)

### 7.1 Agents are values

```fsharp
type Agent<'R,'I,'O> =
  { Fn: Fn<'R,'I,'O>; Description: string
    Strategy: StrategyRef; Interpreter: InterpreterRef option
    Model: ModelRole | ModelRef
    Caps: CapSet; Sandbox: SandboxOverrides
    Context: Fresh | Resume of key | Shared
    Budgets: Budgets
    Skills: SkillRef list; Memory: MemoryScope; Ledger: LedgerAccess }
```

- Attenuation enforced twice: types (e.g. no `IFsWrite`) and sandbox where the host turns one on (the sandbox is opt-in, §9.1; otherwise, and on Windows v1, type-level only).
- Untyped agents default to `string → TaskResult`.

### 7.2 Authoring: three forms, one value

| Form | Good for | Location |
|---|---|---|
| **Markdown + frontmatter** | Simple, reviewable, shareable; imports from Claude Code / OpenCode | `.fx/agents/*.md`, extensions |
| **TypeScript** (`@fx/sdk`) | Dynamic instructions, composition, shared types, tests | `.fx/agents/*.ts`, extensions |
| **F#** (`Fx.Sdk`) | Built-ins, F# teams | Extension packages |

```markdown
---
name: reviewer
description: Reviews a change for correctness, scope and test quality. Read-only.
input: ReviewRequest
output: Review
model: { role: review }
capabilities: [fs.read, repo, shell.test, ledger.read, llm]
sandbox: { workspace: read-only }
context: fresh
budget: { usd: 2, turns: 30 }
ensure: ["every finding cites file:line evidence"]
---
You are a code reviewer. Judge the diff against the stated intent and settled decisions…
```

```ts
export default agent({
  name: "reviewer", description: "Reviews a change… Read-only.",
  input: ReviewRequest, output: Review,
  caps: [caps.fs.read, caps.repo, caps.shell.test, caps.ledger.read, caps.llm],
  sandbox: { workspace: "read-only" }, model: { role: "review" }, context: "fresh", budget: { usd: 2 },
  instructions: ({ input, repo }) => `You review changes in ${repo.name}. Intent: ${input.intent}. …`,
  ensure: [(_, out) => out.findings.every(f => f.evidence) || "every finding needs file:line evidence"],
})
```

### 7.3 Where agents are used

- From cells: `fx.agents.<name>(…)` with typed results; agent list in the cached prefix.
- Workflow `agent:` nodes with typed handoffs (schemas checked at load).
- Goal evaluators (`Check.Evaluator`).
- Primary agent of a session; `@agent` mentions.

### 7.4 Rules

- **Independence:** fresh context by default; reviewers/QA receive only declared inputs (never implementer reasoning); optional model diversity (variant).
- Communication via typed results passed by reference.
- Each invocation is a child session (inspectable, attachable, steerable, forkable); budgets carved from the parent; depth/concurrency limits.
- **Built-in agents:** `implementer`, `reviewer` (read-only), `qa` (no source edits), `planner`, `explorer`.

---

## 8. Runs: goals and workflows

### 8.1 One run kernel

- Session turns, goal runs and workflow runs are `Machine`s on one event log, sharing journaling/replay, budgets, checkpoints, triggers, environments, safe outputs and observability.
- **A goal is a workflow with one loop node; any node can be a goal.** Nesting with carved budgets and depth limits.

### 8.2 Goals (loop style)

```fsharp
type Goal =
  { Id: GoalId; Objective: string
    Verify: Check list; Constraints: Constraint list
    Budgets: Budgets
    Context: Continue | Compact | FreshHandoff     // default: FreshHandoff
    Progress: ProgressSpec
    Stop: StopRules }

and Check =
  | Command   of argv: string list * expect: ExitCode
  | Hidden    of CheckRef
  | Evaluator of AgentRef * rubric: string
  | Judgment  of PointRef
  | Ext       of ExtensionRef

type GoalStatus =
  | Active | Paused | BudgetLimited | Stuck of Signal
  | Blocked of DecisionId list | Impossible of Evidence | Complete of Evidence
```

- **Authority:** the model only *proposes* completion; the harness runs `Verify`. `NeedInput` → **Blocked** (decision). Impossible requires evidence. Budgets/pause/resume are system/human; **raising a budget is a grant**.
- **Default context policy: `FreshHandoff`** (Ralph/Amp style); evals may override per task class.
- **Progress:** structured plan (`fx.goal.mark` sticks only if checks pass), progress log, checkpoint commit per iteration.
- **Stop rules in code:** no tool calls, empty diff, same failing checks, repeated fingerprints, atomic budgets, min/max iterations → `Stuck` → decision.
- **Anti-reward-hacking:** protected paths (sandbox-enforced where a sandbox is on; type-level otherwise, e.g. on Windows v1), pinned verifier, hidden checks, separate evaluator, tamper metrics.

### 8.3 Workflows (graph style)

```fsharp
type Node =
  | Agent   of AgentRef * input: Expr
  | Goal    of Goal
  | Run     of Command
  | Script  of FnRef
  | Decide  of PointRef
  | Wait    of WaitSpec                  // Approval (grant) | Choice (ledger decision) | Timer | Event
  | Sub     of WorkflowRef * inputs: Expr
  | FanOut  of items: Expr * body: NodeId * join: JoinPolicy * concurrency: int
  | Loop    of body: NodeId list * until: Predicate * max: int
  | Ext     of NodeKindRef

type Workflow =
  { Id: string; Version: string
    Inputs: Codec<Json>; Outputs: Codec<Json>
    Nodes: Map<NodeId, NodeSpec>; Edges: Edge list
    Budgets: Budgets; Isolation: IsolationSpec
    SafeOutputs: SafeOutputSpec list; Triggers: TriggerRef list }

and NodeSpec = { Node: Node; Context: Fresh | Shared of NodeId | Resume of NodeId
                 Env: EnvRef option; Budgets: Budgets option; Retry: RetryPolicy }
```

- Agent and goal nodes run as child sessions; resume by replay; **`Wait` is durable** (`Approval` → grant, `Choice` → decision); branching only on typed outputs; per-node environments.

### 8.4 Authoring workflows: three forms, one IR

1. **Markdown** — `.fx/workflows/<name>.md`, exposed as `/commands`.
2. **TypeScript** — run by the **host-side deterministic workflow worker** (`Date`/`Math.random` throw, no direct fs/network, every step journaled). Model-authored workflows from cells (`fx.workflow.run`) are submitted here.
3. **F#** — `eff { }` workflows from extensions.

```markdown
---
name: fix-ci
on: { manual: {}, webhook: { secret: "secret:ci-hook" } }
inputs: { run_id: string }
budgets: { usd: 15, wall: 2h }
isolation: { env: cloud, worktree: true }
outputs: { create-pull-request: { max: 1, approval: grant } }
nodes:
  logs:     { run: "gh run view ${{ inputs.run_id }} --log-failed" }
  classify: { agent: explorer, input: { logs: "${{ logs.output }}" }, output: FailureKind }
  rerun:    { run: "gh run rerun ${{ inputs.run_id }} --failed", when: "classify.kind == 'flaky'" }
  fix:      { goal: { objective: "CI failure fixed", verify: ["dotnet test"], protect: ["tests/**"] },
              when: "classify.kind == 'real'" }
  review:   { agent: reviewer, input: { diff: "${{ fix.diff }}", intent: "fix CI" }, needs: [fix] }
  approve:  { wait: { approval: "Open PR with this fix?" }, needs: [review] }
  pr:       { output: create-pull-request, needs: [approve] }
---
Keep changes minimal. Never weaken or delete tests.
```

```ts
export default workflow({ name: "implement-review-qa", input: c.object({ task: c.string() }) },
  async ({ input, run, loop, goal, wait, safe }) => {
    const impl = await run(implementer, { task: input.task })
    const review = await loop({ max: 3, until: r => r.verdict === "approve" }, async () => {
      const r = await run(reviewer, { diff: impl.diff, intent: input.task })
      if (r.verdict === "changes") await run(implementer, { findings: r.findings }, { context: { resume: impl } })
      return r
    })
    await goal({ objective: "all tests pass", verify: [cmd("bun test")], protect: ["tests/**"], budget: { usd: 5 } })
    await run(qa, { change: impl })
    await wait.approval("Open PR?")
    return safe.createPullRequest({ title: input.task, body: review.summary })
  })
```

### 8.5 Triggers and safe outputs

- **Triggers** (extensions): manual, cron, webhook (payload = **untrusted data**), file events, ledger events, run events; concurrency/overlap/rate-cap/deadline policies. **GitHub-event triggers are out of scope for now.**
- **Safe outputs:** workflow agents get no direct external write capability; `ISafeOutputs` records intents (`max:`, schema, staged mode); a deterministic final stage applies them after policy checks and an optional approval **grant**.

### 8.6 Templates (bundled)

`ralph`, `implement-review-qa`, `spec` (requirements → design → tasks → implement with gates), `fix-ci` (manual/webhook trigger).

### 8.7 How fx's own work enters fx

Interactive sessions; **specs in the repo → goals** (Ralph-style plan files).

---

## 9. Environments (extensions)

### 9.1 Contribution types

- **Environment providers** — built-in `fx.env.local`, `fx.env.ssh`, `fx.env.docker`; plugins: E2B, Daytona, Modal, Sprites, Morph, Apple containers, Kubernetes, devcontainers, Codespaces, OpenShell, benchmark task containers.
- **Sandbox drivers** — built-in none, bubblewrap+seccomp, Seatbelt; **opt-in**: the host's privileged `sandbox.driver` is `none` by default; **Windows has only `none` in v1** (AppContainer later); plugins: nsjail, gVisor.
  - **Why opt-in** (a design change, T137): dogfooding (T132) showed that a sandbox with a read-only home and unreadable credential files keeps ordinary tools from working: those that keep state under `~` (NuGet's packages, Cargo's registry, the MSBuild server) and those that fail on an unreadable config rather than ignoring it (NuGet on `~/.nuget/NuGet/NuGet.Config`, Yarn Berry). Moving each tool's home with variables was a list of special cases that kept growing; mainstream agents run without an OS sandbox, with permission prompts as the control, and Windows v1 ran unsandboxed anyway. The general fix, a copy-on-write home per environment (the real home as a read-only lower layer, credential paths absent), comes later (T512).
  - The host wraps every process it starts for an agent (commands, terminals, kernels, setup); envd itself is not sandboxed (it may run in the host's process, keeps its own state, and serves agents with different policies). A policy gives paths by access (the workspace, owned and temporary directories writable; credentials and the product's data directory denied; git hooks/config and `.fx/` protected) and the network (`open` or `none`; allowlists come with the egress proxy).
  - The privileged `sandbox.driver` chooses: `none` (the default: off, with no warning; clients show `sandbox: off`), `auto` (the OS's driver, falling back to `none` with a persistent warning where it cannot run), or a driver's id (required: the environment fails where it cannot run). Only host config, the command line and policy select it, never a project; specs and agents (`SandboxOverrides`) only tighten. The file bindings, which run in envd, are checked against the same policy, sandbox or not, and resolve paths beneath the policy's writable roots without following symbolic links (T113b), so a link a sandboxed process makes there cannot lead them elsewhere, however late it appears. With a sandbox on, what changes git's config or hooks fails (they are protected), as do commits in a linked worktree and tools that write under `~` or read a credential-listed file (`sandbox.writable` helps the former). Details: `docs/modules/sandbox.md`.

### 9.2 Provider contract

```fsharp
type TransportKind = InProcess | Piped | Direct | Dialback

type ProviderCaps =
  { Transports: TransportKind list; Suspend: bool; Fork: bool
    Snapshots: SnapshotKind list; Materialize: MaterializeKind list
    Prebaked: bool; Gpu: bool }

type EnvProvider =
  { Kind: string; Caps: ProviderCaps; Config: Codec<Json>
    Create   : EnvSpec -> Task<Lease>
    Platform : Lease -> Task<Platform>
    Exec     : Lease * Argv * StdioMode -> Task<ByteStreams>
    Upload   : Lease * hostFile: string * path: string -> Task
    Connect  : (Lease -> Task<Endpoint>) option
    Suspend  : (Lease -> Task) option;  Resume: (Lease -> Task) option
    Snapshot : (Lease -> Task<ProviderSnapshot>) option
    Fork     : (Lease * int -> Task<Lease list>) option
    Destroy  : Lease -> Task
    List     : unit -> Task<Lease list> }
```

- Bootstrap: `Platform` → `Upload` `envd` → `Exec` `envd` → install components from the lockfile.
- `InProcess` is embedded mode (§3): envd runs in the host's process over the in-process transport, only for environments on the host's machine (no upload, no exec). `Exec` gives pipes (terminals are envd's); `Upload` copies a host file, as `scp` and `docker cp` do.
- Operations are effects over the provider's environment: the host machine's files and processes, through which providers reach their environments. A lease is named after the environment's id, so `Create` is idempotent and `List` is what the host reconciles with. Details: `docs/modules/environments.md`.

### 9.3 Workspaces

- Materialize: `existing | mount | git | upload | snapshot`.
- Export: `patch | branch | pr | download` after diff review.
- One-way, recorded.

### 9.4 Environment specs

```toml
[environments.dev]
provider = "local"
sandbox  = "bubblewrap"          # a spec may require a sandbox; absent: the host's sandbox.driver (default none)
network  = "allowlist:dev"

[environments.box]
provider  = "ssh"
config    = { host = "devbox.internal" }
sandbox   = "bubblewrap"
workspace = { source = "existing", path = "~/src/app" }

[environments.cloud]
provider   = "e2b"
config     = { template = "fx-node22", apiKey = "secret:e2b" }
workspace  = { source = "git", ref = "HEAD", export = "branch" }
components = { placement = "prebaked", fallback = "host-push" }
setup      = { run = ["bun install"], network = "open", secrets = ["npm"] }
policy     = { network = "allowlist:dev", idle = "suspend:15m", ttl = "8h", budget = "$5/day" }
variables  = { GH_TOKEN = "host:GH_TOKEN", NPM_TOKEN = "secret:npm", RUST_LOG = "debug" }
```

**Variables** (T138) are what an environment's commands get beyond the host's scrubbed variables: `host:<NAME>` (the host's own variable), `secret:<name>` (an fx secret, §11) or a literal, which config refuses for names that look like credentials and for runtime injection vectors. A person chooses them for every environment with the privileged variant `env.variables` (same syntax; it wins over a spec's for the same name), and removes names from any origin with `env.deny` (patterns). A spec from host config needs no consent; a project's credentials (credential-like names, `secret:` references) apply only with a person's grant (`env.credential`, `secret.read`, §13) for that start or that project, never the host. They reach commands (shell, terminals, setup), never kernels; values are resolved by the host each time envd starts and never persisted; the lifecycle records names, sources and outcomes. Credential values are redacted (`[redacted NAME]`) from everything the environment hands a turn and from model requests (§18). Details: `docs/modules/environments.md`, "Variables".

### 9.5 Lifecycle

`Requested → Provisioning → Bootstrapped → Connected → Ready ⇄ Suspended → Lost | Destroyed`; a failed step leaves it `Failed` (destroying it releases what it holds). Leases persisted and reconciled: after a host restart or a lost connection a provisioned environment is detached and starts envd again when next used; one the provider no longer has is `Lost`. Cost/TTL/idle tracked. Moves: same-provider snapshot, or cross-provider workspace transfer + kernel restart.

---

## 10. Models (extensions)

### 10.1 Contribution types

1. **Wire adapters** — chat: `openai.chat`, `openai.responses`, `anthropic.messages`, `gemini.*`, `bedrock.converse`; decisions: `decisions.typesafe|openai|openrouter`; embeddings. Core families are built-in F# (native runtime).
2. **Provider definitions** — endpoints, catalog, auth methods, quirks, transforms, identity profiles.

### 10.2 Contracts

```fsharp
type ModelKind = Chat | Decision | Embedding

type ModelInfo =
  { Ref: ModelRef; Kind: ModelKind; Family: string option   // the wire family that serves it
    Limits: Limits; Caps: ModelCaps; Cost: Cost option; PlanBacked: bool }

type ModelCaps =
  { Reasoning: ReasoningCaps option; Tools: ToolCaps; StructuredOutput: bool
    Logprobs: bool; Caching: CachingCaps; Media: MediaKind list
    Sampling: SamplingCaps; Transports: Transport list }

type ChatWire     = { Family: string; Stream: WireRequest -> Eff<WireCtx, ChatStream> }   // pulled event by event
type DecisionWire = { Family: string; Decide: WireCtx -> DecisionRequest -> Task<DecisionResponse> }
type EmbedWire    = { Family: string; Embed: WireCtx -> EmbedRequest -> Task<EmbedResponse> }

type ProviderDef =
  { Id: ProviderId; Name: string; Endpoints: Map<Family, Uri>; Catalog: CatalogSource
    Auth: AuthMethodRef list; Quirks: Quirks; Transform: RequestTransform option }
```

### 10.3 Canonical model

Typed parts + opaque `ProviderState` replayed byte-exact; recorded model-switch rules; capability-driven requests; cache mapping; typed errors (rate limit, mid-stream quota, overflow → compaction, refusal, transport); small tool surface; **every request passes the model-egress hooks**.

- Streams are pulled (`ChatStream = { Next; Close }`, like a process's output) rather than `IAsyncEnumerable`: nothing runs in the background, each pull is recordable, and nothing needs reflection under Native AOT.
- Wires get requests only as `WireRequest`, which only `ModelAccess` (the host's `ILlm`) makes, after fitting, replay rules and the model-egress hooks; a wire's HTTP reaches only its provider's endpoints and adds the credentials itself. Wire adapters pass a wire-conformance suite over recorded HTTP exchanges (cassettes). Module spec: `docs/modules/models.md`.

### 10.4 Catalog, probes, roles, routing

- Bundled catalog snapshot + live discovery; `fx connect` probes and records evidence.
- Roles: `main`, `fast`, `summarizer`, `review`, `judge`, `embed` + agent/extension roles (variant slots).
- Fallback chains; moving to a paid fallback is a **grant**.
- **Eval profiles are restricted to API-key routes.**

### 10.5 Subscription routes

| Plan | Support |
|---|---|
| **ChatGPT** | Built-in via **Sign in with ChatGPT** (adapter enforces SIWC constraints) |
| **GitHub Copilot** | Built-in, **on by default**, via borrowed identity profiles (§10.6) |
| **Coding plans** (Z.ai, Kimi, MiniMax, Alibaba) | Not supported — use these vendors' regular APIs (e.g. `openai-compatible`) |
| **Claude / Gemini subscriptions** | Not supported — use API / Bedrock / Agent Platform / Foundry |

### 10.6 GitHub Copilot provider (`fx.model.copilot`)

```fsharp
type CopilotIdentity =
  { Name: string; ClientId: string
    TokenMode: Exchange | Direct; IntegrationId: string
    Headers: (string * string) list }
```

- Profiles: `vscode-chat` (Pi-style: editor-plugin OAuth app, `v2/token` exchange, VS Code Copilot Chat headers) and `copilot-cli` (omp-style: GitHub token directly, CLI headers). Profiles are contributions; we don't borrow OpenCode's app; no registered `fx` identity planned.
- Profile switching on 403 / `model_not_supported` (cached per account); device flow (GitHub or GHE); host is the single refresher; endpoint discovery via `copilot_internal/user`; routing by `supported_endpoints`; `X-Initiator` mirrors the official client.
- Good citizen: never silently enables models (enabling is a **grant**), hides policy-disabled models, enforces **content exclusions**.
- UX: labelled "GitHub Copilot (unofficial client identity)"; premium-request usage shown; typed errors.

---

## 11. Identity, credentials and secrets (extensions)

### 11.1 Two kinds of identity

| Kind | Question | Contributions |
|---|---|---|
| **Principal identity** | Who is connecting? | `IdentityProvider` |
| **Service credentials** | How do we authenticate to external services? | `AuthMethod`, `CredentialStore`, `SecretSource`, provider identity profiles |

### 11.2 Principals

```fsharp
type Principal =
  { Id: PrincipalId; Display: string; Kind: User | Device | Service
    Provider: string; Claims: Map<string, string list> }

type IdentityProvider =
  { Id: string
    Login  : (FormValues -> Task<AuthFlow>) option
    Verify : Credential -> Task<Result<Principal, AuthError>>
    Refresh: (Credential -> Task<Credential>) option
    Roles  : Principal -> Task<Role list> }
```

Built-in: `local-os-user`, `device-pairing`, `ssh-key`. Plugins: GitHub OAuth, OIDC. Ledger and grant events carry the principal; git-ledger events also record the git author.

- **Principal ids** are `<kind>:<provider>:<subject>` (`user:local-os-user:<machine>/<uid or SID>`): stable, recorded for ever, and they say what kind of principal acted without a lookup. Only `user` principals answer, grant and revoke (§13.2); a person signing in through a device is a user.
- **`Verify`** gets the client's presented credential with what the host itself established about the connection (in process; the peer's OS account on Unix sockets and named pipes; possession of the listener's owner-only token) and the host's own identity. Providers never mint tokens: the host issues the session token (random, kept hashed, expiring, revocable), bound to the principal, the client and its roles.
- **Roles** are ordered: `viewer` < `collaborator` (drives sessions and runs) < `owner` (also permissions and the host). Module spec: `docs/modules/identity.md`.

### 11.3 Service credentials

```fsharp
type AuthFlow =
  | Browser    of url: Uri * callback: CallbackSpec
  | DeviceCode of userCode: string * verifyUri: Uri * poll: PollSpec
  | Paste      of instructions: string

type ResolvedAuth =
  { Headers: (string * string) list; Query: (string * string) list
    Sign: (HttpRequestMessage -> Task) option; ClientCert: CertRef option }

type AuthMethod =
  { Kind: string; Label: string; Prompts: Form
    Authorize: (FormValues -> Task<AuthFlow>) option
    Complete : FlowResult -> Task<StoredCredential>
    Resolve  : StoredCredential -> Eff<IAuthEnv, ResolvedAuth> }
```

- Built-in methods: `api_key`, `env`, `command`, `oauth_pkce`, `oauth_device`, `sigv4`, `mtls`.
- Credentials live on the host; interactive flows run on a capable client and are forwarded; the host is the single refresher.
- As built, the record's functions are effects with expected failures (`CredentialError`): `Authorize` also returns the method's own state for `Complete` (kept on the host), `Complete` is optional (a method without it, like `env`, stores nothing), and `Resolve` gets the provider's settings with the stored credential and may return a renewed one for the host to store. Methods never touch the store. Module spec: `docs/modules/auth.md`.

### 11.4 Stores and secret sources

- Credential stores: `keyring` (Keychain, **CredMan/DPAPI**, Secret Service), `file`, `ephemeral`, plugins.
- Secret references (`secret:…`) only; sources: store, env, command, plugins.
- The host chooses the store once at startup (`credentials.store`: `auto` or a kind; privileged): `auto` takes the OS keyring when it can be used and otherwise the file, saying why; a named store that cannot be used is an error, never a silent fallback. Sources are asked in the order `secrets.sources` gives; the env source reads only the product's `SECRET_<NAME>` variables.

### 11.5 Rules

Privileged identity/secret extensions only from host config or policy; policy only tightens. Secrets reach an environment's commands only as a person chose (`env.variables` in host config, or a project's `secret:` variable with a `secret.read` grant, §9.4); the host resolves them when envd starts and never persists or journals their values.

---

## 12. Decision ledger (choices, per repository, git-native)

### 12.1 What belongs here

Choices among alternatives with rationale: design/implementation/plan choices, answers to agent questions, workflow `Choice` gates, goal blockers and stuck escalations, `OutcomeUnknown` recovery, capture. **Permissions go to the grants log (§13).**

### 12.2 Lifecycle (from Chopin)

- Frozen questions; options/criteria append-only.
- Only humans settle, reopen, discard, supersede, promote; agents open, suggest, add findings.
- History kept; idempotent opening per key; agent-drafted "why" (human text wins); findings with evidence; option previews and criteria matrix.
- Exactly-once delivery to the originating session or waiting run step.

### 12.3 Scopes and authority

- Scopes: `Session` (never committed) and `Repo(+path prefix)` (monorepo subtrees). No user or org scopes.
- **Repo decisions bind every agent in that repo/branch.** No ownership model yet: any trusted principal can settle; PR review is the control.
- Promotion Session → Repo is a human action.

### 12.4 Model

```fsharp
type Anchor =
  | CodeSpan of path: string * Span * Digest | Symbol of lang: string * name: string
  | PathGlob of string | Commit of string | SessionAt of SessionId * int64
  | RunStep of RunId * StepId | Related of DecisionId * Relation | Topic of string

type DecisionEvent =
  | Opened of key: Key * question: string * context: Markdown * response: Response
              * scope: Scope * anchors: Anchor list * by: Actor * origin: Origin
  | OptionAppended of Opt * Key | CriterionAppended of Criterion * Key
  | Suggested of Answer * rationale: string * by: Actor
  | FindingAdded of Finding | FindingSettled of FindingId * FindingStatus
  | Settled of Settlement * draftRevision: int
  | WhyDrafted of generation: int * string | WhySet of Why
  | Promoted of Scope | Reanchored of Anchor list | AnchorLost of Anchor
  | Reopened of UserId * reason: string option | Superseded of DecisionId | Discarded of UserId
  | ConflictResolved of chosen: EventHash * rejected: EventHash list * by: UserId

type StoredEvent =
  { SchemaVersion: int; Hash: EventHash; Parents: EventHash list
    Decision: DecisionId; Event: DecisionEvent; By: PrincipalId; At: Instant }
```

### 12.5 Persistence: `LedgerStore` extensions

```fsharp
type LedgerStore =
  { Id: string
    Append : DecisionEvent list -> Task<EventHash list>
    Read   : ScopeQuery -> IAsyncEnumerable<StoredEvent>
    Watch  : ScopeQuery -> IAsyncEnumerable<StoredEvent> }
```

- **Working store** (host-local, built-in): open decisions, suggestions, drafts, in-flight findings, Session-scope decisions.
- **Git store** (`fx.ledger.git`, built-in, same API): **only settled history** of Repo-scope decisions (settle/reopen/supersede/discard/conflict-resolution + question definition + findings at settle time).
- **Layout:** `.fx/ledger/<decision-id>/<event-hash>.json` — **one file per event**, content-addressed names, causal `parents`, no textual merge conflicts.
- **Branches:** decisions **follow the branch** (written to the working tree, committed with normal commits/PRs); others see them **only via git pull/merge**.
- **Merge conflicts:** causally unrelated `Settled` events → **Conflicted** until a human records `ConflictResolved`.
- **Opt-in:** git store used only if `.fx/ledger/` exists (`fx ledger init`); otherwise host-local.
- **Schema versioning:** every stored event carries `schemaVersion`; readers **upcast** old events; committed files are **never rewritten**; upcasters are conformance-tested.
- **History rewrites:** after rebase/squash/force-pull, fx compares git with the working store and **flags missing settled events with a warning** in the Decisions panel; no automatic restore.
- Other backends (e.g. team server) are extensions on the same API.

### 12.6 Use

- Projections: anchor/path-prefix/topic/full-text indexes; UI model; optional ADR rendering.
- `fx.ledger`: `relevant`, `open`, `ask`, `suggest`, `addFinding`, `check` — no settle.
- Baseline without judgments: anchors/paths/lexical retrieval, exact digest reuse, explicit capture, anchor-based staleness.
- Settled decisions can trigger runs.

---

## 13. Grants log (permissions)

### 13.1 What belongs here

Tool/command approvals, component installs, budget extensions, model enablement, paid fallbacks, environment cost and secrets, `.env` access, network opening, safe-output approvals, workflow `Approval` gates, optional first-time-provider exposure.

### 13.2 Model

```fsharp
type Grant =
  { Id: GrantId; Action: ActionName; Resource: Pattern
    Effect: Allow | Deny; Scope: Once of RequestId | Session of SessionId | Repo of RepoId * path: string option | Host
    By: PrincipalId; At: Instant; Expires: Instant option; Reason: string option
    Origin: Answer of RequestId | Direct of via: string }

type GrantEvent = Requested of GrantRequest | Granted of Grant   // a grant from an answer also answers its request
                | Denied of RequestId * PrincipalId              // "deny, just this once"
                | Revoked of GrantId * PrincipalId | Expired of GrantId
                | Closed of RequestId * CloseReason              // withdrawn, covered by a later grant, refused, timed out, session ended
```

- **Actions** are an open vocabulary (`shell.exec`, `fs.write`, `net.connect`, `component.install`, `budget.raise`, `model.enable`, `model.paidFallback`, `secret.read`, `env.credential`, `env.dotenv`, `safeOutput.apply`, `workflow.approval`, …; extensions add theirs). Each names its resource kind (path, command, name) and the scopes an answer may choose. **Patterns** are globs over canonical resources (`*` within a segment, `?`, `**` across segments, `\` escapes); allows match exactly, denies and policy ignoring case (fail closed).
- **Answers** choose a scope (*once / session / repo (+path) / host*, §19.4), filled in from the request's context, and may broaden the resource to a pattern that matches it. A request offers `repo` only when it names a repository: an environment's credential asked for by a project names the project (its directory), so a `repo` answer (or `fx grants allow … --scope repo` there) covers that project alone.
- Effective rules are a projection: deny wins, then most-specific grant, else ask. Policy only tightens: policy rules and gates can only ask or deny.
- **`GrantStore` is an extension point**; default host-local (a stream of the host's event store); a repo-policy store can use the same API.
- Agents can only **request** grants (`IGrants`: check, request, await, withdraw); people answer, grant and revoke through the client protocol (`grants/*`), or beforehand on the host's machine with `fx grants` as the OS user who owns the host. A request nobody can answer (a headless run without a terminal) is withdrawn by the host, never answered on a person's behalf, and requests are keyed by what asks (a call, an install attempt), so it is asked again next time. Module spec: `docs/modules/grants.md`.

### 13.3 Approval modes

Asking a person about every command is tiring (T132's dogfooding: "an experience more similar to opencode"). The CodeAct grants guard (`docs/modules/codeact.md`, "Approval modes") therefore has **auto approvals**: a command it can see stays in the workspace runs without asking anyone, **sandbox or not**. Seen to stay in the workspace means written plainly: nothing the shell expands, every argument and input in the workspace and not excluded from models, every working directory the line may be in inside it, not one of `codeact.grants.askCommands`, and a line the parser splits.

- **Still asked** (or refused): writes outside the workspace and to protected paths (`.git`, hooks, the project's directory: `fs.write` needs), reads outside it, network bindings (`net.connect`), first installs (`component.install`), lines the parser does not split (opaque: the guard cannot see what they reach), and `codeact.grants.askCommands` (privileged): downloads, remote shells, git's remotes, hosting CLIs, package installs and publishes, a shell's inline command line. Deny grants, policy rules and gates apply first: auto approvals cover only what nothing but "no grant applies" stands in the way of.
- **What it then trusts** (a design change, T134 after T137 made the sandbox opt-in, §9.1): without a sandbox, the agent's workspace commands run with the user's rights, as in opencode and other mainstream agents. Their arguments stay in the workspace, but the programs do not: a build, a test runner or a script the agent wrote can read the user's files (credentials included) and write anywhere the user can. Auto approvals trust the agent with that for commands that look local; the person sees each one on its activity line, and turns the mode on only on purpose.
- **A sandbox makes the same mode stronger, it no longer gates it.** With a driver that confines (`sandbox.driver = auto|bubblewrap|seatbelt`), what auto-approved commands do is held to the environment's policy: writes to the workspace and the environment's own directories, credentials hidden.
- **The network is the line either way:** the sandbox leaves it open unless the environment's spec closes it (§9.4 `network`), and without a sandbox nothing closes it, so an auto-approved command can send what it reads elsewhere. The ask list is a guardrail of known spellings, not a boundary (a script or a cell's own `fetch` reaches the network in any mode). Auto-approved commands with the network closed, and asking to reopen it, need the egress proxy (§18).
- **Who turns them on:** the host (`codeact.grants.mode = auto`, a privileged variant: host config or the command line, never a project or a session override), or a person for a scope with a grant of `approvals.auto` (the session from the TUI's switch, the host with `fx grants allow`, a repository once requests name one); a deny grant turns them off for its scope. The grant's resource is the environment's sandbox driver, `none` when nothing confines.
- **Policy forbids them:** a policy file that forbids `auto` for the mode (or pins it to another value) forbids them everywhere (the host makes it a deny rule on `approvals.auto *`), and a grants policy rule on resource `none` forbids them only where nothing confines the commands.
- **Yolo approvals** (a design change, T134b: the mode `off`, which checked nothing, becomes `yolo`, which a person can also switch on for a session): nobody is asked about anything, sandbox or not. What would ask (or, in `deny` mode, be refused for want of a grant) runs: commands of any kind, downloads and installs, writes and reads outside the workspace and to protected paths, network bindings, calls nothing can name in a grant, and first installs of components. Deny grants and policy still refuse: yolo approves what nobody decided, never what someone forbade. Turned on by the host (`codeact.grants.mode = yolo`) or by a person with a grant of `approvals.yolo` (the TUI's `/yolo` for the session, `fx grants allow` for the host; resource: the sandbox driver, `none` when nothing confines); a deny grant turns them off for its scope (`auto` and `ask` switch it off for a session). A policy file that forbids `yolo` or `auto` for the mode, or pins it to another value, forbids them (a deny rule on `approvals.yolo *`). The installer approves a first install on them when the host's mode or a host grant holds, or a session in the environment that asks has them.
- **Shown and recorded:** clients show the mode and, without a sandbox, that it is unsandboxed (`approvals: auto · unsandboxed`, `approvals: yolo · unsandboxed`); `fx run` says it once. What ran on auto approvals is never silent: the guard says how each call ran (auto approvals, or a person's answer by request key), the step journal keeps it on each host call, receipts carry it for clients (the TUI's activity lines say `auto` or `asked`), and the replay journal has it with the guard's decision.

---

## 14. Judgments (optional research capability)

```fsharp
type JudgmentPoint<'Input,'Outcome> =
  { Id: PointId; Version: int; Latency: Inline of budgetMs: int | Background
    Baseline : 'Input -> Eff<IJudgeEnv, 'Outcome>
    Prepare  : 'Input -> JudgeState
    Question : Question<'A>
    Policy   : Policy<'A, Verdict<'Outcome>>
    Advice   : Verdict<'Outcome> -> Annotation option
    Labels   : LabelSource list }
```

- Modes: `Off`, `Shadow`, `Advise`, `Enforce` (abstain/error/timeout → baseline).
- Backends from the model registry (decision models, LLM-as-judge, heuristics, cascades).
- Questions: yes/no, choice over unions (exhaustive criteria), score, records; applicative `and!` batches independent questions into one request; answers are distributions; policies are pure.
- Labels from ledger answers, grant outcomes, overrides, tests, reverts.
- **No kill criterion** — kept optional and continuously evaluated.
- Candidate points: ledger relevance/conformance, precedent, ask vs proceed, capture, permission gate, injection screening, compaction, stuck detection, goal verify, workflow routing, test triage.

---

## 15. Variants, observability, evaluation

- **Variants:** everything through `IVariants` — **action surface** (`cells-only`/`hybrid`), strategies, interpreter, render/previews/repo map, edit guard, value thresholds, compaction/budgets, judgments, ledger retrieval, model roles/providers, agents/goal policies/evaluators/workflows, UX modes, environment/sandbox, Windows shell. **Profile** = hashed selections + extension versions + conformance results + lockfile hash. Eval profiles: API keys only.
  - **Slots** are typed values (id, codec, default, choices) in one registry; extensions contribute theirs. Values resolve from layers: defaults < host config < trusted project config < session overrides < explicit (eval profile, then CLI). Policy only tightens: pins and forbids, and a selection that conflicts with them is an error, never silently overridden. **Privileged** slots (e.g. sandboxing) are selectable only from host config, explicit selections and policy. Config files are JSON (`variants` member) for now; a TOML reader can come later without changing resolution.
  - **Profile id** = SHA-256 of the profile's canonical JSON; profiles are stored content-addressed in the blob store. Every session, run and eval stream binds its profile id in its first event and again when an override changes it (each event's profile is the latest binding); the reads of each binding are journaled, so exact replay answers from them. Module spec: `docs/modules/variants.md`.
- **Observability:** event logs, local OpenTelemetry spans (run → step → session → turn → call), prompt and seen-range viewer. **No telemetry leaves the machine.**
- **Eval modes:** exact replay, partial replay, counterfactual forks, suite runs, pipeline comparisons.
  - **Exact replay** re-runs a session's turns on an environment (`ReplayEnv`) that answers from the session's journal: every capability call of its turns, hands and kernel processes, inputs as digests and values by blob hash, never secret values. The session's log is its input; what the turns write and send (context and step logs, model requests) is checked against the recording, and the first difference is reported. Code replays only if its path depends on what capabilities answered, never on timing. Module spec: `docs/modules/replay.md`.
- **First suites:** **SWE-bench Verified**, **Terminal-Bench 2.0**, **capability tests** (NOOA-style) via a benchmark-task environment provider.
- **Metrics (folds over logs):** success, tokens, cost, latency, turns, failed cells; interruptions, decision cards/hour, grant requests/hour; read ratio, unseen edits, retyped values; goal success, false completions, tampering, stuck rate; per-agent and per-node metrics; judgment calibration; install failures.
- **Continuous public dashboard:** scores, profiles and cost for every run.
- Tools: simulated user; `fx eval`, `fx replay`, `fx judgments report`; `fx test` (agent/workflow unit tests on replay/fake environments).

---

## 16. Extension system

### 16.1 Packages and runtimes

- **Manifest:** id/version, SDK and protocol versions, placement (`host | environment | client`), extension runtime, required components, capabilities, contributions, RPC contract, hash.
- **Extension runtimes:** core `declarative`, `native`, `process`; `bun` from `fx.exthost.bun`.
- **SDKs at launch:** `Fx.Sdk` (F#) and `@fx/sdk` (TypeScript); other languages via raw JSON-RPC. F# extensions that run as their own process add `Fx.Sdk.Process` (the protocol's server); built-ins see only `Fx.Sdk`. Schemas have one JSON wire form, so `@fx/sdk`'s codecs are compared with the F# ones exactly. Module spec: `docs/modules/sdk.md`.
- **Bootstrap order:** native and declarative (in the binary; native first, so contribution types they define are known to the rest) → components (installed only for admitted extensions: trust comes before installation) → process extensions → extensions on contributed runtimes (bun). Every extension is admitted (trust, pins, versions, conflicts) before any of its code runs, and its activation must provide exactly what its manifest declares.
- **Registry:** a machine whose state is a fold of its events (admitted, activated, failed, disabled); contributions are keyed by typed contribution types, which the core, host hook points and native extensions define. Module spec: `docs/modules/extensions.md`.

### 16.2 Contribution types

- **Core logic:** codecs/events, machines, capabilities, strategies, views, preview renderers, judgment points, **model-egress hooks**.
- **Agents and runs:** agents, workflows, goal templates, node kinds, check kinds, trigger sources, safe-output types.
- **Persistence:** `LedgerStore`, `GrantStore`.
- **Installation and execution:** `Component`, package/install methods, `RuntimeSpec`, `InterpreterProvider`, `ExtensionRuntime`, environment providers, sandbox drivers.
- **Models:** wire adapters, provider definitions, identity profiles.
- **Identity and secrets:** identity providers, auth methods, credential stores, secret sources.
- **Plumbing and UI:** intent interpreters, middleware, commands, renderers, UI parts.

Project-local `.fx/agents` and `.fx/workflows` require project trust.

### 16.3 Hook algebra

| Kind | Type | Combination |
|---|---|---|
| Gate | `'Intent -> Eff<_, Allow\|Ask\|Deny>` | `max` over Deny > Ask > Allow; commutative; errors = Deny |
| Transform | `'T -> Eff<_, 'T>` | Composed in declared priority order; an error fails the pipeline |
| Contribute | `'T list` | Concatenation |
| Observe | `'Evt -> Eff<_, unit>` | Parallel; cannot affect results; failures isolated |
| Interpret | `Interpreter -> Interpreter` | Middleware stack; in process only |

- Transforms are effects (not `'T -> 'T`) so that an extension process can implement them over the extension protocol; a pure function is `Transform.ofFunction`. Interpreters are records of functions that live in one process, so only native extensions contribute middleware.

### 16.4 Built-ins: bundled packages, same API

- Embedded in the binary, extracted with hash checks, loaded through the same path; can be disabled, replaced, overridden or forked.
- Enforcement: architecture tests (public SDKs only), transport parity, conformance suites, same versioning.
- **Packages:** `fx.runtime.bun`, `fx.exthost.bun`, `fx.interpreter.bun-ts`; `fx.env.local|ssh|docker`; `fx.sandbox.none|bubblewrap|seatbelt`; `fx.model.openai|anthropic|google|openrouter|copilot|openai-compatible` + adapters; `fx.identity.*`; `fx.auth.standard`; `fx.store.keyring|file|ephemeral`; `fx.ledger` (working store + `fx.ledger.git`); `fx.grants`; `fx.agents.core`; `fx.runs.templates`; `fx.triggers.core` (manual, cron, webhook, file, ledger, run); `fx.outputs.github` (PRs, comments, issues); `fx.egress.exclusions`; `fx.judgments`, `fx.policy`, `fx.memory`, `fx.compaction`, `fx.todo`, `fx.snapshots`, `fx.repo`, `fx.lsp`, `fx.mcp`, `fx.acp`.
- **Candidate plugins:** `fx.sandbox.appcontainer`, `fx.egress.redaction`, `fx.runtime.dotnet` + `fx.interpreter.fsi`, `python`, `monty`, cloud environments, secret sources, team ledger stores.

### 16.5 Trust

Pinned hashes; nothing project-local before trust; policy extensions unremovable. **Privileged contributions** only from host config/policy: components, runtimes, interpreters, extension runtimes, environments, sandboxes, models, identity, credentials, secrets, triggers, safe outputs, ledger/grant stores, egress hooks. Memory (agent-written, decays) is distinct from the ledger (human-written, authoritative).

---

## 17. Protocols and connectivity

- **Client protocol:** JSON-RPC 2.0 over WebSocket with binary frames; `initialize` carries identity + capability negotiation → session token; `_meta` / `_`-prefixed methods for extensions.
- **Method groups:** `session/*`; creation `goal/set`, `workflow/validate|run`, `agent/invoke`; `run/*` (get, subscribe, pause, resume, cancel, extendBudget, answer, attach, fork, relaunch); `trigger/*`; `ledger/*`; `grants/*`; `judgment/*`; `env/*`; `components/*`; `models/*`; `auth/*`; `blobs/*`; `pty/*`; `ext/<id>/*`; `eval/*`.
- **Events:** `session.*`, `run.*`, `step.*`, `decision.*`, `grant.*`, …
- **Fallbacks:** HTTP/SSE offset-read mirror; ACP adapter.
- **Environment protocol:** multiplexed streams with credit backpressure, sequenced output, blobs, installer operations, lease survival, transports `Piped | Direct | Dialback`. Output is numbered by byte offset per stream and resumable from any offset (processes outlive connections; overflow spills to a greppable file); apply-patch takes a structured edit list (line ranges plus the file's digest), the base of anchored edits (§6.8). Module spec: `docs/modules/envd.md`.
- **Kernel protocol:** §6.4.
- **Extension protocol:** the host starts an extension process and speaks JSON-RPC over its stdio: `extension/activate|invoke|call|deactivate`, every request naming the extension, so one process (e.g. `fx.exthost.bun`) serves many (§16.1).
- **Wire (all of the above):** own JSON-RPC 2.0; on byte streams (stdio) newline-delimited JSON plus zero-prefixed binary channel frames, so plain NDJSON peers interoperate; on WebSocket text messages for JSON and binary messages for channels; in-process for embedded mode, with the same serialization. `$/cancelRequest`; `initialize` announces message size, requests in flight, channel windows and the methods each side handles. Module spec: `docs/modules/rpc.md`.
- **Multi-client:** serialized + attributed inputs; epoch-fenced takeover lease.
- **Generated types:** TypeScript for clients, extensions, kernels, workflow worker; F# TUI and Fable web share `Evolve` projections.

---

## 18. Security

- **Trust boundary:** model providers are **outside**. Every model-bound payload (main loop, `fx.llm`, agents, summarizer, judgments) passes **model-egress hooks**: **content exclusions** built in (paths/globs + Copilot org exclusions); **secret redaction** is a policy extension point.
  - Request parts carry their **provenance** (the files their content quotes); exclusions match it, so whoever puts a file's content into a request names the file. Provenance never leaves; content without it (a command's output) is caught upstream (bindings, render), not at egress. Patterns only add up (no negations; lists from different origins combine by union), so policy and host lists cannot be undone by a project; render, bindings and egress share one matcher. Details: `docs/modules/egress.md`.
- **Credentials:** host-only; environments get scoped per-session tokens; secrets always referenced. Host-only means the model never receives them through fx (configuration refers to secrets; egress hooks see every request), not that an agent's processes cannot read files: without a sandbox they run with the user's rights and can read the user's credential files, fx's own credentials file in its data directory included.
- **Person-chosen credentials** (T138, §9.4): an environment's commands get a credential only when a person chose it (`env.variables`, host config) or granted a project's request for it. Values are resolved on the host when envd starts, never persisted, recorded by name only, and redacted by exact match (`[redacted NAME]`) from process and terminal output, kernels' output, files and search results the hands read (below the replay journal), and from model requests. Encoded copies (base64, URL or JSON escapes) are not caught (T601); values under 8 characters are not passed.
- **Sandbox drivers** are the process boundary **when a host turns one on** (opt-in, §9.1, T137). By default an agent's commands, kernels and setup run with the user's rights: they can read the user's credentials, write anywhere the user can and use the network. **Grants** (permission prompts for commands, writes outside the workspace, the network and installs; with auto approvals, §13.3, commands written plainly in the workspace run unasked) are then the primary control, with type-level attenuation, the bindings' policy checks and content exclusions as guardrails; Windows v1 has no confining driver at all. A sandbox a host asks for never falls back silently: `auto` warns where none can run, a named driver fails the environment.
- **Egress proxy:** deny-by-default, resolved-address allowlists, method/path rules, credential injection, git push checks, per-tool-call attribution, install source allowlists.
- **Supply chain:** vendor-index digests by default (pinned-digest policy available), package signatures, sandboxed `Script` installs, read-only `System` installs, lockfiles.
- **Trust before execution:** project config, extensions, agent/workflow definitions, `.fx/ledger` loading, runtime configs and hooks wait for project trust; `LaunchSpec` must deny runtime injection vectors; localhost listeners require token + `Origin` check (and a `Host` check against DNS rebinding; no CORS).
- **Runs:** safe outputs as object capabilities, protected paths, hidden checks, separate evaluators, untrusted trigger payloads, atomic budgets.
- **Grants** gate sensitive permissions; the **ledger** records choices and committed decision files are PR-reviewed.
- **Compliance in code:** SIWC constraints; Copilot content exclusions and model policy; no credential pooling or proxying; evals use API keys only.
- **Kernel boundary:** unforgeable handles; declared-type decoding.
- **Guardrails are not boundaries.**

---

## 19. End-to-end user experience

### 19.1 Principles

Legible; verifiable endings; steerable anytime; fewer, better questions (choices and permissions separated); never lose work; fast and quiet; compatible with existing setups.

### 19.2 A turn in the TUI

```
› replace deprecated Http.retry with RetryPolicy in billing    env: dev · sandbox: bwrap · claude-opus (API)

  ◇ Refs     Http.retry → 37 matches in 14 files
  ◇ Read     Http.retry, RetryPolicy (symbols)
  ◇ Applies  D-12 "Tests use the fake clock, never Thread.Sleep"   (repo · settled on main)
  ◆ Edit     14 files, 37 edits
  ◇ Run      dotnet test tests/Billing           ✗ 2 failed   48s
  ◆ Edit     14 files, 37 edits
  ◇ Run      dotnet test tests/Billing           ✓ 214 passed 46s
  ◇ Agent    reviewer → approve (0 findings)                   $0.21
  ▸ 7 cells (ts, 58 lines)                                       [expand]

  ✓ Done  Migrated 37 call sites to RetryPolicy; converted attempts→retries.
    Evidence  dotnet test tests/Billing → 214 passed · review: approve
    Verify    dotnet test tests/Billing
    Changes   14 files +41 −37               [d]iff  [u]ndo  [r]eview
```

- Verbosity levels, with single items (a cell, an activity line, a result card) expanded or collapsed on their own over them; `// intent:` line per cell; typed endings drive the UI; steer vs queue keys; `Esc` interrupts.

### 19.3 Runs panel (goals, workflows, model-authored workflows)

```fsharp
type RunView =
  { Id: RunId; Kind: Goal | Workflow | Adhoc; Title: string; Status: RunStatus
    Budgets: BudgetUsage; Checks: CheckSummary option; Root: StepView; Edges: Edge list }
and StepView =
  { Id: StepId; Label: string
    Kind: Agent of string | Loop | Iteration | Run | Script | Decide | Wait | Sub | FanOut | Ext of string
    Status: RunStatus; Cost: Cost; Time: Interval; Children: StepView list
    Session: SessionId option; Gate: GateRef option; Verdicts: Verdict list }
and RunStatus =
  | Active | Paused | Waiting | Blocked | Stuck | BudgetLimited
  | Failed | Impossible | Complete | Cancelled
```

```
 Run  ▸ fix-ci #42 (workflow)        ● Active   $3.10 / $15   1h02 / 2h   checks 212/214
 ─────────────────────────────────────────────────────────────────────────────────────
  ✓ logs        gh run view … --log-failed                          4s
  ✓ classify    explorer → FailureKind = real                  $0.08   41s
  ⊘ rerun       skipped (classify.kind ≠ flaky)
  ● fix         goal: "CI failure fixed"            iter 3/40   $2.90
  │  ✓ iter 1   ✗ 3 failing → verify: dotnet test               $0.70
  │  ✓ iter 2   ✗ 2 failing · evaluator: "test weakened?" no     $1.10
  │  ● iter 3   running · session s-91f   [a]ttach
  ◻ review      reviewer (waiting on fix)
  ◻ approve     waiting on approval "Open PR with this fix?"       [answer]
  ◻ pr          safe output: create-pull-request (max 1)
```

- Timeline and Graph layouts (`Tab`); controls on any run/step: pause, resume, cancel, extend budget (grant), attach, answer, fork, relaunch.
- `/goal` (assisted spec card; FreshHandoff by default), `/workflow`, `/runs`, status-line run summary.

### 19.4 Decisions panel vs grants view

- **Decisions panel:** open, settled, **Conflicted** and **history-lost** decisions; scope (Session / Repo + path) and branch filters; history, why, findings, criteria; promote; resolve conflicts; uncommitted-ledger indicator.
- **Grants view:** compact permission prompts (*once / session / repo / host*); active grants with revoke; batched pending requests.
- **Approval mode** (§13.3): the footer shows how the session's commands are decided (`ask`, `auto`, `deny`, `off`; `auto · unsandboxed` when no driver confines them); `Shift+Tab` or `/approvals` switches auto approvals for the session (a person's grant, sandbox or not; refused with the reason where policy forbids them), and switching on without a sandbox says what it means; activity lines say which commands ran on auto approvals and which a person allowed.

### 19.5 Features

- Review and undo: diffs, two-phase undo, forks, review mode.
- Agents: `/agents`, `@agent`, primary-agent picker, badges.
- Plan → decide → implement: decision anchors move from plan to code; settled decision files are committed with the code.
- Context transparency: `/context`.
- Sessions: persist across disconnects (`fx attach`), pairing, attention inbox (sessions + runs), subagents as child sessions, jobs/ports, notifications, serialized multi-client inputs.
- Environments: picker, `/env`, setup logs, cost/TTL, export, the sandbox in the info line (`off` by default), an "unsandboxed" banner when a requested sandbox cannot run.
- Models and auth: `fx connect`, `fx auth …`, `/model`, Copilot device-code login, plan usage, fallback chains.
- Extensions and components: `fx ext …`, `fx runtime …`, install prompts (grants), `fx grants allow|deny|revoke|list` to decide beforehand (unattended runs).
- Authoring: `fx agents …`, `fx workflow …`, `fx runs …`, `fx trigger …`, `fx test`, `fx ledger init|status`.
- Evals: `fx eval`, `fx replay`, dashboard publishing.
- Onboarding: single binary for Linux/macOS/Windows, trust prompt, imports of AGENTS.md / CLAUDE.md, skills, MCP config, Claude Code / OpenCode agents.
- Other surfaces: ACP, VS Code, `fx run -p … --json`, GitHub Action (manual/webhook), SDK, `fx trace`.

### 19.6 Experience choices that are variants

Action surface (`cells-only` / `hybrid`), TUI style (`tui.theme`: `auto` / `dark` / `light` / `ansi16`), edit mode, plan mode, edit guard, interpreter, goal context policy (default FreshHandoff), reviewer model diversity, Windows shell.

---

## 20. Tech stack and engineering rules

| Layer | Choice |
|---|---|
| Toolchain | .NET 11 SDK / F# 11, Native AOT, pinned `global.json` |
| Binary | One `fx` binary (CLI, host, envd, TUI) + bundled packages; Kestrel `CreateEmptyBuilder` + `UseKestrelCore` + `RequestDelegate` (not `CreateSlimBuilder`: it reads `appsettings.json` from the working directory; see `docs/spikes/kestrel.md`) |
| Platforms | Linux (glibc + musl), macOS, **Windows** (x64/arm64); ConPTY, CredMan/DPAPI, PowerShell 7 default shell |
| Protocols | Own JSON-RPC 2.0; WebSocket + SSE; kernel protocol over stdio |
| Contracts / SDKs | F# contracts (Fable-compatible); **codec combinators first**; `Fx.Sdk` (F#), `@fx/sdk` (TypeScript) |
| Models | Built-in F# wire adapters |
| OS integration | `Process`, PTY (`/dev/ptmx` + `posix_spawn`) and ConPTY in our own `Fx.Pty` (no third-party PTY library: see `docs/spikes/pty.md`), `FileSystemWatcher`, git CLI, tree-sitter, static SQLite (pinned amalgamation built from source, FTS5 and JSON; see `docs/spikes/sqlite.md`), OS keychains in our own `Fx.Keyring` (Security.framework, Credential Manager + DPAPI, Secret Service over our own minimal D-Bus client rather than libsecret; see `docs/spikes/keychains.md`), archive extraction |
| CodeAct / workflows | `fx.runtime.bun` + `fx.interpreter.bun-ts`; host-side workflow worker |
| Clients | Own Elm-style F# TUI (v1); Fable web; Tauri desktop; ACP |

**Agent-built engineering**
- **Agent style guide** (`AGENTS.md`): F# idioms and functional-core rules, module layout, banned APIs (`%A`, printf format specifiers, runtime reflection, I/O outside `Eff`), Native AOT rules, Windows path/shell rules.
- **Custom F# analyzers** enforce the guide in CI.
- **Spec per module and per contribution type**; conformance suites are the definition of done.
- **fx builds fx from phase 1** (interactive sessions + spec-driven goals).

**Naming**
- `fx` is a working name (it collides with the antonmedv/fx JSON viewer; accepted for now).
- The name lives in **one build-time constant**: product name, binary name, `.fx/` and `~/.fx` directories, kernel global `fx`, SDK package names, `FX_*` environment variable prefix and protocol namespaces are all derived from it. Generated files (`fx.d.ts`, SDKs) pick it up. A rename = change the constant + a directory migration with a read-old-name grace period; ledger upcasters handle path changes.

**CI gates:** analyzers; AOT publish on Linux/macOS/Windows; JIT-vs-native diff; `Eff` determinism lint; replay tests; architecture tests; transport parity; conformance suites (incl. ledger upcasters); wire-adapter conformance; lockfile reproducibility; workflow IR validation; `fx test` suites; ledger merge and history-rewrite tests.

**Project:** MIT, public repo, no telemetry, continuous public eval dashboard.

---

## 21. Roadmap

0. **Feasibility spike** — native builds on all three OSes; extension loader + bootstrap; component installer (`Archive`); kernel protocol + `bun-ts` prototype; provider bootstrap against Docker; ConPTY + PowerShell on Windows.
1. **Core, usable for building fx with fx** — event store, machine host, run kernel skeleton, session actor (serialized/attributed inputs), JSON-RPC, minimal TUI; CodeAct loop with typed results and **both action surfaces (`cells-only`, `hybrid`) as variants**; variants, recording, replay, blob journal; **all built-ins as bundled packages**: local environment (bubblewrap / Seatbelt / none on Windows), local identity, standard auth, stores, Bun runtime + exthost, `bun-ts`, `anthropic.messages` + `openai.responses`, content-exclusion egress hook, grants store with permission prompts; `OutcomeUnknown` recovery; codec combinators; conformance suites, parity tests, lockfile; agent style guide + analyzers.
2. **Agent quality, core UX, models, agents, goals, evals** — values/references, previews, navigation, edit guard; cache-stable render, activity, diffs, undo, steering, compaction; `openai.chat`, catalog/probes, SIWC, **Copilot (on by default)**; install methods (vendor-index default), mirrors, bundles; **agents**, **goals (FreshHandoff default)**, **Runs panel**; **eval harness**: SWE-bench Verified, Terminal-Bench 2.0, capability tests, `fx eval`, `fx test`, **public dashboard**.
3. **Decision ledger and workflows** — working store + `fx.ledger.git` (one file per event, follows branches, Conflicted, opt-in, schema upcasters, history-rewrite warnings), Decisions panel, path-prefix scopes; workflow IR, Markdown workflows, durable `Approval`/`Choice` gates, graph layout.
4. **Judgments and automation** — judgment points, modes, records, labels; TypeScript workflow worker and model-authored workflows; triggers (manual, cron, webhook, file, ledger, run); safe outputs; templates.
5. **Distribution and teams** — SSH and Docker providers, materialize/export, `host-push`/`prebaked`; relay + Dialback; pairing, GitHub OAuth, OIDC; multiple clients and roles, web client, attention inbox; cloud model routes, auth forwarding, mTLS; first cloud environment plugin; **AppContainer sandbox for Windows**.
6. **Ecosystem** — `fx.egress.redaction`; `fsi`, `python`, `monty` interpreters; memory, MCP, ACP; secret sources, team ledger stores; more environments, LSP components, agent teams; desktop.

---

## 22. Open questions

1. **Ownership model for Repo-scope decisions** (deferred): who may settle, reopen or resolve conflicts once teams use fx.

**Decided by evals rather than upfront:** action surface, goal context policy per task class, interpreter choice, edit-guard mode, reviewer model diversity.

---

## 23. Main risks

- **TS/Bun kernel without snapshots** → workspace snapshots, journal, recall, Jupyter semantics, interpreter variants.
- **Under-reading** → previews, edit guard, stale anchors, evidence, read metrics.
- **Long-horizon goal failures** (reward hacking, false completion, runaway loops) → harness verification, protected paths, hidden checks, evaluators, stop rules, atomic budgets, FreshHandoff.
- **Workflow determinism bugs** → deterministic worker, IR validation, replay tests.
- **Subagent sycophancy** → fresh context, declared inputs, model diversity, false-approve metrics.
- **Git-native ledger edge cases** (merges, rebases, many files, conflicts surfacing after pull, public rationale, schema evolution) → one file per event, causal parents, Conflicted, opt-in, upcasters, history-rewrite warnings, merge tests, PR review.
- **No ownership model** → any trusted principal settles repo decisions; PR review until the deferred model lands.
- **No OS sandbox by default** (opt-in since T137; Windows has none in v1) → grants (permission prompts), type-level attenuation, egress hooks; the drivers for hosts that turn them on; a copy-on-write home (T512) so a sandbox stops breaking tools, AppContainer in phase 5.
- **Agent-built F# / Native AOT** → style guide, analyzers, specs + conformance suites, JIT-vs-native diffs.
- **Borrowed Copilot identity on by default** (account flagging, header drift) → profile switching, header overlay, clear labelling.
- **Secret leakage to providers** (exclusions only built in) → egress extension point, `.env` grant.
- **Vendor-index digest trust** → pinned-digest policy available, lockfiles.
- **Node-specific model code** → clear errors, `fx.*` alternatives.
- **Subscription terms changing** → routes are isolated extensions.
- **Judgments may never pay off** → optional, continuously measured.
- **Install complexity** → data-driven methods, lockfiles, bundles, conformance.
- **Cloud environment variety** → minimal contract, negotiated capabilities.
- **Extensions-everywhere overhead** → native runtime for hot paths, parity tests, fixed bootstrap order.
- **Bun dependency** → pinning, OS sandbox, swappable runtimes.

---

## Key references

- NOOA: github.com/NVIDIA-NeMo/labs-OO-Agents · arXiv 2607.20709
- Chopin decisions: github.com/githubnext/chopin/blob/main/docs/decisions.md
- TypeSafe Jev / System One · OpenAI Decisions · OpenRouter Decisions
- Codex goals · Claude Code `/goal`, workflows · Ralph · LangGraph · Archon · Mastra · Kiro · Spec Kit
- Anthropic long-running harness posts · METR reward hacking · ImpossibleBench
- Sign in with ChatGPT for open-source apps · Copilot providers in Pi and oh-my-pi · OpenCode V2 · Codex app-server / exec-server · ACP · MCP 2026-07-28
- SWE-agent ACI · Aider repo map · Agentless · SWE-bench Verified · Terminal-Bench 2.0
- Bun 1.4 docs · .NET release metadata · Zed / VS Code remote server bootstrap
