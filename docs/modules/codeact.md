# Module spec: the CodeAct strategy (`src/Fx.CodeAct`)

Spec: §4.3–4.5 (functions, machines, strategies, turn results), §5 (sessions, steering at step
boundaries, prompt = pure render), §6.5 (kernel behaviour: host bridge, finishing, activity), §6.9
(action surfaces), §6.10 (example cells), §10 (models), §13 (grants), §15 (variants), §18 (egress).
Tasks: T127, T128 (crash recovery). Builds on [sessions.md](sessions.md) (the turn-runner boundary), [prompt.md](prompt.md)
(the render and context events), [models.md](models.md) (`ILlm`), [interpreters.md](interpreters.md)
(the bridge and kernels), [kernel-protocol.md](kernel-protocol.md), [egress.md](egress.md) and
[environments.md](environments.md).
Code: [`src/Fx.CodeAct`](../../src/Fx.CodeAct) (pure apart from the capabilities it is handed; the EFF
analyzers apply to every file).
Tests: `tests/Fx.CodeAct.Tests` and the `codeact-*` scenarios of the JIT-vs-native diff.

The CodeAct strategy runs a session's turns: it is the `TurnRunner` behind `SessionHost`. The model acts
by writing cells (code for a kernel that keeps its state across cells and turns) and, on the hybrid
surface, by calling a few direct tools; a turn ends only with a result the harness checked.

| Piece | Where |
|---|---|
| Variant slots and settings (`CodeActSlots`, `CodeActSettings`, the policies) | `Settings.fs` |
| Where context events live, and the merged history (`ContextLog`, `History`) | `ContextLog.fs` |
| The action surfaces as tools (`Tools`, `ToolAction`) | `Tools.fs` |
| The step journal (`StepEvent`, `StepCodecs`, `StepJournal`, `JournalWriter`) | `Journal.fs` |
| Crash recovery: the call in doubt, the decision card (kind `decision.outcomeUnknown`), its receipts (`Recovery`) | `Recovery.fs` |
| The agent's questions (`Questions`, kind `agent.question`; `Questions.kinds` has every kind turns use) | `Questions.fs` |
| The loop guard (`LoopGuard`) | `LoopGuard.fs` |
| What the model is told (`Instructions`: sections, notes) | `Instructions.fs` |
| Receipts (`Receipts`, `Reads`, `KernelNote`) | `Receipts.fs` |
| Kernels per session, the host's side of the protocol, `Ensure` (`SessionKernel`, `Interpreter`, `Hands`, `Watch`, `KernelJournal`) | `Kernels.fs` |
| Cells' values kept for `recall` (`Recall`: keep, restore, the `recall` binding) | `Recall.fs` |
| The hybrid tools over the bridge's bindings (`Hybrid`) | `Hybrid.fs` |
| The turn runner (`CodeAct`, `CodeActConfig`) | `CodeAct.fs` |
| Hands in an environment opened through `IEnvs` (`EnvHands`, `KernelPlan`) | `Environment.fs` |

## Composition

```fsharp
let codeact =
    CodeAct.create
        { Sessions = SessionLog.typed store.Log      // the session events
          Contexts = ContextLog.typed store.Log      // the context events, same store
          Steps    = StepJournal.typed store.Log     // the step journal, same store
          Blobs    = Some blobs                      // the host's blob store (large kept values)
          Profile  = fun profileId -> …              // the variants of a turn's profile
          Hands    = fun turn -> …                   // the session's environment and interpreter
          Catalog  = fun modelRef -> …  }            // ModelInfo, for the render's cache mapping
let! host = SessionHost.create sessions snapshots codeact.Runner
                { SessionSettings.defaults with Kinds = Questions.kinds }
```

- The turn runs in the host's environment `'R` (`IClock`, `IScopeWith`, `ILlm`: the `Llm` that
  `ModelAccess.llm` makes, so every request passes the model-egress hooks). Kernels and bindings run in
  the hands' environment `'K` (`IShell`, `IFs`, `IFsOps`, `IClock`, `IScopeWith`): envd's capability
  records through `EnvClient` (`EnvHands.ofHandle` over an `EnvHandle` from `IEnvs.Open`), or the local
  ones in embedded mode.
