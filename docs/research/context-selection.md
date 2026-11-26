# Research: decision-model context selection

Status: **proposal** (research, November 2026). Nothing here is decided; it suggests an implementation path.
Spec: §1 (principles), §5 (sessions, history and context), §6.6 (values and references), §7 (agents),
§8 (goals and workflows), §14 (judgments), §15 (variants and evaluation), §16 (extensions).
Tasks it touches: T206 (steering + compaction), T201 (values & references), T213 (agents), T214 (goals),
T304 (workflows), T402 (judgments). Builds on [prompt.md](../modules/prompt.md) and
[codeact.md](../modules/codeact.md).

**Question.** Should fx stop sending the whole growing conversation until it fills the window and then
compacting it, and instead pick the right context on every turn with a fast decision model such as
TypeSafe's Jev? If so, should that be the default way fx works, or an option behind an extension point?

**Answer.** Make context construction an **extension point** (`ContextPolicy`), keep today's append-only
render with threshold compaction as the default policy, and ship **judgment-driven selection as a second
built-in policy that is off, or in `Shadow` mode when a judge model is configured**. Promote each part
to a default separately, when evals show a better **cost per verified result**. The best place for it is
building **fresh contexts** (agents, goal iterations, workflow nodes). Re-choosing the main conversation's
history on every turn should never be the default: it breaks the rule that every request is a prefix of
the next, costing both the prompt cache and the reasoning of every later reply.

## 1. Background: compaction today

| Finding | Source |
|---|---|
| Compaction is still very lossy, even for frontier models. Failures come from omitted information and from distorted task state: open issues recorded as resolved, earlier results forgotten. | CompAct-Bench, arXiv 2611.25673 |
| Of 13.5M Copilot sessions, 7.8% trigger compaction but use 44.2% of all tokens. Synchronous compaction delays the foreground and breaks prompt-cache reuse; preparing summaries in the background and appending them after a stable prefix (APEC) cuts cost by up to 25.4%. | Microsoft CompactionBench / APEC |
| Masking old tool outputs matched LLM summarization's solve rate on SWE-bench Verified at about half the cost; a hybrid was another 7–11% cheaper. | JetBrains, "The Complexity Trap", arXiv 2508.21433 |
| Optimizing the compression instructions from runs that succeed uncompressed and fail compressed cut peak tokens by 26–54% without losing accuracy, and the compressor distils into small models. | ACON, arXiv 2510.00615 |
| Rewriting summaries again and again causes brevity bias and context collapse; small structured updates help. | ACE, arXiv 2510.04618 |
| Compression failures fall into three groups: a wrong decision before compressing, information lost while compressing, and failing to reach information afterwards. | EMNLP 2026 survey (Wang et al.) |

Summaries are the default, but they are expensive and distort state. Cheap structural pruning is a
surprisingly strong baseline.

## 2. Decision models

TypeSafe calls Jev a "System One model" (September 2026). It takes a state and typed questions
(`choice`, `score`, `noul`: a probability from 0 to 1, `box`), answers all of them in one parallel pass,
never generates free text, and always matches the schema. Limits: 64k tokens per request and 32k per
question (262k behind an experimental header). Input costs $0.042 per million tokens; output is free;
answers take about 70–500 ms. TypeSafe's own guidance is "Jev makes judgments, code makes decisions":
split a decision into small questions and combine the answers in code. Similar services exist (OpenAI
Decisions, OpenRouter Decisions, Cloudflare Clef); spec §10.1 already plans `decisions.typesafe|openai|openrouter`.

### 2.1 As a compactor: delete instead of summarize

| Tool | Mechanism | Reported (by its authors) |
|---|---|---|
| LiteLLM `typesafe` guardrail | One yes/no question per old tool exchange; results below 0.2 become a placeholder; fails open | — |
| fast-jev-compaction (Claude Code plugin) | Two questions per call (keep the call? keep the result?) → keep, truncate or drop; everything kept stays verbatim | ~1M → 86k tokens on one session |
| jev-compactor | Drops a message only at P(drop) ≥ 0.7; code pins what must stay; the same request flags destructive commands | 64 messages: 73% saved, 4 of 4 facts kept, 350 ms, $0.0004, against OpenCode `/compact`: 85% saved, 3 of 4 facts, 17 s, $0.038. Dense sessions: ~24% saved. |

The benchmarks are synthetic, written by the tools' authors and run once per comparison. Jev's
probabilities move by up to ~0.14 between identical requests; averaging three votes halves the spread.

## 3. Choosing the context on every turn

### 3.1 The design argument

