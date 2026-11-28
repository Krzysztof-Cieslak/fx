# Module spec: the CodeAct strategy (`src/Fx.CodeAct`)

Spec: §4.3–4.5 (functions, machines, strategies, turn results), §5 (sessions, steering at step
boundaries, prompt = pure render), §6.5 (kernel behaviour: host bridge, finishing, activity), §6.9
(action surfaces), §6.10 (example cells), §10 (models), §13 (grants), §15 (variants), §18 (egress,
sandbox, guardrails).
Tasks: T127, T127b (grants), T128 (crash recovery), T206 (steering, compaction), T143 (background jobs). Builds on [sessions.md](sessions.md) (the turn-runner boundary),
[prompt.md](prompt.md) (the render and context events), [models.md](models.md) (`ILlm`),
[interpreters.md](interpreters.md) (the bridge and kernels), [kernel-protocol.md](kernel-protocol.md),
[egress.md](egress.md), [environments.md](environments.md) and [grants.md](grants.md).
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
| What cells ask of the history: `history.compact`, `history.list`, `history.read`, `history.open` (`HistoryAsks`, `HistoryBindings`) | `History.fs` |
| Background jobs: the hands' `Jobs`, their interpreter `LiveJobs`, notices (`JobNotices`), a session's jobs (`SessionJobs`) and the `jobs` bindings (`JobBindings`) | `Jobs.fs` |
| The turn runner (`CodeAct`, `CodeActConfig`) | `CodeAct.fs` |
| The sandbox's policy for the file bindings (`SandboxGuard`) | `SandboxGuard.fs` |
| Hands in an environment opened through `IEnvs` (`EnvHands`, `KernelPlan`) | `Environment.fs` |
| Command lines split as sh and PowerShell split them (`ShellSyntax`, `CommandLine`); one simple command's words (`ShellSyntax.words`: `shell.run`'s plain searches) | `ShellSyntax.fs` |
| Host calls as grants, the guard's slots and settings (`Permissions`, `GrantSlots`, `GrantSettings`) | `Permissions.fs` |
| The grants guard (`GrantGuard`, `GrantSetup`) | `GrantGuard.fs` |

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
- In an environment, the session's processes and kernels run in its sandbox under the agent's
  overrides (`KernelPlan.Sandbox`, e.g. a reviewer's read-only workspace), and `SandboxGuard` checks
  every host call of the file bindings against the same policy before the plan's guard
  ([sandbox.md](sandbox.md)). The sandbox is opt-in (T137: the host's `sandbox.driver` is `none` by
  default): without one, processes run with the user's rights and only the bindings keep to the
  policy. `Hands.Sandbox` is the policy when a driver confines them (`None` otherwise): the prompt
  describes it, and approval modes (T134) can read whether commands are confined.
- The session host must know the kinds `agent.question` and `decision.outcomeUnknown` (`Questions.kinds`),
  and the host's registry the slots a turn reads (`CodeActSlots.read`: the core's, the render's, content
  exclusions', the strategy's and its grants guard's). Both are the composition root's (`src/Fx.Host`).
- The composition root (`src/Fx.Host`) attaches the grants guard to each turn's hands
  (`GrantGuard.attach` with `GrantSetup.create grants sessions turn (GrantSettings.ofVariants
  variants)`, the variants of the turn's profile): without it, nothing asks the grants log. `fx run`
  puts the run's grant requests to the person at the terminal ([host.md](host.md)); without one it
  withdraws them, and the call fails "not answered". Requests are keyed by the turn and the call, so a
  later turn (a later run) asks again: no answer, or a person's "once", is final only for its call.
- `codeact.Close()` closes every session's kernel when the host stops.
- The composition root owns the environments' background jobs (`LiveJobs`, below "Background jobs"):
  it gives each turn's hands `Jobs = liveJobs.Jobs`, tells the session host when a job ends
  (`SessionHost.send … (JobNotices.key job) JobNotices.by (Command.NotifyJob …)`), closes them when the
  host stops, and, when it starts, tells every session that waits for jobs that they were lost
  (`JobNotices.lostAll`).

## A turn

Each step:

1. **Boundary** (`TurnContext.Boundary step`): steers that arrived are delivered to the turn and enter the
   history here, never in the middle of a step.
2. **History**: the session's events and its context events, merged (below), folded by
   `Transcript.ofItems`.
3. **Frame**: the prefix's sections (instructions, the surface's tools, the interpreter's prompt with the
   bridge's declarations, and, when a driver confines the hands' processes, the sandbox's description:
   `Instructions.sandbox` of `Hands.Sandbox`, [sandbox.md](sandbox.md) "In the host") through
   `Transcript.reconcile`: recorded once (`Framed`), later changes as `Updated` at the end of the
   history.
4. **Budgets** (`codeact.budget.steps`, `codeact.budget.tokens`): over budget, the turn asks the user to
   go on (a question, below).
5. **Compaction** (below): a summary prepared during the last step is recorded first; otherwise the
   history is compacted now if a cell asked, the window filled during the last reply, or the prompt reached
   the threshold.
6. **Render and request**: `Render.render` with the notes for this call (`Live.Notes`), then
   `Render.request` with the template (the model of `model.role.main`, the surface's tools,
   `codeact.maxOutputTokens`) and the catalog's caching caps.
7. **Model call** through `ILlm`, folded with `ChatAggregate` (the policies below). A request that
   overflows the window is compacted and sent again, once a step.
8. **Reply**: recorded (`Replied`) with the suffix its prompt ended with (`Shown`) and what the call
   used (`Used`: the request's model, the response's usage, the catalog's context window, and the
   cost at the catalog's prices).
9. **Actions**: every tool call in order, each journaled before it runs (`callStarted`, after a workspace
   snapshot for the step's first; "Crash recovery" below) and answered by a receipt (`Returned`) before
   the next call, also calls that did not run (invalid arguments, an unknown tool, calls after an
   accepted `finish`).
   When the reply's prompt reached the threshold, the summary is prepared beside them (below).
10. **Ending**: an accepted `finish` ends the turn with its result; otherwise the loop guard, the length
    and text-only policies decide whether the turn goes on. A summary prepared during the last step, or
    asked for by its last cell, is recorded before the turn ends, so the next turn starts compacted.

**Steering and queueing** (spec §5, [sessions.md](sessions.md#prompts-steer-and-queue-spec-5-192)): a steer
that arrives while a turn runs waits in the session's inbox until the next step boundary, so the model
reads it after every result of the step's reply (a tool call is always followed by its result) and before
its next call; a queued prompt waits for the turn to end and starts a turn of its own. Steps never stop
in the middle for a steer: an interrupt is the control path. (A grant request a call waits on is
withdrawn when a steer arrives: below, "Grants".)

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
| `cells-only` | `cell {code, texts?}`: a cell for the session's kernel |
| `hybrid` | `cell`, and `read {path, offset?, limit?}`, `edit {path, old, new, all?}`, `bash {command, timeoutMs?}` |

- **Texts** (T140): `cell`'s optional `texts` is an object from names to strings, e.g.
  `{"code": "// intent: add the native layer\nawait fx.fs.write(\"src/X.fs\", posix)", "texts": {"posix": "module …"}}`.
  File bodies, commit messages, patches and a command's input go there instead of into the code, so
  backticks and `${` need no escaping (in the dogfooding sessions they ended TypeScript template literals,
  and the model's workarounds changed the text).
  - Names are identifiers (`[A-Za-z_][A-Za-z0-9_]*`, at most 64 characters), never the bridge's global or
    `Out`, at most 32 of them (`Tools.checkName`, `Tools.MaxTexts`): other names do not decode, and the
    receipt names each (`$.texts["1x"]: "1x" is not a name …`); nothing starts.
  - Before `cell/run`, in the action `runAction` journaled, each text is bound with `value/decode` as an
    inline string (`CellTexts.bind`), up to `values.argumentMaxBytes` of JSON each, as a host call's
    argument. A name the kernel refuses (`invalidName`: a reserved word of its language, one of its own
    globals) fails the cell before its code runs, and the receipt says which text and why
    (`Receipts.unbound`, after a new kernel's note).
  - Texts are part of the call's arguments: the reply (`Replied`) keeps them, the step journal's
    `callStarted` their fingerprint, and a cell run again after a crash (`rerun`) binds them again in its
    kernel. The receipt does not repeat them; values the cell passes on are journaled as host calls'
    arguments are (a bounded preview, a digest in the replay journal).
  - Bound names persist like a cell's declarations (until a later cell or text rebinds them). The bun-ts
    kernel declares them to its checker (`declare let posix: string`), so `codeact.cell.typeCheck`
    accepts cells that use them.

- **Finishing is the bridge's `finish`, called from a cell** (`await fx.finish(result)`), on both
  surfaces. A separate finish tool would make the model retype its evidence; from a cell the result is
  built from the values the cell holds (spec §6.5: evidence from values, never retyped). Spec §6.9's
  "one cell tool + finish" is that.
- Tools are made from codecs (`ToolSpec.ofCodec`), and their arguments decode with the same codecs: what
  does not decode is a receipt with the issues, and nothing runs.
- **Same capabilities.** `read`, `edit` and `bash` are host calls of the bridge's own bindings
  (`fs.read`, `fs.edit`, `shell.run`), through the same guard (`Hands.Guard`, and so the same grants),
  settings and content exclusions as cells' calls. `edit` is `fs.edit(path, {old, new, all})` (one
  implementation of edits by text, spec §6.8: the binding replaces the text and patches the lines that
  changed, checked against the digest it read, and its errors are `edit`'s), then, for a file in the
  workspace, a ranged `fs.read` of three lines around what changed for the receipt (outside the
  workspace that read would ask a person again, so the receipt shows the changed lines alone); `read`
  shows numbered lines as a `Piece.File` (they count as seen).
- **A conversation keeps the surface and the tools its frame recorded** (the `tools` section's version):
  tools come before the system prompt in providers' prefix checks, so changing them mid-conversation would
  break Anthropic's check on preserved thinking. A new surface applies to new sessions.
  - **A change to a tool's schema or description is a new version of the tools** (`Tools.Version`,
    `ToolSet`): the section's version is the surface's name for version 1 (`cells-only`, as frames
    recorded before T140) and `<surface>/<version>` after it (`cells-only/2`: `cell` with `texts`).
    `Tools.specs` renders every version exactly as it was (`tests/Fx.CodeAct.Tests/golden/tools-v1.json`
    is kept, never rewritten); a conversation framed with an older version keeps its tools, and one framed
    with a version this host does not know (a newer fx's) gets the current tools, as an `Updated` frame.
  - Arguments decode with the newest codecs, whose older versions are subsets: `texts` works in a
    conversation framed with version 1 too, although its `cell` tool does not declare it.

## Kernels (spec §6.5)

One kernel per session (`SessionKernel`), started when the first cell needs it and kept across turns
and cells: it lives for the session unless it is killed (an interrupt's escalation), it dies, or the
host stops (T135).

- It is started in the hands' environment (`Hands.Env`, the host's scope), not the turn's: an RPC peer
  lives in the scope that connected it, and an interrupt cancels the turn's.
- Its process must not die with the thread that started it. bubblewrap's `--die-with-parent`
  (`PR_SET_PDEATHSIG`) ties a process to the *thread* that forked it, and thread-pool threads end after
  20 seconds idle: in a dogfooding session (T132) the kernel was killed (SIGKILL, exit code 137) between
  turns and while a cell waited for a person's grant, so every turn started a new kernel. The shell and
  terminal interpreters start every process on one thread that lives as long as the host or envd
  (`ProcessStarter` in `Fx.Effects.Live` and `Fx.Pty`; [envd.md](envd.md)).
- The bridge is `CoreBindings.all` with the turn's settings (`CoreSettings.Exclusions` from the session's
  variants, `codeact.output.maxChars`; `Words`, how `shell.run` tells a plain search, from
  `ShellSyntax.words` in the hands' shell dialect; `Sandbox`, the hands' confining policy, which a
  search's matches are checked under), behind the running turn's `Hands.Guard` (`SessionKernel.Guard`,
  set before each action: hands are per turn, a kernel outlives the turn that started it); `finish`
  validates with the session's `Codecs.turnResult` and the turn's checks, and only from the running cell.
- **Interrupts** cancel the turn's scope. The cell runs shielded from it and is stopped in stages
  (`Kernel.interrupt`: `cell/cancel`, then a kill after `ExitGrace`), so a busy cell does not leave the
  kernel busy; the next turn waits until it stopped (one turn at a time uses a kernel). An interrupt that
  began runs to its end, also when the cell answers first: cut short, its `cell/cancel` would be cancelled
  or not depending on timing, which exact replay cannot follow (replay.md).
- **Restarts** (Jupyter semantics): a kernel that exited or was killed is dropped; the next cell starts a
  new one (fresh owned directories per launch, numbered on from the journal's `KernelLaunched`, also
  after the host restarted) and its receipt says that names from earlier cells are gone, why, and which
  values came back (below, "Kept values and `recall`").
- **Every restart is logged with its reason.** How a kernel ended (`KernelEnd`) is journaled as
  `kernelEnded` before the launch that replaces it: when a cell finds it gone (during the cell or after
  it: its exit code and the end of its standard error), when the host kills it (`killed`), when its launch
  failed (`notStarted`), or, at the first launch after the host restarted, `hostStopped` unless the
  journal already says how it ended. `KernelEnds.describe` puts it in words (an exit code above 128 is
  the signal that ended it, except on Windows: `was killed by signal 9 (SIGKILL)`):
  - the cell whose kernel ended: `The cell did not complete: the kernel was killed by signal 9 (SIGKILL).
    The next cell starts a new kernel: …`;
  - the next cell's receipt starts with `[A new kernel started for this cell (the previous kernel …):
    names from earlier cells are gone …]`, and carries `Receipt.Restarted` (`{launch, reason}`) for
    people: the TUI shows `↻ new kernel: <reason>; names from earlier cells are gone` under the call, and
    `fx run` an activity line (`  ↻ new kernel  …`). A receipt with `Restarted` always starts with that
    note (a rerun's line comes after it), so clients drop its first piece from what they show.
- Each cell is `t<turn>-s<step>-<k>` (`-<attempt>` when people chose to run it again after a crash), with
  its `// intent:` line, `codeact.cell.timeoutMs`, and `check` when `codeact.cell.typeCheck` is on and the
  kernel handles `cell/check`. Its receipt names its value `Out["<cell>"]`.
- **The cell timeout and approval waits (T141).** In dogfooding (T132) cells were stopped at the
  600-second timeout while a host call waited for a person to approve `strace`, and the receipt said
  only "timed out". Now no wait for a person counts against a cell:
  - the kernel counts only the cell's own time (`timeoutMs`, kernel-protocol.md): its clock stops while
    any host call of the cell is in flight, guard and binding alike;
  - the host bounds the cell's commands instead (`CellCommands`): `shell.run` and `jobs.wait` (T143: a
    wait for a process is the cell's time, not a person's) from the running cell run for at most what is
    left of `codeact.cell.timeoutMs` after the cell's earlier commands and waits (their `timeoutMs`
    option is added, or lowered when longer; the step journal shows a command's bound). A wait that
    reaches it gives the job's status (`running`), not an error. Their time
    is measured around the binding, after the grants guard answered, on the hands' clock (recorded:
    replay gives the same bounds). Once the commands used it up, the next one is refused before it
    runs: `failed` with code `cellTimeout` and a message saying to run it in a cell of its own. A cell
    looping over commands therefore still ends: at most twice the timeout, plus the time people take;
  - an interrupt still stops a cell at once, waiting or not.

  The host never signals the kernel from the guard (a `cell/hold` notification was planned and
  dropped): replay answers the guard from one journal entry (`Hands.Guard`) without running it, so the
  notification would be missing there and the kernel's input would diverge.

### `Ensure` (spec §4.3, §4.5)

A result is decoded by `Fx.Sessions.Codecs.turnResult`, then checked (`Ensure.all`):

| Check | Refuses |
|---|---|
| `summary` | a `done` whose summary is blank |
| `evidence` (`codeact.finish.evidence = required`) | a `done` without evidence |
| `requests` | a `needInput` naming a request that is not open as input (the session would wait for nothing) |
| `jobs` | a `waiting` on a job that is not one of the session's, or that ended already as far as the strategy knows (`SessionJobs.Check`: the session would wait forever) |

A refused `finish` throws in the cell with the issues (`finishRejected`); the model fixes the result and
finishes again.

### Background jobs (spec §5, §6.5; T143)

Long builds, test runs and watches (`gh pr checks --watch`) run as background jobs instead of `&`,
`nohup` or sleep loops: a cell starts one and goes on, and a turn that needs it to end ends `waiting` on
it.

| Binding | Does |
|---|---|
| `jobs.start(command, {name?, cwd?, stdin?})` | starts the command line with the environment's shell, as `shell.run` runs it (sandboxed, the commands' variables), and returns `Job {id, name, command}` at once; ids are `j1`, `j2`, … in the session |
| `jobs.list()` | the session's jobs this host started, as `JobStatus` (`state`: `running`, `exited`, `killed` or `failed`, `exitCode`, `reason`, `bytes`) |
| `jobs.status(id)` | one job's status; a job that ran before the host restarted fails `lost` (or `gone`), an unknown one `notFound` |
| `jobs.output(id, {from?, maxChars?})` | `JobOutput {text, from, next, bytes, running}`: stdout and stderr together as they arrived, from a byte offset (by default the last `maxChars`, `codeact.output.maxChars`), never cutting a character; `next` is where to read on. A job keeps its last megabyte (`Jobs.RetainBytes`): an older offset reads from the first byte kept |
| `jobs.wait(id, {timeoutMs?})` | the status once the job ended or the wait timed out; bounded by the cell's timeout (above) |
| `jobs.kill(id)` | stops the job and what it started; its status (`killed`) |

- **The environment's side** is a capability of the hands, `Hands.Jobs` (`Jobs<'K>`: data in and out,
  like `Hands.Guard`). `LiveJobs` is its interpreter: it starts each job with the shell of the
  environment the call runs in (the hands' sandboxed one), reads its output until it ends, keeps its last
  megabyte, and tells the session when the job ends on its own (`JobNotices.ended`: how, e.g.
  `` `dotnet test` exited with code 1 after printing 2048 bytes. ``, on the first line, then its last ten
  lines and how to read more), as `Command.NotifyJob` keyed `jobs/<id>/ended`. A job `jobs.kill` stopped
  sends no notice (the cell saw its end). Jobs live as long as the host: `LiveJobs.Close` kills them
  without notices. Reattaching to envd's processes after a restart is later work.
- **Notices reach turns through the session** (sessions.md): a running turn reads `Job j1 finished: …` at
  its next step boundary, like a steer; a turn that ended `waiting` resumes once every job it named ended
  (the notice is the next turn's input); a job that ends while the session is idle and waits for nothing
  starts a turn with its notice, like any input, so the prompt says to kill jobs no longer needed.
- **The strategy's side** (`SessionJobs`, one per session): ids are numbered on from the step journal,
  read once per host process at a session's first turn (`Prepare`). The strategy learns of an end only
  from data a recording journals: what a cell's `jobs.*` call answered (`Observe`), a job's notice among
  a turn's inputs (`Delivered`, at its start and each boundary), or the journal after a restart
  (`Orphans`). Never from a process's timing, so a recorded session replays exactly (replay.md).
- **Journal** (step events, kept in `golden/step-events-jobs-v1.jsonl`): `jobStarted {job, name, command,
  cwd, call}` before the process starts; `jobEnded {job, how, bytes}` when the strategy learns it:
  `exited`, `killed` or `failed` from a cell's answer, `reported` (the notice's note) from a turn's
  inputs, `lost` after a restart.
- **A host restart.** No job survives its host. A host that starts sends `JobNotices.lostAll` to sessions
  waiting for jobs (keyed like the jobs' own notices, so a notice sent before the crash wins): they go on
  with `Job j1 finished: lost: …`. At a session's first turn in a host process, jobs the journal started
  and never ended are journaled `lost` (or `reported`, when the session's log has their notice), and the
  model's next call carries a note naming those it was not told about.
- **Grants**: `jobs.start` needs what `shell.run` of the same command line needs (`Permissions.jobsStart`:
  each simple command, redirections, auto approvals, `codeact.grants.askCommands`). Its process runs in
  the sandbox as commands do, so `SandboxGuard` has no path of its own to check. `jobs.list`, `status`,
  `output` and `wait` only read: the step journal does not journal them as host calls (`kill` and `start`
  are).
- **Receipts** name the jobs an action started (`Receipt.Jobs`, for people, never rendered: the TUI's
  `▸ job j1 started  CI: gh pr checks --watch`, `fx run`'s `  ▸ job j1 started  …`).

### Chosen variables in the prompt (T138)

`Hands.Variables` lists the variables a person chose for the environment's commands, by name, with why one is missing. `Instructions.variables` adds a `variables` section to the prefix (empty when there are none, like `sandbox`): which are set for commands (not for a cell's own process), that `gh` is authenticated when `GH_TOKEN` or `GITHUB_TOKEN` is, that credentials read as `[redacted NAME]`, and which were asked for and left out. `EnvHands.ofHandle` wraps the environment's client with `Redacting.client` below the recording taps. The journal's `HandsInfo.variables` keeps the list (absent when empty) so replay renders the same prompt.

## Receipts

Stable and bounded when made (`codeact.receipt.maxChars`), recorded through `ContextEvent.withhold`:

- **Cells**: what the cell printed, the value it ended with or its error (syntax, type, runtime,
  timeout, cancellation), the names it declared, whether its `finish` was accepted, a note when the
  kernel was new, and a reminder when the `// intent:` line is missing. A cell that stopped early (an
  error, a timeout, a cancellation) lists the names it declared but never assigned apart (the kernel's
  `unassigned`): `Declared: tk, tl` and `Not assigned (the cell stopped first): c1`, so the model does
  not read an `undefined` name as a value next time. A timed-out or cancelled cell names each host call
  it stopped while the guard had it wait for a person (`CellRecord.Waiting`: calls the guard had not
  answered when the cell ended), e.g. `shell.run: strace -f ls (stopped while it waited for its
  approval)` (`Receipts.callTitle`: the binding and its first argument, cut at 120 characters); their
  requests are withdrawn (below, "Grants"). **Provenance:** the files the
  cell read through the bridge (`fs.read` calls attributed to its cell id) make the receipt a
  `Piece.Quote` with those sources: the egress hooks redact it, and the render withholds it, if one of
  them is excluded; it never counts as seen. What a command printed (`shell.run`) has no provenance
  (egress.md).
- **A cell's value** (T203a, `CellValue.ofValue`) shows from its JSON, the one `recall` keeps (so up to
  `codeact.recall.maxBytes`; beyond, or with recall off, the kernel's preview):
  - a string as its text (`Value of Out["…"] (string, N chars):`), cut in the middle at
    `codeact.output.maxChars` as printed output is;
  - what one of the cell's host calls returned, matched by the digest of its canonical JSON (the
    kernel tells the host nothing more, so this works for any kernel): `shell.run`'s `RunResult` as
    `bash` shows it (command, exit code or `timed out (killed)`, output, which a command cut at its
    timeout keeps up to where it was killed), and for a plain search with `matches` (T142,
    interpreters.md) a last line on editing one: ``[2 matches, each a line checked against its file:
    `fx.fs.editRange(Out["t1-s2-1"].matches[0], text)` replaces the first one's line.]``;
    `fs.read`'s `FileText` as `read` shows it (numbered lines in a `Piece.File` from the line it
    starts at, so a ranged read counts exactly its lines as seen, and exclusions withhold them;
    `codeact.read.maxLines` at most, then how to read on),
    `fs.edit`'s and `fs.editRange`'s `Edited` as `edit` shows it (what changed, then each fresh range's
    lines as a `Piece.File`, up to `codeact.read.maxLines` lines in all), `fs.write` and `fs.patch` as
    the files written;
  - anything else as the kernel's preview, within `values.previewLimits` (4000 characters, 50 items, 4
    levels, 1000 characters per string).
- **A cell whose texts could not be bound** (T140): which text and why (`invalidName`, or over
  `values.argumentMaxBytes`), and that nothing ran. Receipts never repeat a cell's texts.
- **`read`**: a `Piece.File` (path, digest, the lines shown), numbered, and where to read on.
- **`edit`**: what changed, and the changed lines with three lines of context as a `Piece.File` of the
  new version (`Receipts.edited`, from the `Edited` result and the ranged read after it; without that
  read, each fresh range's lines).
- **`bash`**: the command, its exit code (or timeout) and its bounded output (a command cut at its
  timeout keeps what it printed). A plain search with `matches` ends with one line on editing one
  (`Receipts.ran`): with `edit` and its line as `old`, or from a cell, whose `fx.shell.run` gives the
  matches as ranges for `fx.fs.editRange` (`bash`'s own result never reaches a cell).
- **Refused host calls** (grants, below): `Refused: <why>` for a direct tool; in a cell, the error the
  kernel throws with code `denied` and the same reason, which the cell's receipt shows unless the cell
  catches it.

## Policies (spec §4.5)

| Situation | Slot | Behaviour |
|---|---|---|
| A reply without a tool call | `codeact.policy.textOnly` = `nudge` (default) \| `ask` \| `done` | `nudge`: the next call carries a note on how a turn ends; a second such reply in the turn is a question. `ask`: a question at once. `done`: `Done` with the text as its summary, if it passes `Ensure` (else a question) |
| A reply cut at the output limit (`maxTokens`) | `codeact.policy.lengthCut` = `continue` \| `fail`, `codeact.policy.lengthRetries` (2) | its calls run if their arguments decode (a cut call does not), the next call says to go on in smaller steps; more cuts in a row than the retries fail the turn |
| An empty reply (no text, no call) | `codeact.policy.emptyRetries` (2) | asked for again, unrecorded (the same request); then the turn fails |
| A retryable model error (rate limit, overload, server, transport, invalid response) | `codeact.policy.modelRetries` (4), `codeact.policy.retryDelayMs` (1000) | retried after the provider's `Retry-After`, else a doubling delay (at most a minute) |
| The request does not fit (`contextOverflow`) | `codeact.compaction.mode` | compacted (the provider's limit, else the catalog's window) and sent again, once a step; with nothing to summarize, or mode `off`, the turn fails |
| The window filled while replying (`contextWindow`) | `codeact.compaction.mode` | the reply's calls run, and the next call compacts first (it fails the turn if it cannot); mode `off` fails the turn |
| Another model error (auth, quota, refusal, egress blocked, …) | — | the turn fails with the error |
| A refusal or a content filter without calls | — | the turn fails with the model's text |
| `pause` | — | the conversation is sent again |
| The same action with the same result, repeatedly | `codeact.loopGuard.repeats` (3; 0 off) | one fewer: a note warns the model; at the limit: a question (`loop`) |
| Steps or tokens used up | `codeact.budget.steps` (100), `codeact.budget.tokens` (none) | a question (`budget`); three steps before the limit a note says so |

**Questions.** The strategy ends a turn `NeedInput [id]` on a request of kind `agent.question`
(`{question, reason}`, answered with text; reply as input, open across turns), opened through
`TurnContext.Open` with id `q-<turn>-<step>`. The answer is the next turn's input (`Request q-1-2 was
answered: …`). The loop guard fingerprints the tool, its canonical arguments and the receipt.

## Compaction (spec §5)

A conversation that fills the model's context window has its oldest part replaced by a summary the model
writes: a `Collapsed` context event ([prompt.md](prompt.md#compaction-compaction-t206), which has the
pure part: cuts, the summary request, the collapse). The originals stay in the logs; the frame (the prefix)
is never touched.

**When** (`codeact.compaction.mode`, read once per turn):

| Trigger | `auto` | `overflow` | `off` |
|---|---|---|---|
| A reply's prompt reached `codeact.compaction.threshold` (80%) of the window: the provider's `Usage.Input` and output, else the estimate | prepared in the background | — | — |
| The prompt about to be sent reached the threshold (estimated: `Tokens`) and nothing was prepared | compacted first | — | — |
| The request overflowed (`contextOverflow`), or the window filled during the last reply | compacted, sent again | same | the turn fails |
| A cell called `fx.history.compact(focus?)` | compacted before the next call | same | the binding refuses (`off`) |

- **The window** is the catalog's (`ModelInfo.Limits.Context`); on overflow the provider's limit, when it
  says. Without one, only overflow and cells' asks compact (the threshold needs a window).
- **The cut** (`Compaction.choose`): the latest that keeps `codeact.compaction.keep` (30%) of the window as
  it is, among those whose summary request leaves room for the summary
  (`codeact.compaction.summaryTokens`, 8192, plus 1024; at most half the window); failing that, the
  earliest that fits.
- **The summary request** is the conversation's own request up to the cut (the call that ended there, or
  this one), with its breakpoints, then the instruction (`Instructions.summarize`: what to keep, ids for
  `fx.recall`, and the cell's focus): same model, tools, tool choice and reasoning, so the provider's cache
  hits; only the output limit is the summary's. It goes through the turn's `ILlm` (`ModelAccess`: the
  egress hooks, recorded and replayed like every call). Only the reply's text counts; the model's retry and
  empty-reply policies apply.
- **Background preparation**: when a reply's prompt reached the threshold, the summary is asked for beside
  the step's actions (`Eff.both`, each in its flow); the next call records it first if it still applies.
  Nothing else waits on it until then. A summary that fails changes nothing: the threshold starts no other
  in the turn (overflow and asks still do), and a defect in it fails the summary, never the cell beside it.
- **Recorded** through the strategy's `record`, keyed `<turn start>/collapse/<k>`: one collapse per step at
  most from the threshold, another after an overflow. The summary is a `Quote` of the files its request
  quoted, so exclusions added later withhold it.
- **What the model sees afterwards**: `[Summary of an earlier part of this conversation:]` and the summary
  where the range began; pins hidden by the range show again; the frame and the cache key are unchanged.
  Replies after the range's start lose their reasoning (prompt.md's boundary; CodeAct requests no reasoning,
  so providers' checks on it do not apply).
- **Activity**: clients show a collapse as an activity line (the TUI's `◇ Compact`, `fx run`'s `◇ compacted`).

`fx.history.compact(focus?)` (`HistoryBindings.compact`, group `history`): the model asks for a compaction
before its next step, keeping `focus` in particular; it answers at once, and the next step compacts first
(if nothing is old enough yet, a note says so). Asks of an earlier turn are forgotten when a turn starts.

**Reading collapsed parts back** (T201b): the originals of a collapsed range stay in the context log, and
cells read them back. `fx.history.list()` gives each range (`r1`, `r2`, … in the order the collapses
were applied) with the entries it covers, its summary and its originals' length; `fx.history.read(range,
{from, maxChars})` gives the originals as text (`Originals.text`: inputs, replies' text and calls,
receipts, turn ends; files content exclusions keep from models withheld as the render does) from a
character offset, bounded (`codeact.outputChars` by default), with `next` to read on;
`fx.history.open(id)` gives a `HistoryRange` handle, which `history.read` takes as well as an id. They load
the session's log as the turn does.

**Pins** (spec §6.6, T201c): `fx.context.pin(name, value)` keeps a value in view at every later step as it
is now (a string as its text, data as JSON, a handle or blob by reference; at most `codeact.outputChars`
characters); `fx.context.pinFile(name, path, {lines})` keeps a live view of a workspace file (outside the
workspace or excluded is refused); `fx.context.unpin(name)`; `fx.context.pins()` gives each pin with its
characters and estimated tokens, file pins as their files are now. The bindings only ask (`ContextAsks`,
per session; asks of an earlier turn are dropped when a turn starts); the turn records them as
`Pinned`/`Unpinned` context events right after the cell's receipt, so the log's order is the order the
model saw. Before each call the turn reads the live-pinned files through the hands, as `fs.read` reads
them (`ContextBindings.readPinned`: content exclusions, the sandbox's policy; journaled for replay),
into `Live.Files`, and the `pins` view (prompt.md) shows them; a file that cannot be read, or is longer
than `codeact.outputChars` characters, shows as unavailable.

**Handles** (T201b): a session's handles come from per-session tables (`HandleTable`, one per kind), so
they resolve only in that session. A cell that ends with one shows it by reference (`Piece.Ref`:
`[handle Out["cell"]: HistoryRange] preview`, never its id). The journal keeps it by reference
(`KeptJson.Handle`); a new kernel gets it back as `Out[cell]` while the table has it (the host did not
restart: then it is gone), and `fx.recall` of it says it has no JSON form. Handle ids are random bytes from
the hands' `IIds`, journaled in the hands' lane, since they reach the kernel in the host's replies.

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
| `kernelEnded {launch, how}` | when the host learns how a kernel ended, before the next launch (key `kernel/<launch>/ended`); `how` is `exited {code?, stderr}`, `notStarted {reason}`, `killed` or `hostStopped` (T135; `golden/step-events-kernel-ended-v1.jsonl` is what it wrote first) | |
| `jobStarted {job, name, command, cwd?, call?}` | before a background job's process starts (key `jobs/<job>/started`; T143, `golden/step-events-jobs-v1.jsonl`) | |
| `jobEnded {job, how, bytes?}` | when the strategy learns how a job ended (key `jobs/<job>/ended`); `how` is `exited {code}`, `killed`, `failed {reason}`, `lost` or `reported {note}` ("Background jobs" above) | |
| `hostCallStarted {call, index, binding, args, approved?}` | before a host call of the running call runs (not the reads: `fs.read`, `fs.list`, `fs.stat`, `recall`, and `jobs.list`, `status`, `output`, `wait`); `approved`: how it ran that no grant in force allowed (auto approvals, a person's answer; T134, absent when none) | `/host/<i>` |
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

### Overflow (spec §6.6, T201a)

What is cut is stored, not lost. `shell.run` stores a cut stream (interpreters.md); CodeAct asks
kernels for all of a cut output (`overflowChars`: a third of `values.argumentMaxBytes`, in characters;
kernel-protocol.md) and stores it; and a cell's string or command result whose text its receipt cuts
to `codeact.outputChars` is stored before the receipt is made (`Receipts.valueBody`). Each goes to the
environment's blob store from the redacted text, and the marker line of the cut text names where (the
blob's file, else its hash); a command's result in a receipt also gets a line per stream it cut. The
grants guard's workplace lists the store's directory as readable (`Workplace.Readable`, from
`Hands.BlobRoot`, journaled in `HandsInfo.blobRoot`): `fs.read` there needs no grant, and a command's
path arguments and inputs there count as in the workspace (listed commands, auto approvals); writes
there are still writes outside the workspace.

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

## Grants (spec §13, §18; T127b)

Before a host call runs, from a cell or a direct tool alike, the turn's grants guard (`GrantGuard`,
attached to the hands by the composition root) asks the grants log ([grants.md](grants.md)) about
what the call needs. A guardrail, not a boundary: the sandbox (T113) is the boundary where a host turns
one on, and it is off by default (T137; Windows v1 has none): then the grants guard is the control
that stands between the agent and what the user's rights reach (spec §18).

**What a call needs** (`Permissions.needs`, pure: from the call's arguments and the workplace, never
the filesystem):

| Binding | Needs |
|---|---|
| `shell.run` | `shell.exec` for each simple command of the line (`ShellSyntax`, below), and `fs.write` for each file a redirection writes outside the workspace or to a protected path |
| `jobs.start` (T143) | what `shell.run` of the same line in the same working directory needs (`Permissions.jobsStart`) |
| `fs.write`, `fs.patch` | `fs.write` for each file outside the workspace or under `codeact.grants.protectedPaths` (default `.git` and the project's directory: hooks run code, configuration changes behaviour) |
| `fs.edit`, `fs.editRange` | as `fs.write` for the file, and `fs.read` for a file outside the workspace: their results (and a stale range's error) show its lines |
| `fs.read` | `fs.read` for a file outside the workspace (content leaves for the model) |
| network bindings | `net.connect host:port` for each endpoint (`Permissions.connects` with `Permissions.endpoint`); no core binding is one yet |
| others (`fs.list`, `fs.stat`, `jobs.status`, `jobs.kill`, …) | nothing: a binding that runs programs, writes, reads outside the workspace or connects must have a rule (`BindingRule`) |

Resources are canonical (`Resource.validate`): commands are their words joined by single spaces
(`ShellSyntax.resource`), paths in the workspace are workspace-relative, others absolute, all
`/`-separated with `..` resolved (with the OS's case rules, as content exclusions compare them). A path
grants cannot name (a UNC share) refuses the call. Symbolic links are not followed.

**Commands** are split by `ShellSyntax.parse` with the environment's shell (sh; PowerShell 7 on
Windows): lists (`;`, `&&`, `||`, `&`, newlines), pipelines, subshells and groups are flattened, and
command substitutions (`$(…)`, backquotes; `$(…)`, `@(…)`, `(…)` in PowerShell) are parts of their own,
before the command that holds them. Quotes and escapes are removed; nothing is expanded, so a word that
the shell expands (a parameter, a tilde, braces; a variable, splatting) is marked. Whatever it does not
follow is **opaque**, and the whole line is one `shell.exec` for a person: control flow, functions,
here-documents and here-strings, process substitutions, arithmetic, script blocks, hashtables, type
literals and member calls, `--%`, unclosed quotes, nesting deeper than 8, more than 256 commands; so is
a line that redirects to a name the shell expands, to a relative path after a `cd`, or that needs more
than 16 grants. A `cd` (and its PowerShell spellings) needs nothing itself; after a `cd` to a plain path
later commands may run in any directory the line may be in (subshells and groups are flattened, so a
`cd` in one may have been undone: the set only grows, and an argument is checked from each), and after
any other `cd` (alone, to `-`, with an option or an expansion) their working directory is unknown.

**Listed commands.** `codeact.grants.allowCommands` (privileged; default `pwd`, `ls`, `cat`, `head`,
`tail`, `wc`, `grep`, `echo`, `git status` and their PowerShell counterparts: commands that read, write
nothing and start no other program) run without a person when written plainly: no word the shell
expands, in a known working directory in the workspace, and no argument or input redirection (`<`)
that names a path outside it (an option with a path or `..` attached counts) or a file content
exclusions keep from models (`GrantSettings.Exclusions`, the turn's): what they print goes to the
model. Deny grants, policy rules and gates still apply: a
listed command is allowed only when nothing but "no grant applies" stands in the way.

**Deciding.** For each need, `Grants.check`: allowed, denied (the call fails `denied` with the reasons
and a reminder not to work around a denial), or a person must decide. Then, by `codeact.grants.mode`
(privileged):

| Mode | A call a person must decide |
|---|---|
| `ask` (default) | its requests are made at once (batched prompts), and the call waits for their answers |
| `auto` | as `ask`, with auto approvals on (below): commands written plainly in the workspace run unasked, sandbox or not |
| `deny` | refused at once, nothing recorded: unattended runs |
| `yolo` | with yolo approvals on (below): it runs unasked, whatever it reaches, sandbox or not; deny grants and policy still refuse |

- **What the guard says.** `Hands.Guard` gives `Ok` with the call's `Approval`s: each need that no
  grant in force allowed and how it ran (`auto`, or `person` with the request's key; listed commands
  and needs a grant covered are not repeated). `SessionKernel.Call` hands them to the step journal
  (`hostCallStarted.approved`, for audits) and to the running action, whose receipt keeps the first 16
  (`Receipt.Approvals`, for clients; the render never reads it, and it is written only when not empty).
  The replay journal's guard result carries them (`null` when there are none, as before).

- **Keys** derive from what asks: `<turn's start event>/<cell or tool call>/<action>/<digest of the
  resource>` (`GrantGuard.key`). A retried call is the same request with the same answer; the same
  command in another cell asks again (or is covered by a broader grant, e.g. answered for the session
  with a suggested pattern such as `npm run **`).
- **Requests** name the session (`Context.Session`), the repository when the composition root knows it
  (`GrantSetup.Repo`, the place: the file or the command's working directory), the turn's first
  sender as principal and the agent; their evidence says which turn and call asked
  (`asked by: session s1, turn 3, t3-s2-1`), the whole command line and why a line was not split.
  This is how the turn shows its pending requests: clients list them with `grants/pending` and
  `grants/subscribe` by session (T130); the grants log owns them, the session opens none.
- **Waiting**: the first denial or unanswered request refuses the call and withdraws the others. An
  **interrupt** cancels the wait (a cell's host call is cancelled with the cell) and withdraws the
  requests, with the reason "the call was cancelled: the turn was interrupted or its cell stopped". The
  wait never counts against the cell's timeout (above, "Kernels"), and a cell stopped while one of its
  calls waits names the call in its receipt. A **steer** that arrives while a call waits withdraws its requests too: the call fails
  ("a message from the user arrived"), and the steer comes at the next boundary. A request times out
  after `codeact.grants.timeoutMs` (the grants service closes it), and the call is refused.
- **A host that stops** mid-wait cancels the turn and so withdraws its requests; after a crash they stay
  pending in the grants log (durable) until answered, timed out or withdrawn: what happens to the turn
  is recovery's (T128).

### Approval modes (spec §13.3; T134)

Auto approvals run, without asking anyone and **sandbox or not**, a `shell.exec` need the rules leave
to "no grant applies" (deny grants, policy rules and gates first, as for listed commands) whose
command stays in the workspace as far as the guard can see (`GrantNeed.InWorkspace`): written plainly,
as listed commands must be (no word the shell expands, every directory it may run in inside the
workspace, no argument or input outside it or excluded from models), and not one of
`codeact.grants.askCommands`. This is opencode's experience: the agent's local commands run, what
reaches outside the workspace asks.

- **What still asks.** Writes outside the workspace or to protected paths (`fs.write`, also a
  redirection's), reads outside it (except the environment's blob store: below), network bindings (`net.connect`), opaque lines, commands with an
  expansion or a path outside the workspace, and `codeact.grants.askCommands` (privileged; matched
  ignoring case after leading assignments and wrappers such as `env`, `sudo`, `nohup`, `time`,
  `timeout`, `nice`, `xargs`, with the program's directory dropped): downloads and remote shells
  (`curl`, `wget`, `ssh`, `scp`, `rsync`, `nc`, …), git's remotes (`git clone|fetch|pull|push|ls-remote`),
  hosting CLIs (`gh`, `glab`), installs, adds and publishes of the package managers (npm, pnpm, yarn,
  Bun, pip, uv, Poetry, Cargo, Go, `dotnet add package|tool install|nuget push`, gem, Bundler, Homebrew,
  apt, apk, dnf, yum, snap, winget, Chocolatey, Scoop) and PowerShell's (`Invoke-WebRequest`,
  `Install-Module`, …), and a shell's inline command line (`sh -c`, `bash -c`, `pwsh -c`, `cmd /c`).
  First installs of components ask through the installer as always.
- **Opaque lines ask.** The guard cannot see whether one names a download or reads outside the
  workspace for the model, so a person judges it.
- **What it trusts without a sandbox** (the default since T137): the command's arguments stay in the
  workspace, but the program runs with the user's rights. A build, a test runner or a script the agent
  wrote can read the user's files, credentials included, and write anywhere the user can. Clients say
  so (`approvals: auto · unsandboxed`, `fx run`'s line), and only the host's config, the command line
  or a person turn the mode on.
- **A sandbox makes it stronger.** With a driver that confines (sandbox.md "Opt-in"), what an
  auto-approved command does is held to the environment's policy: writes to the workspace and the
  environment's own directories, credentials hidden. The same commands run unasked either way.
- **The network is the line.** Nothing closes it without a sandbox, and a confining sandbox leaves it
  open unless the environment's spec closes it: an auto-approved command can send what it reads. The
  ask list is a guardrail of known spellings, not a boundary (a script the agent wrote, or a cell's own
  `fetch`, reaches the network in any mode). Auto-approved commands with the network closed, and asking
  to open it, wait for the egress proxy (spec §18).
- **Where they hold** (`AutoApprovals.decide`, pure): the grants log's check of `approvals.auto` on the hands' driver
  (`GrantSetup.Sandbox`: the composition root's, from the environment's sandbox; `none` when nothing
  confines), in the session's context: a grant allows them (a person's, for the session, the host or a
  repository: the TUI's switch, `fx grants allow approvals.auto '*'`); a deny grant or policy forbids
  them; with no grant they follow the mode (`auto` and `yolo` on, `ask` and `deny` off); a policy that asks leaves
  them off. Policy forbids them everywhere with a deny rule on `*` (the host makes one from a policy
  file that forbids `auto` or pins the mode to another value: `AutoApprovals.policyRules`), or only
  unsandboxed with a deny rule on `none`.
- **Checked per call**, once and only when a need is in the workspace: a switch applies to the next
  command, also in the running turn. In `deny` mode with auto approvals on, what they cover runs and the
  rest is refused.

**Yolo approvals** (`codeact.grants.mode = yolo`, or a grant of `approvals.yolo`): nobody is asked about
anything. Once a call's needs are checked, everything that would ask (or, in `deny` mode, be refused for
want of a grant) runs: commands of any kind (downloads, git's remotes, installs, `sh -c`, opaque
lines), writes and reads outside the workspace or to protected paths, network bindings, and calls
nothing can name in a grant (`CallNeeds.Refused`). The guard says so on each (`ApprovedBy.Yolo`:
activity lines `yolo`, `✓ yolo-approved`); auto approvals still say `auto` for what they cover.

- **What still refuses.** Deny grants and policy rules (and gates) for what the call needs: yolo
  approves what nobody decided, never what someone forbade.
- **Sandbox or not.** Unsandboxed, everything the agent does runs with the user's rights, network
  included; clients say so (`approvals: yolo · unsandboxed`, `fx run`'s line). With a confining
  driver its policy still holds.
- **First installs.** The host's installer approves a component's first install on yolo approvals too
  (`YoloInstalls`, the composition root's): the host's mode, a host grant of `approvals.yolo`, or a
  session in the environment whose `Ensure` asks; checked on `none` (the installer is not confined).
  An environment is created before its first session exists, so a session's switch covers only later
  installs (an environment recreated); the host's mode or a host grant covers the first.
- **Where they hold** (`YoloApprovals.decide`, pure): the grants log's check of `approvals.yolo` on the
  hands' driver in the session's context, as for auto approvals: a grant allows them (a person's: the
  TUI's `/yolo`, `fx grants allow approvals.yolo '*'`), a deny grant or policy forbids them, with no
  grant they follow the mode (`yolo` on, others off). A policy file that forbids `yolo` or `auto`
  for the mode, or pins it to another value, forbids them (`YoloApprovals.policyRules`: a deny rule on
  `*`); a rule on `none` forbids them only unsandboxed.
- **Checked per call**, once and only when something would ask or be refused for want of a grant.
- **A person's switch** (`session/setApprovals`, host.md): `yolo` grants `approvals.yolo` for the
  session; `auto` and `ask` switch it off (revoking the session's grant, and a session deny grant
  where the host's mode is `yolo`). The mode was called `off` before (it checked nothing, deny grants
  included); `yolo` replaces it.

| Slot | Default | |
|---|---|---|
| `codeact.grants.mode` | `ask` | privileged |
| `codeact.grants.allowCommands` | the read-only commands above | privileged: a project can never loosen it |
| `codeact.grants.askCommands` | downloads, remotes, installs and inline shell lines (above) | privileged |
| `codeact.grants.protectedPaths` | `.git`, `.git/**`, the project's directory | privileged |
| `codeact.grants.timeoutMs` | none | only tightens |

## Content exclusions (spec §18)

`ContentExclusions.compile` over the turn's variants (`ContentExclusions.read`: policy, host, project
lists) gives one `Exclusions` for the render (`RenderConfig.Exclusions`), the context log
(`ContextEvent.withhold`), the bindings (`CoreSettings.Exclusions`: `fs.read`, `fs.edit` and
`fs.editRange` refuse excluded files before reading them, so `read` and `edit` do too) and, through the same slots, the egress hooks. Settings that do not compile fail
the turn rather than send excluded content.

## Variants

Every setting is read once per turn through the turn's profile (`CodeActConfig.Profile`), so it is
recorded and replayable (spec §15): `agent.actionSurface`, `model.role.main`, the render's slots
(prompt.md), content exclusions' (egress.md), `values.previewLimits`, `values.inlineMaxBytes`,
`values.argumentMaxBytes` (the largest argument of a host call a cell passes: a file's text for
`fs.write`, a command's input; the kernel's `argumentBytes`, T139), and:

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
| `codeact.grants.mode`, `.allowCommands`, `.askCommands`, `.protectedPaths`, `.timeoutMs` | see Grants |
| `codeact.compaction.mode`, `.threshold`, `.keep`, `.summaryTokens` | `auto`, 80 (percent of the window), 30 (percent), 8192 |

Defaults are provisional until evals decide (spec §1, principle 7).

## Testing

`tests/Fx.CodeAct.Tests`:

- **Units**: tools from codecs and their decoding (texts' names checked), every version of the tools
  rendered as it was (golden: `tools-v1.json` kept, the current version's rewritten with
  `UPDATE_GOLDEN=1`), the loop guard, line edits (line endings kept), `read` receipts, the merged
  history's order, the checks of a result, questions, the recorded surface and tools' version, quotes'
  provenance, the slots.
- **Turns** on the real session host (in-memory store of payloads: every event through its JSON), with the
  script kernel in process and a scripted model that can hold a request: both surfaces doing a small
  coding task (read, edit, run a command, finish with evidence; each request a prefix of the next),
  finishing refused and accepted, a steer delivered at the next boundary, one kernel for the session
  (names declared in earlier cells and turns stay, one launch, never ended), an interrupt that leaves the
  kernel's names, a busy cell killed and the kernel restarted, a kernel that exits during a cell and one
  that died between turns (each end journaled before the next launch, the receipts saying why), the loop
  guard, each text-only policy and a question answered into the next turn, length cuts, empty replies,
  retries, overflow, fatal errors, the step budget, content exclusions for cells and direct tools, an
  unknown tool, calls after `finish`; texts (a 50 KB F# file with double backticks and fences written
  byte for byte, names refused by the codec and by the kernel before anything runs, a conversation framed
  with version 1 of the tools keeping them); a cell's commands sharing its timeout (one cut at what is
  left, the next refused), and a cell stopped while its call waits for approval (the receipt names the
  call and what the cell never assigned); a grant answered after longer than the cell's timeout (the
  cell completes) is in the grants turns.
- **Grants**: the parsers (both dialects case by case; seeded random lines: plain lists split exactly,
  substitutions at any depth flattened in order, and any line split or opaque, never a crash), host
  calls as grants (`PermissionTests`), the guard over the real grants service (listed, asked and
  answered, keys, a broader session grant, policy and deny grants, modes, batched requests, an
  interrupted wait, timeouts, network; auto approvals: workspace commands unasked without a sandbox and
  with one, nothing in the grants log, downloads, installs, opaque lines and writes outside asking, a
  person's session grant and deny grant, policy everywhere and only unsandboxed, the decision and the
  policy rules a policy file makes; `cd` and the ask list in `PermissionTests`), and turns on the real session host for both surfaces (answered,
  denied, refused by policy, a write outside the workspace, interrupts, steers, timeouts, mode deny, a
  host stopping and crashing mid-wait), with replay and determinism of the logs and requests.
- **Replay**: the recorded logs render, through the render alone, to exactly the requests sent; the same
  inputs give the same context log, requests and session events.
- **Compaction** (`CompactionTests`, a catalog with a small window): prepared in the background beside a
  step's cell and recorded before the next call, the summary request being the previous call's request plus
  the instruction; the threshold before a call; an overflow compacted and sent again; a cell's
  `history.compact` with a focus, also in the turn's last cell; the window filled during a reply; a failed
  summary; modes `off` and `overflow`; each replays into the requests sent.
- **Steering**: a steer after every result of a step's reply, a queued prompt in a turn of its own.
- **Background jobs** (`JobTests`): a job's output read from offsets without cutting a character, its
  tail by default and its last megabyte kept; a cell that starts a job, waits for it and reads its
  output (both ends journaled, the receipt naming the job, the notice delivered at a step boundary); a
  turn that ends waiting for a job and resumes with its notice; a killed job (no notice); a wait cut at
  what is left of the cell's timeout and the next refused; `finish` waiting for an unknown or ended job;
  a host restart with a running job (journaled lost, the next turn told) and one while a turn waits for
  it (the host's lost notice resumes the session); replay of the logs into the requests sent. Grants:
  `jobs.start` needs what `shell.run` needs (`PermissionTests`).
- **Crash recovery** (`RecoveryUnitTests`, `CrashTests`): the journal's version 1 read back (and
  `kernelEnded` as first written); how kernels ended in words; finding the
  call in doubt; the card and its answers; each decision's receipt; kept values. End to end, the host
  process is stopped mid-cell as a crash would stop it and started again on the same stores (with a
  command that hangs the first time): the decision and its evidence (output, host calls, workspace
  changes), nothing run before the answer, one decision over two restarts, every answer path (rerun
  included; a rerun binds its cell's texts again in the new kernel), calls after the one in doubt, a crash during a model call (abandoned), values back after a
  host restart and a kill, `fx.recall`, launch numbers; each replays into the requests sent.
- **Providers**: turns through `ModelAccess` with `anthropic.messages` and `openai.responses` and their
  catalogs, on streams in each provider's documented format (tool calls and results, tools from codecs,
  breakpoints, a stable cache key).
- **Live** (the pinned Bun; skipped offline, required in CI): TypeScript cells on the bun-ts kernel for
  both surfaces, a rejected `finish`, a cell writing a file with backticks from its texts (type-checked;
  a reserved word refused), a turn in an environment of `fx.env.local` opened through `IEnvs`, TypeScript
  cells that start jobs, wait for one and end a turn waiting for another (and the prompt's jobs examples
  type-check in the session's kernel), and a host stopped during a TypeScript cell (the decision shows what it printed; `Out` and `fx.recall` give a
  kept value back to the new kernel).

Native AOT: `codeact-hybrid`, `codeact-text-only` (a session on the session host with the script
kernel, an in-memory workspace and a scripted model; the receipts, the context log and the logs replayed
into the requests), `codeact-compaction` (a summary prepared beside a cell and one a cell asked for: the
summary requests as the calls before them plus the instruction, the collapses, the logs replayed into the
calls), `codeact-recovery` (two restarts mid-cell, the decision, a rerun, `recall`; the
card, the journal, the receipts, the session's events), and `codeact-shell-syntax`, `codeact-grant-needs`
and `codeact-grant-guard` (the parsers, host calls as grants, the guard over the grants service on the
fake clock, auto approvals and their decision), on all eight targets.

## Left for later

| What | Task |
|---|---|
| The decision ledger takes over `decision.outcomeUnknown` (its cards as Session-scope decisions, anchors) | T301 |
| Partial output of commands in doubt (`shell.run`, `bash`): they run as envd processes (spawned since T142); after a restart, resume their output logs by byte offset, and journal the process | T205 |
| Background jobs that survive a host restart: reattach to envd's processes and resume their output by byte offset (today a restart loses them, reported `lost`) | later |
| Kernels with snapshots (`snapshot`/`restore`) restored instead of kept values | interpreters that have them |
| Values in envd's blob store (remote environments); handles inside data | T501; T213 |
| Network bindings (`fx.http`, …) and their `net.connect` rules; the egress proxy for commands' own connections | T113 and the bindings' tasks |
| Following symbolic links for workspace checks; commands in `sh -c`/`pwsh -c` strings split too | the sandbox is off by default (T137), so nothing catches them there |
| Withdrawing grant requests a crash left pending (the call in doubt never ran; recovery knows its keys) | when recovery holds `IGrants` |
| Exact replay of model streams and variant reads from a journal | done: T131, [replay.md](replay.md) (`Recording.codeact`) |
| Activity events and per-cell diffs; `Read` activity as provenance | T205 |
| A person's compaction (a session command, the TUI's `/compact`) beside the model's `fx.history.compact` | when clients ask (T130's successors) |
| Compaction as the bundled extension `fx.compaction` (spec §16.4), a summarizer contributed by extensions | with a strategy contribution type (below) |
| Brokered bindings (`fx.ask` for the model's own questions, `fx.llm`, agents) | T213 and their tasks |
| A strategy contribution type for extensions (the SDK would need sessions, the render and interpreters) | when a second strategy exists |
