# fx.model.openai

The bundled package for OpenAI's models (spec §10.1, §16.4): the **`openai.responses`** chat wire
(the Responses API, `POST /v1/responses`, streamed and stateless) and the **OpenAI API** as a
provider definition. A native extension built on the public SDK only (`src/Fx.Sdk`); the module
spec is [docs/modules/models.md](../../docs/modules/models.md#the-openai-wire-openairesponses).

| File | What |
|---|---|
| `Provider.fs` | The family, the quirks the wire reads, the bundled catalog and `Provider.def` (data) |
| `Strict.fs` | Strict mode's JSON Schema dialect: the rewrite of tool parameters and output formats (pure) |
| `Request.fs` | Canonical requests as Responses API requests (pure): input items, reasoning replay, phases, cache breakpoints |
| `Decoder.fs` | The event stream as canonical events (a pure fold) |
| `Errors.fs` | Error responses, `error` events and failed responses as typed `ChatError`s |
| `OpenAI.fs` | The adapter (`SseWire.create` over the pure parts), the replay scope, the manifest and the extension |

The layout is `fx.model.anthropic`'s (T119); later adapters (T207, …) follow it.

## The provider

`Provider.def`: id `openai`, endpoint `https://api.openai.com/v1`, catalog discovery at `/models`
(T208), auth methods `api_key` and `env` (`OPENAI_API_KEY`), both sending `Authorization: Bearer`
(T121 resolves them; the wire never sees credentials). The catalog lists current models (GPT-6.2
Astra, GPT-6.2 Sol, GPT-6.1 Terra, GPT-6 Luna, GPT-5.5) with their limits, capabilities and prices
as OpenAI documents them (November 2026); it is data, refreshed by hand until discovery and probes
(T208) exist.

Quirks (any provider of the family may set them, e.g. a gateway or another cloud):

| Quirk | Default | Meaning |
|---|---|---|
| `organization` | none | the `OpenAI-Organization` header |
| `project` | none | the `OpenAI-Project` header |
| `system` | `developer` | where the system prompt goes: a `developer` message first in `input` (cacheable), or `instructions` |
| `cacheBreakpoints` | as the catalog says | `true` or `false`: send `prompt_cache_breakpoint`s for every model, or for none |

Anything else a provider needs (a service tier, a field a compatible server refuses) is the
provider's `Transform`: body edits and headers, as data.

## Testing

- `tests/Fx.Ext.ModelOpenAI.Tests` runs the wire-conformance suite on the case files in
  `cassettes/openai.responses/`. The hand-written ones come from `sources/` (the Responses API
  request each canonical request must become, and the documented event streams and error bodies);
  `UPDATE_GOLDEN=1` rewrites the case files from them.
- With `OPENAI_API_KEY` set, the live smoke test calls the OpenAI API through `ModelAccess` and
  records what it sent and received (credentials redacted) into
  `artifacts/cassettes/openai.responses/`. Review a recorded case, then copy it into
  `cassettes/openai.responses/` to make it part of the suite. Without the key the test is skipped.
- The `models-openai-*` scenarios of the JIT-vs-native diff (`tests/aot`) replay a hand-written
  cassette through the wire with its follow-up, decode, map errors and build request bodies under
  Native AOT and the JIT.
