# Variants and profiles (`Fx.Variants`)

Spec: §15 (with §1 principle 7, §4.2, §6.5, §6.8, §6.9, §8.2, §10.4, §11.5, §16, §18 and §19.6). Task: T106.
Code: [`src/Fx.Variants`](../../src/Fx.Variants) (pure: no I/O).
Tests: `tests/Fx.Variants.Tests` and the `variants-*` scenarios of the JIT-vs-native diff.

Every configurable behaviour is a **variant slot**: a named, typed choice with a default. Code reads slots
through the `IVariants` capability, never configuration directly. The values come from layered config under
policies; together with the extension versions, conformance results and the component lockfile they form a
**profile**, whose hash every session, run and eval record refers to. So every behaviour is recorded,
replayable, and comparable across runs (spec §1, principle 7: measured, not assumed).

| Piece | Types | File |
|---|---|---|
| Slots | `SlotId`, `Slot<'T>` (typed), `SlotInfo` (erased), `SlotDecl` (JSON), `SlotOwner` | `Slot.fs` |
| The variant registry | `SlotRegistry`, `RegistryIssue` | `Registry.fs` |
| Where values come from | `Layer`, `Source`, `Selections`, `Policy` | `Sources.fs` |
| Resolution | `Resolution.resolve`, `Resolved`, `VariantIssue` | `Resolution.fs` |
| Profiles | `Profile`, `ProfileId`, `ProfileDifference` | `Profile.fs` |
| The capability | `Variants`, `IVariants`, `VariantReads` | `Variants.fs` |
| Eval matrices | `Axis`, `Matrix.expand` | `Matrix.fs` |
| The core's slots | `Slots.*`, `ActionSurface`, `EditGuard`, `GoalContext`, `WindowsShell` | `Slots.fs` |
| Test double | `FakeVariants` | `Fakes/FakeVariants.fs` |

`Fx.Variants` references only `Fx.Contracts` and `Fx.Effects`, so the public SDK can expose it to extensions
(it is on the SDK surface, [sdk.md](sdk.md)).

## Slots

```fsharp
type Slot<'T> =
    { Id: SlotId; Description: string; Owner: SlotOwner
      Codec: Codec<'T>; Default: 'T; Choices: 'T list option; Privileged: bool }

let editGuard =
    Slot.enum "edit.guard" "What editing a range the agent has not read does: …"
        [ EditGuard.Off, "off"; EditGuard.Warn, "warn"; EditGuard.Require, "require" ] EditGuard.Warn

let inlineValueMaxBytes =
    Slot.create "values.inlineMaxBytes" "The largest value … copied inline; …"
        (Codec.int |> Codec.between 0 (1024 * 1024)) (16 * 1024)
```

- **Ids** are two or more dotted segments, each a lowercase letter followed by letters, digits, `_` or `-`
  (`agent.actionSurface`, `model.role.main`), at most 128 characters. They appear in config files, on the
  command line, in profiles and in eval matrices, so a released id never changes: add a new slot instead.
- **Values** are what the codec reads, writes and refines (`Codec.between`, `Codec.pattern`, …). A record
  slot's value is the whole record: a selection replaces it, it is not merged. Things that vary
  independently are separate slots.
- **Choices**: an enumerated slot lists the values it may take (`Slot.enum`, `Slot.withChoices`); eval
  matrices use them as an axis. An open slot takes any value its codec accepts.
- **Privileged** slots (`Slot.privileged`) can be selected only by host config, explicit selections and
  policy, never by a project's config or a session override. Use it for slots that weaken isolation or
  protection, e.g. a sandbox driver (T113): a cloned repository must not be able to switch them off.
- **Defaults** are provisional until evals decide (spec §1). Changing a default changes every profile's id,
  which is the point: behaviour changed.
- `Slot.info` erases the type: `SlotInfo` keeps the schema, the default and choices as canonical JSON, and a
  `Check` that decodes with the codec, applies the choices and returns the canonical re-encoding. Resolution
  and the registry work on `SlotInfo`; code reads with the typed `Slot<'T>`, which is the key.

