# Module spec: service credentials (`Fx.Sdk/Auth.fs`, `Fx.Sdk/Credentials.fs`, `Fx.Auth`, `fx.auth.standard`, `fx.store.*`)

Spec: §11.1 (service credentials: `AuthMethod`, `CredentialStore`, `SecretSource`), §11.3
(`AuthFlow`, `ResolvedAuth`, `AuthMethod`, `StoredCredential`; built-in methods; the host is the single
refresher), §11.4 (stores `keyring`, `file`, `ephemeral`, plugins; `secret:` references from store, env,
command and plugin sources), §11.5 (privileged identity and secret extensions only from host config or
policy), §10.1–10.2 (providers name their auth methods), §16.4 (`fx.auth.standard`,
`fx.store.keyring|file|ephemeral`), §18 (credentials are host-only; secrets are referenced, never
inline). Task: T121, on T007's keyrings ([docs/spikes/keychains.md](../spikes/keychains.md)).
Code: [`src/Fx.Sdk/Credentials.fs`](../../src/Fx.Sdk/Credentials.fs) and
[`src/Fx.Sdk/Auth.fs`](../../src/Fx.Sdk/Auth.fs) (the contracts, public),
[`src/Fx.Effects`](../../src/Fx.Effects) (`IKeyring`, `IPrivateFiles`, `IHostVariables`, `SecretText`),
[`src/Fx.Auth`](../../src/Fx.Auth) (the host's side: store choice, `secret:` resolver, auth service,
`auth` commands), the live interpreters `LocalKeyring`/`LocalPrivateFiles` ([`src/Fx.Keyring`](../../src/Fx.Keyring))
and `SystemVariables` ([`src/Fx.Effects.Live`](../../src/Fx.Effects.Live)), and the built-ins
[`fx.auth.standard`](../../extensions/Fx.Ext.AuthStandard),
[`fx.store.keyring`](../../extensions/Fx.Ext.StoreKeyring),
[`fx.store.file`](../../extensions/Fx.Ext.StoreFile) and
[`fx.store.ephemeral`](../../extensions/Fx.Ext.StoreEphemeral).
Tests: `tests/Fx.Auth.Tests` (the credential-store conformance suite in `Conformance/`, the stores, the
methods and sources, the service, the commands, loading the built-ins), the private-files and
host-variables conformance suites (`tests/Fx.Effects.Tests/Conformance`, run live by
`tests/Fx.Keyring.Tests` and `tests/Fx.Effects.Live.Tests`), and the `auth-*` and
`keyring-credential-store` scenarios of the JIT-vs-native diff.

Two kinds of identity (spec §11.1): **principal identity** (who connects: [identity.md](identity.md)) and
**service credentials** (how the host authenticates to model providers and other services: this module).
Credentials live on the host only. Providers name the auth methods they take; extensions contribute
the methods, the stores that keep credentials and the sources `secret:` references resolve from; the
host picks one store, resolves each request's credentials, and runs logins whose flows a client runs.

