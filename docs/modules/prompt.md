# Prompt render (`Fx.Prompt`)

Spec: §5 (prompt = pure render; stable receipts; cache breakpoints; content exclusions; `SeenRanges`;
compaction), with §2 (cache-friendly transcript), §6.6–6.8 (references, pins, previews, the repo map, the edit
guard), §7.3 (agents in the prefix), §10.3 (cache mapping), §15 (variants) and §18 (content exclusions).
Task: T126. Builds on [models.md](models.md) (the canonical request), [sessions.md](sessions.md) (the session's
events), [variants.md](variants.md) and [contracts.md](contracts.md).
Code: [`src/Fx.Prompt`](../../src/Fx.Prompt) (pure: no I/O; the EFF analyzers apply to every file).
Tests: `tests/Fx.Prompt.Tests` and the `prompt-*` scenarios of the JIT-vs-native diff.

The prompt is a projection of the log (spec §1, principle 4): the same log renders the same bytes, and
appending to the log only appends to what renders. That is what makes a conversation cacheable, and what
providers that check a reasoning block's prefix require.

```fsharp
type Prompt = { Prefix: Block list; History: Message list; Suffix: Block list }

let rendered = Render.render cfg transcript live          // pure
let request  = Render.request cfg template rendered       // the canonical ChatRequest
```