Design notes by TypeSafe's founder (compiled by a third party as "Jev Engineering for Coding Agents")
start from one question: how would you design a coding agent if models had no KV cache? Agents keep
append-only transcripts only because reusing the cache is cheap. Compaction compresses before it knows the
next question; compression that knows the question is far easier. The proposal: keep state as typed,
addressable chunks and ask per query (a) whether to reuse the cache or rebuild, and (b) how visible each
chunk should be (hide, short, long, full). The same mechanism chooses tools, loads instruction sections
only when relevant, and builds contexts for sub-agents, which is also what makes routing to cheaper
models pay off.

### 3.2 Implementations

| Tool | When it decides | What it selects | Notes (reported by its authors) |
|---|---|---|---|
| jevselector (OpenCode v2) | Each model turn | Skills and tools | System prompt ~5.7k → 2.7k tokens; 250–950 ms, reused within a turn |
| jev-context (Pi) | Each user message; after each turn | Skills (top 3 at ≥ 0.6), tool groups; pruning of a finished turn in hindsight | −34% billed context on 12 replayed sessions, −3.8% on the full corpus. 6 prunes in one live session, 3 of them later quoted back. Errors are always kept. |
| pi-jev-context-curator | Before model calls (Jev every 5th call) | History units, each judged once (content fingerprint) | 1–3 new units per call; only above 8k tokens; fails open |
| winnow (Claude Code) | When a tool result arrives | 25-line blocks → a stub that restores them | Hides nothing when the output looks like an error |
| Self-GC (arXiv 2607.00692; a planner model, not Jev) | Separate planner | Fold, mask or prune each object by future dependency; committed at cache-safe points with a recoverable copy | 33 hard sessions: 44% pruned, 85% of later steps unaffected (baselines 55–70%); 332 production sessions: 91–95%; production: −10–15% input tokens |
| Jev-Mem (arXiv 2609.23986) | Memory retrieval | Jev routes, budgets and stops retrieval; the LLM only reasons | LoCoMo +11%, query latency −37% |

### 3.3 Counter-evidence

"When Faster Decisions Do Not Make Better Agents" (arXiv 2610.13092, 4,500 runs) used Jev to choose
actions, not context. Each decision became 66–78% cheaper, but success fell by up to 18 points for two
mobile agents, and for one agent the cost per successful task rose: failed recovery and premature
completion ate the savings. Measure cost per delivered result, not tokens saved.

## 4. The binding constraint: cache and reasoning

- Reading a cached prefix costs about **0.1×** the input price; writing a new one costs up to **1.25×**
  on Anthropic (OpenAI-style automatic caching has no write premium). Clearing context always forces a
  write; Anthropic's context editing has `clear_at_least` for exactly this reason.
