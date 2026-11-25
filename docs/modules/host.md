# Module spec: the host (`src/Fx.Host`, `fx run`, `fx serve`, `fx trust`, `fx variants`, `fx auth`)

Spec: §3 (the host, embedded mode), §5 (sessions persist; reconnect; crash recovery), §11 (identity;
service credentials, §11.3–11.5), §13 (grants), §15 (variants), §16.1 (bootstrap order), §16.4–16.5
(built-ins, trust), §17 (the client protocol), §18 (trust before execution; credentials on the host),
§19.5 (`fx run -p … --json`). Tasks: T129a (the composition, embedded mode, `fx run`, `fx auth`),
T129b (the client protocol, `fx serve`).
Code: [`src/Fx.Host`](../../src/Fx.Host) (the composition root: pure apart from the capabilities it is
handed; `Live.fs` builds the live interpreters), [`src/Fx.Cli`](../../src/Fx.Cli) (`Cli.parse` is pure,
`Program` is the entry point).
Tests: `tests/Fx.Host.Tests`, `tests/Fx.Cli.Tests`, the `host-*` scenarios of the JIT-vs-native diff and
the CI's embedded-host smoke test.

The host is the "brain" of spec §3: sessions, the agent loop, grants, service credentials, identity,
environments and model access, over one event store. `Fx.Host` is where everything built so far is put together; nothing
else references the live interpreters and the built-in extensions together.

| Piece | Where |
|---|---|
| The data directory and its files (`HostPaths`) | `HostPaths.fs` |
| The host's own slots (`HostSlots`), and every core slot (`HostSlots.core`) | `HostSlots.fs` |
| Config files and the policy file (`ConfigFile`, `PolicyFile`) | `Config.fs` |
| Project trust (`TrustStore`) | `Trust.fs` |
| The static list of built-ins and the catalog (`Builtins`) | `Builtins.fs` |
| The session index (`SessionIndex`, stream `host/sessions`) | `SessionIndex.fs` |
| The host's environment (`HostEnv`) and stores (`HostStores`) | `HostEnv.fs`, `Stores.fs` |
| What a host starts with (`HostOptions`), config, the bootstrap and resolution, service credentials (`Setup`) | `Setup.fs` |
| The start, the running host and its stop (`Host`) | `Host.fs` |
| Activity lines and the result card (`Activity`) | `Activity.fs` |
| Signing in, creating and prompting sessions (`HostSessions`) | `HostSessions.fs` |
| `fx run -p` (`Headless`) | `Headless.fs` |
| `fx trust`, `fx variants`, `fx auth` (`Commands`) | `Commands.fs` |
| The client protocol's messages (`ClientProtocol`) | `ClientProtocol.fs` |
| Its handlers and serving a connection (`ClientServer`) | `ClientServer.fs` |
| The local listener (`Listening`, `Serve`) and clients (`HostClient`) | `Clients.fs` |
| The live interpreters (`Live`, `ProcessFacts`) | `Live.fs` |

## The data directory

One per user: `FX_HOME` if set (absolute), else `~/.<product>` (Unix) or `%LOCALAPPDATA%\<product>`
(Windows); names come from `Fx.Product`.

| Path | What |
|---|---|
| `config.json` | host config: `variants`, `environments`, `extensions.disabled` |
| `policy.json` | policy: pins and forbids (`Policy.ofJson`) |
| `host.lock` | held while a host runs (`IInstallFiles.TryLock`): one host per data directory |
| `state/events.db` | the event store (SQLite): sessions, their context, grants, environments, the index |
| `state/blobs/` | the blob store: profiles, large content |
| `trust.json` | the trusted projects |
| `machine-id` | the host's stable machine id (identity.md), random, created once |
| `runtimes/` | the component installer's root (the same `runtimes` environments on this machine use) |
| `credentials/` | the file credential store's owner-only files (`LocalPrivateFiles`), when it is the store in use |
| `environments/` | `fx.env.local`'s leases and envd's state |
| `listener.token` | the local listener's token, owner-only (`TokenFile`), new each `fx serve` |
| `listener.json` | where a serving host listens: `{"kind": "unix-socket" \| "named-pipe" \| "loopback", "address"}` |
| `host.sock` | the listener's Unix socket (Linux, macOS) |

## Config, policy and trust