- The session host must know the kinds `agent.question` and `decision.outcomeUnknown` (`Questions.kinds`), and the host's registry the
  slots a turn reads (`CodeActSlots.read`: the core's, the render's, content exclusions' and the
  strategy's). Both are the composition root's (T129).
- `codeact.Close()` closes every session's kernel when the host stops.

## A turn

Each step:

1. **Boundary** (`TurnContext.Boundary step`): steers that arrived are delivered to the turn and enter the
   history here, never in the middle of a step.
2. **History**: the session's events and its context events, merged (below), folded by
   `Transcript.ofItems`.
3. **Frame**: the prefix's sections (instructions, the surface's tools, the interpreter's prompt with the
   bridge's declarations) through `Transcript.reconcile`: recorded once (`Framed`), later changes as
   `Updated` at the end of the history.
4. **Budgets** (`codeact.budget.steps`, `codeact.budget.tokens`): over budget, the turn asks the user to
   go on (a question, below).
5. **Render and request**: `Render.render` with the notes for this call (`Live.Notes`), then
   `Render.request` with the template (the model of `model.role.main`, the surface's tools,
   `codeact.maxOutputTokens`) and the catalog's caching caps.
6. **Model call** through `ILlm`, folded with `ChatAggregate` (the policies below).
7. **Reply**: recorded (`Replied`) with the suffix its prompt ended with (`Shown`).
8. **Actions**: every tool call in order, each journaled before it runs (`callStarted`, after a workspace
   snapshot for the step's first; "Crash recovery" below) and answered by a receipt (`Returned`) before
   the next call, also calls that did not run (invalid arguments, an unknown tool, calls after an
   accepted `finish`).
9. **Ending**: an accepted `finish` ends the turn with its result; otherwise the loop guard, the length
   and text-only policies decide whether the turn goes on.

A turn ends only with a `TurnResult` (spec §4.5): `Done` from a cell's `finish`, `NeedInput` on a
question the strategy opened, or the `Error` of a policy (`FailTurn`: the session records the reason).

### The context log

Context events (frames, replies, receipts; prompt.md) live in the stream `context/<session>` of the host's
event store, version 1 of their JSON (`ContextCodecs.event`). The strategy is their only writer, one turn
at a time per session. **A context event's place in the history is its envelope's `Cause`**: the last
event of the session's stream when it was recorded. `ContextLog.merge` puts each context event after that
session event, in the context stream's order. Session events appended meanwhile (a steer, an answer)
only wait in the inbox until a boundary delivers them, so the merged order is the order the model saw
them. A fork's history is its parent's items up to the fork point, then its own (`ContextLog.load`).

Keys derive from the turn's start event and the step: `<session>#<seq>/step/<n>/frame`, `/reply`,
`/receipt/<k>`; a retried append is deduplicated.

## Action surfaces (spec §6.9)

`agent.actionSurface` (`cells-only` | `hybrid`; both ship, the default is provisional until T218):

| Surface | Tools |
|---|---|
| `cells-only` | `cell {code}`: a cell for the session's kernel |
| `hybrid` | `cell`, and `read {path, offset?, limit?}`, `edit {path, old, new, all?}`, `bash {command, timeoutMs?}` |

- **Finishing is the bridge's `finish`, called from a cell** (`await fx.finish(result)`), on both
  surfaces. A separate finish tool would make the model retype its evidence; from a cell the result is
  built from the values the cell holds (spec §6.5: evidence from values, never retyped). Spec §6.9's
  "one cell tool + finish" is that.
- Tools are made from codecs (`ToolSpec.ofCodec`), and their arguments decode with the same codecs: what
  does not decode is a receipt with the issues, and nothing runs.
- **Same capabilities.** `read`, `edit` and `bash` are host calls of the bridge's own bindings
  (`fs.read`, `fs.patch` after `fs.read`, `shell.run`), through the same guard (`Hands.Guard`), settings
  and content exclusions as cells' calls. `edit` replaces text read from the file and patches the lines
  that changed, checked against the digest it read; `read` shows numbered lines as a `Piece.File` (they
  count as seen).
- **A conversation keeps the surface its frame recorded** (the `tools` section's version): tools come
  before the system prompt in providers' prefix checks, so changing them mid-conversation would break
  Anthropic's check on preserved thinking. A new surface applies to new sessions.

## Kernels (spec §6.5)

One kernel per session (`SessionKernel`), started when the first cell needs it and kept across turns:

- It is started in the hands' environment (`Hands.Env`, the host's scope), not the turn's: an RPC peer
  lives in the scope that connected it, and an interrupt cancels the turn's.
- The bridge is `CoreBindings.all` with the turn's settings (`CoreSettings.Exclusions` from the session's
  variants, `codeact.output.maxChars`), behind `Hands.Guard`; `finish` validates with the session's
  `Codecs.turnResult` and the turn's checks, and only from the running cell.
- **Interrupts** cancel the turn's scope. The cell runs shielded from it and is stopped in stages
  (`Kernel.interrupt`: `cell/cancel`, then a kill after `ExitGrace`), so a busy cell does not leave the
  kernel busy; the next turn waits until it stopped (one turn at a time uses a kernel).
- **Restarts** (Jupyter semantics): a kernel that exited or was killed is dropped; the next cell starts a
  new one (fresh owned directories per launch, numbered on from the journal's `KernelLaunched`, also
  after the host restarted) and its receipt says that names from earlier cells are gone and which
  values came back (below, "Kept values and `recall`").
- Each cell is `t<turn>-s<step>-<k>` (`-<attempt>` when people chose to run it again after a crash), with
  its `// intent:` line, `codeact.cell.timeoutMs`, and `check` when `codeact.cell.typeCheck` is on and the
  kernel handles `cell/check`. Its receipt names its value `Out["<cell>"]`.

### `Ensure` (spec §4.3, §4.5)

A result is decoded by `Fx.Sessions.Codecs.turnResult`, then checked (`Ensure.all`):

| Check | Refuses |
|---|---|
| `summary` | a `done` whose summary is blank |
| `evidence` (`codeact.finish.evidence = required`) | a `done` without evidence |
| `requests` | a `needInput` naming a request that is not open as input (the session would wait for nothing) |
| `jobs` | a `waiting` on a job that does not exist (none can be started yet: T212; the session would wait forever) |

A refused `finish` throws in the cell with the issues (`finishRejected`); the model fixes the result and
finishes again.

## Receipts

Stable and bounded when made (`codeact.receipt.maxChars`), recorded through `ContextEvent.withhold`:

- **Cells**: what the cell printed, its value's preview or its error (syntax, type, runtime, timeout,
  cancellation), the names it declared, whether its `finish` was accepted, a note when the kernel was
  new, and a reminder when the `// intent:` line is missing. **Provenance:** the files the cell read
  through the bridge (`fs.read` calls attributed to its cell id) make the receipt a `Piece.Quote` with
  those sources: the egress hooks redact it, and the render withholds it, if one of them is excluded;
  it never counts as seen. What a command printed (`shell.run`) has no provenance (egress.md).
