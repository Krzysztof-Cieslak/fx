# fx.egress.exclusions

The bundled package for **content exclusions** (spec §16.4, §18): the content of excluded files never
leaves for a model. A native extension built on the public SDK only (`src/Fx.Sdk`); the module spec
is [docs/modules/egress.md](../../docs/modules/egress.md).

| Contribution | Type | What |
|---|---|---|
| `egress.exclusions.policy`, `.host`, `.project` | `variant-slot` | gitignore-style patterns, one list per origin (policy and host privileged) |
| `egress.exclusions.placeholder` | `variant-slot` | `plain` (default) or `path`: what replaces excluded content |
| `egress.exclusions.action` | `variant-slot` | `redact` (default) or `deny` |
| `exclusions` | `transform:model-egress` (privileged) | replaces every part whose sources are excluded with the placeholder; priority -1000, before every other egress transform |
| `exclusions` | `gate:model-egress` (privileged) | denies a request that still holds excluded content (or whose settings do not compile) |

Everything is built from the SDK's `ContentExclusions` (`src/Fx.Sdk/Models/Egress.fs`) and the matcher
`Fx.Models.Exclusions`; the package only puts them together. Parts are matched by their provenance
(`Sources`): the code that puts a file's content into a request says which file it came from.

```json
{ "variants": { "egress.exclusions.host": ["*.env", "**/*.{pem,key}", "secrets/"] } }
```

`EgressExclusions.create settings` builds the extension with how to read its settings for each request:
the host passes `ContentExclusions.ofVariants` over the resolution in effect (T129) until extensions
read their own slots through their environment (T109b).

## Testing

- `tests/Fx.Ext.EgressExclusions.Tests`: settings from variant layers under policy, the hooks with other
  transforms by priority, the package bootstrapped through the extension host with the Anthropic,
  OpenAI and canonical wires (what leaves for each provider), and the hooks over the extension protocol.
- `tests/Fx.Models.Tests/ExclusionTests.fs`: the matcher (gitignore cases, a regular-expression model,
  normalization, case, union of lists) and redaction.
- The `egress-*` scenarios of the JIT-vs-native diff.