- **Files are JSON, read strictly**: an unknown member is an issue with its path, so typos are seen.
  Host config may have `variants`, `environments` and `extensions` (`{"disabled": [ids]}`); a project's
  `.fx/config.json` only `variants` and `environments`.
- **Layers** (variants.md): host config < trusted project config < explicit (the command line:
  `--variant slot=value`, `--model` is `model.role.main`, `--surface` is `agent.actionSurface`), under
  the policy file's pins and forbids. Any issue stops the start, every issue listed.
- **Trust before execution** (spec §18): a project's config (and so its environments' setup commands)
  is read only once the project is trusted. Trust is host-local (`trust.json`), never in the project:
  a cloned repository cannot trust itself. A trusted directory covers what is below it.
  `fx trust [<dir>]`, `fx trust --remove [<dir>]`, `fx trust --list`. (The TUI's trust prompt: T130.)
- `fx variants [list|explain|schema]`: every slot (id, default, choices, privileged, description),
  the values in effect here and where they come from, or the JSON Schema of `variants` for editors.
  Neither takes the lock nor opens a store.

## The start (`Host.start`)

In this order; any expected failure is an `Error` with every line, and what started is undone:

1. The data directory and its lock (`another host is running on …`).
2. Trust, host config, policy, and the project's config once trusted (`Setup.readConfig`).
3. **Extensions** (`Setup.bootstrap`, spec §16.1): the static list (`Builtins.candidates`: env.local,
   sandbox.bubblewrap, sandbox.seatbelt, sandbox.none (the confining drivers first: `auto` takes the
   first that confines on an environment's OS), identity.local-os-user, store.keyring, store.file,
   store.ephemeral, auth.standard, model.anthropic, model.openai, egress.exclusions,
   interpreter.bun-ts, runtime.bun) plus `HostOptions.Extensions`,
   through `ExtensionHost.bootstrap` with the host's catalog (models, identity, grants, service
   credentials) and host config's disabled list. Failed or rejected ones become warnings.
4. **The variant registry**: `HostSlots.core` (the variants module's, the render's, CodeAct's,
   identity's, service credentials', the sandbox's (`SandboxSlots`, privileged: `sandbox.driver`,
   `sandbox.writable`, `sandbox.denied`) and the host's) plus the contributed slots; then the host's resolution. Content
   exclusions read it (`ContentExclusions.ofVariants`) until extensions read their own slots (T109b).
5. **Stores**: `SqliteEventStore` and `FileBlobStore` with `host.store.durability`.
6. **Grants**: `GrantsService.start` on the host-local store, with the contributed actions, policy
   rules and gates.
7. **Components**: the lockfile planned for this machine from the contributed definitions and every
   interpreter's requirements; the installer (`ComponentInstaller.start` on `runtimes/`, journal in
   memory) with `Approvals.viaGrants` (a first install is a grant request a person answers) and a small
   probe environment (`Host.probeEnvironment`). Environments on this machine get the lockfile's
   requirements installed when they are created.
8. **Identity**: the machine id, `PeerCredentials.currentAccount`, session tokens and the
   authenticator over the contributed identity providers.
9. **Environments**: `EnvsHost` on `environments/host` with the contributed providers and sandbox
   drivers, envd embedded (`EnvdMain.embed`) or piped (the running binary).
10. **Service credentials** (`Setup.auth`, [auth.md](auth.md)): `AuthHost.start` over the contributed
    credential stores, auth methods and secret sources, with the options' keyring, private files and
    variables. A store that cannot be used as configured stops the start; `AuthService.check`'s
    problems with the providers' methods become warnings.
11. **Models**: `ModelAccess` from the contributed wires and providers, the egress hooks, the
    adapters' replay scopes (`Anthropic.withReplay`, `OpenAI.withReplay`) and
    `AuthService.modelAuth`.