| Piece | Where |
|---|---|
| Content: `Piece`, `Block`, `FileView`, `Reference`, `Paths` (`LineRange` is `Fx.Models`') | `Content.fs` |
| Content exclusions in the render (`Withhold`; the matcher is `Fx.Models.Exclusions`) | `Withhold.fs` |
| `SeenRanges` | `SeenRanges.fs` |
| Context events and their JSON (`ContextEvent`, `ContextCodecs`) | `Events.fs` |
| The fold (`LogItem`, `Transcript`, `Entry`, `Visible`, `reconcile`) | `Transcript.fs` |
| Views and live data (`View`, `Live`, `Views.*`) | `Views.fs` |
| The render (`RenderConfig`, `Render.render`, `Render.request`, `Render.cacheKey`) | `Render.fs` |
| The prefix's sections (`Sections.*`) | `Sections.fs` |
| Variant slots and settings (`PromptSlots`, `PromptSettings`) | `Settings.fs` |

## Content

A **piece** is what context is made of: `Text`, `File` (a `FileView`: normalized path, the digest of the
version read, the lines shown or none for the whole file, and the text), `Withheld` (a file's content kept out
by content exclusions: only its path shows), `Ref` (a value by reference: kind, the name the model passes,
type, `Preview`; T201 fills these in) and `Media`. Pieces that show a file say so, so the render can withhold
excluded files and record what was seen. A **block** is pieces with an id and an optional version: a section
of the prefix or a view in the suffix.

- `Piece.render` gives a piece as text: a file's text comes under a header `src/a.fs:10-20 (sha256:9f86d0818…)`
  (12 hex digits name the version), a reference as `[handle h1: Match[]] Match[](len=37)`.
- `Piece.parts` turns pieces into message parts: consecutive pieces joined by line breaks into one text part,
  media apart, empty text left out. `Piece.toolContent` does the same for a tool result.
- **Provenance:** a text part (and a tool result, and a system part) made from pieces that show files quotes
  them in its `Sources` (`Piece.sources`: the path and each range of lines, or the whole file), so the
  model-egress hooks can redact a file excluded after it was recorded ([egress.md](egress.md)). Empty sources
  leave a part's JSON unchanged.
- `Quote` (`{text, sources}`) is text that may quote files without showing them as such: what a cell
  printed after it read them through the bridge (T127). Its sources are its part's provenance; the render
  withholds it (as its first excluded source) when one of them is excluded, and it never counts in
  `SeenRanges`, since which lines it shows is unknown. Adding the case kept version 1 of the JSON.
- Receipt helpers bound content when it is recorded: `Piece.output maxChars text` keeps the head and the tail
  with how much was left out (never splitting a surrogate pair), `Piece.preview name preview` and
  `Piece.value limits name codec value` show a value's preview (`values.previewLimits`). Large values travel by
  reference (`Piece.Ref`), never copied.
- `Paths.normalize`: `/`-separated, no `.` segments, repeated or trailing separators or leading `/`; `..` is
  never resolved.

## The log the history projects

The history is the fold of two kinds of facts, in log order, each at its position (`EventRef`):

- **The session's events** (T125): prompts, steers, settled requests and finished jobs are inputs; they enter
  the history when they are delivered (`TurnStarted`, `InputsDelivered`), never when they arrive, so a withdrawn
  prompt never shows. `TurnEnded` closes calls left open and notes a turn that did not complete.
- **Context events** (`ContextEvent`), which the strategy that runs turns (T127) appends:

| Event | What it records |
|---|---|
| `Framed sections` | the prefix the conversation runs with, recorded before the first call; a later one starts a new frame |
| `Updated {changed, removed}` | sections of the frame that changed since, stated in the history where they changed |
| `Replied {turn, step, shown, parts}` | a model's reply, as the provider returned it (provider state included), with the suffix (`shown`) its prompt ended with |
| `Returned {turn, step, call?, name, pieces, isError}` | a **stable receipt**: a tool call's or cell's result as the model will see it from now on |
| `Pinned {name, pin}`, `Unpinned name` | `fx.context.pin` (a value as it was) and `pinFile` (a live view of a file's lines) |
| `Collapsed {from, to, summary}` | compaction (T206): the entries from `from` to `to` show as the summary; the originals stay in the log |

Where context events are stored is the strategy's choice: the CodeAct strategy (T127, [codeact.md](codeact.md))
keeps them in `context/<session>`, each placed after the session event its envelope's cause names; the fold
needs only one order and distinct positions. A fork's history is its parent's items up to the fork point
followed by its own (`Transcript.ofItems`); positions name their stream, so they stay distinct.

`ContextCodecs.event` is version 1 of their JSON (`ContextCodecs.Version`). Events are facts kept forever:
change one's JSON only through a new version and an upcaster. `tests/Fx.Prompt.Tests/golden/context-events-v1.jsonl`
is what version 1 wrote, for every kind of event; it is written once and never rewritten.

## The transcript (`Transcript`)

`Transcript.apply` is a pure, total fold. An item only appends entries; it never changes earlier ones.

- **Entries**: `Inputs(turn, step, inputs)`, `Reply`, `Receipt(receipt, answers)`, `Closed(calls, reason)`,
  `Ended(turn, end)`, `Update`. Each has the position of the event that made it and an index.
- **Calls are always answered.** The last reply's tool calls stay open until receipts answer them. Whatever
  comes first instead (the turn ends, is interrupted or fails, a new turn starts, the model replies again)
  first appends `Closed`: an error result for each open call saying why (`No result: the turn was
  interrupted.`). A receipt for a call that is not open is read as text (`[Result of cell (call c1), after the
  call was closed:]`). So no call goes to a model without its result, and no result without its call.
- **Collapses** apply only when the range is in the history, keeps every call with its result (the calls made
  inside it are exactly those answered inside it), and nests: it contains or misses every earlier collapse. Any
  other is recorded in `Issues` and changes nothing. `Transcript.visible` shows each outermost range's summary
  where it began and hides what it covers.
- **Rewrites**: a collapse and a new frame record which entries were made with a history that no longer is
  (`Rewrite {From; At}`); `Transcript.isStale index` tells whether a reply is one of them.
- **Pins** are kept in pin order; pinning a name again moves it to the end.

### The frame and its updates

Providers that keep a model's reasoning check that what came before each reasoning block is unchanged
(Anthropic's prefix check fails a request with 400 on several models), and prompt caches hit only on an
unchanged prefix. So the conversation follows three habits: send each reply back as it came; build the prefix
once per conversation and send that copy every time; put every update in a new message at the end and leave it
there.

`Transcript.reconcile sections transcript` keeps the prefix stable: before the first call it returns `Framed
sections`; afterwards, when the sections the strategy builds now (`RenderConfig.Sections`) differ from the frame
with its updates (`Transcript.Current`), an `Updated` with the sections that changed or were added and the ids
that went away; else nothing. Sections are compared by id and content; order counts only in the frame. The
render always takes the prefix from the recorded frame.

## The render (`Render.render`)

`Render.render cfg transcript live : Rendered` gives the `Prompt` and what it shows: `Seen` (`SeenRanges`),
`Withheld` (paths, sorted), `Stale` (replies that lost their reasoning) and `Ends` (where earlier calls' prompts
ended, before each visible reply). Pure and deterministic: the same configuration, transcript and live data give
the same prompt, byte for byte, also from the log read back from its JSON.

- **Prefix**: the frame's sections (the configuration's until one is recorded), content exclusions applied.
- **History** (consecutive parts of one role merged into one message):

| Entry | Renders as |
|---|---|
| `Inputs` | user text, one part per input: the prompt; `Request d1 was answered: …`; `Job j1 finished: …` |
| `Reply` | the shown suffix as user parts, then the reply's parts as the assistant's, verbatim (cache breakpoints dropped) |
| `Receipt` answering a call | a user `toolResult` with the receipt's pieces |
| `Receipt` read as text | user text (with a header if it names a closed call) |
| `Closed` | an error `toolResult` per call |
| `Ended` | `[Turn 2 was interrupted.]`, `[Turn 2 failed: …]`, `[Turn 2 was abandoned: …]` |
| `Update` | `[Context update: …]`, then each changed section under `[id]`, then `[No longer in effect: …]` |
| a collapse's summary | `[Summary of an earlier part of this conversation:]` and the summary's pieces |

- **Suffix**: the views (`cfg.Views`) over `{Transcript; Visible; Live}`, exclusions applied, empty blocks left
  out. The strategy records the suffix it sent as the reply's `Shown`, so what the model saw stays exactly where
  it was. The suffix is therefore part of the history once the model answers it: views show only what changed.

### Stability and its documented boundaries

Appending to the log never changes the prefix or the history rendered before it, and every call's whole
request (history and suffix) is a prefix of every later request's content. Three things rewrite history, and
each is a documented boundary:

| Boundary | From where | Replies after it |
|---|---|---|
| a collapse (`Collapsed`) | the range's first entry | lose their reasoning |
| a new frame (a second `Framed`) | the prefix | all lose their reasoning |
| content excluded after it was recorded (the exclusions changed) | the entry that showed it | lose their reasoning |

A reply that lost its reasoning keeps its text and calls; its reasoning parts and the provider state of its other
parts are left out (they would fail the provider's prefix check; the API allows leaving out the newest
reasoning, never one in between). Replies made after the boundary keep theirs.

### Cache mapping (`Render.request`)

`Render.request cfg template rendered` makes the canonical request: the template's model, tools and options;
the prefix as system text parts, one per section; the history, then the suffix in a user message (in the
history's last one if it is the user's).

- **Breakpoints** (`prompt.cache.breakpoints`), only for models whose caching is `Breakpoints`:
  - `off`: none. `prefix`: after the system parts.
  - `rolling` (default): candidates, most valuable first: the end of this request (the next call reads it), the
    prefix (shared by every call), then where the latest earlier calls ended (so a call that added more than a
    provider's look-back still reads what the last one wrote). Each previous request ended exactly where its
    reply begins in this one, because its suffix is in the history.
  - At most the model's `MaxBreakpoints` (four for Anthropic), minus one when the template asks for a
    breakpoint after the tools. The prefix's lifetime is `prompt.cache.prefixTtl` where the model takes long
    lifetimes; later ones are short (a long one may not follow a short one).
- **Cache key**: where the model takes one (OpenAI `prompt_cache_key`) and breakpoints are not `off`,
  `Render.cacheKey`: the first 32 hex digits of the SHA-256 of the canonical JSON of the system parts and the
  tools. It changes only with the prefix: never with the history or the suffix.

## SeenRanges

Which file ranges, of which versions, entered the context (spec §5, §6.8): `{path → {digest → {whole; lines}}}`,
a pure projection of what a rendered prompt shows. The edit guard (T204) reads it.

- Every `Piece.File` the prompt shows (prefix, visible history, suffix) adds its lines, or the whole file, to its
  normalized path and digest. Withheld files and what a collapse hides add nothing: after a collapse the model
  has to read again before it edits.
- Ranges of one version merge (overlapping and adjacent lines join); the whole file covers every range.
- Versions never mix: lines seen in one digest say nothing about another. An edit makes a new version; its
  result shows the lines it anchored, which count as seen.
- Queries: `covers path digest range`, `unseen path digest range` (the gaps), `tryFind`, `versions`; `union`;
  `SeenRanges.codec` for the prompt and seen-range viewer (spec §15).

## Content exclusions

The render uses the shared matcher, `Fx.Models.Exclusions` ([egress.md](egress.md#the-matcher-fxmodelsexclusions)):
the same patterns, semantics and precedence as the host bindings and the model-egress hooks. `RenderConfig.Exclusions`
is the value `ContentExclusions.compile` makes from the session's settings (policy, host and project lists, patterns
that only ever add up; T127 builds it). Patterns follow `.gitignore` without negations or escapes: `*`, `?`,
`[abc]`, `[a-z]`, `[!a]`, `**`, `{a,b}`; a pattern without a `/` matches a name at any depth; a leading or inner
`/` anchors it at the workspace root; a trailing `/` matches directories only; a matched directory excludes
everything under it. Matching ignores case, `\` separates segments as `/` does, and every pattern that cannot be
used (blank, a negation, no path) is an error: an exclusion silently ignored would leak what it was meant to
withhold.

- The render withholds every excluded `Piece.File` (prefix, history, suffix, live pins; `Withhold.pieces`), shows
  `[path: withheld by content exclusions]` instead, and keeps it out of `SeenRanges`.
- Record context events through `ContextEvent.withhold exclusions`: the log then never holds excluded content,
  and a render has nothing to rewrite. A file the render withholds that the log holds means the exclusions
  changed since: a boundary (above).
- Only pieces that say they show a file can be withheld. Text that merely contains a file's content (a cell's
  output of `cat`) is the kernel's to refuse and the model-egress hooks' to catch; parts made from file pieces
  carry `Sources`, so the hooks also catch files excluded by lists the render was not given (a provider's
  organization policy, T210).

## Views

A view is `{Id; Render: ViewInput -> Block list}`, pure, over the transcript, what its history shows and the
`Live` data the strategy gathered through its capabilities (the render does no I/O): `Now`, the current
`Files` of live pins (`Views.pinnedFiles` says which to read), `Status` lines and one-off `Notes`. A view should
show a block only when its version differs from what the visible history last showed under its id
(`Transcript.lastShown`).

| View | Shows |
|---|---|
| `pins` | each pinned value once (again after a collapse hides it); each pinned file when its version or lines changed, or that it is not available |
| `status` | `Live.Status` when it changed |
| `time` | today's date (UTC) when it changed |
| `notes` | `Live.Notes` (a loop-guard warning, a reminder to finish), always, last |

## Sections

`Sections.instructions`, `interpreter` (the prompt pack with its declarations, `PromptPack.render`), `agents`
(sorted by name, as `<global>.agents.<name>`), `repoMap` (when `prompt.repoMap` is on; T202 makes it), `file`
(a file pinned by configuration, e.g. a project's agent instructions), `ordered`.

## Settings

| Slot | Type | Default | Spec |
|---|---|---|---|
| `prompt.views` | list of `pins`, `status`, `time` (each once) | `["pins", "status"]` | §5 |
| `prompt.cache.breakpoints` | `off` \| `prefix` \| `rolling` | `rolling` | §5, §10.3 |
| `prompt.cache.prefixTtl` | `short` \| `long` | `short` | §10.3 |
| `prompt.repoMap` | boolean | `false` | §6.7 |

`PromptSettings.read` reads them (and `values.previewLimits`) through `IVariants`; `PromptSettings.config
settings sections exclusions caching` makes a `RenderConfig`. They are core slots defined next to the code that
reads them: `Fx.Variants` cannot reference this project, so the host registers `PromptSlots.all` with
`Slots.all` (T129). Defaults are provisional until evals decide.

## What a strategy does for each call (T127)

1. Read the settings; build the sections; gather `Live` (the clock, the pinned files, status, notes).
2. Append `Transcript.reconcile sections transcript`, if any (through `ContextEvent.withhold`).
3. `Render.render`, then `Render.request` with its template (model, tools, options); call the model through
   `ILlm`.
4. Append `Replied` with the reply's parts and `Shown = rendered.Prompt.Suffix`.
5. Append a receipt (`Returned`) for every call, bounded when it is made; answer every call before the next
   call.

## Native AOT

No reflection: codecs by hand, unions over records (`Codec.caseOf`, `case1`), single-parameter view functions.
Scenarios: `prompt-render` (a session through the session machine with every kind of entry, its request,
breakpoints, ends, seen ranges and withheld files, each call a prefix of the next), `prompt-replay` (the log
through its JSON renders the same bytes, the cache key, a collapse with stale reasoning), `prompt-exclusions`
(globs) and `prompt-settings` (slots read through `IVariants`), on all eight targets.

## Testing

`tests/Fx.Prompt.Tests`: one test per rule; a simulated session (`Support.Sim`) that drives the real session
machine and records context events as a strategy would; FsCheck properties over random sessions (appending
never changes the prefix or the history before it; every call's request is a prefix of every later one and
within the breakpoint limit; requests with no call open are valid for a provider; rendering is deterministic,
also from the log's JSON; the cache key depends on the prefix only; excluded content never shows and is never
seen; a collapse rewrites only from its start); golden renders of a coding session, an interrupted and a
failed turn, and a collapsed conversation with changed instructions (`UPDATE_GOLDEN=1` rewrites them); and the
kept JSON of version 1's context events.

## Left to later

| What | Task |
|---|---|
| `Value` references and handles in receipts (`Piece.Ref`), `fx.context.pin`/`pinFile`, `fx.recall` | T201 |
| Type-aware previews and outlines in receipts | T203 |
| The edit guard over `SeenRanges` | T204 |
| Choosing and summarizing ranges to collapse, cache-sharing summary requests, `fx.history.*` | T206 |
| The repo map's content | T202 |
| The agent list from the agents registry | T213 |
| Copilot organization exclusions (a provider's own egress hooks) | T210 |
| Registering `PromptSlots.all` in the host's registry | T129 |
| A per-message render for clients (`/context` with cost, the prompt and seen-range viewer) | T130, T205 |