## The variant registry

`SlotRegistry.create (slots: SlotInfo list)` is the variant registry of spec §3: every slot this host knows,
in registration order. It rejects duplicate ids (naming both owners), defaults and choices that fail their
own slot's check, and enumerated slots without distinct choices; these are defects in whoever defined the
slot. The host builds one registry from `Slots.all` and the slots extensions contribute, and builds a new one
when extensions change.

- `SlotRegistry.describe` gives every slot as JSON (id, owner, description, default, choices, privileged, JSON
  Schema), for `fx` to list and for docs (`tests/Fx.Variants.Tests/golden/slots.json`).
- `SlotRegistry.configSchema` is the schema of a config file's `variants` object, one optional member per
  slot; `Schema.toJsonSchema` makes the JSON Schema that editors use to complete and check config files
  (`golden/config.schema.json`).

## Where values come from

| Layer (precedence) | What | Who builds it |
|---|---|---|
| `Default` (0) | the slot's default | the slot |
| `Host` (1) | the host's config file, `variants` member | the host (T129), from the user's config directory |
| `Project` (2) | the project's config file, `variants` member | the host, **only once the project is trusted** (spec §18) |
| `Session` (3) | overrides made in a session (a TUI command such as `/model`) | the session (T125) |
| `Explicit` (4) | an eval profile, then command-line flags (`--variant slot=value`) | the CLI and the eval harness (T217) |
| `Policy` | pins and forbids, on top of all layers | the host, from policy files or policy extensions |

- A `Source` is a layer and a human-readable origin (`host (~/.fx/config.json)`, `explicit (--variant)`). It
  goes with every resolved value and every recorded read, so a person can always tell why a session behaves
  as it does.
- **Config files are JSON**, read by our strict reader: `{"variants": {"edit.guard": "require"}}`. Other
  members belong to other readers (`Selections.ofConfig` ignores them). JSON because it is what the rest of
  the system reads and writes (manifests are JSON too, T108) and our reader is AOT-safe and strict. The TOML
  in the spec's examples needs an AOT-safe TOML reader that produces `Json`; everything downstream would stay
  the same, so it can come later without changing this module.
- **Command line**: `Selections.parseArgument "edit.guard=require"`. The value is JSON if it parses as JSON,
  and a string otherwise (`depth=4` is a number, `mode=fast` a string; quote a string that looks like JSON:
  `x.y="true"`).
- **Policies**: `{"pin": {"shell.windows": "pwsh"}, "forbid": {"agent.actionSurface": ["hybrid"]}}`
  (`Policy.ofJson origin json`), or typed with `Policy.pin` and `Policy.forbid`.
- Environment variables are not a source of their own: the composition root may turn them into a layer, with
  a source that says so.

## Resolution

```fsharp
Resolution.resolve (registry: SlotRegistry) (policies: Policy list) (layers: Selections list)
    : Validation<Resolution, VariantIssue>
```

Pure and deterministic: the same inputs give the same resolution or the same issues, in the same order.

- **Precedence.** For each registered slot, the selection of the highest layer wins; within one layer
  (several explicit sources), the later one. Layers of different ranks can be given in any order.
- **Every selection is checked**, also those a higher layer overrides: unknown slots (with up to three
  suggestions: close ids, or ids with the same last segment), invalid values (the codec's issues, paths
  included), a slot selected twice in one layer, and privileged slots selected by a project or a session.
- **Policies only tighten.** A pinned slot takes the pinned value, with source `policy`; a selection that
  differs from the pin is an issue rather than silently overridden, so an eval never runs with something
  other than what it asked for. A value in effect that a policy forbids is an issue, including a default
  ("select another value"); a forbidden selection that a higher layer overrides is not. Pins and forbids are
  checked against the slots like selections, and two policies pinning different values conflict.
- **Any issue fails resolution**, and every issue is reported. Config errors are fixable by the person who
  sees them, and silent fallbacks would make profiles lie.

Issues read like this (from the `variants-resolve` scenario):