| Piece | Where |
|---|---|
| `CredentialId`, `StoredCredential`, `StoreStatus`, `StoreError`, `CredentialStore` (capability), `IStoreEnv`, `CredentialStoreDef` (contribution), `SecretSource`, `ISecretSourceEnv`, their contribution types | `Fx.Sdk/Credentials.fs` |
| `FormField`, `CredentialError`, `LoginRequest`, `AuthStart`, `FlowResult`, `LoginCompletion`, `ResolveRequest`, `Resolution`, `IAuthEnv`, `AuthMethod`, `AuthCodecs`, `AuthMethods` | `Fx.Sdk/Auth.fs` (`AuthFlow` is T122's, in `Fx.Sdk/Identity.fs`) |
| The host's capabilities for them: the OS keyring, private files, the host's variables | `Fx.Effects` (`Keyring.fs`, `PrivateFiles.fs`, `HostVariables.fs`; fakes in `Fakes/`) |
| `credentials.store`, `secrets.sources` | `Fx.Auth/AuthSlots.fs` |
| Choosing the store | `Fx.Auth/StoreChoice.fs` (pure) |
| `secret:` resolution | `Fx.Auth/SecretResolver.fs` |
| Resolving, logins, logout, list, status; the `ModelAccess` seam | `Fx.Auth/AuthService.fs` |
| Starting from the contributions | `Fx.Auth/AuthHost.fs` |
| `auth login|logout|list|status` | `Fx.Auth/AuthCommands.fs` |

## Credential stores

```fsharp
type CredentialStore =
  { Kind: string; Protection: string
    Probe : CancellationToken -> Task<StoreStatus>                       // Ready | Locked hint | Unavailable reason
    Read  : CredentialId -> CancellationToken -> Task<Result<StoredCredential option, StoreError>>
    Write : CredentialId -> StoredCredential -> CancellationToken -> Task<Result<unit, StoreError>>
    Delete: CredentialId -> CancellationToken -> Task<Result<bool, StoreError>>
    List  : CancellationToken -> Task<Result<CredentialId list, StoreError>> }

type CredentialStoreDef = { Kind: string; Description: string; Open: Eff<IStoreEnv, CredentialStore> }
type IStoreEnv = inherit IExtensionEnv; inherit IKeyring; inherit IPrivateFiles

type StoredCredential = { Method: string; Secret: SecretText; Meta: Map<string, string> }
```

- **The capability** is capability-shaped (data in and out, the cancellation token last): the host's
  auth service and the `store` secret source use it. **The contribution** (`credential-store`,
  privileged, in process) is a kind and how to open one: the host runs `Open` once at startup in the
  store's extension environment with the host's keyring and private files (`StoreEnv`), and the store
  keeps what it needs of them. Expected failures are `StoreError`s: `unavailable` (why, and what to do),
  `locked` (how to unlock), `tooLarge`, `corrupt` (what the store holds cannot be read), `failed`.
- **Ids** (`CredentialId`) are 1–128 characters of lowercase letters, digits and `. _ - /`. Lowercase
  because the Windows Credential Manager compares names without case: every store then agrees on which
  ids are the same. A provider's credential is under its id (`anthropic`) unless the method's settings
  name another (`credential`).
- **What is stored** (`StoredCredential`): the method that made it (`api_key`, …, or `secret` for a plain
  secret), the secret (an API key, or a method's token set as JSON text) and facts that are not secret
  (an account, an expiry; at most 16). Its `ToString` and its codec's preview never show the secret; its
  JSON does, so it goes only into stores, never into events, logs or traces (spec §18).
- **Conformance:** `CredentialStoreConformance.tests name subject` (`tests/Fx.Auth.Tests/Conformance`),
  with a subject that opens the store anew over the same backing and says whether it persists. It
  checks: ready, with a kind and protection; missing ids; write, read back whole (method, secret, facts),
  replace; delete; ordinal listing with every id character; Unicode, JSON, whitespace and empty secrets
  and facts; a credential stored whole or refused as `tooLarge` (never cut, the old one kept); concurrent
  writes; persistence across opens; secrets that never print. Each test uses ids of its own and deletes
  them, so it runs against a person's real keyring. It runs on `ephemeral`, `file` over in-memory and
  this machine's private files (every OS), `keyring` over the in-memory keyring (and a Windows-sized
  one), and `keyring` over the OS keyring on Windows and in CI's keyring steps (gnome-keyring on glibc and
  musl, a temporary keychain on macOS), `KEYRING_TESTS=1`.

| Store | Keeps credentials in | Protection |
|---|---|---|
| `fx.store.keyring` (`keyring`) | one OS keyring item per credential: service the product's name, account the id, value the credential's canonical JSON; a value stored by hand that is not that JSON is a plain secret | `keychain`, `credman`, `secret-service` |
| `fx.store.file` (`file`) | `credentials.json` among the host's private files, `{"credentials": {…}, "version": 1}`; whole-file rewrites, one at a time; a damaged file is never overwritten | `owner-only` (Unix `0600` in `0700`), `dpapi` (Windows: DPAPI and an owner-only ACL), `acl` (Windows without DPAPI keys) |
| `fx.store.ephemeral` (`ephemeral`) | the host's memory, forgotten when it stops | `memory` |

### Private files and the host's variables

Two host capabilities serve the built-ins (both in `Fx.Effects`, so the SDK exposes them, with fakes
and conformance suites; host-only, spec §18):

