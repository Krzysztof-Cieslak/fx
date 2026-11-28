# Research: prompting models to work in the kernel

Status: **proposal** (research, November 2026). Nothing here is decided; it suggests an implementation path.
Spec: §6.5 (kernels), §6.6 (values and references), §6.9 (action surfaces), §6.10 (example cells),
§15 (variants and evaluation). Tasks it touches: T139–T143, T203a, T204a (landed), T201 (values &
references), T217 (eval harness), T218 (default action surface). Builds on
[codeact.md](../modules/codeact.md), [interpreters.md](../modules/interpreters.md) and
[prompt.md](../modules/prompt.md). Measured with `bun scripts/cells.ts` (§2.1).

**Question.** Models given cells mostly use them as a slower tool call: run one command, print its
output, read it, then type what they need into the next cell. Should fx prompt them more strongly to
work *in* the kernel instead: keep values in variables and pass them on by name, not through their own
context? If so, how: louder rules, a principle with its reasons, examples, or feedback at the moment it
happens?

**Answer.** Yes, but not with louder rules. Use four levers together:
1. Add a short, language-neutral **"working in the kernel"** section to the instructions that says
   *why*.
2. Give the interpreter's prompt pack **examples that carry values across cells** and process them in
   the kernel.
3. Add a **receipt hint** when a cell retypes text that an earlier receipt showed.
4. Put it behind a **variant slot**, measured on dogfooding sessions now and on the eval harness (T217)
   later.

Before changing the prompt, measure a baseline on the current binary. T139–T143, T203a and T204a
removed many of the causes visible in the sessions below, so tuning against those sessions would mean
tuning against fixed bugs.

## 1. What the model is told today

A CodeAct prompt has three parts (`Instructions.sections`): the instructions (`Instructions.text`), the
`tools` section, and the interpreter's prompt (bun-ts: `BunTs.promptPack`).

| Part | What it says about values |
|---|---|
| Instructions | "Work in small steps you can check: look before you change something…". Nothing about values. |
| Tools (cells-only) | "Everything you do … happens in cells." |
| Tools (hybrid) | "`cell` … use it for work of several steps, for values you want to keep." |
| Prompt pack | The last clause of one long bullet: "Keep large values in variables and pass them on by name." Six examples, each a single operation; none uses a value from an earlier cell. |

This leaves three gaps:
- **The principle is buried and given no reason.** Nothing tells the model that its context is re-read
  on every step and that retyping text is slow and lossy, while the kernel keeps values exactly and
  costs nothing to hold them.
- **The instructions pull the other way.** "Small steps … look before you change" reads as one
  look-and-print cell per round trip.
- **No example shows the habit.** Examples are the strongest signal a prompt gives, and ours show
  single calls.

## 2. What models do

### 2.1 fx dogfooding: eight sessions before T139, 1,051 cells (Claude Opus 5.5)

These are every session in the dogfooding store (November 26–27, 2026, fx working on itself) recorded
with the binary before T139. The command was `bun scripts/cells.ts --latest 8`. The script reads each
session's context log (`context/<session>`) from the event store and classifies every cell:

| Kind of cell | Cells | Share | Since addressed by |
|---|---:|---:|---|
| Run a command and print | 485 | 46% | – |
| Read and print | 68 | 6% | – |
| Run and read, then print | 41 | 4% | – |
| Write (other: chunked writes, `fs.patch`) | 123 | 12% | T139 (the 16 KiB argument limit made chunks) |
| Edit in memory (`replace`) and write back | 79 | 8% | T204a (`fs.edit`, `fs.editRange`) |
| Process in the kernel (loops, filters, parsing) | 73 | 7% | – |
| Write a big literal (≥ 1,000 characters) | 66 | 6% | T139, T140 (`texts`) |
| Look at an earlier value again | 48 | 5% | T203a (previews cut strings at 120 characters) |
| Finish | 28 | 3% | – |
| Poll or wait (sleeps, `setTimeout`) | 20 | 2% | T143 (jobs) |
| Other, define helpers | 20 | 2% | – |

