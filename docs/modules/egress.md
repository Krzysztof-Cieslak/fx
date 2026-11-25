# Module spec: model egress and content exclusions

Spec: §18 (with §5 content exclusions in render, §6.4–6.5 kernels and bindings, §10.3 every request
passes the egress hooks, §10.6 Copilot organisation exclusions, §16.3 hook algebra, §16.5 privileged
contributions). Task: T124.
Code: the matcher and request redaction in [`src/Fx.Models/Exclusions.fs`](../../src/Fx.Models/Exclusions.fs),
provenance on parts in [`src/Fx.Models/Chat.fs`](../../src/Fx.Models/Chat.fs) (`ContentSource`), the
hook points and the single entry point in [`src/Fx.Sdk/Models/Access.fs`](../../src/Fx.Sdk/Models/Access.fs),
the settings and hooks in [`src/Fx.Sdk/Models/Egress.fs`](../../src/Fx.Sdk/Models/Egress.fs)
(`ContentExclusions`), the built-in [`extensions/Fx.Ext.EgressExclusions`](../../extensions/Fx.Ext.EgressExclusions)
(`fx.egress.exclusions`), and `fs.read` in [`src/Fx.Interpreters/CoreBindings.fs`](../../src/Fx.Interpreters/CoreBindings.fs).
Tests: `tests/Fx.Models.Tests/ExclusionTests.fs` (matcher, properties, redaction),
`tests/Fx.Ext.EgressExclusions.Tests` (settings, hooks, end to end, over the extension protocol), the
architecture test in `tests/Fx.Extensions.Tests`, `tests/Fx.Interpreters.Tests` (`fs.read`), the
kernel conformance case, and the `egress-*` scenarios of the JIT-vs-native diff.

Model providers are outside the trust boundary (spec §18). Everything that leaves for a model leaves
through one place, which runs the **model-egress hooks**; the built-in hooks keep the content of
**excluded files** out of what leaves.

## One way out: `ModelAccess`

```
 session / agent / summarizer / judgment / fx.llm (cells) / extensions
        │  Llm (ILlm)  — the only model capability anyone is handed
        ▼
 ModelAccess.stream: provider, wire, fit, replay rules
        │
        ▼
 ModelEgress.clear: transform:model-egress (by priority) → gate:model-egress (all; Deny > Ask > Allow)
        │  failing transform, Ask or Deny: ChatError.EgressBlocked, nothing sent
        │  provenance (sources) dropped
        ▼
 WireRequest ──► ChatWire.Stream (WireCtx: HTTP confined to the provider's endpoints)
```

- **The hook kind** (spec §16.3) is two contribution types, both privileged (spec §16.5: only host
  config, policy or the binary contribute them): `ModelEgress.transforms`
  (`transform:model-egress`, `Transform<ChatRequest>`, composed in ascending priority; one that fails
  fails the pipeline) and `ModelEgress.gates` (`gate:model-egress`, `Gate<ChatRequest>`, all run on
  the transformed request; a failing gate denies). They serve over the extension protocol like any
  transform and gate, so process extensions can contribute them.
- **The only way to a wire.** A `ChatWire` takes only a `WireRequest`, and only `ModelEgress.clear`
  makes one, after the hooks. The architecture tests read the compiled SDK and check that
  `WireRequest` is sealed with an internal constructor and no static members, that the SDK shows its
  internals to no assembly, that only `ModelEgress` constructs it, and that only `ModelAccess` calls
  `clear` (an IL scan for calls of the methods and their generic instantiations). Extensions and
  host code that have a `ChatWire` cannot call it.