```
project (project.json): edit.gaurd: unknown variant slot; did you mean "edit.guard"?
project (project.json): demo.isolated: privileged: only host config, explicit selections and policy may select it
project (project.json): demo.budget: expected a number from 0 to 100, got number 101
session (/x): values.previewLimits.maxChars: expected an integer of at least 0, got number -1
shell.windows: pinned to "pwsh" by policy org.json and to "powershell" by policy lab.json
project (project.json): goal.context: "continue" is forbidden by policy org.json
```

`VariantIssue.toIssues` gives the same as model-readable `Issue`s with paths into the selections object
(`$["edit.guard"]: … (from host (~/.fx/config.json))`), for a model that writes config or eval matrices.

A `Resolution` has one `Resolved` (`{Slot; Value; Source}`, the value canonical) per registered slot.
`Resolution.explain` prints them one per line (`edit.guard = "warn" (default)`); `Resolution.value slot`
decodes one; `Resolution.defaults registry` is every slot at its default.

## The capability: `IVariants`

```fsharp
type Variants = { Read: SlotId -> Resolved option }     // None: not registered
type IVariants = abstract Variants: Variants

let! guard = Variants.get Slots.editGuard               // Eff<#IVariants, EditGuard>
let! surface, source = Variants.read Slots.actionSurface
```

- **This is the only way configurable behaviour reads its configuration** (AGENTS.md). Effects never read
  config files, environment variables or flags: those become layers, resolved once.
- Reads are synchronous lookups with no cancellation token: the values are resolved before the environment
  is built, and they never change for one interpreter. A session override builds a new resolution and a new
  interpreter (and a new profile, below).
- **Defects**: reading a slot the registry does not have throws `UnregisteredSlotException` (its value would
  not be in the profile); a value that does not decode with the reader's codec throws
  `SlotTypeMismatchException` (two slots with one id and different types).

| Interpreter | What it does |
|---|---|
| `Variants.ofResolution resolution` | the values of a resolution; pure, so it is also the production interpreter (no analyzers entry, no live counterpart) |
| `Variants.record (reads: VariantReads) variants` | records each slot's **first** read, with value and source, in the order first read |
| `Variants.replay (reads: Resolved list)` | answers from recorded reads, whatever today's config says; a slot the recording did not read throws `ReplayDivergedException` |
| `FakeVariants.create slots selections`, `FakeVariants.core selections` | slots at their defaults with selections on top (`Selections.select slot value`); throws on invalid setups |

- Recording keeps one read per slot because one interpreter's values never change: the list says which
  slots a run depended on (useful for evals), and replay needs nothing more. Replay does not depend on the
  order or number of reads, so concurrent effects (`Eff.all`) replay too.
- `Variants.readsCodec` writes recorded reads as `[{"slot", "value", "source": {"layer", "origin"}}]`.
- `VariantsConformance` (tests) is the conformance suite: a function from an interpreter (made from a
  resolution) to tests. `ofResolution`, `record` and `replay` pass it.

## Profiles

```fsharp
type Profile =
    { Format: int                                       // 1
      Core: string                                      // the binary's version
      Selections: Map<SlotId, Json>                     // every registered slot, defaults included
      Extensions: Map<string, ExtensionVersion>         // { Version; Hash: manifest sha256 option }
      Conformance: Map<string, Map<string, SuiteResult>> // subject → suite → { Passed; Failed; Skipped }
      Lockfile: string option }                         // sha256 of the component lockfile
```

- `Profile.ofResolution core resolution`, then `withExtension`, `withConformance` and `withLockfile`. The
  host fills extensions from the extension registry (T108), conformance results from the suites the selected
  subjects passed (kernels, wire adapters, providers), and the lockfile from the component planner
  (`Lockfile.hash`, [components.md](components.md)).
- **Id**: `ProfileId` is the SHA-256 of the UTF-8 bytes of the canonical JSON (`Json.serializeCanonical` of
  `Profile.codec`), written `sha256:<64 lowercase hex>`; `Short` is the first 12 hex digits for display.
  Maps and canonical JSON make it independent of the order anything was added in and of number notation
  (`0.250` and `2.5e-1` hash alike); sources are not in it, values are. Golden: `golden/profile.json`.
