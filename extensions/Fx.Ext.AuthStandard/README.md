# fx.auth.standard

The bundled auth methods every API-key provider needs, and the built-in secret sources (spec
§11.3–11.4, §16.4). A native extension built on the public SDK only (`src/Fx.Sdk`); the module spec is
[docs/modules/auth.md](../../docs/modules/auth.md).

| File | What |
|---|---|
| `Settings.fs` | The methods' settings (`AuthMethodRef.Settings`): where the key goes, where it comes from, checked; the key's own check |
| `AuthStandard.fs` | The `api_key` and `env` methods, the `store` and `env` secret sources, the manifest and the extension |

## Methods

| Kind | Login | Resolves from | Settings |
|---|---|---|---|
| `api_key` | `Paste`: the person pastes the key; stored trimmed (method `api_key`) | the provider's stored credential, or `key` | `header` (+ `scheme`) or `query`; `key` (a `secret:` reference); `credential` (the store id, default the provider's id); `instructions` (shown with the paste prompt) |
| `env` | none (nothing is stored) | the host's variable `variable`, read at each request | `header` (+ `scheme`) or `query`; `variable` |

- **Where the key goes** is the provider's setting, never the method's guess: Anthropic sends
  `x-api-key: <key>` (`{"header": "x-api-key"}`), OpenAI `Authorization: Bearer <key>`
  (`{"header": "authorization", "scheme": "Bearer"}`), a provider that takes the key in the URL
  `?key=<key>` (`{"query": "key"}`).
- **Unknown settings are errors** (a misspelt `header` would otherwise send no key); so are a `key`
  that is not a `secret:` reference (configuration never holds keys) and settings of the other method.
- **Keys** are printable ASCII without spaces once surrounding whitespace (a pasted line's end) is
  dropped: anything else could split a header or a query, and is `invalid`.
- **Nothing to authenticate with** is `missing`, with a hint (`run 'fx auth login anthropic'`,
  `ANTHROPIC_API_KEY is not set`): the host then tries the provider's next method, and shows every
  hint if none resolves.

## Secret sources

| Id | `secret:<name>` is |
|---|---|
| `store` | the secret of the credential `<name>` (lowercase) in the credential store the host chose |
| `env` | the variable `<PREFIX>SECRET_<NAME>`: the product's prefix (`Product.envVar`), the name in upper case with `.`, `-` and `/` as `_` (`secret:github-token` is `FX_SECRET_GITHUB_TOKEN`) |

The `env` source reaches only variables meant for the product: a configuration that names
`secret:aws_secret_access_key` reads `FX_SECRET_AWS_SECRET_ACCESS_KEY`, never the AWS variable. The
host asks the sources in the order of `secrets.sources` (default `store`, then `env`).