- **`read`**: a `Piece.File` (path, digest, the lines shown), numbered, and where to read on.
- **`edit`**: what changed, and the changed lines with three lines of context as a `Piece.File` of the
  new version.
- **`bash`**: the command, its exit code (or timeout) and its bounded output.

## Policies (spec §4.5)

| Situation | Slot | Behaviour |
|---|---|---|
| A reply without a tool call | `codeact.policy.textOnly` = `nudge` (default) \| `ask` \| `done` | `nudge`: the next call carries a note on how a turn ends; a second such reply in the turn is a question. `ask`: a question at once. `done`: `Done` with the text as its summary, if it passes `Ensure` (else a question) |
| A reply cut at the output limit (`maxTokens`) | `codeact.policy.lengthCut` = `continue` \| `fail`, `codeact.policy.lengthRetries` (2) | its calls run if their arguments decode (a cut call does not), the next call says to go on in smaller steps; more cuts in a row than the retries fail the turn |
| An empty reply (no text, no call) | `codeact.policy.emptyRetries` (2) | asked for again, unrecorded (the same request); then the turn fails |
| A retryable model error (rate limit, overload, server, transport, invalid response) | `codeact.policy.modelRetries` (4), `codeact.policy.retryDelayMs` (1000) | retried after the provider's `Retry-After`, else a doubling delay (at most a minute) |
| The request does not fit (`contextOverflow`), or the window filled while replying (`contextWindow`) | — | the turn fails saying the conversation needs compaction (T206 compacts and retries) |
| Another model error (auth, quota, refusal, egress blocked, …) | — | the turn fails with the error |
| A refusal or a content filter without calls | — | the turn fails with the model's text |
| `pause` | — | the conversation is sent again |
| The same action with the same result, repeatedly | `codeact.loopGuard.repeats` (3; 0 off) | one fewer: a note warns the model; at the limit: a question (`loop`) |
| Steps or tokens used up | `codeact.budget.steps` (100), `codeact.budget.tokens` (none) | a question (`budget`); three steps before the limit a note says so |