- **The codec is part of every stored id.** Change the profile's JSON only with a new `Format` (old ids stay
  valid for the documents they name). Adding an optional field that is left out when absent keeps old ids.
- **Storage**: `Profile.bytes` is exactly what is hashed, so putting it in the blob store gives a blob whose
  hash is the profile's id (`sha256:` either way). `Profile.ofBytes id bytes` reads it back and checks it.
- `Profile.diff a b` lists what differs, in a fixed order (format, core, selections, extensions, conformance,
  lockfile): what an eval report shows next to two scores.
- `Profile.layer profile` turns a profile's selections into an explicit layer. Resolving it with today's
  registry reproduces the values, gives new slots their defaults, and reports slots that no longer exist:
  counterfactual and partial replay (spec §15). Exact replay uses recorded reads instead.

## Recording on every record

The contract the session machine (T125), record and replay (T131), the run kernel (T212) and the eval
harness (T217) follow. It keeps the profile out of the event envelope: a profile belongs to the actor's run,
not to each command, and every event of a stream would otherwise repeat the same 71 characters.

1. **Every stream that runs under a profile binds it.** The first event of every session, run and eval-case
   stream carries the id of its profile (`string profileId`; machine events stay FSharp.Core types, spec
   §4.4). A change of selections (a session override) resolves again and appends an event with the new id
   and the slots that changed (`Profile.diff`).
2. **Each event's profile is the latest binding before it**: a fold over the stream, like any state.
3. **Profiles are stored before they are bound**, content-addressed in the blob store (`Profile.bytes`), so
   anyone with the log and the blob store can load the profile of any event (`Profile.ofBytes`). Storing the
   same profile again stores nothing.
4. **Reads are journaled per binding**: the session's environment wraps its interpreter with
   `Variants.record`, and T131 journals the reads with the step results; exact replay builds its environment
   with `Variants.replay`.
5. **Eval results** (T217) are keyed by profile id; the dashboard (T220) publishes the profile documents next
   to the scores.

`RecordingTests` runs the whole contract: a stream that binds two profiles, a fold that tells each step's
profile, profiles loaded back from the blob store by id, and a replay of the journaled reads.

## Eval matrices

```fsharp
Matrix.expand "smoke"
    [ (Matrix.choices (Slot.info Slots.actionSurface)).Value      // every choice of an enumerated slot
      Matrix.axis Slots.editGuard [ EditGuard.Warn; EditGuard.Require ] ]
// one explicit layer per combination, the first axis slowest, origin "smoke: agent.actionSurface=…, …"
```

Resolving each combination with the base layers and policies gives one profile per combination, or the
issues of a combination a policy forbids. Eval profiles use API-key routes only (spec §10.4); that rule lives
with the model routes (T208, T217).

## The core's slots (`Slots`)

| Slot | Type | Default | Spec |
|---|---|---|---|
| `agent.actionSurface` | `cells-only` \| `hybrid` | `cells-only` (provisional; both ship without a baseline preference, T218 decides) | §6.9 |
| `edit.guard` | `off` \| `warn` \| `require` | `warn` | §6.8 |
| `values.previewLimits` | `Limits` (`maxChars`, `maxItems`, `maxDepth`, `maxStringChars`) | 4000, 50, 4, 1000 (larger than `Limits.defaults`, T203a) | §6.6, §6.7 |
| `values.inlineMaxBytes` | integer, 0 to 1 MiB | 16384 | §6.6 |
| `values.argumentMaxBytes` | integer, 1 KiB to 12 MiB | 8388608 | §6.6 |
| `shell.windows` | `pwsh` \| `powershell` | `pwsh` | §6.5 |
| `goal.context` | `continue` \| `compact` \| `fresh-handoff` | `fresh-handoff` | §8.2 |
| `model.role.<role>` for `main`, `fast`, `summarizer`, `review`, `judge`, `embed` | `provider/model` or `null` | `null` (unset until configured) | §10.4 |