12. **Sessions**: CodeAct (`CodeAct.create`: profiles loaded from the blob store, hands per turn, the
    catalog, the step journal `steps/<session>` on the same store, the blob store for cells' values)
    behind `SessionHost` (`Questions.kinds`, with crash recovery's `decision.outcomeUnknown`;
    `sessions.*` slots), profiles bound with `ProfileBinder.ofVariants` over the host's layers (with the
    extensions' versions and the lockfile's hash); then every indexed session's actor starts, so
    recovery runs (spec §5; [codeact.md](codeact.md#crash-recovery-spec-5-65-121-191)): a turn that
    stopped in the middle of a call waits for a person's decision about it, one that stopped elsewhere
    is abandoned; timers are armed again.

`Host.stop` undoes it in reverse: sessions (turns cancelled), kernels, environments (envds stop, leases
stay), the installer, grants, extensions, the stores, the lock.

`HostOptions` carries what the host reads the world through. `Live.options` builds the live ones: the
OS keyring (`LocalKeyring.create` with `LocalHostFacts.read`), private files in `credentials/`
(`LocalPrivateFiles.create … Product.Name`), `SystemVariables.variables` for credentials, and keyring
prompts only where someone can answer (`Headless.prompts`). Tests replace them with fakes.

### Sessions and their environments

- The **session index** (`host/sessions`, version 1, kept forever: `golden/index-events-v1.jsonl`)
  records each session the host creates: its workspace and environment. Clients list sessions from it
  (`session/list`); the host starts them at boot.
- A session's **hands** (`CodeActConfig.Hands`, per turn) are its environment opened through `IEnvs`
  (`EnvHands.ofHandle`, with envd's workspace snapshots for the step journal) and a `KernelPlan`: the interpreter named by `agent.interpreter` (default
  `bun-ts`) on the runtime it names, the project's trust, the host's variables scrubbed. Each turn's
  hands get the grants guard (`GrantGuard.attach` with `GrantSettings.ofVariants` of the turn's
  profile, [codeact.md](codeact.md#grants-spec-13-18-t127b)): commands, writes outside the workspace
  or to protected paths, reads outside it and network bindings ask the grants log; its slots
  (`codeact.grants.*`) are in `HostSlots.core`. Every process of a session (commands, the kernel) runs
  in its environment's sandbox ([sandbox.md](sandbox.md)): bubblewrap on Linux, Seatbelt on macOS,
  none on Windows; the agent's overrides come with agents (T213, `KernelPlan.Sandbox`).
- The workspace's **environment**: the spec named `default` in host config (or trusted project
  config), on the project's directory unless it names another; otherwise `fx.env.local` on the
  project, envd in the host's process. One per workspace, created with the first session; one that
  failed is destroyed so the next try starts afresh.

## The client protocol (spec §17)

JSON-RPC 2.0 ([rpc.md](rpc.md)) on a WebSocket at `/rpc` of the local listener, or in process in
embedded mode, with the same messages. Every method is a value with codecs in `ClientProtocol`; the
host's handlers are `ClientServer.handlers`, and clients call the same values (`Rpc.call peer
ClientProtocol.prompt …`). The TypeScript types are generated from the codecs
(`tests/Fx.Host.Tests/golden/client.d.ts`, type-checked by `bun scripts/declarations.ts`).

**`initialize`** signs the client in (identity.md: `{"identity": {"kind": "provider", "provider":
"local-os-user"}}`, or a session token) and says which request kinds it can answer: `{"answers":
["confirm", "agent.question", …]}` (spec §5: requests are offered to capable clients). The result adds
`session` (the client's session, with its token when issued) and `clientProtocol` (`"1"`).

| Method | Params → result | Least role |
|---|---|---|
| `session/create` | `{session?, title?}` → `SessionEntry` (creates the workspace's environment on first use; the same id again is harmless) | collaborator |
| `session/list` | → `SessionEntry[]` | viewer |
| `session/get` | `{session}` → `{seq, state}` (`SessionState`) | viewer |
| `session/prompt` | `{session, key, text, mode: steer \| queue}` → `{after, count}` | collaborator |
| `session/interrupt` | `{session, key}` → `{after, count}` (the control path) | collaborator |
| `session/answer` | `{session, key, request, answer}` → `{after, count}`; only a client that offered one of the request's capabilities (`cannotAnswer`) | collaborator |
| `session/fork` | `{session, key, child?, upTo?}` → `{session}` (the child, indexed with its parent's workspace) | collaborator |
| `session/overrides` | `{session, key, set: {slot: value}, unset: [slot]}` → `{after, count}` (`invalid` with the resolution's issues) | collaborator |
| `session/close`, `session/reopen` | `{session, key}` → `{after, count}` (closing ends the session's grants) | collaborator |
| `session/offers` | `{session}` → the open requests this client can answer | viewer |
| `session/subscribe` | `{session, after}` → runs until cancelled, sending `session/event` `{session, item}` (`SessionFeed`: replay after `after`, a snapshot first if far behind, then live) | viewer |
| `grants/*` | `GrantsProtocol` ([grants.md](grants.md)); answering, granting and revoking need an owner who is a person (`notAPerson` otherwise) | viewer / owner |
| `identity/whoami` | → `SessionInfo` (no token) | viewer |
| `identity/sessions` | → the live client sessions | owner |
| `identity/revoke` | `{id}` → revoked (its connections end) | owner |
| `identity/signOut` | → revokes this connection's token (and ends it) | viewer |
| `env/list`, `env/get` | → `EnvSummary` (id, name, provider, phase, transport, workspace, reason, warnings) | viewer |
| `env/destroy` | `{id}` → `EnvSummary` | owner |
| `ext/<id>/<name>` | the method's JSON params → its JSON result; `ExtCallError` (`failed` with the `MethodFailure`) | collaborator |

- **Keys** are the client's: the host keys a command `client/<client id>/<key>`, so a retry (also after
  a reconnect with the same token) gets the first outcome, and no client can collide with another or
  with the host's own keys.
- **Errors** are `ClientError` (`forbidden` with the identity failure, `unknownSession`,
  `unknownEnvironment`, `rejected` with the session's code, `cannotAnswer`, `invalid`, `unavailable`).
- **Revocation** ends every connection the token opened (`SessionTokens.Revocation`).

### The local listener and `fx serve`

`fx serve [--tcp [<port>]]` starts the host for the current directory and serves until Ctrl+C:

- on its Unix socket (`host.sock`; Linux and macOS) or a named pipe of its own (`<product>-<machine
  id>`; Windows), where the OS reports the client's account (`local-os-user`'s proof), or on loopback
  TCP (`--tcp`; port 0 picks one);
- behind the listener's token (a new one each start, in the owner-only `listener.token`), `Origin` and
  `Host` checks (spec §18); a session token the host issued is a valid bearer too, so a client resumes
  its session on reconnect without signing in;
- `listener.json` says where; `HostClient.locate` reads it and the token, `HostClient.connect` dials.

Embedded mode is the same protocol in process: `HostClient.embedded host service answers` (the
in-process transport, signed in as the OS user). The TUI (T130) uses it, or a serving host.

## Service credentials and `fx auth`

`ModelAccess.Auth` is `AuthService.modelAuth` over the service the host started (auth.md): each
provider's methods in order, the first that resolves wins. The built-in providers list a stored
`api_key` first and `env` second, so `ANTHROPIC_API_KEY` and `OPENAI_API_KEY` keep working through the
`env` method (read through `SystemVariables`) when nothing is stored. Keys never reach environments:
envd and kernels get the host's variables scrubbed (`HostEnvironment.scrub`).

```
fx auth login <provider> [--method <kind>] | logout <provider> | list | status
```

`Commands.auth` starts the service the host would (config, built-ins, resolution, `Setup.auth`) and
runs `AuthCli.run`. It takes the data directory's lock, since the credential stores have one writer
at a time: beside a running host it fails (`a host is running on …`; `auth/*` over the client protocol
is later). The entry point's `FlowRunner` runs a login's flow at the terminal: a paste prompt that
does not echo (or a line of standard input when it is redirected), a device code to enter elsewhere;
browser logins are not supported on the command line yet. Output never holds a secret.

## Embedded mode and `fx run -p`

```
fx run -p <prompt> [--json] [--model <provider/model>] [--surface cells-only|hybrid]
       [--variant <slot>=<value>]... [--session <id>] [--title <text>]
```

A host in the process, on the current directory (spec §3 embedded mode, §19.5):

1. The person signs in in process through `local-os-user` (client `cli`): their principal is on every
   input.
2. A new session (index, environment, profile) or `--session <id>` (sessions persist across runs).
   The environment's warnings are shown once (`warning: …` on standard error): an environment no
   sandbox driver confines runs unsandboxed, and says why (Windows; `sandbox.driver = none`; bubblewrap
   that cannot run here, with what to do).
3. Grant requests of the run (its session's, e.g. a command a cell runs, and those of no session, e.g.
   installing Bun the first time) are put to the person at the terminal (`GrantPrompts`): the summary,
   the action and resource, and the evidence (which turn and call asked, the command line), then `y`
   once, `s` for this session and `a` always on this host where the request offers them, anything else
   denies once. Without a terminal they are denied once, saying so. A decision about a call in doubt (a
   turn the host stopped during a call, recovered at its start) is put to the person too: the card's
   question, the call, what it printed, and its options (`completed`, `failed`, `rerun`, `inspect`); without a terminal (or with an empty
   answer) the run ends with an error saying what waits, and the decision stays open.
4. The prompt is steered in; activity lines go to standard error (`◇ cell  <intent>`, `◆ edit`, `✗`
   for failures); when the turn ends, the result card goes to standard output, or with `--json` one
   object: `{"session", "turn", "ending", "questions"}` (`Headless.outcomeCodec`; `ending` is the
   session's `TurnEnd` JSON).
5. Ctrl+C interrupts the turn on the control path; a second Ctrl+C exits (130).

Exit codes: 0 done (or waiting), 3 needs input (the questions are printed), 130 interrupted, 1 failed
or abandoned, 2 bad arguments.

`fx` without arguments prints the usage; the interactive interface is T130's.

## Native AOT

`fx` carries the whole host, Kestrel included (`Fx.Transport.Http`). Scenarios: `host-composition`
(paths, config, trust, the static list, parsing `auth`, the index's JSON, the host's slots),
`host-embedded` (on the JIT and natively: `fx auth` login, list and status on the file store, a host
started with its lock, built-ins, SQLite stores and service credentials, the stored key resolving,
`fx auth` refused beside it, sign-in, a headless run in an embedded environment, a restart that
continues the session), `host-protocol-codecs` (the protocol's JSON, least roles) and `host-protocol`
(a client in process and over loopback with the token file: sign-in, a session created, prompted and
followed, the typed errors, environments, a wrong token). The diff also runs `fx variants list` and
`fx variants schema`. CI smoke-tests the
published binary: `fx variants list` and `fx run -p` in an empty project, which stops at the first
install's approval (no terminal).

## Testing

- `UnitTests`: the data directory, config files, trust, the static list against `extensions/`, the probe environment, activity lines, the index's golden file, the outcome's JSON.
- `HostTests` (no network): start, lock and machine id; config errors; trust and project config;
  `fx variants`; `fx auth` (a pasted key stored in the file store, listed, resolved by the host's
  model access, refused beside a running host, logged out); a session in an embedded environment whose turn fails at its hands (no interpreter),
  continued after a restart.
- `ProtocolTests`: in process, create, prompt, subscribe, reconnect after a sequence number, a restart;
  least roles with a viewer's identity provider, grants answered over the protocol; over a Unix socket
  or named pipe and over loopback: the token file, a wrong token, a session token as the bearer, an
  extension's method, revocation ending connections; with the bun-ts kernel (network): a client answers
  the first install's grant request while `session/create` waits, then a turn finishes; the generated
  types; the least roles.
- `LiveTests` (the network for Bun; cached in `artifacts/host-tests/downloads`; skipped offline,
  required in CI): `fx run -p` with TypeScript cells on bun-ts, Bun installed by the host's installer
  after the person approves; a restart that continues the session without asking again; a turn the
  host stopped during a model call, abandoned at the next start; a cell the host stopped during, a
  decision at the next start that `fx run` cannot make without a terminal, then a person's `failed`
  and the turn going on; a cell's command that asks the grants log and runs once a person allows it,
  and one denied once with nobody at a terminal (the model reads the denial).
- `GrantPromptTests`: what the terminal shows of a grant request, and replies as answers.

## Left to later tasks

| What | Task |
|---|---|
| `auth/*` over the client protocol (status, list, logout; logins forwarded to a capable client); browser logins on the command line (a loopback listener) | T130, T509 |
| The TUI (`fx` without arguments), the trust prompt, showing warnings | T130 |
| Extensions from host config (process and Bun extensions, pins), installing their components in the bootstrap's components phase | T115, with host-config extensions |
| Persisting the installer's journal in the event store; the project's lockfile (`.fx/`), frozen-lockfile checks | with T211 |
| Machine retry delays and store page sizes as slots | when tuned |
| Choosing an environment per session (the picker) | T130 |
| Epoch-fenced takeover and sharing with other people | T507 |
| The HTTP/SSE offset-read mirror of the protocol (spec §17 fallbacks), binary channels for bulk data (`blobs/*`, `pty/*`) | with their method groups |
