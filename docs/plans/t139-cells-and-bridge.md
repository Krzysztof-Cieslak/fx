# T139–T143, T203a, T204a: cells that do what tools do

A plan decided with the person (the project owner) on 2026-11-28, after reviewing the dogfooding
sessions (T132) and comparing them with NOOA. It covers seven tasks, one lane each, landed as one
stack in this order:

1. **T139** Cells: large arguments
2. **T203a** Readable results
3. **T141** Cells: outcomes that say why
4. **T140** Cells: text without escaping
5. **T204a** `fs.edit` and ranged reads
6. **T143** Background jobs
7. **T142** Shell results with structure

The first four remove failures that fx causes; the last three give cells a bridge worth calling.
Once a task lands, its rules belong in `spec.md`, the module docs and `AGENTS.md`. Keep this file as
the record of why.

## Why

Seven dogfooding sessions (853 cells, all on the `cells-only` surface, Claude Opus) were reviewed
from the host's stores. The latest, T113b, ran 116 cells for $12.52.

**What the model does with cells:**
- 40–60% of cells run a shell line (`grep …; cat …`) and print its output with `console.log`; 82%
  print something; 72% make at most one host call that acts.
- Across all 853 cells: no `Promise.all`, 2 `JSON.parse`, and 13 uses of `Out[…]` or `recall`, all of
  them to print a cut preview again.
- Every session rebuilds the usual tools inside the kernel: `sh` (bash), `show(path, a, b)` (read with
  line numbers), `rep`, `patchReplace` and `subst` (find and replace that throws when the text is
  missing), `put`, `putBig`, `putRaw` and `save` (writes in 12 KB chunks through `cat >>`, and an LCS
  diff turned into `fs.patch`).

**Failures fx caused:**
- **A 16 KiB cap on host-call arguments**, while results have none. `fs.write` of `tasks.md` (43 KB)
  failed, and `shell.run`'s stdin has the same cap.