- **Every sender uses it.** The host builds one `ModelAccess` from the contributions in effect (T129)
  and hands out `Llm`s made from it (`ModelAccess.llm`): the session's loop (T127), `fx.llm` from cells
  (a brokered host binding over the session's `ILlm`), agents (T213), the summarizer (T206), judgments
  (T402), extensions (through `IExtensionEnv`, T109b). None of them holds a wire, credentials or an
  HTTP client for a provider, so there is no other path to write. AGENTS.md makes it a rule.
- Failures before the wire are a stream with one `failed` event; `egressBlocked {ask, reasons}` names
  the contribution and its reason (`fx.egress.exclusions/exclusions: content of .env, excluded by host
  (*.env), would leave in system[1]`). The caller decides what to do (a strategy shows it; an `ask`
  becomes a grant request in T127/T123).
- **Secret redaction** is the next transform on the same point (T601, `fx.egress.redaction`, a policy
  extension); it runs after the exclusions.

## Provenance: what a part quotes

Content exclusions are about files, but a request holds text. So parts say where their content came
from: `TextPart`, `MediaPart` and `ToolResult` carry `Sources: ContentSource list`
(`{path, lines?}`, the path relative to the workspace with `/`, or absolute outside it). The JSON field
`sources` is left out when empty, so requests without provenance keep their JSON (cassettes, cases).

| Producer | Sources it sets | Task |
|---|---|---|
| Prompt render (`Fx.Prompt`): every text part and tool result made from pieces that show a file (`Piece.File`: sections, pinned files, receipts, summaries, views) | each file and its lines (`Piece.sources`) | T126 (done), T203 |
| The CodeAct loop: a cell's result as a tool result or message | every file the cell read through the bridge (`fs.read` calls of its cell), as a `Piece.Quote` | T127 (done), T205 |
| Hybrid tools (`read`, `edit` diffs) | the file read or edited (`Piece.File`) | T127 (done) |
| Per-cell diffs shown to the model | the files changed | T205 |
| Summaries (compaction) | the union of the sources summarized | T206 |
| `fx.llm` from cells | the files the cell read through the bridge before the call (its `Read` activity) | T213 |

- Provenance is host metadata. Every stage keeps it with its part (fitting, replay rules, the hooks);
  `ModelEgress.clear` drops it after the gates, so no wire sends it (`ChatRequest.withoutSources`).
- **Granularity is the part.** The render joins consecutive pieces into one text part, so a part that
  quotes an excluded file is redacted whole, with whatever else it joined. The render withholds the
  files its own exclusions name before that (`Withhold`), so egress redaction only meets files excluded
  after they were recorded, or by lists only the egress hooks have (a provider's policy).
- A transform that rewrites a part must keep its sources (or redact it). One that adds content from a
  file must set them; the gate catches it if the file is excluded.
- **Unknown provenance is the limitation.** Content without sources is not matched: a command's
  output that printed an excluded file (`shell.run("cat .env")`), text the user pasted, a model's own
  output quoting what it saw. The defences there are upstream: `fs.read` refuses excluded files, render
  filters its inputs (T126), and redaction of secrets by content is T601. Matching content against the
  excluded files' bytes (fingerprints) is possible later, at the cost of reading them.

## The matcher (`Fx.Models.Exclusions`)

One pure module for every place that keeps content from models: the egress hooks, the prompt render
(`Fx.Prompt.Withhold`), the host bindings, activity and diffs (T205). Fx.Models is under every one of
them and exposed by the SDK.

T126 had a matcher of its own in `Fx.Prompt`; when the two were stacked they were merged here, keeping
the stronger rule of each: from T126, no negations, `\` as a separator (no escapes), an unclosed `[`
as a literal, blank patterns as errors, case always ignored; from T124, lists by origin, `{a,b}`,
`..` resolved before matching, linear matching (T126's backtracked: `*a*a*a*a*b` was exponential),
reversed ranges and patterns of only separators or `.` as errors (T126 read `.` as "everything"). Both
test suites run against it (`tests/Fx.Models.Tests/ExclusionTests.fs`, `tests/Fx.Prompt.Tests`).

```fsharp
WorkspacePath.normalize : string -> string                    // "/" separators, ".", ".." resolved
Glob.parse   : string -> Result<Glob option, string>          // None: a # comment
Glob.isMatch : Glob -> path -> isDirectory -> bool
Exclusions.create : ExclusionList list -> Validation<Exclusions, Issue>
Exclusions.test          : Exclusions -> path -> ExclusionMatch option   // a file
Exclusions.excludes      : Exclusions -> path -> bool
Exclusions.testDirectory : Exclusions -> path -> ExclusionMatch option   // a directory and its contents
Exclusions.firstExcluded : Exclusions -> ContentSource list -> ExclusionMatch option
ExclusionRedaction.redact : (ExclusionMatch -> string) -> Exclusions -> ChatRequest -> ChatRequest * ExcludedContent list
ExclusionRedaction.find   : Exclusions -> ChatRequest -> ExcludedContent list
Placeholder.text : PlaceholderStyle -> ExclusionMatch -> string
```

**Patterns** are `.gitignore`'s (what people already write in `.gitignore`, `.copilotignore` and
`.aiexclude` files) without negations or escapes, with ripgrep's `{a,b}`:

| Pattern | Meaning |
|---|---|
| `*`, `?`, `[a-z]`, `[!a-z]` / `[^a-z]` | any characters but `/`, one character but `/`, a class (never `/`) |
| `**` as a whole segment | `**/x`: at any depth; `x/**`: everything inside `x`; `a/**/b`: zero or more directories between; elsewhere `**` is `*` |
| `{a,b}` | alternatives (nested allowed, at most 256 expansions) |
| no `/` but at the end | matches the name at any depth (`*.env`, `node_modules`) |
| a leading or inner `/` | anchored at the workspace root (`/config/*.json`, `docs/*.md`) |
| a trailing `/` | directories only, so everything inside them (`secrets/`) |
| `\` | a separator, as `/` (a pattern written on Windows works); there are no escapes: `[*]`, `[?]`, `[[]`, `[{]` match those characters |
| an unclosed `[` or `{` | the character itself (`a[b` matches `a[b`) |
| `# …` | a comment; patterns are trimmed |
| `!pattern`, a blank pattern, only separators or `.`, a reversed range (`[z-a]`), more than 256 alternatives | an error, never ignored: an exclusion silently dropped would leak what it was meant to withhold |

- **Patterns only add up.** A path is excluded if any pattern of any list matches it or a directory
  above it. There are no negations, so the order of patterns and lists does not matter, adding a
  pattern or a list only ever excludes more ("policy only tightens"), and no list can undo another's
  (a property test checks it). `Exclusions` keeps one `ExclusionList {origin, patterns}` per origin to
  report which one excluded a path (the first that does).
- **Paths** are normalized first: `\` becomes `/`, repeated separators, `.` and a trailing separator go,
  `..` is resolved where it can be (`C:\..\x` is `C:/x`; a relative path above the workspace keeps its
  `..`s). Absolute paths keep their root (`/`, `C:/`, `//server/`): unanchored patterns match them
  (`*.env` excludes `/home/u/p/.env`), anchored ones do not.
- **Case** is always ignored (patterns and paths): a pattern must hold on case-insensitive filesystems,
  the render and the egress hooks cannot know which filesystem a path came from, and excluding too much
  is the safe side. One rule for every caller keeps them in agreement.
- Unicode normalization is not applied (the binary runs with invariant globalization, where
  `String.Normalize` does nothing): a pattern written in NFC does not match an NFD name (macOS can store
  either). Write patterns with wildcards around accented letters if this matters.
- Matching is dynamic programming over the pattern's tokens and the path (no backtracking, linear in
  both), so a project's patterns cannot make it slow.
- Invalid patterns are issues with their list and index; the slot codecs reject them when config is
  resolved.

**Redaction** replaces each part whose sources are excluded:

| Part | Becomes |
|---|---|
| text (system or message) | a text part with the placeholder (provider state dropped) |
| media | a text part with the placeholder |
| tool result | the same call id, name and error flag, the placeholder as its content |
| media inside a tool result | a text item with the placeholder |
| reasoning, tool calls, refusals, breakpoints | unchanged (model output; they carry no sources) |

The **placeholder** is stable (the same text for every request, so a redacted history stays a
cacheable prefix) and tells the model the content exists but is excluded: `[content excluded: it
comes from a file that content exclusions keep out of model requests]`, or with the `path` style
`[content excluded: <path> is kept out of model requests by content exclusions]`. It never carries a
size or a digest. A redaction before a provider's signed reasoning changes the prompt prefix; Anthropic
then refuses the request (`invalidRequest`), which is why the render withholds excluded files first and
egress redaction is the backstop.

## Configuration (`ContentExclusions`, Fx.Sdk)

Variant slots (recorded in every profile), contributed by `fx.egress.exclusions`:

| Slot | Type | Default | Who sets it |
|---|---|---|---|
| `egress.exclusions.policy` | patterns | `[]` | policy files (pins); privileged |
| `egress.exclusions.host` | patterns | `[]` | the host's config, explicit selections; privileged |
| `egress.exclusions.project` | patterns | `[]` | the trusted project's config (`.fx/config.json`, names via `Product`) |
| `egress.exclusions.placeholder` | `plain` \| `path` | `plain` | any layer |
| `egress.exclusions.action` | `redact` \| `deny` | `redact` | any layer |

```json
{ "variants": { "egress.exclusions.host": ["*.env", "**/*.{pem,key}", "secrets/"] } }
```

- **Precedence:** each origin has its own slot, so no layer replaces another's list: a project can add
  patterns, never remove the host's or policy's (privileged slots refuse project and session
  selections). `ContentExclusions.read` gives the lists in the order policy, host, project. A session
  override may change the project's list, as it may change any of the project's settings. Project
  config is read only once the project is trusted (spec §18), so an untrusted clone excludes nothing of
  its own.
- **Provider policy** (Copilot organisation exclusions, T210, spec §10.6) is fetched at run time per
  repository: the provider contributes its own `transform:model-egress` and `gate:model-egress` from
  `ContentExclusions.hooks` with its lists (and applies them to requests for its models). Hooks compose:
  its transform redacts more, its gate is combined by `max`, so it can only tighten.
- `action = deny`: nothing is rewritten; the gate denies any request holding excluded content. For
  teams that would rather a request fail than send a placeholder.
- Without the extension, the slots are not registered and `ContentExclusions.read` reads no
  exclusions; a config that sets them then fails resolution ("unknown variant slot") rather than
  silently sending excluded content.

## The hooks and `fx.egress.exclusions`

`ContentExclusions.hooks (settings: Eff<IExtensionEnv, ExclusionSettings>)` makes the transform and the
gate; settings are read for each request (in the extension's environment) and compiled once per
change:

- **transform** (`Priority = -1000`, before every other egress transform): with `redact`, the request
  with excluded parts replaced; with `deny`, unchanged. Settings that do not compile fail it (so the
  pipeline fails and nothing is sent).
- **gate**: `Deny` if the request (after every transform) still holds a part whose sources are
  excluded (the deny action, or a later transform that put content back) or if the settings do not
  compile; otherwise `Allow`. The reason names the first file, its
  origin and pattern, where it was, and how many more.

`fx.egress.exclusions` (`Product.extensionId "egress.exclusions"`, native, public SDK only) contributes
the five slots (`variant-slot`), the transform and the gate (`exclusions`). `EgressExclusions.create
settings` builds it; the host passes `ContentExclusions.ofVariants` over the resolution in effect (T129),
and once extensions read their own slots through `IExtensionEnv` (T109b) the default becomes
`ContentExclusions.read`, which reads the caller's resolution, so a session's project exclusions apply
to its requests.

## Upstream: bindings, render, activity

- **Host bindings** (`CoreSettings.Exclusions`, none by default): `fs.read` of an excluded file fails
  with code `excluded` (the origin and pattern as `error.data`) before reading a byte. Writes, patches,
  listings and stats are allowed (names and digests are not content). The path is matched normalized
  and relative to the workspace (`CoreBindings.exclusionPath`), so `..` cannot step around an anchored
  pattern; a symbolic link is matched by its own path, not its target (exclusions are a guardrail, not
  a sandbox). The strategy fills the exclusions from the session's settings (`ContentExclusions.compile`,
  T127).
- **Kernels** never see excluded content through the bridge; the kernel conformance suite checks that a
  read refused as excluded fails in the cell with the reason and no content.
- **Prompt render** (`Fx.Prompt`, [prompt.md](prompt.md)): `Withhold.pieces` replaces every excluded
  `Piece.File` (prefix, history, suffix, pins) with `Piece.Withheld` and keeps it out of `SeenRanges`;
  `ContextEvent.withhold` keeps excluded content out of the log. Its `RenderConfig.Exclusions` is the
  same `Exclusions` value the hooks compile (`ContentExclusions.compile settings`), so render and egress
  exclude the same files. Every part it makes from pieces that show files carries their `Sources`;
  `Render.cacheKey` hashes the parts without them.
- **Activity and per-cell diffs** (T205) skip excluded files and attribute the rest.

## Native AOT

`egress-globs` (a table of patterns and paths, invalid patterns, normalization),
`egress-redaction` (the session request redacted in both placeholder styles, as canonical JSON) and
`egress-model-access` (the extension bootstrapped with the Anthropic, OpenAI and canonical wires;
what reaches each provider; the deny action; invalid settings), on all eight targets.

## Left to later tasks

| What | Task |
|---|---|
| `Sources` from a cell's `Read` activity (kernel-side reads the bridge does not see) | T205 |
| Extensions reading their own slots (`ContentExclusions.read` in `IExtensionEnv`) | T109b |
| Copilot organisation exclusions as a provider's own egress hooks | T210 |
| Secret redaction by content | T601 |
| Fingerprints of excluded files, to catch their content where provenance is unknown | later |
