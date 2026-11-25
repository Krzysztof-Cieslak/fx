# Module spec: models (`src/Fx.Models`, `src/Fx.Sdk/Models`)

Spec: §10.1–10.3 (with §4.5, §5, §6.9, §10.4, §11.3, §15, §16.2, §18). Tasks: T118, T119.
Code: [`src/Fx.Models`](../../src/Fx.Models) (the canonical model, pure),
[`src/Fx.Sdk/Models`](../../src/Fx.Sdk/Models) (the wire contract, the model-egress hooks and
`ModelAccess`), the `IHttp` capability in [`src/Fx.Effects/Http.fs`](../../src/Fx.Effects/Http.fs)
with its live interpreter `SystemHttp` (`src/Fx.Effects.Live`), the reference wire
[`extensions/Fx.Ext.ModelCanonical`](../../extensions/Fx.Ext.ModelCanonical), and the Anthropic wire
[`extensions/Fx.Ext.ModelAnthropic`](../../extensions/Fx.Ext.ModelAnthropic).
Tests: `tests/Fx.Models.Tests` (with the wire-conformance suite in `Conformance/WireConformance.fs`),
`tests/Fx.Ext.ModelAnthropic.Tests`, the HTTP conformance suite in `tests/Fx.Effects.Tests` and
`tests/Fx.Effects.Live.Tests`, and the `models-*` scenarios of the JIT-vs-native diff.

Every model call goes through one provider-neutral model. Wire adapters (one per provider API
family: `anthropic.messages` T119, `openai.responses` T120, `openai.chat` T207, …) translate it to
and from their HTTP; everything else (sessions, prompt render, strategies, evals) speaks only the
canonical model.

## Layers

| Where | What | Who uses it |
|---|---|---|
| `Fx.Effects` (`Http.fs`) | `IHttp`: requests, streamed bodies, timeouts, typed failures; `InMemoryHttp` fake | adapters (through `WireCtx`), auth flows (T121), catalogs (T208) |
| `Fx.Models` | model metadata, the canonical chat model and its codecs, the fold, `ChatStream`, `ILlm`, SSE/NDJSON parsers, provider definitions, cassettes, replay rules, fitting | everyone; exposed by `Fx.Sdk` |
| `Fx.Sdk/Models` | `WireCtx`, `WireRequest`, `ChatWire`, `SseWire`, the contribution types, the model-egress hook points, `ModelAccess` | extensions (adapters, providers, egress hooks), the host's composition root |
| `extensions/Fx.Ext.ModelOpenAI` | `fx.model.openai`: the `openai.responses` wire and the OpenAI API as a provider | the host (bundled) |
| `extensions/Fx.Ext.ModelCanonical` | the `canonical` wire, written like any adapter | tests, a future fx-served model |
| `extensions/Fx.Ext.ModelAnthropic` | `fx.model.anthropic`: the `anthropic.messages` wire and the Claude API as a provider | the host (bundled) |