- **`IPrivateFiles`** (`Protection`, `Read`, `Write`, `Delete` of named files, 1–64 characters of
  `A-Z a-z 0-9 . _ -`): files only this OS user can read, rooted by the host in a directory of its own
  (`<data dir>/credentials`), so code holding it can touch nothing else. `LocalPrivateFiles.create root
  owner` (`Fx.Keyring`) creates the directory `0700` (tightening a wider one) and each file `0600` from
  its creation on Unix; an ACL with only the user on Windows, the content encrypted with DPAPI for the
  current user with the owner's entropy (a marker byte says which), or the ACL alone where the logon
  session has no DPAPI keys. Writes are staged next to the file and renamed over it. One host writes a
  data directory at a time (T129's lock), so the staging name is fixed.
- **`IHostVariables`** (`Read: name -> SecretText option`; unset and empty are `None`):
  `SystemVariables` (`Fx.Effects.Live`), read at each call.

## Choosing the store

`credentials.store` (privileged) is `auto` (default) or a store's kind. At startup the host opens and
probes every contributed store once (`AuthHost.probe`), then `StoreChoice.choose setting statuses
prompts` decides ([keychains.md](../spikes/keychains.md#headless-behaviour-recommendations-for-t121)):

| Setting | Probes | Store | Notice (said once: `auth status`, the first login) |
|---|---|---|---|
| `auto` | keyring ready | `keyring` | — |
| `auto` | keyring locked, prompts possible | `keyring` (its first use asks) | — |
| `auto` | keyring locked, nobody to ask (SSH, CI) | `keyring` | the keyring is locked: how to unlock, or `credentials.store = file` |
| `auto` | keyring unavailable (no session bus, no Secret Service, no default keyring, no keychain, no logon session) or not enabled | `file` | credentials are in a file only you can read, because: the reason |
| `auto` | neither usable | error | — |
| a kind | ready or locked | that store | as above when locked |
| a kind | unavailable, or no such store | **error** naming the reason and the fix | — |

- **Never a silent fallback:** a store named in config that cannot be used stops the host with the
  reason (`credentials.store is keyring, but that store cannot be used here: no Secret Service on the
  session bus: install gnome-keyring or KeePassXC, or set credentials.store to file`), and `auto`'s
  downgrade to the file is a notice.
- `auto` never picks `ephemeral` (it would lose what a login stored); CI chooses it with the `env`
  methods and sources, so nothing is written to disk.
- **Prompts** come from the host's facts (`Headless.prompts`: never in CI or over SSH, on Linux only with
  a display), passed as `AuthSettings.Prompts`, and the same value goes into the keyring's settings.

## Auth methods

```fsharp
type AuthMethod =
  { Kind: string; Label: string
    CheckSettings: Json -> Result<unit, string>
    Prompts  : FormField list                                                          // never secrets
    Authorize: (LoginRequest -> Eff<IAuthEnv, Result<AuthStart, CredentialError>>) option
    Complete : (LoginCompletion -> Eff<IAuthEnv, Result<StoredCredential, CredentialError>>) option
    Resolve  : ResolveRequest -> Eff<IAuthEnv, Result<Resolution, CredentialError>> }

type IAuthEnv = inherit IExtensionEnv; inherit ISecrets; inherit IHostVariables; inherit IHttp
type Resolution = { Auth: ResolvedAuth; Updated: StoredCredential option }
```

Spec §11.3's record with effects for tasks (spec §4.2) and expected failures as `CredentialError`
(`missing` hint, `invalid`, `expired` hint, `unavailable`), never exceptions. Every function runs in the
method's `IAuthEnv`: its extension's environment, the host's `secret:` references, variables and HTTP
(for token endpoints). Methods never get the credential store: the host reads and writes credentials for
them, so the host is the single refresher (`Resolution.Updated` is a renewed credential the host
stores). The contribution type is `auth-method` (privileged, in process), with the kind as the id.

- **A provider names its methods** in order (`ProviderDef.Auth`: kinds and settings, spec §10.2). The
  settings are the provider's, checked by the method (`CheckSettings`; `AuthService.check` reports every
  provider's problems at startup). `credential` is the one setting every storing method may have.
- **Storing methods** are those with `Complete`: the host stores what a login completes under
  `AuthMethod.credentialId` and hands it to `Resolve` (only a credential the same method made). Methods
  without `Complete` (`env`) resolve from what the host has.

### Resolving a request

`AuthService.resolve service provider` tries the provider's methods in its order: the first that
resolves wins. A method with nothing to authenticate with (`missing`: no stored credential, an unset
variable) gives way to the next; so does one that fails (a locked keyring), traced as `auth.skipped`,
since another method (the environment) may still work. If none resolves, the first failure other than
`missing` is the error, else every method's hint (`no API key for anthropic: run 'fx auth login
anthropic'; or ANTHROPIC_API_KEY is not set`). A provider without methods needs no credentials.

**The `ModelAccess` seam:** `ModelAccess.Auth` is `ProviderDef -> CancellationToken ->
Task<Result<ResolvedAuth, ChatError>>` ([models.md](models.md)); `AuthService.modelAuth service` is
exactly that, mapping a failure to `ChatError.Unavailable` with the provider and the hint. The host's
composition root (`Setup.auth` in `src/Fx.Host`) builds the service once (`AuthHost.start`) and sets
`{ access with Auth = AuthService.modelAuth service }`; the wire then gets the resolved headers through
`ModelAccess.confine` and never sees a credential. The built-in providers list `env` second, so a host
configured by variables (`ANTHROPIC_API_KEY`, read through `SystemVariables`) works with nothing
stored ([host.md](host.md#service-credentials-and-fx-auth)).

### Logins and flows

A login runs in two steps, so a client can run the flow in between (spec §11.3: interactive flows run
on a capable client and are forwarded; the client protocol's `auth/*` methods come later):

1. `AuthService.startLogin service provider method form callback` picks the method (the one named, or the
   provider's first that stores), checks the form's required fields, and asks `Authorize` for an
   `AuthStart`: the `AuthFlow` a client runs and the method's state (a PKCE verifier, a device code),
   which stays on the host (`PendingLogin`, never sent, never printed).
2. The client runs the flow and returns a `FlowResult`; `AuthService.completeLogin service pending
   result` asks `Complete` for the credential (it must carry the method's kind) and writes it to the
   store. The outcome says where it went, with the store choice's notice.

| `AuthFlow` (T122's type) | The client | `FlowResult` |
|---|---|---|
| `Paste instructions` | shows the instructions and reads what the person pastes, without echo | `pasted` (a `SecretText`) |
| `Browser(url, callback)` | listens on the loopback `callback` it offered in `LoginRequest.Callback`, opens `url`, waits for the redirect | `redirected` with the redirect's query (`code`, `state`, or `error`) |
| `DeviceCode(userCode, verifyUri, interval)` | shows the code and the URL | `device`: the method polls its provider every `interval` until the person has entered the code, within the call's scope |
| — (`Authorize = None`) | nothing | `noFlow`: the form is all |

- **Secrets are never asked in forms** (`FormField`s are for a region, an account): they are pasted, so
  they only ever travel as `SecretText`. `AuthCodecs` has the codecs of `FormField`, `AuthFlow` (a device
  code's interval in milliseconds) and `FlowResult` (a pasted secret is written, since the client sends
  it to the host; previews never show it, nor a redirect's values).
- A browser flow's callback is the client's: it picks a loopback port it can listen on and offers the
  URL before `Authorize` builds the authorization URL (the redirect URI). Clients that cannot listen send
  `None`, and methods that need a browser refuse.
- Built-in today: `api_key`'s `Paste`. OAuth device and PKCE flows (`oauth_device`, `oauth_pkce`) come
  with the providers that need them (Copilot, T509); `Refresh` is their `Resolve` returning `Updated`.

### `fx.auth.standard`

| Kind | Login | Resolves from | Settings |
|---|---|---|---|
| `api_key` | pasted, stored trimmed | the stored credential, or `key` (a `secret:` reference) | `header` (+ `scheme`) or `query`; `key`; `credential`; `instructions` |
| `env` | none | the host variable `variable`, read at each request | `header` (+ `scheme`) or `query`; `variable` |

Where the key goes is the provider's setting: `anthropic` sends `x-api-key: <key>`, `openai`
`Authorization: Bearer <key>` (the providers' defs, T119, T120). Unknown settings are errors; a `key`
that is not a `secret:` reference is refused (configuration never holds keys); keys are printable ASCII
without spaces after trimming, so a key cannot split a header. Details:
[its README](../../extensions/Fx.Ext.AuthStandard/README.md).

## `secret:` references

Configuration never holds secret values, only `secret:<name>` references (`SecretRef` in `Fx.Effects`;
names 1–128 characters of `A-Z a-z 0-9 . _ - /`, case-insensitive), which the host resolves through
`ISecrets`. Its interpreter is `SecretResolver.create` over the contributed secret sources (`secret-source`,
privileged, in process), asked in the order of `secrets.sources` (privileged; default `["store",
"env"]`):

- the first source that has the secret answers; one that cannot tell (a locked keyring) does not hide
  the later ones; if none has it, the reference is `SecretUnavailable` with the first source's reason,
  else `SecretNotFound`;
- `store` (`fx.auth.standard`): the secret of the credential `<name>` (lowercase) in the chosen store;
- `env` (`fx.auth.standard`): the variable `<PREFIX>SECRET_<NAME>` (`secret:github-token` is
  `FX_SECRET_GITHUB_TOKEN`); only variables meant for the product are reachable, so a configuration
  cannot name an arbitrary host variable;
- `command` (run a program that prints the secret) and plugins (vaults) are later sources.

Sources run in `ISecretSourceEnv`: their extension's environment, the chosen store and the host's
variables. Resolved values are never journaled or traced: `Secret` prints only its reference. An
`api_key` method whose settings name `key: "secret:work-anthropic"` resolves through these sources.

**Inline secrets** are refused where the host reads credentials: `api_key`'s `key` must be a reference.
Elsewhere in configuration, values are the reader's to check; a reader that takes a credential takes a
`SecretRef`. Reading project-local `.env` files is a grant (spec §13), not a secret source.

### Secrets for environments' commands (T138)

An environment's `variables` (and the person's `env.variables`) may name `secret:<name>`: the host resolves it through the same sources (`AuthService.Secrets`) each time envd starts, after a person's `secret.read` grant when a project asked for it, and passes the value to the environment's commands only. It is never persisted or journaled, and it is redacted from what turns read and from model requests (environments.md, "Variables").

## The host

```fsharp
let! started = AuthHost.start settings stores methods sources   // Eff<#IVariants & #IScope, Result<AuthService, string>>
let access = { access with Auth = AuthService.modelAuth service }
```

- `AuthSettings`: the keyring (`LocalKeyring.create facts.Os (LocalKeyring.settings facts)`), private
  files (`LocalPrivateFiles.create (<data dir>/credentials) Product.Name`), `SystemVariables.variables`,
  the host's HTTP, whether prompts are possible (`Headless.prompts facts`), the trace. The
  contributions come from `ExtensionHost.contributions` for `CredentialStores.contributionType`,
  `AuthMethods.contributionType` and `SecretSources.contributionType`; the catalog gets
  `AuthMethods.contributionTypes`, the registry `AuthSlots.all`.
- `AuthHost.start` opens and probes every store, chooses one, checks the methods (`AuthMethod.validate`,
  the contribution id is the kind) and the sources the slot names, and binds the sources to the chosen
  store. An error is shown at startup and stops the host: credentials are never kept where the person
  did not expect.
- **Privileged:** stores, methods and sources are privileged contribution types (spec §11.5, §16.5):
  admission refuses a project extension that contributes one (`trust.privileged`; tested in
  `LoadTests`), and both slots are privileged, so a cloned repository cannot move credentials or choose
  where secrets come from.

### `auth` on the command line

`AuthCommand.parse` (the arguments after `auth`) and `AuthCli.run service providers runFlow command`
(an exit code and lines that never contain a secret) are what the CLI's entry point calls (`fx auth`
through `Commands.auth`, under the data directory's lock: [host.md](host.md#service-credentials-and-fx-auth));
it supplies the `FlowRunner` over the terminal (a paste prompt without echo, or a line of redirected
standard input; a device code; browser logins and their loopback listener later).

| Command | Does | Prints |
|---|---|---|
| `auth login <provider> [--method <kind>]` | `startLogin`, the flow, `completeLogin` | `Stored the api_key credential for anthropic in the keyring store (secret-service) as anthropic.` and the store's notice |
| `auth logout <provider>` | deletes the credentials of the provider's storing methods | `Removed anthropic from …` or `No stored credentials for …` |
| `auth list` | the stored credentials | id, method and facts per line |
| `auth status` | the store, why, its probe, and how each provider resolves now | `Credential store: file (owner-only), auto: no D-Bus session bus …`, then `anthropic  api_key`, `openai  env`, or what is missing |

## Native AOT

No reflection; the keyrings' interop is T007's. Scenarios: `auth-store-choice` (the table),
`auth-credentials` (credentials and flows through their codecs, redaction), `auth-service` (the service
over fakes with the built-ins: resolution per provider, a login, status, list, secrets, logout, parsing),
`auth-host-variables` (`SystemVariables`), `auth-file-store` (the file store over this machine's private
files: modes, DPAPI on Windows, no staging file left) on every target, and `keyring-credential-store`
(the keyring store over the OS keyring) where `keyring-roundtrip` runs (Windows; CI's keyring steps).

## Left to later tasks

| What | Task |
|---|---|
| `auth/*` over the client protocol (login forwarded to a capable client, list, logout, status) | T130, T509 |
| `command` and `sigv4` methods, auth-flow forwarding, mTLS (`ClientCert` in `ResolvedAuth`); browser logins on the command line (a loopback listener for `AuthFlow.Browser`) | T509 |
| OAuth device and PKCE methods (`Refresh` through `Resolution.Updated`), the Copilot identity profiles | T509, `fx.model.copilot` |
| The `command` secret source; plugin sources (vaults) | with their first user |
| Values over 2560 bytes on Windows (keep them in the file store, or a key in the Credential Manager that encrypts them) | when a provider's token sets need it |
| Scoped per-session tokens for environments (spec §18: environments get scoped tokens, never credentials) | with the egress proxy |
