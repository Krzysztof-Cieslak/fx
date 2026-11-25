# fx.model.anthropic

The bundled package for Anthropic's models (spec §10.1, §16.4): the **`anthropic.messages`** chat
wire (the Messages API, `POST /v1/messages`, streamed) and the **Claude API** as a provider
definition. A native extension built on the public SDK only (`src/Fx.Sdk`); the module spec is
[docs/modules/models.md](../../docs/modules/models.md#the-anthropic-wire-anthropicmessages).

| File | What |
|---|---|
| `Provider.fs` | The family, the API version, the quirks the wire reads, the bundled catalog and `Provider.def` (data) |
| `Request.fs` | Canonical requests as Messages API requests (pure): content blocks, thinking, cache breakpoints |
| `Decoder.fs` | The event stream as canonical events (a pure fold) |
| `Errors.fs` | Error responses and `error` events as typed `ChatError`s |
| `Anthropic.fs` | The adapter (`SseWire.create` over the pure parts), the replay scope, the manifest and the extension |

T120 (`openai.responses`) and later adapters follow the same layout.

## The provider

`Provider.def`: id `anthropic`, endpoint `https://api.anthropic.com/v1`, catalog discovery at
`/models` (T208), auth methods `api_key` and `env` (`ANTHROPIC_API_KEY`), both sending the key in
`x-api-key` (T121 resolves them; the wire never sees credentials). The catalog lists current Claude
models with their limits, capabilities and prices as Anthropic documents them (November 2026); it is
data, refreshed by hand until discovery and probes (T208) exist.

Quirks (any provider of the family may set them, e.g. a gateway or another Claude platform):

| Quirk | Default | Meaning |
|---|---|---|
| `anthropicVersion` | `2026-10-21` | the `anthropic-version` header; versions before `2026-10-21` also get `temperature`, `top_p` and `top_k` |
| `betas` | none | beta features, sent as `anthropic-beta` (comma-separated) |
| `maxCacheBreakpoints` | 4 | the most `cache_control` breakpoints a request gets |
| `defaultMaxTokens` | 16384 | `max_tokens` when neither the request nor the catalog sets an output limit |

## Testing

- `tests/Fx.Ext.ModelAnthropic.Tests` runs the wire-conformance suite on the case files in
  `cassettes/anthropic.messages/`. The hand-written ones come from `sources/` (the Messages API
  request each canonical request must become, and the documented event streams and error bodies);
  `UPDATE_GOLDEN=1` rewrites the case files from them.
- With `ANTHROPIC_API_KEY` set, the live smoke test calls the Claude API through `ModelAccess` and
  records what it sent and received (credentials redacted) into
  `artifacts/cassettes/anthropic.messages/`. Review a recorded case, then copy it into
  `cassettes/anthropic.messages/` to make it part of the suite. Without the key the test is skipped.
- The `models-anthropic-*` scenarios of the JIT-vs-native diff (`tests/aot`) replay a hand-written cassette through the wire, decode, map errors and build request bodies under Native AOT and the JIT.