- **Reuse is mostly helpers.**
  - 52% of cells call a function an earlier cell defined. These are helpers the model wrote to work
    around gaps in the bridge: `sh` (a shell wrapper), `put` and `putBig` (writes in chunks, with `§`
    standing in for a backtick), `rep` (replace or fail).
  - Only 22% use data an earlier cell computed. Names bound to a literal, such as a workspace path, are
    not counted.
- **Text retyped from receipts.**
  - 89 cells (8%) contain text that an earlier receipt had shown verbatim: about 68,600 characters,
    roughly 17,000 output tokens, the slowest and most expensive tokens.
  - Most of it is the `old` side of in-memory edits and file bodies copied back. This is a lower bound,
    since edited copies are missed.

**Method** (`scripts/cells.ts`):
- **Kinds.** A cell gets the first kind whose rule matches, in this order:
  1. finish, jobs, polling;
  2. in-memory edits, then big literals, then writes from `texts`;
  3. `fs.edit`, then other writes;
  4. helper definitions only;
  5. processing;
  6. run, read or both, then print;
  7. looking again at an earlier value.
- **What a cell calls.** Bridge calls are found in the cell's code with strings and comments removed.
  A call of a helper counts as the calls in that helper's body.
- **Earlier names.** These come from the receipts' `Declared:` lines, and a new kernel forgets them.
- **Retyped text.** A string literal or text of at least 200 characters is probed with 120-character
  windows, 60 characters apart. A probe counts when it appears verbatim in an earlier receipt; file
  pieces are compared without their line numbers. The retyped characters are the span the matching
  probes cover.
- **Accuracy.** It is a heuristic for comparing the same kind of work across prompts and binaries, not
  a TypeScript parser.

### 2.2 NOOA trajectories: five CyberGym runs, 1,788 Python cells (GLM-5.2, Nemotron-3-ultra, DeepSeek-v4-flash)

The cells were classified with Python's `ast`, in a one-off analysis:

| Kind of cell | Share |
|---|---:|
| A single `shell.run`, printed | 36% |
| Loops and comprehensions | 22% |
| Defines a helper | 7% |
| Several tool calls, no logic | 4% |
| Uses a variable an earlier cell assigned | 11% |

NOOA's bridge is richer and its agents' prompts differ, yet one call per cell is still the most common
cell. Strong and weaker models alike default to it.

### 2.3 Elsewhere

- **CodeAct** (Wang et al., 2024). Code actions beat JSON tool calls, mostly because code composes
  calls and keeps intermediate results. Open models needed fine-tuning (CodeActInstruct) to benefit
  fully; prompting alone helped them less.
- **smolagents' `CodeAgent`** relies on worked examples (thought, code, observation) far more than on
  rules.
- **Anthropic's "code execution with MCP" and Cloudflare's "Code Mode"** make the same argument: let
  the model write code against an API, and keep intermediate data in the execution environment,
  filtered before anything returns to the model. The token savings they report come from data that
  never enters the context.
- **Anthropic's prompting guidance for recent Claude models:**
  - instructions work better with their reasons;
  - examples are followed closely;
  - aggressive wording ("CRITICAL: you MUST …") now makes models over-apply a rule, and plain wording
    is enough.

## 3. Options

| Lever | What changes | Strength | Cost and risk |
|---|---|---|---|
| A. Louder rules (MUST, NEVER) | Wording | Weak for recent models, and rigid | Over-application (batching everything, never looking). Rejected |
| B. A principle with its reasons | Instructions | Medium | A few hundred tokens of prefix |
| C. Examples that show the habit | Prompt pack | Strong | Examples must type-check and stay current |
| D. The `cell` tool's description | Tools schema | Medium | A new tools version (`Tools.Version` 3). Defer unless B and C fall short |
| E. Receipt hints | Receipts | Strong at the moment it happens | False positives; the check must stay pure and replay-safe |
| F. References by default | Bridge shape | Strongest | T201's scope (blobs, handles); complementary, not this proposal |

## 4. Recommendation

### 4.1 Instructions: a "working in the kernel" section