**Questions.** The strategy ends a turn `NeedInput [id]` on a request of kind `agent.question`
(`{question, reason}`, answered with text; reply as input, open across turns), opened through
`TurnContext.Open` with id `q-<turn>-<step>`. The answer is the next turn's input (`Request q-1-2 was
answered: …`). The loop guard fingerprints the tool, its canonical arguments and the receipt.

## Crash recovery (spec §5, §6.5, §12.1, §19.1)

When the host stops in the middle of a turn (a crash, a kill, an upgrade), nothing auto-resumes: the
session's actor recovers the turn (`Intent.RecoverTurn`, [sessions.md](sessions.md)) through the
runner's `Recover`. A call that started and has no receipt is **in doubt** (`OutcomeUnknown`): its side
effects may or may not have happened. The turn pauses on a decision for a person, with the evidence the
logs give, and goes on only with their answer. If nothing is in doubt (the host stopped during a model
call, or before a call started), the turn is abandoned as before and the session is held.

### The step journal

Journal before side effects. The strategy writes the stream `steps/<session>` of the host's event store
(version 1 of `StepCodecs.event`; `tests/Fx.CodeAct.Tests/golden/step-events-v1.jsonl` is what it wrote,
never regenerated), one entry at a time per session (`JournalWriter`), every entry keyed:

| Event | Written | Key (after `<turn start>/step/<n>/call/<k>/attempt/<a>`) |
|---|---|---|
| `callStarted {call, tool, cell?, intent?, fingerprint, snapshot?}` | before the call does anything | `/started` |
| `kernelLaunched {launch}` | before a kernel starts (key `kernel/<launch>`) | |
| `hostCallStarted {call, index, binding, args}` | before a host call of the running call runs (not the reads: `fs.read`, `fs.list`, `fs.stat`, `recall`) | `/host/<i>` |
| `hostCallEnded {call, index, failed, summary}` | after it | `/host/<i>/ended` |
| `printed {call, text}` | as the running cell streams output (at most 64 entries and 16000 characters per call) | `/output/<j>` |
| `valueKept {call, cell, value, type}` / `valueLost {call, cell, type, reason}` | after a cell returned a value | `/value` |

- **A call's end is its receipt** (`Returned` in `context/<session>`); the journal does not repeat it, so
  there is no window between two writes. A call is in doubt exactly when it has a `callStarted` and no
  receipt.
- `call` is `{turn, step, index, id, attempt}`: the reply's tool call (its id is the model's) and which
  attempt at running it. `fingerprint` hashes the tool and its canonical arguments.
- `snapshot`: with `codeact.journal.snapshots` on and hands that have workspace snapshots
  (`Hands.Snapshots`, envd's shadow git), each step records the workspace before its first action.
- Bounded: arguments and results 400 characters, output as above, values as below. Events stay small.

### Recovering a turn

`CodeAct.Runner.Recover` (`Recovery.find` over the context log and the journal, pure):

1. The turn's last reply; its calls without a receipt. The first with a `callStarted` (its latest
   attempt) is in doubt; those after it never started.
2. **Evidence**: the call (tool, cell, intent, its code or arguments), what it printed, its host calls
   (ended or running), and the workspace's changes since the step began (a new snapshot diffed against
   the step's; or why they are not known).
3. **The decision** (`Recovery.request`): a request of kind `decision.outcomeUnknown`, id
   `unknown-<call name>` (`-<attempt>` after the first), origin the turn, `ToOpener`, `Lifetime.Turn`.
   Its payload is a decision card (`OutcomeUnknownDecision`: `question`, `scope: "session"` (spec §12.3),
   `call`, `evidence`, `options`); the answer is `{option, note?}`, one of the options:

   | Option | The call's receipt |
   |---|---|
   | `completed` | what people decided, then the evidence; not an error |
   | `failed` | the same, an error: the model checks what it left behind |
   | `rerun` | the call runs again (attempt `a+1`, journaled like any call, cell `<name>-<a+1>`); its new result, after a line saying so |
   | `inspect` | the evidence, an error: the model looks at what it did before going on |
   | (withdrawn, `CancelRequest`) | "nobody decided", an error |

4. The turn **pauses**: it stays running and awaits the answer (`TurnContext.Await`). Steers wait in the
   inbox for the next boundary; an interrupt ends the turn (and withdraws the decision).
5. With the answer, the call gets its receipt (keyed as it would have been), the calls after it get
   "Not run: the host stopped before this call started.", and the turn goes on from the next step: the
   model's next request is the previous one plus these receipts (append-only).

- **Idempotent.** Every write is keyed by what it records: recovering again (the host stops again before
  the answer) re-opens nothing (`TurnContext.Open` with the same key returns the first opening) and
  waits for the same request. A rerun that is in doubt again is a new attempt with a request of its own.
- **Never re-runs by itself.** Only a person's `rerun` runs a call again.
- Not in scope of a decision: calls that never started (they did not run) and model calls (they have no
  side effects); both end as above.

### Kept values and `recall` (spec §6.5 "Durability")

The bun-ts kernel has no snapshots (`caps.snapshot = false`), so durability is Jupyter semantics plus
the journal: after a cell returns a value, the host asks the kernel to encode `Out[cell]`
(`value/encode`, at most `codeact.recall.maxBytes`) and journals its JSON, inline up to
`values.inlineMaxBytes`, in the host's blob store (`CodeActConfig.Blobs`) beyond. A value that cannot be
kept is journaled as lost with why (no JSON form, too large, a handle).

- A **new kernel** (after a kill, or after the host restarted) gets the latest `codeact.recall.restore`
  kept values back as `Out[cell]` (`recall`), and its first receipt lists them.
- **`fx.recall(id)`** (the `recall` binding, from cells): any kept value, by the cell's id or the id of the
  tool call that ran it; `notKept` with the reason otherwise.
- What comes back is JSON: names cells declared, functions, class instances, promises, open resources
  and handles are gone with their kernel.

## Content exclusions (spec §18)

`ContentExclusions.compile` over the turn's variants (`ContentExclusions.read`: policy, host, project
lists) gives one `Exclusions` for the render (`RenderConfig.Exclusions`), the context log
(`ContextEvent.withhold`), the bindings (`CoreSettings.Exclusions`: `fs.read` refuses excluded files, so
`read` and `edit` do too) and, through the same slots, the egress hooks. Settings that do not compile fail
the turn rather than send excluded content.

## Variants

Every setting is read once per turn through the turn's profile (`CodeActConfig.Profile`), so it is
recorded and replayable (spec §15): `agent.actionSurface`, `model.role.main`, the render's slots
(prompt.md), content exclusions' (egress.md), `values.previewLimits`, `values.inlineMaxBytes`, and:

| Slot | Default |
|---|---|
| `codeact.policy.textOnly`, `.lengthCut`, `.lengthRetries`, `.emptyRetries`, `.modelRetries`, `.retryDelayMs` | `nudge`, `continue`, 2, 2, 4, 1000 |
| `codeact.loopGuard.repeats` | 3 |
| `codeact.budget.steps`, `codeact.budget.tokens` | 100, none |
| `codeact.maxOutputTokens` | none (the model's) |
| `codeact.cell.timeoutMs`, `codeact.cell.typeCheck` | 600000, false |
| `codeact.output.maxChars`, `codeact.receipt.maxChars`, `codeact.read.maxLines` | 16000, 20000, 2000 |
| `codeact.finish.evidence` | `optional` |
| `codeact.journal.snapshots` | true |
| `codeact.recall.maxBytes`, `codeact.recall.restore` | 1048576, 20 |

Defaults are provisional until evals decide (spec §1, principle 7).

## Testing

`tests/Fx.CodeAct.Tests`:

- **Units**: tools from codecs and their decoding, the loop guard, line edits (line endings kept),
  `read` receipts, the merged history's order, the checks of a result, questions, the recorded surface,
  quotes' provenance, the slots.
- **Turns** on the real session host (in-memory store of payloads: every event through its JSON), with the
  script kernel in process and a scripted model that can hold a request: both surfaces doing a small
  coding task (read, edit, run a command, finish with evidence; each request a prefix of the next),
  finishing refused and accepted, a steer delivered at the next boundary, an interrupt that leaves the
  kernel's names, a busy cell killed and the kernel restarted, a kernel that died between turns, the loop
  guard, each text-only policy and a question answered into the next turn, length cuts, empty replies,
  retries, overflow, fatal errors, the step budget, content exclusions for cells and direct tools, an
  unknown tool, calls after `finish`.
- **Replay**: the recorded logs render, through the render alone, to exactly the requests sent; the same
  inputs give the same context log, requests and session events.
- **Crash recovery** (`RecoveryUnitTests`, `CrashTests`): the journal's version 1 read back; finding the
  call in doubt; the card and its answers; each decision's receipt; kept values. End to end, the host
  process is stopped mid-cell as a crash would stop it and started again on the same stores (with a
  command that hangs the first time): the decision and its evidence (output, host calls, workspace
  changes), nothing run before the answer, one decision over two restarts, every answer path (rerun
  included), calls after the one in doubt, a crash during a model call (abandoned), values back after a
  host restart and a kill, `fx.recall`, launch numbers; each replays into the requests sent.
- **Providers**: turns through `ModelAccess` with `anthropic.messages` and `openai.responses` and their
  catalogs, on streams in each provider's documented format (tool calls and results, tools from codecs,
  breakpoints, a stable cache key).
- **Live** (the pinned Bun; skipped offline, required in CI): TypeScript cells on the bun-ts kernel for
  both surfaces, a rejected `finish`, and a turn in an environment of `fx.env.local` opened through `IEnvs`.

Native AOT: `codeact-hybrid`, `codeact-text-only` (a session on the session host with the script
kernel, an in-memory workspace and a scripted model; the receipts, the context log and the logs replayed
into the requests) and `codeact-recovery` (two restarts mid-cell, the decision, a rerun, `recall`; the
card, the journal, the receipts, the session's events), on all eight targets.

## Left for later

| What | Task |
|---|---|
| Grants on sensitive bindings (`shell.exec` with compound commands split, writes outside the workspace, network) through `Hands.Guard`, pending grant requests shown in the turn | T127b |
| Composition: the profile's variants, hands from `IEnvs` per session (`EnvHands.ofHandle`), `ModelAccess.llm`, `Questions.kinds`, registering `CodeActSlots.read`, the catalog | T129 |
| The decision ledger takes over `decision.outcomeUnknown` (its cards as Session-scope decisions, anchors) | T301 |
| Partial output of commands in doubt (`shell.run`, `bash`): run them as envd processes, whose output logs resume by byte offset, and journal the process | T205 |
| Kernels with snapshots (`snapshot`/`restore`) restored instead of kept values; values in envd's blob store and handles in the journal | T201, interpreters that have them |
| Exact replay of model streams and variant reads from a journal | T131 |
| Activity events and per-cell diffs; `Read` activity as provenance | T205 |
| Compaction on overflow (`Collapsed`), steering and queueing UX | T206 |
| Brokered bindings (`fx.ask` for the model's own questions, `fx.llm`, agents, values and handles) | T201, T213 and their tasks |
| A strategy contribution type for extensions (the SDK would need sessions, the render and interpreters) | when a second strategy exists |