`Fx.Models` references only `Fx.Prelude`, `Fx.Contracts` and `Fx.Effects`, so `Fx.Sdk` exposes it
(the architecture tests' SDK surface includes it). The EFF analyzers apply to all of it. It is packed
as `<Name>.Models` with the other SDK packages (`bun scripts/packages.ts` builds the canonical wire's
package from the SDK package alone), and `@fx/sdk`'s generated `contracts.d.ts` has the TypeScript
types of `ChatRequest`, `ChatEvent`, `ChatResponse`, `ChatFailure`, `ModelInfo` and `ProviderDef`
([sdk.md](sdk.md)).

## Contracts (spec §10.2)

- `ModelRef` is `provider/model` (the model id may contain `/`). Provider ids are
  `^[a-z0-9][a-z0-9.-]*$`.
- `ModelInfo = { Ref; Kind; Family; Name; Limits; Caps; Cost; PlanBacked }`. `Family` names the wire
  family that serves the model (`None`: the provider's first endpoint); spec §10.2 gained the field.
  `ModelLimits` (the spec's `Limits`; renamed because `Fx.Contracts.Limits` bounds previews) has the
  context window and the most output tokens.
- `ModelCaps`: `Reasoning` (efforts, budget range, visible, needs replay), `Tools` (calls,
  parallel, forced, strict), `StructuredOutput`, `Logprobs`, `Caching` (none, automatic,
  breakpoints with a maximum; cache keys; long time-to-live), `Media`, `Sampling` (which
  parameters), `Transports`.
- `Cost`: dollars per million tokens for input, output, cache reads and cache writes.
  `Usage.cost` prices a usage.
- `ModelRole` (spec §10.4): `main`, `fast`, `summarizer`, `review`, `judge`, `embed`, plus agents'
  and extensions' own. Which model plays a role is a variant (T208).
- `ProviderDef = { Id; Name; Endpoints; Catalog; Auth; Quirks; Transform }` is data:
  `Endpoints` maps families to base URLs; `Catalog` bundles `ModelInfo`s (discovery is T208);
  `Auth` lists auth method refs (kinds and settings; the methods are T121's); `Quirks` are named
  JSON flags for adapters; `Transform` sets and removes headers and query parameters and edits JSON
  bodies. Declarative extensions and host config can add providers without code.
- `ResolvedAuth = { Headers; Query; Sign }` (spec §11.3) is what an auth method resolves for one
  call. `Sign` takes and returns our `HttpRequest` (not .NET's `HttpRequestMessage`), so signing is
  testable and recordable. Its `ToString` hides values.

## The canonical model (spec §10.3)

### Requests

`ChatRequest = { Model; System; Messages; Tools; ToolChoice; ParallelToolCalls; Sampling;
Reasoning; MaxOutputTokens; Output; Cache; Metadata }`.

- **Messages** have a `Role`: `User` or `Assistant`. Instructions are the request's `System` parts
  (text and cache breakpoints). Tool results are parts of user messages; adapters map them to
  their provider's form (Anthropic `tool_result` blocks, OpenAI `function_call_output` items).
- **Parts** (`Part`, tagged `kind`):

  | Part | Fields | Notes |
  |---|---|---|
  | `text` | `text`, `state?` | |
  | `media` | `media` (image, pdf, audio, video), `mediaType`, `source` (`base64`, `url`, `blob`), `name?` | a `blob` source is a blob-store digest, resolved before the request leaves (T201) |
  | `reasoning` | `text` (thinking or summary, may be empty), `state?` | |
  | `toolCall` | `id`, `name`, `arguments` (the JSON text as the model wrote it, possibly invalid), `state?` | |
  | `toolResult` | `callId`, `name`, `content` (text and media), `isError` | error results carry `isError` |
  | `refusal` | `text` | |
  | `cacheBreakpoint` | `ttl` (`short`, `long`) | everything before it is a cacheable prefix |

- **`ProviderState`** `{ family, model, kind, data }` is opaque: a thinking signature, redacted
  thinking, an encrypted reasoning item, a thought signature. `data` is a string replayed byte for
  byte (ordinal equality through the codecs, tested with control characters, lone surrogates and
  100 000-character values). The adapter decides what goes in it (it may pack several fields as JSON
  text) and stamps it with the call's family and model (`WireCtx.state`).
- **Tools** are `ToolSpec { name; description; parameters; strict }`, `parameters` being JSON
  Schema. `ToolSpec.ofCodec` makes one from a codec (`Schema.toJsonSchema`); provider dialects (OpenAI
  strict mode) are the adapters' transformations. `ToolChoice`: auto, none, required, a named tool.
  The hybrid action surface (spec §6.9) is these tools: a `cell` tool plus `read`, `edit` and `bash`.
- **Options:** `Sampling` (temperature, top-p, top-k, seed, stop sequences), `Reasoning` (effort
  `off`…`max`, budget tokens, visible), `MaxOutputTokens`, `Output` (`text`, or `json` with a schema:
  structured output), `Cache` (`key`, OpenAI's `prompt_cache_key`; `tools`, a breakpoint after the
  tool definitions), `Metadata` (tags such as a user id; never secrets).

### Streamed events and the fold

`ChatEvent` (tagged `kind`): `started {id?, model?}`, `partStarted {index, part}`, `delta {index,
text}`, `partState {index, state}`, `partEnded {index}`, `usage {usage}`, `finished {stop}`, `failed
{error}`. `part` is `text`, `reasoning`, `toolCall {id, name}` or `refusal`; a delta appends to the
part's text, reasoning, arguments or refusal.

The **stream rules** (`ChatEvents.check`):

1. `started` comes first, if at all, and once.
2. Parts are numbered 0, 1, 2, … in the order they start; several may be open at once.
3. Deltas, state and ends refer to started, open parts; a part gets its state at most once, and
   a refusal none.
4. Usage reports are cumulative snapshots: each replaces the last, and no count decreases.
5. Exactly one `finished` or `failed`, last. `finished` requires every part ended; `toolUse` requires
   a tool call. A `failed` stream keeps its parts as they were.

`ChatAggregate.apply` is the pure, total fold. An event that breaks a rule becomes a violation, and
`ChatAggregate.result` then gives `ChatError.InvalidResponse` with the first one: a misbehaving
provider or adapter is an expected failure, never an exception. `result` is `Ok ChatResponse {id,
model, parts, stop, usage}` or `Error ChatFailure {error, id, model, parts, usage}` (what arrived
before the failure). `ChatEvents.coalesce` merges adjacent deltas (streams that differ only in how
text was split compare equal); `ChatEvents.ofResponse` is the canonical stream of a response.

**Usage** counts every input token in `input` (cached included; `cacheRead` and `cacheWrite` are
parts of it) and every output token in `output` (`reasoning` is part of it). Adapters convert:
Anthropic's `input_tokens` excludes cache reads and writes, OpenAI's includes cached tokens.

**Stop reasons:** `endTurn`, `toolUse`, `maxTokens`, `stopSequence {sequence?}`, `refusal`,
`contentFilter`, `contextWindow` (the window filled while generating), `pause` (Anthropic
`pause_turn`: send the conversation again), `other {reason}`.

### Typed errors

`ChatError` is what adapters map provider responses to (expected failures, spec §4.2):

| Case | When | Retry? |
|---|---|---|
| `rateLimited {retryAfterMs?, message}` | 429 | yes, after the wait |
| `quotaExhausted` | credits, billing, plan quota, also mid-stream | no |
| `contextOverflow {tokens?, limit?, message}` | the request does not fit: compact (spec §5), then retry | after compaction |
| `refused` | the request was refused on safety or policy grounds | no |
| `authFailed`, `permissionDenied`, `notFound` | 401, 403, 404 | no |
| `invalidRequest` | other 4xx | no |
| `overloaded {retryAfterMs?, message}` | 503, Anthropic's 529 | yes |
| `serverError {status, message}` | 5xx | yes |
| `transport {error}` | connection failed, reset (also mid-stream), timed out | yes (not `invalidRequest`, `refused`) |
| `cancelled` | the provider cancelled the response | no |
| `invalidResponse` | unreadable bodies, broken stream rules, streams that end early | yes |
| `egressBlocked {ask, reasons}` | the model-egress gates asked or denied: nothing was sent | no |
| `unavailable` | no provider, endpoint, wire, credentials or capability for the request | no |

`ChatError.isRetryable`, `retryAfter` and `needsCompaction` say what to do. `ErrorMapping.ofStatus`
maps an HTTP status (with `retry-after-ms` or `retry-after`) for adapters whose error bodies say
nothing more specific. The caller's own cancellation is an `OperationCanceledException`, never an
error (spec §4.2).

### Model switches and capability-driven requests

- **Replay rules** (`ReplayPolicy`, data with a codec, recorded with the profile): state replays
  only to the family that produced it, and within it as far as the family's `ReplayScope` allows
  (`sameModel` by default; `sameProvider`; `sameFamily`). Text and tool calls keep their content
  without the state; reasoning whose state may not travel is dropped (default) or kept as text.
  `Replay.prepare` returns what it changed. Adapters that know their family's signatures hold across
  models widen their family's scope in the policy the host uses: `Anthropic.withReplay` sets
  `anthropic.messages` to `sameProvider` (see below); T120 does the same for `openai.responses`.
- **Fitting** (`Fit.request`): a request is fitted to its model's `ModelInfo`: sampling parameters
  the model does not take are dropped, the output limit clamped, efforts mapped to the nearest the
  model has, budgets clamped, media it does not take replaced by a note in text, cache breakpoints
  beyond its maximum dropped (the earliest first) and long lifetimes shortened, cache keys, parallel
  calls, strict tools and forced tool choices dropped where unsupported. Each change is an
  `Adjustment` (traced). Tools, structured output and non-chat models it cannot serve are
  `unavailable`.

## Streams, wires and the single entry point

### `ChatStream`: a pull, not `IAsyncEnumerable`

```fsharp
type ChatStream = { Next: CancellationToken -> Task<ChatEvent option>; Close: unit -> Task<unit> }
type ChatWire   = { Family: string; Stream: WireRequest -> Eff<WireCtx, ChatStream> }
```

Spec §10.2 sketched `IAsyncEnumerable<ChatEvent>`. A record of functions pulled one event at a time
(like `Proc` and `HttpBody`) is used instead:

- nothing runs in the background: the next event is read when it is asked for, so cancellation and
  failures stay in the caller's scope (structured concurrency, no `Task.Run`, no channel to drain);
- every pull is data out, so a stream is recorded and replayed as its list of events (T131);
- F# has no `IAsyncEnumerable` builder without a library, and hand-written enumerators under Native
  AOT are more machinery than this.

`ChatStream.collect`/`fold` read to the end and always close. `Llm.complete` runs a request to a
`Result<ChatResponse, ChatFailure>`.

### Wire adapters (`ChatWire`, `SseWire`)

An adapter gets a `WireRequest` and a `WireCtx`:

- `WireCtx = { Http; Provider; Family; Endpoint; Model; Info; Timeouts; Clock; Trace; Scope }`.
  Its `Http` is the provider's connection: it refuses URLs outside the provider's endpoints
  (`ProviderDef.allows`: same scheme, host and port, path below the endpoint's), applies the
  provider's `Transform`, then the `ResolvedAuth`. So an adapter never holds credentials and cannot
  send a payload anywhere else.
- `SseWire.create` builds a wire from an adapter's pure parts (`SseAdapter<'S>`): `Request` (canonical
  request → `HttpRequest`), `Error` (an error status's body → `ChatError`), and a decoder fold
  (`Initial`, `Decode: 'S * SseEvent -> Decoded<'S>`, `End`). The driver sends with the call's
  timeouts, maps statuses outside 2xx (reading at most 64 KiB of the body), parses the event stream
  incrementally, and guarantees one terminal event: the adapter's, a transport failure
  (`transport`), an unreadable stream, or one that ended early (`invalidResponse`). It then stops
  reading and closes the body. Adapters are pure functions tested without a network.
- Wires are contributed under `ChatWires.contributionType` (`model.chat-wire`, in-process code:
  native extensions only, as spec §10.1 says core families are built-in F#), provider definitions
  under `ProviderDefs.contributionType` (`model.provider`, data). Both are privileged.

### `ModelAccess`: the only way to a wire (spec §18)

`ModelAccess = { Wires; Providers; Egress; Http; Auth; Timeouts; Replay; Clock; Trace }`, built by
the composition root from the contributions in effect and the host's HTTP and credentials (T121
provides `Auth`; `ModelAccess.noAuth` sends none). `ModelAccess.stream`, and the `Llm` that
`ModelAccess.llm` makes of it, are the single entry point:

1. find the provider (`unavailable` if none), the model's catalog entry, its family, endpoint and
   wire;
2. fit the request to the catalog entry (if any) and apply the replay rules; trace each change
   (`model.adjusted`, `model.replay` events in a `model.chat` span);
3. run the **model-egress hooks**: every `transform:model-egress` in priority order, then every
   `gate:model-egress` on the result (the gates see what would leave). A transform that fails, or a
   gate that asks or denies, ends the call as `egressBlocked`: nothing is sent;
4. resolve credentials; build the `WireCtx` around the confined connection;
5. only now call the wire, with a `WireRequest`.

`WireRequest`'s constructor is internal to `Fx.Sdk` and only step 3 makes one: a wire cannot be
handed a request that skipped the hooks, and code that has only an `Llm` cannot reach a wire at all.
Sessions, agents, the summarizer and judgments get the `Llm`; extensions will get it through
`IExtensionEnv` (T109). The hook points are `ModelEgress.transforms` and `ModelEgress.gates`
(privileged); T124 contributes the built-in content exclusions. Failures before the wire are a
stream with one `failed` event, like failures inside it.

## HTTP (`IHttp`)

A capability like the others ([eff.md](eff.md)): `Http = { Send: HttpRequest -> CancellationToken ->
Task<Result<HttpResponse, HttpError>> }`. Requests are data (method, absolute `http`/`https` URL,
headers, body bytes, `Timeouts`); responses have a status, lower-case headers and a streamed
`HttpBody { Read; Close }`. Any status is a response. `HttpError`: `invalidRequest`, `refused`,
`connectFailed`, `reset`, `timedOut {response | body}`.

- `SystemHttp` (the only network code; `analyzers.json` allows `EFF007` for that file): HttpClient
  with streamed bodies, **no redirects** (a payload goes only where its sender named), no automatic
  decompression, no cookies, the product's user agent. A timeout is an `HttpError`; the caller's
  cancellation an `OperationCanceledException`.
- `InMemoryHttp` (`Fx.Effects.Fakes`): scripted replies (`HttpReply` with `BodyStep`s: `chunk`,
  `pause`, `hang`, `break`) that honour timeouts on its clock.
- Both pass `HttpConformance` (the live one against a loopback socket server that flushes chunked
  pieces).

## Recorded streams: cassettes

A `Cassette` is recorded HTTP exchanges: per interaction the expected request (method, URL, the
headers to check, a body compared as canonical JSON or exact text) and the outcome (status,
headers, body text, and how it ended: complete, reset, hang; or a failure before a response).

- `CassetteHttp(cassette, chunking)` replays them in order. A request that differs, or one too many,
  throws `CassetteMismatchException` with the differences: the code under test did something other
  than what was recorded. Bodies are cut into pieces by `Chunking` (whole, every n bytes, at
  offsets). A hanging body waits for cancellation, or times out at once under an idle timeout.
- `CassetteRecorder` wraps a live `Http` and records into a cassette, redacting credential headers
  (`authorization`, `x-api-key`, `api-key`, `x-goog-api-key`, cookies) and identifying response
  headers. A header recorded as `<redacted>` only has to be present on replay. Review recorded
  cassettes before committing them.
- Files are indented JSON (`Cassette.render`/`parse`).

## The wire-conformance suite

`WireConformance.tests wire cases` (tests/Fx.Models.Tests/Conformance/WireConformance.fs; adapters'
test projects link the file) runs each case through `ModelAccess` on a `CassetteHttp`. A case
(`WireCase`, a JSON file) has a name, the endpoint, the canonical request, the cassette, the
expectation (a response, or a typed error) and an optional follow-up (messages after the response)
whose request must replay the provider state. Checks per case:

| Check | Fails when |
|---|---|
| sends the recorded request and folds the response as expected | the request differs from the cassette; the fold differs from the expected response (parts, stop, usage, id, model) or error; the stream breaks the rules; a body is left open |
| gives the same events however the body is cut | any of 13 cuts (1, 2, 3, 7 and 64 bytes, eight pseudo-random cut sets) changes the coalesced events or the outcome |
| accounts usage | usage decreases, or the result's usage is not the last report |
| stops for the recorded reason | the stop reason or error differs |
| keeps provider state byte for byte through its JSON | state data changes through the codecs |
| replays the provider state verbatim in the follow-up request | the follow-up request differs from the cassette's, or (unless the case opts out with `statesVerbatim: false`) a state's data is not a string of its JSON body |
| reports a connection that breaks off mid-stream as a transport failure | the body is cut in half and reset, and the stream does not end with `transport {reset}` (or throws) |
| reports a stalled stream as a timeout | the cut body hangs under an idle timeout, and the stream does not end with `transport {timedOut body}` |
| ends at once when cancelled while waiting for the provider | a pull waiting on a hung body is not cancelled within 10 s, or closing leaves the body open |
| reports a failed connection as a transport failure | a connection failure is not `transport {connectFailed}` |

The suite's own tests prove it catches wires that lose events cut across pieces, ignore
cancellation, send another request, map errors wrong, drop provider state, or throw on a broken
connection.

The canonical wire's cases are built in `CanonicalCases.fs` and kept as files in
`tests/Fx.Models.Tests/cassettes/canonical/` (golden: `UPDATE_GOLDEN=1` rewrites them): text,
Unicode, a tool call with a follow-up, refusal, max tokens, quota exhausted mid-stream, rate limit
with `retry-after`, context overflow, overload with a plain body, and an unauthorized key.
`tests/Fx.Models.Tests/streams/` holds example streams in Anthropic's and OpenAI's documented
formats; T119 and T120 build their cassettes from those formats (no API keys are needed to run the
suite).

### Adding a wire adapter (T119, T120, T207, …)

1. A native extension under `extensions/` with an `SseAdapter` (or a hand-written `ChatWire` for
   another transport), contributed under `ChatWires.contributionType` with the family as its id,
   and its providers as `ProviderDefs` data.
2. Map errors from the provider's error bodies first, `ErrorMapping.ofStatus` for the rest; map
   usage to the canonical meaning; put opaque state in `PartState` events, stamped with
   `WireCtx.state`.
3. Write cases in the provider's documented format (or record them with `CassetteRecorder` and
   review them) in `tests/<Package>.Tests/cassettes/<family>/`, and run `WireConformance.tests` on
   them, linking the file.
4. Unit-test the adapter's pure parts: the request JSON per option, the decoder over the example
   streams.

## The canonical wire (`canonical`)

The reference adapter speaks the canonical model itself: `POST <endpoint>/chat` with the
`ChatRequest` JSON, an event stream whose `data` are `ChatEvent` JSON (the `event` field is the
event's `kind`), and error bodies `{"error": <ChatError>}` (with `Retry-After` filled in if the body
has no wait). It is about 60 lines of pure functions over `SseWire`. `Canonical.echo` is a toy server
for tests and scenarios.

## The Anthropic wire (`anthropic.messages`)

`fx.model.anthropic` ([`extensions/Fx.Ext.ModelAnthropic`](../../extensions/Fx.Ext.ModelAnthropic),
T119) is a native extension on the public SDK: `SseWire.create` over pure parts, and a provider as
data. Later adapters (T120, T207) mirror its layout:

| File | What |
|---|---|
| `Provider.fs` | the family, the API version, the quirks the wire reads (`Settings.ofProvider`), the bundled catalog, `Provider.def` |
| `Request.fs` | canonical request → Messages API body and HTTP request |
| `Decoder.fs` | server-sent events → canonical events (a fold over `Decoder.State`) |
| `Errors.fs` | error bodies and `error` events → `ChatError` |
| `Anthropic.fs` | the `SseAdapter`, the wire, the replay scope, the manifest and the extension |

### The provider

`anthropic`, endpoint `https://api.anthropic.com/v1`, discovery path `/models` (T208), auth methods
`api_key` and `env` (`ANTHROPIC_API_KEY`) sending `x-api-key` (T121 resolves them; the Claude API also
takes `Authorization: Bearer`). The catalog lists current Claude models (Fable 5.2, Opus 5.5, Sonnet
5.5, Haiku 5.5, Opus 5, Sonnet 5, Sonnet 4.6, Haiku 4.5) with their limits, prices (cache writes at the
5-minute rate) and capabilities as data: adaptive thinking with the efforts each model takes (`off`
only where `thinking: {type: "disabled"}` is accepted), a budget range for the extended-thinking-only
Haiku 4.5, forced tool choice only where the model takes it, four breakpoints with the 1-hour
lifetime, image and PDF input, and stop sequences as the only sampling parameter.

The wire speaks API version `2026-10-21` (the `anthropic-version` header). Quirks a provider of the
family may set: `anthropicVersion` (versions before `2026-10-21` also get `temperature`, `top_p`,
`top_k`), `betas` (sent as `anthropic-beta`), `maxCacheBreakpoints` (4), `defaultMaxTokens` (16384).
Beta features are only ever data in these quirks.

### Requests

`POST <endpoint>/messages` with `stream: true`.

| Canonical | Messages API |
|---|---|
| `System` text | `system` text blocks (other parts: `unavailable`) |
| `Text`, `Refusal` | `text` blocks (empty ones left out: the API refuses them) |
| `Media` image / PDF | `image` / `document` (`title` from the name) with a `base64` or `url` source; blob sources and other kinds are `unavailable` |
| `Reasoning` with `signature` state | `thinking` with the text and the signature, exactly as they came |
| `Reasoning` with `redacted` state | `redacted_thinking` with its data |
| `Reasoning` without this family's state | left out (and a message left empty is left out) |
| `ToolCall` | `tool_use`; the arguments parsed back into the `input` object (`{}` if they are not an object) |
| `ToolResult` | `tool_result` (`is_error`), its text and media as content; results go first in their user message |
| `Tools` | `name`, `description`, `input_schema` (the JSON Schema as given), `strict` |
| `ToolChoice` | `auto` (sent only to turn parallel calls off), `none`, `any`, `tool`; `disable_parallel_tool_use` for `ParallelToolCalls = false`; nothing without tools |
| `MaxOutputTokens` | `max_tokens` (required): the request's, else the catalog's output limit, else `defaultMaxTokens` |
| `Sampling.Stop` | `stop_sequences`; seeds never |
| `Output = Json` | `output_config.format` (`json_schema`; the schema as given) |
| `Metadata` | `metadata.user_id` from `user_id` or `userId`; the rest is left out |
| `Cache.Key` | nothing (the API has no cache key; `Fit` drops it for catalog models) |

**Thinking** (`Request.thinking`): no reasoning request sends nothing (the model's default; thinking
is on by default from Claude 5). `Off` is `{type: "disabled"}`. A model whose catalog entry has a
budget range (extended thinking only) gets `{type: "enabled", budget_tokens}`: the budget asked for,
else the effort's (`minimal` 1024, `low` 4096, `medium` 8192, `high` 16384, `max` 32768), clamped to
the range and below `max_tokens` (thinking is left off when that leaves less than 1024). A model the
catalog does not know gets the same for an explicit budget. Otherwise it is adaptive thinking,
`{type: "adaptive"}`, with `output_config.effort` (`minimal` is `low`). `Visible` asks for
`display: "summarized"`; otherwise the model's default (`omitted` from Claude 5: thinking blocks
carry only a signature). Not reachable from the canonical efforts yet: `xhigh`, Sonnet 5.5's
`between_tools`, per-message effort.

**Cache breakpoints** (`Part.CacheBreakpoint`, `Cache.Tools`) become `cache_control` (`{type:
"ephemeral"}`, `ttl: "1h"` for `long`):

1. A breakpoint marks the last block before it, in prompt order (tools, system, messages), that can
   carry one: not thinking blocks, not empty text. With none before it, it is dropped. `Cache.Tools`
   marks the last tool.
2. Two breakpoints on one block are one, with the longer lifetime.
3. At most `maxCacheBreakpoints` (4): the earliest are dropped, as `Fit.request` does for catalog
   models, so the breakpoints nearest the end of the conversation (which make the next request's
   prefix hit) stay. The prompt render places no more than four, the stable system prefix first
   ([prompt.md](prompt.md)).
4. A 1-hour breakpoint may not follow a 5-minute one (the API refuses it): later ones become 5-minute
   ones.

### Streams

| Event | Canonical events |
|---|---|
| `message_start` | `started {id, model}`, `usage` |
| `content_block_start` `text`, `thinking`, `redacted_thinking`, `tool_use` | `partStarted` text, reasoning, reasoning, toolCall (and a delta for any initial text) |
| `text_delta`, `thinking_delta`, `input_json_delta` | `delta` (empty ones dropped) |
| `signature_delta` | gathered; `partState {kind: "signature"}` before `partEnded` |
| a redacted block's `data` | `partState {kind: "redacted"}` before `partEnded` |
| `content_block_stop` | `partEnded` (a call whose input never streamed gets its start `input` as arguments, `{}` for none) |
| `message_delta` | `usage`; the stop reason is kept |
| `message_stop` | `finished` with the stop reason (none: `invalidResponse`) |
| `error` | `failed` (`Errors.ofEvent`) |
| `ping`, other events, blocks (server tools, fallback markers) and deltas (citations) | nothing |

Parts are numbered as they start, so skipped blocks leave no gap. A block started twice, a delta or
stop for a block that is not open, or data that is not JSON is `invalidResponse`. Usage is
cumulative: counts a report leaves out keep their values, none decreases; `input` is
`input_tokens + cache_creation_input_tokens + cache_read_input_tokens`, `reasoning` is
`output_tokens_details.thinking_tokens`. Stop reasons: `end_turn`, `tool_use`, `max_tokens`,
`stop_sequence` (with the sequence), `refusal` (a streaming classifier's: a normal stop, parts kept),
`pause_turn` → `pause`, `model_context_window_exceeded` → `contextWindow`, others → `other`.

### Errors

The error's `details.error_code` first, then its `type`, then the status:

| Anthropic | `ChatError` |
|---|---|
| `enforced_spend_limit_reached`, `contract_spend_limit_reached`, `linked_organization_*` codes; `billing_error`; `invalid_request_error` about the credit balance or usage limits | `quotaExhausted` |
| `invalid_request_error` "prompt is too long: N tokens > M maximum" or "exceed context limit: a + b > M"; `request_too_large`, 413 | `contextOverflow` (tokens and limit when the message says) |
| `invalid_request_error` "content filtering policy" | `refused` |
| other `invalid_request_error` (400) | `invalidRequest` |
| `authentication_error`, `permission_error`, `not_found_error` | `authFailed`, `permissionDenied`, `notFound` |
| `rate_limit_error`, `ramp_limit_exceeded` | `rateLimited` (`retry-after-ms`, `retry-after`) |
| `overloaded_error` (529) | `overloaded` |
| `api_error`, `timeout_error` | `serverError` (the status; 500 and 504 in a stream) |
| unknown types, bodies without an error object | `ErrorMapping.ofStatus` (413: `contextOverflow`), the body cut to 500 characters; an unknown type in a stream is `serverError 500` |

### Model switches

Thinking signatures and redacted data go back only to `anthropic.messages`. `Anthropic.withReplay`
widens the family's scope to `sameProvider`: Anthropic's API reads thinking blocks from the models
that can read them and drops the others without an error ("keep passing thinking blocks back
unchanged when you switch models"), so within one provider they may go to any model. Not across
providers: another provider of the family may be a gateway or a third party's Messages-compatible
API that does not check Claude signatures, and some models' thinking is bound to the account that
produced it. A host whose providers of the family are all Anthropic platforms may set `sameFamily`.
Editing what came before a thinking block (system, tools, earlier messages) fails Anthropic's prefix
check (400): the prompt render keeps the history append-only ([prompt.md](prompt.md)).

### Cassettes

`tests/Fx.Ext.ModelAnthropic.Tests` runs `WireConformance.tests` on the case files in
`cassettes/anthropic.messages/`: text, thinking with a tool call and its follow-up, redacted and
omitted thinking, interleaved thinking with parallel calls and an error result, cache usage with five
breakpoints, media, structured output, a forced call without arguments, stop sequences, max tokens,
the context window, a refusal, an overload mid-stream, and every error response above. They are
written by hand from Anthropic's documentation: `sources/` holds the request body each canonical
request must become and the event streams (never generated from the adapter), and `Cases.fs` the
canonical requests and outcomes; `UPDATE_GOLDEN=1` rewrites the case files. With `ANTHROPIC_API_KEY`
set, `LiveTests` calls the Claude API through `ModelAccess` and `SystemHttp`, records the exchanges with
`CassetteRecorder` (the key redacted and the credential headers dropped) into
`artifacts/cassettes/anthropic.messages/live-*.json`, and replays them through the conformance checks;
reviewed recordings can be copied into the suite.

## The OpenAI wire (`openai.responses`)

`fx.model.openai` ([`extensions/Fx.Ext.ModelOpenAI`](../../extensions/Fx.Ext.ModelOpenAI), T120;
tests in `tests/Fx.Ext.ModelOpenAI.Tests`) is a native extension on the public SDK, laid out like
`fx.model.anthropic`: `Provider.fs` (the provider and catalog as data, `Settings.ofProvider`),
`Strict.fs` (strict mode's schema dialect), `Request.fs`, `Decoder.fs`, `Errors.fs` (pure), and
`OpenAI.fs` (the `SseAdapter`, the wire, the replay scope, the manifest and the extension).

### The provider

`openai`, endpoint `https://api.openai.com/v1`, discovery path `/models` (T208), auth methods
`api_key` and `env` (`OPENAI_API_KEY`) sending `Authorization: Bearer` (T121 resolves them). The
catalog lists GPT-6.2 Astra, GPT-6.2 Sol, GPT-6.1 Terra, GPT-6 Luna and GPT-5.5 with their limits
(1,050,000 tokens of context, 128,000 of output), prices (for prompts up to 272,000 input tokens;
cache writes at the 30-minute rate) and capabilities: reasoning with summaries and replay, the
efforts each model takes (`none` only where accepted; `xhigh` is not reachable from the canonical
efforts), strict tools, structured output, image and PDF input, no sampling parameters, cache keys,
and explicit cache breakpoints (with the 12-hour lifetime) from GPT-5.6 on; GPT-5.5 caches
implicitly only.

Quirks a provider of the family may set: `organization` and `project` (the `OpenAI-Organization` and
`OpenAI-Project` headers), `system` (`developer`, the default, or `instructions`), `cacheBreakpoints`
(`true` or `false` for every model; otherwise the catalog decides). Other differences (service
tiers, fields a compatible server refuses) are the provider's `Transform`.

### Requests: stateless and cache-friendly

`POST <endpoint>/responses` with `stream: true` and **`store: false`**: OpenAI keeps no
conversation state, and every request carries the whole history (no `previous_response_id`, no
conversations). The session's log is the source of truth (spec §1, §5), so replays and forks need
nothing from the provider; nothing of a conversation stays at a provider outside the trust boundary
(§18) beyond what its retention policy keeps; organizations with zero data retention work
unchanged; and a conversation can move to another model or provider. Reasoning travels as encrypted
items instead: `include: ["reasoning.encrypted_content"]` (unless the catalog says the model does not
reason; current models return them by default when stateless, the field stays for older servers).

| Canonical | Responses API |
|---|---|
| `System` text | a `developer` message first in `input` (`input_text` blocks), or `instructions` (texts joined by blank lines; quirk `system`); other parts: `unavailable` |
| user `Text`, `Refusal` | `input_text` blocks of a `user` message (empty ones left out) |
| `Media` image / PDF | `input_image` (`detail: auto`; a data URL for base64) / `input_file` (`file_data` as a data URL with a file name, or `file_url`); blob sources, other kinds and media from the assistant are `unavailable` |
| `ToolResult` | a `function_call_output` before the user's content: `output` the text, or `input_text`/`input_image`/`input_file` blocks for media or a breakpoint; `isError` has no field (the content says it) |
| assistant `Text` | an `assistant` message with string content and its `phase` (state `phase`) |
| assistant `Reasoning` with `reasoning` state | the reasoning item exactly as it came (id, summary, `encrypted_content`) |
| `Reasoning` without this family's state | left out: a stateless request cannot replay it |
| `ToolCall` | a `function_call` item (`call_id`, `name`, the arguments as written); ids of messages and calls are never sent |
| `Tools` | function tools: `name`, `description`, `parameters`, `strict` (always sent: the API makes tools strict when it is left out) |
| `ToolChoice` | `auto` (left out), `none`, `required`, `{type: "function", name}`; `parallel_tool_calls`; nothing without tools |
| `Reasoning` | `reasoning.effort` (`off` is `none`), `reasoning.summary: "auto"` when visible; budgets have no place |
| `MaxOutputTokens` | `max_output_tokens` |
| `Sampling` | `temperature`, `top_p` (top-k, seeds and stop sequences do not exist in the API) |
| `Output = Json` | `text.format`: `json_schema` with a sanitized name, the schema (rewritten when strict) and `strict` |
| `Cache.Key` | **`prompt_cache_key`** |
| `CacheBreakpoint` | `prompt_cache_breakpoint: {mode: "explicit"}` (`ttl: "12h"` for `long`) on the block before it |
| `Cache.Tools` | nothing: the API has no breakpoint after the tools (they precede the input in its prefix) |
| `Metadata` | `user_id`/`userId` → `safety_identifier`; the rest → `metadata` (16 pairs, keys up to 64 characters, values up to 512) |

**Cache layout** (spec §5): the stable prefix is tools, then the developer message, then the
history, appended to and never rewritten. The system prompt is a developer message rather than
`instructions` because models from GPT-6.2 place `instructions` after the input and never cache it.
A breakpoint marks the last content block before it in canonical order (tool results of a user
message move before its content, their marks with them); with no block before it, it is dropped; two
on one block are one with the longer lifetime. Breakpoints are sent only to models whose catalog
entry takes them (GPT-5.6 on) or with the quirk: earlier models do not take them, and a model the
catalog does not know gets none (conformance cases, which run without the catalog, therefore show
none; `RequestTests` replays a cassette with them through `ModelAccess` and the catalog). The API
keeps its implicit breakpoint at the latest user message or tool output, and looks for matches at up
to 80 explicit ones (the catalog's `maxBreakpoints`; `Fit` drops the earliest beyond). Prompt render
(T126) must keep the prefix stable: on GPT-6.2 an edit before a reasoning item also drops that
reasoning.

**Strict mode** (`Strict.tryRewrite`): OpenAI's strict mode takes a JSON Schema subset in which every
object is closed and every property required. A strict tool's parameters (or a strict output
format) are rewritten into that dialect where the meaning survives: objects get
`additionalProperties: false`; optional properties become required and nullable (`anyOf` with
`{"type": "null"}`), and `default` is left out, which is safe because our codecs read `null` as
absent or as the default (`Field.optional`, `Field.withDefault`), so `ToolSpec.ofCodec` tools decode
the model's input unchanged. A schema with anything outside the subset (`minLength`, `prefixItems`,
`allOf`, `oneOf`, an unsupported `format`, an open or property-less object, a root that is not an
object) is sent as given with `strict: false`, never loosened.

### Streams

| Event | Canonical events |
|---|---|
| `response.created` | `started {id, model}` (once) |
| `response.output_item.added` `reasoning`, `function_call` | `partStarted` reasoning, toolCall `{call_id, name}` |
| `response.content_part.added` `output_text`, `refusal` (or the first delta) | `partStarted` text, refusal |
| `response.output_text.delta`, `response.refusal.delta`, `response.function_call_arguments.delta` | `delta` |
| `response.reasoning_summary_text.delta`, `response.reasoning_text.delta` | `delta` of the reasoning part; summary parts joined by a blank line |
| `*.done` of texts and arguments | the whole text if none streamed |
| `response.output_item.done` | the item's texts if none streamed; `partState`: a reasoning item with `encrypted_content` → `reasoning` (the item's JSON), an assistant message's `phase` → `phase` on its text parts; `partEnded` |
| `response.completed` | open parts ended, `usage`, `finished`: `toolUse` with a function call, `refusal` with a refusal, else `endTurn` |
| `response.incomplete` | open parts ended, `usage`, `finished`: `max_output_tokens` → `maxTokens`, `content_filter` → `contentFilter`, others → `other` |
| `response.failed` | `usage` if reported, `failed` with the response's error |
| `error` | `failed` (`Errors.ofEvent`) |
| `response.in_progress`, `queued`, summary part events, annotations, hosted tools' items and events, unknown events | nothing |

Parts are numbered in the order they start, so hosted tools' items leave no gap. An item added
twice or done twice, a delta for an item that is not open, a function call without `call_id` or
`name`, or data that is not JSON is `invalidResponse`. Usage comes with the terminal event:
`input_tokens` already counts cached tokens and cache writes, as the canonical input does
(`cacheRead` is `cached_tokens`, `cacheWrite` is `cache_write_tokens`), and `reasoning` is
`reasoning_tokens` (at most the output).

### Encrypted reasoning and model switches

A reasoning part's state (`kind: reasoning`) is the reasoning item as `response.output_item.done`
sent it, with its id, summary parts and `encrypted_content`, as JSON text; the next request sends that
item back unchanged, so the encrypted content (and the summary, with its part boundaries) replays
byte for byte, and the part's text (the summaries joined) is only for people. The conformance cases
with follow-ups set `statesVerbatim: false` (the state is unpacked into the item) and the cassettes
check the replay; `RequestTests` checks ordinal equality of 100,000-character content with control
characters. Text parts carry their message's `phase` (`commentary`, `final_answer`), which OpenAI
asks clients to send back.

`OpenAI.withReplay` sets the family's scope to `sameProvider`: the OpenAI API itself withholds
reasoning a model cannot use (another model family, a less capable model, another organization or
end user, an edited prefix), reports it in `input_transformations` and does not fail; so within one
provider the items go to every model and the API decides. Not across providers: another provider of
the family (a gateway, a compatible server, another cloud) may refuse OpenAI's items. Reasoning
whose state may not travel is dropped or kept as text by the policy; the phase is dropped with it.
A safety identifier that changes between requests also makes GPT-6.2 drop reasoning: keep the
`user_id` metadata stable within a conversation.

### Errors

The error's `code` first (OpenAI tells clients to read it: `insufficient_quota` is a 429 like a rate
limit), then its `type`, then the status:

| OpenAI | `ChatError` |
|---|---|
| `insufficient_quota`, `credit_balance_exhausted`, `organization_spend_limit_exceeded`, `project_spend_limit_exceeded`, `organization_usage_limit_exceeded`, `billing_*` codes; type `insufficient_quota` | `quotaExhausted` |
| `context_length_exceeded`; an `invalid_request_error` 400 about the context window or length; 413 | `contextOverflow` (tokens and limit when the message names them) |
| `rate_limit_exceeded`, `slow_down`; types `rate_limit_error`, `tokens`, `requests`; 429 | `rateLimited`: `retry-after-ms`, `retry-after`, else "try again in 1m6.24s" from the message (errors in a stream have no headers) |
| `server_is_overloaded`; type `service_unavailable_error`; 503 | `overloaded` |
| `invalid_prompt`, `content_policy_violation`, `bio_policy`, `cyber_policy`, `misalignment_policy_violation`, `image_content_policy_violation` | `refused` |
| `invalid_api_key`, `unsupported_country_region_territory`, `model_not_found`; types `authentication_error`, `permission_error`, `not_found_error`; 401, 403, 404 | `authFailed`, `permissionDenied`, `notFound` |
| image codes, `data_residency_mismatch`; other `invalid_request_error` (400) | `invalidRequest` |
| `server_error`; types `server_error`, `api_error`; 5xx | `serverError` (500 in a stream) |
| bodies without an error object | `ErrorMapping.ofStatus`, the body cut to 500 characters; an unknown code or type in a stream is `serverError 500` naming it |

### Cassettes and scenarios

`tests/Fx.Ext.ModelOpenAI.Tests` runs `WireConformance.tests` on 29 case files in
`cassettes/openai.responses/`: text with a cache key, a safety identifier and metadata; encrypted
reasoning with a two-part summary, a commentary and a tool call, and its follow-up; parallel calls
after reasoning without a summary, and a follow-up with an error result and text; a history with
phases and breakpoints (left out: the suite's provider has no catalog); strict tools (one from a
codec, one that cannot be strict); structured output; media; a forced call whose arguments come
only with the done events; max tokens; the content filter cutting an open part; a refusal; a failed response, a rate
limit and a quota error mid-stream; and every error response above. They are written by hand from
OpenAI's documentation (`sources/`: the request body each canonical request must become and the
streams, never generated from the adapter); `UPDATE_GOLDEN=1` rewrites the case files from them.
With `OPENAI_API_KEY` set, `LiveTests` calls the OpenAI API through `ModelAccess` and `SystemHttp`,
records the exchanges with `CassetteRecorder` (the key redacted, credential headers dropped) into
`artifacts/cassettes/openai.responses/live-*.json` and replays them through the conformance checks;
reviewed recordings can be copied into the suite. The `models-openai-*` scenarios of the
JIT-vs-native diff replay a cassette with its follow-up under cuts and check the byte-for-byte replay,
decode, map errors, and build request bodies (strict mode, breakpoints per model, efforts, the system
prompt's placement) on all eight targets.

### Left to later

| What | Task |
|---|---|
| Recorded OpenAI cassettes (the live test records them; needs a key) | T132 |
| Options the canonical model cannot express yet: `xhigh` effort, `reasoning.mode: pro`, `reasoning.context`, `configuration_update` items (changing effort without rewriting the prefix), `text.verbosity`, `prompt_cache_options` (explicit-only mode, a request-wide lifetime, prewarming), service tiers, background mode, hosted tools, compaction items, the WebSocket transport | when a strategy needs them |
| Sign in with ChatGPT on this wire, with its request constraints | T209 |
| Other OpenAI platforms (Azure, Bedrock, Google Cloud) as providers of the family | later, as provider definitions |

## Native AOT

- No reflection: codecs are hand-written; `Codec.union` handles the 15-case `ChatError`.
- Adapter decoders take tupled arguments and return records (`Decoded<'S>`), keeping curried
  function types over generic state out of ILC's generic-cycle limits.
- Scenarios: `models-codecs` (every type's canonical JSON and round trip), `models-fold` (fold,
  violations, fitting, replay), `models-sse` (parsers under cuts), `models-cassette` (the canonical
  wire on cassettes under cuts, egress gates, mismatches) and `models-live-http` (`SystemHttp`
  streaming from a loopback canonical server through `ModelAccess`, confinement, a refused
  connection), and `models-anthropic-*` (a hand-written Anthropic cassette replayed through the wire
  under cuts with its follow-up, decoding, error mapping, request bodies per thinking mode and the
  breakpoint policy), on all eight targets.

## Left to later tasks

| What | Task |
|---|---|
| `openai.chat` adapter with real-format cassettes | T207 |
| Recorded Anthropic cassettes (the live test records them; needs a key) | T132 |
| Anthropic options the canonical model cannot express yet: `xhigh` effort, `between_tools`, per-message effort and mid-conversation system messages, automatic caching, server tools, server-side fallback, `speed`; transforming schemas to the structured-output subset | when a strategy needs them |
| Other Claude platforms as families or providers (Bedrock, Vertex, Foundry) | later, as provider definitions or wires |
| Auth methods resolving `ResolvedAuth` from stored credentials | T121 |
| The built-in content exclusions on the egress hook points | T124 |
| Compaction on `contextOverflow` (the render's cache mapping: [prompt.md](prompt.md)) | T206 |
| The CodeAct loop and the action surfaces on `ILlm` | T127 |
| Building `ModelAccess` from the host's contributions (with the adapters' replay scopes, `Anthropic.withReplay`), `ILlm` in session environments | T129 |
| Journaling streams for exact replay | T131 |
| Catalog discovery, probes, roles as variant slots, fallback chains; timeouts and replay policies from variants | T208 |
| Decision and embedding wires (`DecisionWire`, `EmbedWire`) | T401 and later |
| Runtime codecs of the canonical model in `@fx/sdk` (the types are generated now), for TypeScript extensions that call models through `ILlm` or wires written in TypeScript | T109b, or the first TypeScript wire |