The section is language-neutral, lives in `Instructions` (the core), uses `{global}`, and is shown on
both surfaces, since hybrid includes `cell`. Draft:

> Working in the kernel:
> - The kernel keeps what your cells compute, exactly, until it restarts. Your context holds what you
>   have seen and is re-read on every step, and text you retype costs output and can come out wrong.
>   So keep results in variables and use them by name in later cells (`tests.stdout`,
>   `Out["<cell id>"]`, a file you read, its ranges, a search's `matches`). Never retype text you have
>   seen (file contents, command output, ids, paths): pass the variable instead.
> - End a cell with what you need to decide, not everything you have: a count, the failing tests'
>   names, the few lines that matter. The rest stays in the kernel for the next cell.
> - When you know the next steps, take them in one cell: read several files, loop over a search's
>   matches, edit, then run the tests. Start a new cell where you must see a result before you can
>   decide.
> - Write new text (file bodies, commit messages, patches) in the cell's `texts`, and change files with
>   `{global}.fs.edit` or `{global}.fs.editRange`, never by writing a file out again.

The "How you work" bullet becomes: "Work in steps you can check: look before you change something, and
check what you changed (run the tests, or the command that shows it works). One cell can take several
steps."

The pack's bullets then drop what this section now says (variables, `texts`, edits) and keep what is
specific to the language.

### 4.2 Prompt pack: examples that carry values across cells

Keep the examples that already teach something specific (ranges, `texts`, jobs, finish), and add two.

Use what an earlier cell found, without copying it (two cells):

```ts
// intent: find the callers of retryOld
const hits = await fx.shell.run("rg -n 'retryOld\\(' src")
hits.matches?.map((m) => `${m.path}:${m.start}`)
```

```ts
// intent: switch every caller to retryNew, then run the tests
for (const path of new Set(hits.matches?.map((m) => m.path)))
  await fx.fs.edit(path, { old: "retryOld(", new: "retryNew(", all: true })
const run = await fx.shell.run("bun test")
run.exitCode === 0 ? "tests pass" : run.stdout.split("\n").filter((l) => l.includes("fail")).slice(0, 20)
```

Read several files in one cell and show only what matters:

```ts
// intent: see which configs set a timeout
const timeouts = []
for (const entry of await fx.fs.list("config"))
  if (entry.name.endsWith(".json")) {
    const file = await fx.fs.read(entry.path)
    const t = JSON.parse(file.text).timeoutMs
    if (t !== undefined) timeouts.push({ path: entry.path, timeoutMs: t })
  }
timeouts
```

Then add one line of contrast under them: "Not: print a file in one cell, then paste its text into the
next."

The pack's tests already type-check every example on the pinned Bun, including examples that span two
cells (T140 bound their `texts` the same way).

### 4.3 Receipt hint: text retyped from an earlier receipt

When a cell's code, or one of its `texts`, contains a verbatim run of at least `N` characters (200 by
default) that an earlier receipt in the visible history showed, the cell's receipt ends with one line:

> [Note: this cell retyped 1,240 characters that `Out["t1-s3-2"]` showed. Use that value by name
> instead (or the variable that held it).]

- **Pure and replay-safe.** It is computed from the cell's input and the transcript, recorded as part
  of the receipt, and never re-rendered, so replay renders the same line.
- **Never quotes text.** The hint names the source receipt: a cell's `Out`, or for a hybrid `read`,
  "read it in a cell to keep it in a variable". Exclusions therefore cannot leak through it.
- **Language-neutral matching.**
  - Unescape `\n`, `\t`, `\"` and `\\` in the code, and compare file pieces without their line numbers.
  - Probe windows at a fixed stride against the visible receipts' text, as `scripts/cells.ts` does.
  - This is bounded: a few hundred substring searches per cell.
  - If it proves too coarse, the interpreter can contribute a pure `Literals` function (open
    question 4).
- **Never blocks.** It is only a note; the cell has already run.
- **A slot.** `codeact.hints.retypedChars` is an integer; 0 turns it off.

A second hint, for a long multi-line template literal ("put it in `texts`"), is language-specific. It
waits until the first hint shows its worth.

