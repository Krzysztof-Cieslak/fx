# Module spec: models (`src/Fx.Models`, `src/Fx.Sdk/Models`)

Spec: §10.1–10.3 (with §4.5, §5, §6.9, §10.4, §11.3, §15, §16.2, §18). Task: T118.
Code: [`src/Fx.Models`](../../src/Fx.Models) (the canonical model, pure),
[`src/Fx.Sdk/Models`](../../src/Fx.Sdk/Models) (the wire contract, the model-egress hooks and
`ModelAccess`), the `IHttp` capability in [`src/Fx.Effects/Http.fs`](../../src/Fx.Effects/Http.fs)
with its live interpreter `SystemHttp` (`src/Fx.Effects.Live`), and the reference wire
[`extensions/Fx.Ext.ModelCanonical`](../../extensions/Fx.Ext.ModelCanonical).
Tests: `tests/Fx.Models.Tests` (with the wire-conformance suite in `Conformance/WireConformance.fs`),
the HTTP conformance suite in `tests/Fx.Effects.Tests` and `tests/Fx.Effects.Live.Tests`, and the
`models-*` scenarios of the JIT-vs-native diff.

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
| `extensions/Fx.Ext.ModelCanonical` | the `canonical` wire, written like any adapter | tests, a future fx-served model |

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
  models (T119, T120) widen their family's scope in the policy the host uses.
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

## Native AOT

- No reflection: codecs are hand-written; `Codec.union` handles the 15-case `ChatError`.
- Adapter decoders take tupled arguments and return records (`Decoded<'S>`), keeping curried
  function types over generic state out of ILC's generic-cycle limits.
- Scenarios: `models-codecs` (every type's canonical JSON and round trip), `models-fold` (fold,
  violations, fitting, replay), `models-sse` (parsers under cuts), `models-cassette` (the canonical
  wire on cassettes under cuts, egress gates, mismatches) and `models-live-http` (`SystemHttp`
  streaming from a loopback canonical server through `ModelAccess`, confinement, a refused
  connection), on all eight targets.

## Left to later tasks

| What | Task |
|---|---|
| `anthropic.messages` (thinking signatures, `cache_control` mapping), `openai.responses` (encrypted reasoning, `prompt_cache_key`), `openai.chat` adapters with real-format cassettes | T119, T120, T207 |
| Auth methods resolving `ResolvedAuth` from stored credentials | T121 |
| The built-in content exclusions on the egress hook points | T124 |
| Mapping stable receipts to cache breakpoints, compaction on `contextOverflow` | T126, T206 |
| The CodeAct loop and the action surfaces on `ILlm` | T127 |
| Building `ModelAccess` from the host's contributions, `ILlm` in session environments | T129 |
| Journaling streams for exact replay | T131 |
| Catalog discovery, probes, roles as variant slots, fallback chains; timeouts and replay policies from variants | T208 |
| Decision and embedding wires (`DecisionWire`, `EmbedWire`) | T401 and later |
| Runtime codecs of the canonical model in `@fx/sdk` (the types are generated now), for TypeScript extensions that call models through `ILlm` or wires written in TypeScript | T109b, or the first TypeScript wire |