- **Backticks.** F# identifiers in double backticks and Markdown code spans end TypeScript template
  literals (`String.raw` failed twice). The model's workaround, `§` standing for a backtick, turned a
  real `spec §18` into ``spec `18``.
- **Wasted output:** 59 KB of the 183 KB of code the model wrote was in cells that failed, mostly file
  bodies sent again.
- **A failed cell leaves its names declared but unassigned:** `LB` was `undefined` in the next cell, so
  a 9 KB body was sent again.
- **Hand-rolled splicing:** `indexOf` returned -1, the code was appended to the end of `Host.fs`, and
  recovering took 5 cells.
- **Previews cut strings at 120 characters:** 40 of 76 cut string previews were followed by a cell that
  only printed the value again.
- **Approval waits inside the cell timeout:** four cells were stopped at 600 s while their host calls
  waited for the person to approve `strace` and `pkill`. The receipt said only "timed out", so the
  model assumed hung processes. That cost about 40 minutes of a 91-minute turn, and the requests were
  withdrawn as "the turn was interrupted".
- **Polling** with `setsid nohup … &` and `setTimeout` loops, for test runs and CI.

**What NOOA sees** ([arXiv 2607.20709](https://arxiv.org/abs/2607.20709),
[NVIDIA-NeMo/labs-OO-Agents](https://github.com/NVIDIA-NeMo/labs-OO-Agents)): the same model
behaviour. In their SWE-bench shell bake-off the agent ran about 12 searches per session and used
the structured matches 0 times; they call escaping in inline edits their "#1 error class". Their
answer is an object API shaped for that behaviour:
- `shell.run` returns text that also carries `.matches`;
- `read(path, lines)` returns a region, and `replace(region, new)` edits it without copying text;
- `write_file`, and `stdin=` instead of heredocs;
- returned strings shown up to 10,000 characters;
- background jobs (`self.jobs`).

Their cells run in the agent's own process, so nothing caps arguments.

The failures above are fx's, not CodeAct's. These tasks remove them and give cells the kind of bridge
NOOA's agents have. **The default action surface does not change** (T218 decides it).

## Decisions (fixed)

1. **Large arguments:** host-call arguments get their own inline limit now
   (`values.argumentMaxBytes`, 8 MiB). Real blob arguments stay T201's; remote environments (T501)
   need them, so that big content does not travel to the host and back.
2. **Text without escaping:** an optional `texts` member on the `cell` tool, not a separate write
   tool. It serves both surfaces, since `hybrid` includes `cell`.
3. **Approval waits:** a person's wait never counts against a cell. Not "exclude every host call's
   time" without a bound: a cell looping over commands must still time out. *Changed in the T141 lane:*
   the kernel stops a cell's clock while any of its host calls is in flight, and the host bounds the
   cell's commands with the cell's timeout instead, timed after the grants guard. The planned `cell/hold`
   notification was dropped: replay answers the guard from one journal entry without running it, so a
   notification sent from the guard would be missing in replay and the kernel's input would diverge.
4. **Kernel protocol stays version 1:** changes are optional fields and notifications that an older
   kernel or host ignores. If a lane finds a change an old peer would misread, it is version 2
   ([kernel-protocol.md](../modules/kernel-protocol.md)).
5. **One implementation per behaviour:** hybrid's `edit` tool becomes a host call of `fs.edit`; the
   rendering of cells' results reuses hybrid's receipts (`Receipts.ran`, `Receipts.read`).
6. **No measurement gate:** after each lane, run one dogfooding session and look at its cells.

## T139 Cells: large arguments

**What:**
- Host-call arguments travel inline up to `values.argumentMaxBytes`: `fs.write`'s text,
  `shell.run`'s stdin, `fs.patch`'s lines. It is a new core slot: 8 MiB by default, at most 12 MiB,
  so that a message stays under Fx.Rpc's 16 MiB.
- `values.inlineMaxBytes` (16 KiB) keeps bounding the values a kernel keeps (`value/encode`,
  `recall`) and `finish`.
- An argument over the limit fails with `invalidArguments`, naming the limit and the way out:
  `fs.patch` in parts.

**Code map:**
- `src/Fx.Kernel/KernelProtocol.fs`: `KernelLimits` gains an optional `argumentBytes`
  (`defaultLimits`, the `kernelLimits` codec). Absent means `inlineBytes`, so an older peer behaves as
  today.
- `packages/kernel-bun-ts/src/kernel.ts`: `hostCall` encodes arguments with `argumentBytes` (the
  `limits` field). The script kernel (`tests/aot/Fx.Aot.Scenarios/Scenarios/Kernel`) does the same.
- `src/Fx.Variants/Slots.fs`: the slot, next to `inlineValueMaxBytes`, in `Slots.all`.
- `src/Fx.CodeAct/Settings.fs`, and `CodeAct.fs` where it builds the kernel's limits: read the slot
  per turn and pass it.
- **Check what records arguments.** The step journal keeps 400 characters (`StepJournal.ArgsChars`); the
  replay journal keeps digests of inputs. No event may hold a multi-MB argument (events are at most
  1 MiB).

**Tests:**
- Kernel conformance, on both kernels: a 1 MB argument reaches the host intact; one over the limit
  fails `tooLarge`; kept values still respect `inlineBytes`.
- CodeAct: a cell writes a 200 KB file with `fs.write` and passes 100 KB on `shell.run`'s stdin; the
  step journal and the replay journal stay small.
- A new core slot changes golden files: `tests/Fx.Variants.Tests/golden`, `variants.d.ts` and
  `fixtures.ts` in `tests/Fx.Contracts.TypeScript.Tests/golden`, and every profile id.

**Docs:**
- spec §6.6: arguments are copied up to a limit; blob arguments are T201's.
- `kernel-protocol.md`: the limits.
- `codeact.md`, and `variants.md` if it lists the slots.

**Done when:** a cell writes a 43 KB file with one `fs.write`.

## T203a Readable results (split from T203)

**What:**
- **Strings.** A string a cell ends with is shown as text, not as a 120-character quoted preview:
  `Out["t1-s1-1"] (string, 700 chars):`, then the text, cut in the middle at
  `codeact.output.maxChars` as printed output is.
- **Values a binding returned** are shown the way hybrid's tools show them:
  - a `RunResult` as its command, output and exit code (`Receipts.ran`);
  - a `FileText` as numbered lines in a file piece (`Receipts.read`), which counts in `SeenRanges` and
    is withheld when content exclusions cover the file;
  - a `Written` as its path and line count.
- **Other values:** previews with larger defaults (`values.previewLimits`, for example 4,000
  characters, 50 items, depth 4, strings of 1,000).
- **The prompt pack** (`BunTs.promptPack`, `extensions/Fx.Ext.InterpreterBunTs/BunTs.fs`):
  - say that the last expression is shown, strings and bridge results in full up to a limit;
  - print several things with `console.log`;
  - keep values in variables to pass them on;
  - examples end with the value, not with `.stdout`.

**Code map:**
- **Kernel:** it remembers which binding produced each object it handed to a cell (a `WeakMap`, as for
  handles) and reports it on a completion value: an optional `from` on the `completed` outcome's value
  info (`packages/kernel-bun-ts/src/values.ts`, `kernel.ts`; the script kernel too).
- **Host:** for a known `from`, it reads the value (`value/encode` with a `maxBytes` large enough for
  what is shown), decodes it with that binding's return codec, and renders it with hybrid's receipt
  for the type. If anything fails, it shows the plain preview. For strings: `value/encode`, then render
  as text.
  - `src/Fx.CodeAct/Receipts.fs`: `outcomeText`, `cell`.
  - Where the strategy builds a cell's receipt in `CodeAct.fs`.
- **Alternative,** if the kernel side is awkward: match the completion value against the results of
  that cell's host calls by digest of their canonical JSON, with no protocol field.

**Tests:**
- Receipt golden files for each type.
- Kernel conformance: `from` is reported for a host result and absent for plain data, on both kernels.
- CodeAct: a cell ending with `await fx.shell.run("ls")` shows the listing in its receipt; a
  `FileText` shown counts as seen.
- Variant golden files, since the default limits change.

**Docs:**
- spec §6.6: what the model sees of a completion value.
- `kernel-protocol.md` (`from`), `codeact.md` (receipts), `interpreters.md` (prompt pack).

**Done when:** a dogfooding session's first `ls` or `git log` cell shows its output, with no second
cell to print it.

The rest of T203 (outlines of large files, logs, data) becomes T203b.

## T141 Cells: outcomes that say why

**What** (as built; the lane changed the design of the clock, see decision 3):
- **Approval waits do not count.** The kernel counts only a cell's own time: its clock stops while any
  host call of the cell is in flight; an interrupt still stops the cell at once. So that a cell looping
  over commands still ends, the host gives a cell's `shell.run` calls at most the cell's timeout between
  them, timed around the binding after the grants guard (a person's wait counts against neither).
- **A cell stopped while waiting says so.** A timed-out or cancelled cell's receipt names the host
  calls it stopped while the guard had them wait for approval, e.g.
  `shell.run: strace -f ls (stopped while it waited for its approval)`. Their requests are withdrawn
  with "the call was cancelled: the turn was interrupted or its cell stopped", not "the turn was
  interrupted". (An interrupted turn has no cell receipt: the render closes its calls.)
- **Unassigned names.** When a cell stops early, the names it declared but never assigned are listed
  apart: `Declared: tk, tl, i` and `Not assigned (the cell stopped first): c1`.

**Why not `cell/hold`** (the plan's host-to-kernel notification while the guard waits): replay answers
the guard (`Hands.Guard`) from one journal entry without running it, so a notification sent from inside
the guard would be missing in replay, and the kernel's input (journaled writes) would diverge.

**Code map:**
- **Kernels:** bun-ts's `CellClock` (`kernel.ts`) pauses around `hostCall`; the script kernel pauses its
  `CancelAfter` around its host call. No protocol message.
- **Host:** `CellCommands` and `SessionKernel.Call` (`src/Fx.CodeAct/Kernels.fs`): the bound added to
  `shell.run`'s `timeoutMs`, `Clock.timed` on the hands' clock (recorded), `cellTimeout` once used up;
  the calls the guard has not answered (`CellRecord.Waiting`). `GrantGuard.fs`: the withdrawal reason.
- **Unassigned names:**
  - `CellResult` gains an optional `unassigned`: the declared names (replMode's hoisted ones) still
    `undefined` after a runtime error, a timeout or a cancellation (`kernel.ts`'s `result`; the script
    kernel: names not bound).
  - `Receipts.cell` renders it.

**Tests:**
- Kernel conformance, on both kernels: a host call answered after three times the cell's timeout lets
  the cell complete; the cell's own time goes on after a host call and runs out; unassigned names after
  a throw and a timeout.
- CodeAct: a grant answered after more than the cell timeout (the call runs, the cell completes); a
  long command cut at the cell's timeout and the next refused; a cell stopped while its call waits for
  approval; the receipt's lines as units; the withdrawal reason in the grants log.

**Docs:**
- `kernel-protocol.md` (`timeoutMs`, `unassigned`), `codeact.md` (the cell timeout, receipts, grants),
  spec §6.5.

**Done when:** a cell that asks to run a command not on the auto list can wait as long as the person
takes, and no receipt says "timed out" because of a person.

## T140 Cells: text without escaping

**What:**
- The `cell` tool takes optional `texts`: names to text, bound as top-level constants before the code
  runs.
- File bodies, commit messages, patches and stdin go there instead of into TypeScript literals, so
  backticks need no escaping:

```json
{"code": "// intent: add the native layer\nawait fx.fs.write(\"src/X.fs\", posix)", "texts": {"posix": "module …"}}
```

**Code map:**
- **Tool:**
  - `src/Fx.CodeAct/Tools.fs`: `cellCodec` gains `texts`, an object from identifier to string, with
    the names checked and their count bounded; `ToolAction.Cell` carries them.
  - A new tool schema is a new version of the tools section: a conversation keeps the one its frame
    recorded (codeact.md, "Action surfaces").
- **Binding:**
  - Before `cell/run`, bind each text with `value/decode {name, value}` (an inline string).
  - A name the kernel refuses (`invalidName`) fails the cell before anything runs, and the receipt says
    which.
  - Strings over 16 KiB passed on to `fs.write` need T139.
- **Journal and recovery:**
  - The texts are part of the tool call's arguments, so the reply and `callStarted` already hold them.
  - A cell run again after a crash binds them again.
  - The receipt does not repeat them.
- **Prompt pack:** use `texts` for any content with backticks or more than a few lines; add an example.

**Tests:**
- Tool-schema golden files and frame renders.
- A cell writes a 50 KB F# file with double-backtick identifiers and Markdown fences through `texts`,
  byte for byte.
- Invalid names.
- A recorded session that used `texts` replays.

**Docs:**
- spec §6.9 (the cell tool), §6.10 (example cells).
- `codeact.md`, `interpreters.md`.

**Done when:** writing a new F# file with backticks in its comments takes one cell.

## T204a `fs.edit` and ranged reads (split from T204)

**What:**
- **Ranged reads.** `fx.fs.read(path, { lines: [first, last] })` returns those lines as a range:
  `{path, start, end, text, digest, lines}`. `digest` is the whole file's; `lines` is its line count.
  Without `lines`, it returns the whole file, as today.
- **Edits by text.** `fx.fs.edit(path, change)`, where `change` is `{ old, new, all? }` or a list of
  them:
  - changes apply in order to the text read;
  - each `old` must occur exactly once unless `all` is set;
  - the result is one patch, checked against the digest read;
  - errors read as hybrid's `edit` already says them (missing, ambiguous, nothing to change).
- **Edits by range.** `fx.fs.edit(range, text)` replaces exactly the range's lines if they are
  unchanged; otherwise it fails `stale` with the current lines.
- **Fresh ranges.** Both return `{path, digest, lines, ranges}`: a fresh range for each change, so the
  same lines can be edited again.
- **One implementation:** hybrid's `edit` tool becomes a host call of `fs.edit`.

**Code map:**
- `src/Fx.Interpreters/CoreBindings.fs`: `fsEdit`, and ranges in `fsRead`, over `FsOps.readRange` and
  `FsOps.applyPatch`. Move `occurrences`, `replaceFirst` and `lineEdit` from `src/Fx.CodeAct/Hybrid.fs`
  there.
- **The argument is a path or a range.** A union codec, declared in TypeScript as `string | Range`.
  If that makes the declaration awkward, use two bindings (`fs.edit`, `fs.editRange`): decide in the
  lane, and make the spec say the result.
- **Guards:**
  - grants: a `BindingRule` like `fs.patch`'s (writes outside the workspace and protected paths ask),
    in `Permissions.core`;
  - the new binding's paths in `SandboxGuard.paths`;
  - content exclusions as `fs.read` applies them.
- **Replay:** `fs.edit` is made of `FsOps` calls that are already recorded; a recorded session that
  uses it must replay.
- **Rendering and prompt:**
  - receipts: T203a's rendering of a range (numbered lines, a file piece);
  - prompt pack examples: read a range and edit it; edit by `old` and `new`.

**Tests:**
- Binding tests: unique, ambiguous, `all`, missing, CRLF files, a stale range, several changes in one
  call, fresh ranges.
- Grants tests on both surfaces; hybrid's `edit` tests unchanged, passing through the binding.
- Replay coverage.

**Docs:**
- spec §6.8: this API (the edit guard stays T204b).
- `codeact.md`, `interpreters.md`.

**Done when:** dogfooding sessions stop defining their own `rep` and `patchReplace` helpers.

## T143 Background jobs

**What:** `fx.jobs`:
- **`start(command, {name?, cwd?, stdin?})`** returns `Job {id, name, command}`. The command runs in
  the environment's shell, sandboxed and granted as `shell.run` is, and the cell goes on.
- **`list()`, `status(id)`** (running, or its exit code, and the bytes of output so far), and
  **`output(id, {from?, maxChars?})`** (text from a byte offset, bounded).
- **`wait(id, {timeoutMs?})`** returns the status when the job ends or the wait times out. It runs on
  the cell's clock: it is a wait for a process, not for a person.
- **`kill(id)`**.
- **When a job ends,** a `JobFinished` input (job, exit code, last lines) reaches the session:
  - a running turn sees it between steps, as a steering message;
  - a turn that ended `waiting` on the job resumes.

  The session machine already supports this (`Event.JobNotified`, `AwaitingJobs`).
- **`finish({kind: "waiting", jobs, note})`** names jobs of the table: `Kernels.jobs` checks against it
  instead of `[]`.

**Code map:**
- **The job table:** one per session on the host, next to `SessionKernel` (`src/Fx.CodeAct/Kernels.fs`),
  living in the hands' scope as kernels do. Processes start through the hands' sandboxed `Shell.Spawn`
  (`src/Fx.Effects/Shell.fs`); envd keeps their output with byte offsets.
- **Grants:** `jobs.start` asks as `shell.exec` does (commands split, auto approvals,
  `codeact.grants.askCommands`).
- **Journal and replay:**
  - Jobs' starts and ends are step events: new cases, with a golden file of their own;
    `step-events-v1.jsonl` is never regenerated.
  - `Shell.Spawn` and process reads are already recorded capabilities; add a recorded session that
    starts a job, waits for it and reads its output.
- **Host restart:** jobs still running are reported once as lost (a `JobFinished` with a note), as a
  kernel's end is. Reattaching to envd's processes is later work.
- **Clients:** the TUI shows a job's start and end as activity lines.
- **Prompt pack:** run long builds, test runs and `gh pr checks --watch` as jobs; end the turn
  `waiting` on CI.

**Tests:**
- CodeAct with the script kernel and a scripted model: start a job, read its output, wait for it, kill
  one.
- A turn that ends `waiting` resumes when its job ends.
- Grants; a host restart (lost jobs); replay.

**Docs:**
- spec §5 (jobs in sessions) and §6.5 (the bridge).
- `codeact.md`, `sessions.md`, `tui.md`.

**Done when:** watching CI or a long test run is one cell that starts a job, then a `waiting` result;
dogfooding sessions no longer use `setsid nohup` or sleep loops.

## T142 Shell results with structure

**What:**
- **Partial output on timeout.** A command that times out keeps the output it printed, and it is killed
  with its children. Today `shell.run` returns an empty `stdout` and `stderr` on a timeout.
- **Matches for plain searches.** A plain search also returns `matches`: one-line ranges (T204a's type)
  that `fs.edit` takes.
  - A plain search is `grep -n`, `rg -n` or `git grep -n`, as one simple command by
    `ShellSyntax.parse`, with no pipes and nothing cut.
  - Matches are attached only when every line equals its file's line (checked through `FsOps`), so an
    anchor is never wrong; otherwise there are none.
- **A hint.** The receipt of a search with matches ends with one line on editing one, e.g.
  `fx.fs.edit(r.matches[0], …)`, as NOOA's `ShellTools` does.

**Code map:**
- `CoreBindings.shellRun` (`src/Fx.Interpreters/CoreBindings.fs`): read output from `Shell.Spawn`
  until the process exits or the time runs out, instead of `Eff.timeout` around `Shell.run`, which
  drops the output.
- `RunResult.matches`, optional.
- Parse search output conservatively.
- Hybrid's `bash` tool gets the same results.

**Tests:**
- A timeout with partial output, and its children killed.
- A search with matches.
- Every disqualifier gives no matches: a pipe; `-c`, `-l` or context flags; output that was cut; a path
  with `:`; a line that changed since.
- `fs.edit` of a match; replay.

**Docs:**
- spec §6.7.
- `codeact.md`, `interpreters.md`.

**Done when:** a timed-out command's receipt shows what it printed, and a grep hit can be edited
without copying its text.

## Every lane

- **Docs:** spec and module docs updated in the same lane, as listed per task.
- **Kernel changes:**
  - regenerate the kernel's files: `UPDATE_GOLDEN=1 dotnet test --project tests/Fx.Ext.InterpreterBunTs.Tests`,
    then rebuild;
  - review `src/protocol.d.ts`, the declaration golden files and the prompt renders;
  - both kernels (the script kernel and bun-ts) pass `KernelConformance`.
- **Replay:** `tests/Fx.Replay.Tests/golden/session-v1.jsonl` must keep replaying. Re-record it only
  deliberately (`RERECORD_GOLDEN=1`), and say why.
- **Landing:**
  - `lane check` and the CI commands before pushing;
  - each lane on top of the previous one in the stack (`lane new <task> --base <top-branch>`);
  - after it lands, one dogfooding session with a look at its cells. Rerunning a cell classifier
    like the one in the review is enough measuring.

## Out of scope

- Handles for agents, `fx.llm`, `fx.judge` and the ledger (T213 and later).
- `fx.repo` navigation (T202).
- The edit guard (T204b).
- Blob arguments and output overflow into blobs (T201).
- Smart previews of large files, logs and data (T203b).
- Changing the default action surface (T218).

## Notes for the implementer (fx working on fx)

- **Until T139 lands and the person rebuilds `fx`,** a cell cannot pass more than 16 KiB to a host
  call. Change large files with `fs.patch` line edits (read the file, find the lines, patch with its
  digest), never `fs.write` of the whole file.
- **Until T140 lands and the person rebuilds `fx`,** never put F# or Markdown inside a TypeScript
  template literal. Use double-quoted strings or arrays of lines (`fs.patch`'s `lines`), where backticks
  need no escaping. Once it has, pass such text in the `cell` tool's `texts` and use it by name.
- **Until T141 lands and the person rebuilds `fx`,** a command not on the auto list waits for the
  person, and a cell that waits longer than 10 minutes is stopped with only "timed out". Ask the
  person; do not retry with `pkill`.