### 4.4 A variant slot, and measuring it

**The slot.** `codeact.prompt.style` = `concise | kernel-first` is a core slot read once per turn, like
the others. It selects whether §4.1's section and bullet are rendered.
- The examples (§4.2) apply either way, because they are correct under both.
- A conversation keeps the frame it recorded; a changed section reaches an existing conversation once,
  as an `Updated` frame.

**Measuring it.** Run `bun scripts/cells.ts` over the sessions of each style:

| Metric | Before T139 (§2.1) | Direction |
|---|---|---|
| Run, read, or both, then print | 57% | down |
| Look at an earlier value again | 5% | down (mostly T203a) |
| Cells retyping receipt text | 8% (≈68,600 characters) | down |
| Cells using earlier data | 22% | up |
| Cells calling earlier helpers | 52% | down (the bridge now covers what they wrapped) |
| Cells and output tokens per task | to be measured | down |
| Turns finished `done` | to be measured | not down |

**Later.** The slot becomes an axis of the eval harness's matrix (T217, T218).

## 5. Suggested implementation path

0. **Baseline.** Run two or three dogfooding sessions on the current binary (T139–T143, T203a and T204a
   included), and report them with `scripts/cells.ts` next to §2.1.
1. **Instructions.** Add the §4.1 section behind `codeact.prompt.style` (default `concise`) and the §4.2
   examples. Rewrite the goldens: the prompt pack's `prompt.md`, frame renders, and a deliberate
   re-record of `session-v1.jsonl`.
2. **The retyped-text hint** (§4.3), with its slot. Tests:
   - the window matching, as pure unit tests;
   - a turn test where a cell retypes a receipt's text;
   - a hybrid `read` as the source;
   - an excluded source;
   - replay.
3. **Compare.** Run sessions under both styles. Flip the default if the metrics move the right way and
   finishing does not suffer. Record the outcome in spec §6.9 and §6.10 and in `codeact.md`.
4. **Only if needed.** Change the `cell` tool's description (tools version 3), or keep a prompt pack
   per model family.

## 6. Risks

| Risk | Mitigation |
|---|---|
| Over-batching: long cells that fail halfway | The section says where to split; examples keep cells short |
| Models stop looking before they change things | "Look before you change, check what you changed" stays |
| Hint noise: legitimate quoting, such as fixtures built from earlier output | A threshold, one line, never blocking, a slot to turn it off |
| A longer cached prefix | About 200 tokens; existing conversations get one `Updated` frame |
| Models differ (Claude against open models) | The slot; examples help weaker models most (CodeAct) |
| Replay | Every prompt change re-records the golden session; older recordings diverge at their first request |
| The classifier drifts from what cells look like | `scripts/cells.test.ts` runs in CI; kinds are compared across runs of the same script only |

## 7. Open questions

1. Should the principle live in the core instructions (language-neutral) or in each interpreter's pack?
   This proposal puts it in the core and the examples in the pack.
2. Should `kernel-first` be the default before the comparison, given that dogfooding is today's only
   eval?
3. Should retyping into `texts` be flagged too? `texts` is for new text, but copying old text into it
   still goes through the context.
4. Is normalising escapes enough for matching, or should interpreters contribute a pure `Literals`
   function (SDK surface)?
5. On the hybrid surface, should `read`'s receipt suggest reading in a cell when the file is large or
   about to be edited?

## References

- `scripts/cells.ts` and its tests; the dogfooding sessions of T132 (November 26–27, 2026, Claude Opus
  5.5)
- NOOA trajectories: five CyberGym runs (GLM-5.2, Nemotron-3-ultra, DeepSeek-v4-flash)
- Wang et al., "Executable Code Actions Elicit Better LLM Agents" (CodeAct), ICML 2024, arXiv 2402.01030
- Hugging Face, smolagents (`CodeAgent`)
- Anthropic, "Code execution with MCP: building more efficient agents"
- Cloudflare, "Code Mode: the better way to use MCP"
- Anthropic, prompting best practices for Claude 4.x models (reasons with instructions, examples,
  aggressive wording)