- In fx, every `Collapsed` range and every new frame is a rewrite boundary, and **replies after it lose
  their reasoning parts** ([prompt.md](../modules/prompt.md#stability-and-its-documented-boundaries)).
- A rebuild pays off when, with $N$ turns left,

  $$\text{read}_{\text{old}}\cdot N \;>\; \text{write}_{\text{new}} + \text{read}_{\text{new}}\cdot(N-1) + \text{reasoning-loss penalty}$$

  With Anthropic-style prices, shrinking 100k tokens to 40k pays off after about 8 turns. Rebuilding on
  every turn never does.
- What works: remove only, never re-add, within an epoch (the stretch between two rebuilds); decide at
  boundaries such as a new user message; inject at a fixed position; judge each item once and remember
  the verdict; batch removals behind a minimum amount freed; make removals recoverable; judge in the
  background, off the critical path.

## 5. How this fits fx

| Needed | In fx |
|---|---|
| A full record kept apart from the prompt | The log is the source of truth; the prompt is a pure render (§1.4, §5) — built |
| Reversible removal | `Collapsed` keeps the originals (built); `fx.recall` and `Out[n]` (T201, todo) |
| One-line stubs as the judge's input | Values by reference with bounded `Preview`s (§6.6; codec previews built, `Value` handles T201) |
| Compaction | Threshold, overflow and model-asked compaction in the CodeAct strategy, cache-sharing summary requests, background preparation (T206, in review) |
| A pluggable judge | §14 judgment points: `Off/Shadow/Advise/Enforce`, batched questions, several backends, labels (T402, todo) |
| Safe experiments | Variants, exact replay, counterfactual forks (§15) |
| Fresh contexts | Agents start `Fresh` with declared inputs (T213), goals default to `FreshHandoff` (T214), workflow nodes are `Fresh/Shared/Resume` (T304) |

Two consequences:

1. **Tool-output bloat is mostly designed away.** Once T201 lands, large outputs stay in the kernel as
   values and the model sees previews, so per-item pruning is worth less in fx than in Claude Code. What
   builds up instead is cell source, previews, replies and reasoning. Measure where history tokens go
   before investing.
2. **Keep the prefix rule.** Re-choosing history every turn conflicts with it through both cache cost
   and reasoning loss.

## 6. Recommendation

### 6.1 An extension point with a conservative default

This follows §1 (defaults come from evals, judgments are optional, built-ins use the public API).

Do **not** expose a `Transform` over `Prompt`. A prompt transform can silently break the prefix rule,
call/result pairing, content exclusions and the provenance the egress hooks rely on, `SeenRanges` and the
edit guard, and replay.

Instead add a **`ContextPolicy` contribution type**. Policies propose; the core validates, cost-gates,
batches and records the result as ordinary context events (`Pinned`, `Updated`, `Collapsed`, views):

```fsharp
type ContextPolicy =
  { Id: string
    Include : TurnInput      -> Eff<IPolicyEnv, Inclusion list>         // append-only: views, Updated, pins
    Propose : TurnOutcome    -> Eff<IPolicyEnv, CollapseProposal list>  // in the background, after a turn
    Rebuild : RebuildInput   -> Eff<IPolicyEnv, RebuildAdvice>          // continue | commit | hand off
    Handoff : HandoffRequest -> Eff<IPolicyEnv, HandoffPacket> }        // agents, FreshHandoff, Fresh nodes

and CollapseProposal =
  { Range: EntryRange; Summary: CollapseSummary; Confidence: float option }
and CollapseSummary = Stubs | Text of string | Mixed of verbatim: EntryRef list * text: string
```

The core:

- **validates**: collapses nest and keep every call with its result (`Compaction.cuts` already checks
  this), exclusions apply, and handoffs respect declared inputs, so a reviewer never sees the
  implementer's reasoning (§7.4);
- **batches** collapses so they share one boundary, as early as possible;
- **cost-gates** them with §4's formula, from `ModelCaps.Caching` and `Cost` per provider;
- **records** everything as events and journals the judge's answers, so exact replay still works;
- **falls back** to the baseline policy when a policy fails or times out, as judgments' `Off` mode does.

`IJudge` is a capability a policy may use, not the extension point; a heuristic policy implements the
same type with no model. A variant slot `context.policy`, settable per role (main session, agent, goal
iteration, workflow node), selects the policy.

This is the shape §5 already anticipates: T206 keeps compaction in the CodeAct strategy and moves it to
the bundled `fx.compaction` (§16.4) once strategies are extension contributions. `ContextPolicy` is a
proposal for that contribution.

### 6.2 Built-in policies

| Policy | Default | Purpose |
|---|---|---|
| `append-compact` | **on** | Today's behaviour (T206); the baseline |
| `masking` | off (an eval baseline) | Age-based collapses into stubs, no model |
| `dynamic` | **off; `Shadow` when a judge model is configured** | Judgment-driven `Include`, `Propose`, `Rebuild`, `Handoff` |

### 6.3 Judgment points inside `dynamic`

| Point | Latency | Question | Cache and reasoning |
|---|---|---|---|
| `handoff.select` | inline | Which parent chunks, at which visibility, go into this fresh context? | untouched (a new conversation) |
| `context.include` | inline, at turn start | Which ledger decisions, skills, instruction sections and memories apply? | untouched (append-only) |
| `context.collapse` | background, after a turn | Will later turns need this range verbatim? | batched and cost-gated |
| `context.rebuild` | inline, at a boundary | Is this a task boundary, or is the context polluted? | feeds the cost-gated choice |

The judge's state is built from `Preview`s and receipt stubs, to stay under 32k tokens a question.
Independent questions are batched with `and!`.

## 7. Suggested implementation path

Each step has an exit criterion.

0. **Measure.** Replay metrics for the composition of history tokens: cells, receipts and previews,
   replies, reasoning, updates. *Exit:* we know which buckets dominate long sessions.
1. **The seam and the baselines** (after T206). Add the `ContextPolicy` contribution and the core's commit
   path (validation, batching, cost gate, events). Allow stub-only and mixed summaries in `Collapsed`.
   Implement `append-compact` (T206's behaviour behind the seam) and `masking`. Add a wrong-collapse
   label: a recall or `Out[n]` of a collapsed handle within K turns; a collapse never referenced again is
   a correct one. Build a rough `dynamic` alongside, so the interface comes from two implementations,
   not one. *Exit:* both baselines on the eval dashboard; the cost gate checked against real cache
   prices on Anthropic and OpenAI.
2. **Handoff** (with T213, T214, T304). Route `Agent.Context = Fresh`, `Goal.Context = FreshHandoff` and
   `NodeSpec.Context = Fresh` through `Handoff`. The baseline handoff is declared inputs, relevant ledger
   decisions, the progress log and pins, chosen by heuristics. *Exit:* goal and workflow success, and
   cost per verified result, measured with the baseline handoff.
3. **Judgments in shadow** (with T402). `IJudge` backends: a decision wire, LLM-as-judge, heuristics. Run
   `dynamic` in `Shadow` for `handoff.select` and `context.include`, logging answers next to their
   outcomes. Using the judge requires explicit configuration, because data leaves the machine. *Exit:*
   enough labels to estimate calibration and how often the judge would be wrong.
4. **Promote handoff and include.** Move `handoff.select`, then `context.include`, to `Advise` and then
   `Enforce`, per role, when counterfactual forks show cost per verified result at least as good with no
   loss of success. *Exit:* the new defaults recorded in variant profiles and published.
5. **Long sessions.** `context.collapse` (background, batched) and `context.rebuild` in `Shadow`, then
   `Advise`. An *epoch handoff* for very long interactive sessions: a new frame seeded from a `Handoff`
   packet instead of an LLM summary. Promote only when the wrong-collapse rate is low and the cost gate
   shows net savings on each provider. *Exit:* compaction-heavy sessions cost less per result than with
   `append-compact`.
6. **Optional: our own judge.** A small classifier trained on the logged answers and labels, to reduce
   vendor dependence and keep data on the machine.

## 8. Evaluation

- **Main metric:** cost per verified result, including cache reads and writes, judge calls and failed
  attempts.
- **Secondary:** success and false completions; tokens per turn and peak context; latency on the critical
  path; recall rate and wrong-collapse rate; facts surviving a handoff; reasoning parts lost to rewrite
  boundaries; spread of the judge's answers across repeated calls.
- **Baselines:** `append-compact`, `masking`, LLM summaries.
- **Method:** exact replay of real fx sessions, counterfactual forks at compaction and handoff points,
  then SWE-bench Verified and Terminal-Bench 2.0.

## 9. Risks

| Risk | Mitigation |
|---|---|
| Wrong removals (3 of 6 in jev-context's one live session) | Recoverable stubs; only confident drops; always keep errors and reasoning; recall-based labels |
| Cache and reasoning loss erase the savings | Remove only within an epoch; batch at boundaries; cost gate per provider |
| Cheaper decisions, worse outcomes (arXiv 2610.13092) | Judge by cost per verified result; keep the harness's verification |
| One vendor, data leaving the machine, Jev in early access | Pluggable `IJudge`; egress hooks; explicit configuration; our own classifier |
| Unstable answers (±0.14) | Average votes; margins around thresholds; judge each item once |
| Freezing the interface too early | Derive `ContextPolicy` from two implementations (step 1) |
| Smaller payoff in fx, where values stay in the kernel | Measure first (step 0); start with handoff |

## 10. Open questions

1. How much of a long fx session is cell source and replies, compared with receipts and previews?
2. Should an epoch handoff of the main session look like a fork or a new frame to the user?
3. How should the reasoning-loss penalty be priced: fixed, per model, or learned from evals?
4. Should handoff packets use the visibility ladder (hide, preview, summary, full) from the start, or
   only keep or drop?
5. When `dynamic` is selected, should `Shadow` turn on whenever a judge model is configured, or only on
   explicit opt-in?

## References

- CompAct-Bench, arXiv 2611.25673; Microsoft, "Benchmarking Long-Horizon Agent Context Compaction with
  CompactionBench" (APEC)
- JetBrains, "The Complexity Trap", arXiv 2508.21433; ACON, arXiv 2510.00615; ACE, arXiv 2510.04618;
  Wang et al., "Context Compression for LLM Agents: A Survey", EMNLP 2026
- TypeSafe: "Introducing System One Models & Jev", "Don't Call It a Decision Model", "Jev 1.14",
  docs.typesafe.ai
- "Jev Engineering for Coding Agents" (an independent synthesis of D. Almeida's design notes),
  jevforagents.com
- LiteLLM TypeSafe guardrail docs; tamaratran/fast-jev-compaction; edwardyen724-g/jev-compactor
  (`docs/BENCHMARK.md`)
- universam1/jevselector; Growth-Kinetics/jev-context; Shashank-H/pi-jev-context-curator;
  GhalebDweikat/winnow; kunchenguid/compact-adviser
- Self-GC, arXiv 2607.00692; Jev-Mem, arXiv 2609.23986; "When Faster Decisions Do Not Make Better
  Agents", arXiv 2610.13092
- Anthropic, context editing (`clear_tool_uses_20250919`); Nadir, "Pay to Forget"