The code that reads them comes with later tasks (T127, T201, T203b, T204b, T111, T214, T208). Agents and
extensions add their own model roles with `Slots.modelRole role |> Slot.ownedBy id`. Slots of a feature that
lives in an extension belong to that extension.

Core modules above `Fx.Variants` define their slots next to the code that reads them and list them for the
host, which registers them with `Slots.all`: `PromptSlots.all` (`prompt.views`, `prompt.cache.breakpoints`,
`prompt.cache.prefixTtl`, `prompt.repoMap`; [prompt.md](prompt.md)).

## Extensions contribute slots

Slots are values and `SlotInfo` is their erased form, as `Contribution` is for contribution types (T108), so
an extension adds slots without changing the core:

- **In process** (native and built-in extensions): the extension keeps its `Slot<'T>` values (the typed keys
  it reads with) and contributes `Slot.info slot` under the `variant-slot` contribution type
  (`VariantSlots.contributionType` in `Fx.Sdk`, which exposes `Fx.Variants`); the host builds its registry
  from `VariantSlots.slots`, which sets the owners.
- **Declared** (declarative manifests, extension processes): the JSON form `SlotDecl`
  (`{"id", "description", "default", "choices", "privileged"}`, `SlotInfo.declCodec`) becomes a `SlotInfo`
  with `SlotInfo.ofDecl owner decl`. Declared slots are enumerated, since the host cannot run the
  extension's codec; their values are limited to their choices, compared in canonical form.
- **The host sets the owner** (`SlotInfo.withOwner (SlotOwner.Extension id)`), so an extension cannot claim
  a core slot, and the registry rejects an id that two owners define. Name an extension's slots after it
  (`acme.tools.mode`) to avoid collisions.
- **Reading from another process**: extension processes read slots through the host's `IVariants` over the
  extension protocol; the capability is data in, data out, so it crosses JSON-RPC as is.
- Profiles record the version of every extension (`Profile.withExtension`), so a contributed slot's
  behaviour is pinned along with its value.

## Adding a slot

1. Define it next to the code that reads it, with `Slot.enum` or `Slot.create`: an id (see the rules above),
   a description that says what it changes, a codec with refinements, a default, choices if enumerated, and
   `Slot.privileged` if a project must not be able to set it.
2. Register it: core slots in `Slots.all`; an extension's as contributions (above).
3. Read it with `Variants.get slot` in an `Eff` whose environment has `IVariants`, never from configuration
   or the environment.
4. Test the code under different values with `FakeVariants.create` and `Selections.select`.
5. Update `tests/Fx.Variants.Tests/golden` (`UPDATE_GOLDEN=1`) when you add a core slot, and review the diff.

## Native AOT

The `variants-*` scenarios run resolution with every kind of issue, the config schema, profiles (canonical
text, ids, order independence, reading back, diffs, matrices) and the capability (typed reads, `Eff.all`,
recording, replay, the defects) on the JIT and as a native binary on every target. The module uses no
reflection; slots are generic records of closures, and SHA-256 comes from the base class library.

## Left for later

| What | Task |
|---|---|
| Reading an extension's slots from its code (`IVariants` in `IExtensionEnv`, and over the extension protocol) | T109b |
| Batch and page sizes and actor retry delays as slots (durability and the subscribe gap are the host's: [host.md](host.md)) | when tuned |
| Binding profiles in run streams (sessions bind theirs: [sessions.md](sessions.md)) | T212 |
| Journaling reads with step results, `ReplayEnv` | done: T131, [replay.md](replay.md) |
| Eval profiles over matrices, API-key-only routes, results keyed by profile | T217 |
| A TOML reader producing `Json`, if config files move to TOML | when needed |
| Allowlists in policies (only these values) for open slots such as model roles | when policies need them |
| Relaxing unknown slots in host config to warnings for slots of disabled extensions | when extensions can be disabled from the TUI |
