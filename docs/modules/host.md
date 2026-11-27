# Module spec: the host (`src/Fx.Host`, `fx run`, `fx serve`, `fx trust`, `fx variants`, `fx auth`, `fx replay`, `fx grants`)

Spec: §3 (the host, embedded mode), §5 (sessions persist; reconnect; crash recovery), §11 (identity;
service credentials, §11.3–11.5), §13 (grants), §15 (variants), §16.1 (bootstrap order), §16.4–16.5
(built-ins, trust), §17 (the client protocol), §18 (trust before execution; credentials on the host),
§19.5 (`fx run -p … --json`), §15 (recorded sessions, `fx replay`). Tasks: T129a (the composition,
embedded mode, `fx run`, `fx auth`), T129b (the client protocol, `fx serve`), T131 (recording,
`fx replay`), T132a (headless approvals, `fx grants`, running unattended).
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
| `fx grants`' commands, the command that allows a request, its lines (`GrantsCommand`) | `GrantsCommand.fs` |
| `fx trust`, `fx variants`, `fx auth`, `fx replay`, `fx grants` (`Commands`) | `Commands.fs` |
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
  `fx trust [<dir>]`, `fx trust --remove [<dir>]`, `fx trust --list`. The TUI says when a project is
  not trusted and how to trust it.
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
12. **Sessions**: CodeAct, recorded (`Recording.codeact`, [replay.md](replay.md#recording): every
    capability a turn and its hands reach is journaled in `journal/<session>` unless `session.record`
    is off; profiles loaded from the blob store, hands per turn, the catalog, the step journal
    `steps/<session>` on the same store, the blob store for cells' values) behind `SessionHost` (`Questions.kinds`, with crash recovery's `decision.outcomeUnknown`;
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
  (`codeact.grants.*`) are in `HostSlots.core`. The guard's `Sandbox` is the environment's driver when
  it confines (`EnvSandbox.Info`), `none` otherwise: auto approvals
  ([codeact.md](codeact.md#approval-modes-spec-133-t134)) hold sandbox or not, and are checked on it, so
  a grants policy rule on `none` forbids them only unsandboxed; the grants log gets a policy rule that
  denies `approvals.auto` from a policy file that forbids `codeact.grants.mode = auto` or pins the mode to another value
  (`AutoApprovals.policyRules`). A session keeps the profile it started with: a host restarted with
  another mode changes new sessions' (a person switches an existing session's with
  `session/setApprovals`). The hands are recorded (`Wraps`: their clock, files,
  envd's file operations and the sandboxed shell; terminals refused), the guard with its decisions, and
  each kernel launch with what it starts from (replay.md); `session.record` is in `HostSlots.core`
  (`RecordingSlots.all`). Every process of a session (commands, the kernel) runs
  in its environment's sandbox ([sandbox.md](sandbox.md)), which is **off by default** (T137:
  `sandbox.driver = none`; a host turns it on with `auto`, which takes bubblewrap on Linux and
  Seatbelt on macOS, or a driver's id); the agent's overrides come with agents (T213,
  `KernelPlan.Sandbox`). When a driver confines them, the prompt describes it (`Hands.Sandbox`,
  `Instructions.sandbox`).
- Hands are per turn, the **kernel is per session**: it lives in the hands' environment with the
  host's scope, across turns, until it dies, is killed or the host stops (codeact.md, T135). How each
  kernel ended is journaled (`kernelEnded`); the cell in the new kernel says why, and `fx run` shows it
  (`  ↻ new kernel  <why>; names from earlier cells are gone`) as the TUI does.
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
| `session/approvals` | `{session}` → `ApprovalsView` `{session, mode: ask \| auto \| deny \| off, configured, sandbox?, reason}`: what the grants guard does with the session's commands now (`configured` is `codeact.grants.mode` of the session's profile; `sandbox` the driver that confines its environment, absent when none does: its commands run with the user's rights; `reason` why auto approvals are on or off) (T134) | viewer |
| `session/setApprovals` | `{session, auto}` → `ApprovalsView`: a person turns auto approvals on (a session grant of `approvals.auto` by the caller, after revoking the session's deny grants) or off (revoking the session's allow grants, and a session deny grant where they would still hold); allowed sandbox or not; `invalid` with the reason where they cannot hold (mode `off`, policy) (`HostApprovals`, T134) | owner |
| `session/subscribe` | `{session, after}` → runs until cancelled, sending `session/event` `{session, item}` (`SessionFeed`: replay after `after`, a snapshot first if far behind, then live) | viewer |
| `session/context` | `{session, after}` → runs until cancelled, sending `session/contextEvent` `{session, item}` (a context event with its envelope, after `after` of the context log, then live) and `session/streaming` `{session, turn, fresh, text, tool?}` as the running turn's model writes (T130) | viewer |
| `grants/*` | `GrantsProtocol` ([grants.md](grants.md)); answering, granting and revoking need an owner who is a person (`notAPerson` otherwise) | viewer / owner |
| `identity/whoami` | → `SessionInfo` (no token) | viewer |
| `identity/sessions` | → the live client sessions | owner |
| `identity/revoke` | `{id}` → revoked (its connections end) | owner |
| `identity/signOut` | → revokes this connection's token (and ends it) | viewer |
| `env/list`, `env/get` | → `EnvSummary` (id, name, provider, phase, transport, workspace, reason, warnings, the sandbox driver once ready; `env/get` also the branch its workspace has checked out, read from git's files through the environment, `GitHead`) | viewer |
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
in-process transport, signed in as the OS user). The TUI ([tui.md](tui.md)) uses it (`fx`), or a
serving host (`fx attach`).

**Streaming** (T130): the host wraps CodeAct's runner (`Streams.runner`) so each turn's model access
taps its responses (`ChatStream.tap`): text deltas and tool calls started go to the session's
`session/context` subscribers (`Host.Streams`). Not durable: the reply is recorded in the context log
when complete.

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

## `fx replay`

```
fx replay <session> [--json] [--stall <seconds>]
```

`Commands.replay` replays a session the data directory recorded, exactly ([replay.md](replay.md)): under
the data directory's lock (as `fx auth`: beside a running host it fails), on the host's stores (only
read), with the built-ins' interpreter providers and runtimes (`Setup.interpreter`), the host's kernel
options (`Setup.kernelOptions`) and the model catalog. No model, kernel, file or clock is touched. The
report goes to standard output (`--json`: one object); exit code 0 when replay matched the recording, 1
with the first difference (or when the session was not recorded), 2 for bad arguments.

## Embedded mode and `fx run -p`

```
fx run -p <prompt> [--json] [--model <provider/model>] [--surface cells-only|hybrid]
       [--variant <slot>=<value>]... [--session <id>] [--title <text>]
```

A host in the process, on the current directory (spec §3 embedded mode, §19.5):

1. The person signs in in process through `local-os-user` (client `cli`): their principal is on every
   input.
2. A new session (index, environment, profile) or `--session <id>` (sessions persist across runs).
   Before a new session is made, its model is checked (`Headless.modelReady`): one is configured for
   the main role and its provider's credentials resolve (`AuthService.resolve`). Otherwise the run
   fails at once with what the turn would have said (`no model is configured for the main role`,
   `the model call would fail: unavailable: anthropic: no credentials: …`), before the session's
   environment installs anything (Bun is a 50 MiB download).
   The environment is shown once on standard error with its sandbox (`env: default (local) ·
   sandbox: off` by default, or the driver that confines it), then its warnings (`warning: …`): a
   sandbox the host asked for (`auto`) that cannot run here says so and why (Windows; bubblewrap that
   cannot run, with what to do). A driver the host names that cannot run fails the run. With auto
   approvals on, one line says what runs unasked and with what rights (`HostApprovals.notice`:
   `approvals: auto · unsandboxed: …` without a driver, `approvals: auto · <driver>: …` with one).
3. Grant requests of the run (its session's, e.g. a command a cell runs, and those of no session, e.g.
   installing Bun the first time) are put to the person at the terminal (`GrantPrompts`): the summary,
   the action and resource, and the evidence (which turn and call asked, the command line, an install's
   sizes), then `y` once, `s` for this session and `a` always on this host where the request offers
   them, anything else denies once. **Without a terminal nobody can answer**: the host withdraws the
   request (`GrantsService.withdraw`, reason `nobody could answer: not run from a terminal`), never
   answers it on the person's behalf, and the run says so and names the command that allows it
   beforehand when the request offers host scope (`GrantPrompts.unasked`: `! … needs a person's
   approval, and nobody can answer here (input is not a terminal): not done. Allow it beforehand with
   <command>, or run from a terminal.`, the command being e.g. `fx grants allow component.install
   bun@1.4.5`, the resource quoted for the shell and escaped as an exact pattern). The requester gets no
   answer (an install fails `not approved: withdrawn: …`; a cell's call fails `its grant request was
   not answered`), and since requests are keyed by what asks (a turn's call; an install attempt), the
   next run asks again: a headless run never leaves an answer behind for a later one. Requests
   already pending when the run starts (left by a host that stopped while it asked; a recovered
   install or turn asks again with the same key and waits for them) are put to the person first, so
   a run never waits for a prompt nobody sees. A decision about a call in doubt (a
   turn the host stopped during a call, recovered at its start) is put to the person too: the card's
   question, the call, what it printed, and its options (`completed`, `failed`, `rerun`, `inspect`); without a terminal (or with an empty
   answer) the run ends with an error saying what waits, and the decision stays open.
4. The prompt is steered in; activity lines go to standard error (`◇ cell  <intent>`, `◆ edit`, `✗`
   for failures, `◇ compacted` when the conversation's oldest part was summarized); when the turn ends, the result card goes to standard output, or with `--json` one
   object: `{"session", "turn", "ending", "questions"}` (`Headless.outcomeCodec`; `ending` is the
   session's `TurnEnd` JSON).
5. Ctrl+C interrupts the turn on the control path; a second Ctrl+C exits (130).

Exit codes: 0 done (or waiting), 3 needs input (the questions are printed), 130 interrupted, 1 failed
or abandoned, 2 bad arguments.

`fx` without arguments runs the TUI on a host in this process ([tui.md](tui.md)); without a terminal
it prints the usage.

## `fx grants`

```
fx grants list [--all]
fx grants allow <action> <pattern> [--scope host|session:<id>] [--reason <text>]
fx grants deny <action> <pattern> [--scope host|session:<id>] [--reason <text>]
fx grants revoke <id> [--reason <text>]
```

`Commands.grants` lets the person who runs it grant beforehand what a run would ask them, or forbid
it, on this data directory's grants log ([grants.md](grants.md)):

- **Who grants**: the OS user, signed in in process through `local-os-user` as embedded mode signs them
  in (client `cli`): a `user:` principal, and only the host's owner (`Role.Owner`, the least role of
  `grants/grant` and `grants/revoke`). Grants record that principal as `By` and `Direct "cli/…"` as
  their origin. No agent or extension reaches this: it is a command a person types, and the grants
  machine refuses anything but a person.
- **Under the data directory's lock**, as `fx auth`: it reads config, loads the built-ins (for the
  actions extensions contribute), opens the stores and the grants log, and stops. Beside a running
  host it fails (`a host is running on …: stop it first`); grants over the client protocol to a
  serving host are left for later.
- **Scopes**: `host` (the default) or `session:<id>` (an indexed session). Requests on this host name
  no repository yet (CodeAct's `GrantSetup.Repo` is `None`), so a repository grant would never apply;
  the command does not offer one. The action must be known and its definition must allow the scope
  (`GrantsCommand.draft`: `component.install` takes host, not session).
- **Patterns** are the grants log's globs over canonical resources: `bun@1.4.5` exactly, `'bun@*'`
  every version, `'git **'` every `git` command; quote them for the shell.
- `list` prints the grants in force, one a line (`<id>  allow component.install bun@*  (host) by
  user:… — reason`); `--all` adds spent, revoked and expired ones with their status. `allow`/`deny`
  print the grant made; `revoke` prints `revoked <id>`. Errors (an unknown action, a pattern of the
  wrong kind, an unknown grant or session) exit 1 with the grants log's message; bad arguments exit 2.

## Running unattended

A person who wants `fx run -p` to run without a terminal (CI, a script) decides beforehand what it may
do; nothing is ever approved for them:

1. **Credentials**: `fx auth login <provider>` once, or the provider's variable (`ANTHROPIC_API_KEY`,
   `OPENAI_API_KEY`). A run without them fails before anything is installed.
2. **Installs**: the first install of a component is always a person's grant; no variant skips it.
   Allow it once per data directory: `fx grants allow component.install bun@1.4.5` (the command a
   headless run prints), or `fx grants allow component.install 'bun@*'` for every version.
3. **What turns do**: the CodeAct guard asks for commands outside `codeact.grants.allowCommands`,
   writes outside the workspace or to protected paths, reads outside it and network bindings
   ([codeact.md](codeact.md#grants-spec-13-18-t127b)). Without a terminal those requests are withdrawn
   and the call fails "not answered" (the model reads it). Allow what the run needs beforehand
   (`fx grants allow shell.exec 'git **'`, `fx grants allow fs.write '/tmp/out/**'`), or choose the
   guard's mode: `codeact.grants.mode` is **privileged**, so it is set on the command line
   (`fx run --variant codeact.grants.mode=deny …`) or in host config (`<data directory>/config.json`:
   `{"variants": {"codeact.grants.mode": "deny"}}`), never by a project's config or a session
   override, and policy may pin or forbid it. `deny` refuses what no grant allows without asking
   anyone (the unattended choice); `auto` runs unasked commands written plainly in the
   workspace, sandbox or not (downloads, installs, writes outside the workspace still ask, so without a
   terminal they fail "not answered"; unsandboxed, the commands run with the user's rights, and the run
   says so once: `approvals: auto · unsandboxed: …`); `off` checks nothing and is only for
   sandboxed or throwaway environments. The sandbox applies either way. Auto approvals are also a
   grant: `fx grants allow approvals.auto '*'` turns them on for every session of this host (with
   `deny`, what they cover runs and the rest is refused).
4. **The sandbox** is off by default ([sandbox.md](sandbox.md) "Opt-in"): the run's commands and kernel
   have the user's rights (they can read the user's credentials, the product's own credentials file
   included, write anywhere the user can and use the network), and the grants above are the guard.
   To confine them, turn it on in host config (`{"variants": {"sandbox.driver": "auto"}}`) or on the
   command line (`--variant sandbox.driver=auto`, or `bubblewrap`/`seatbelt` to fail rather than run
   unsandboxed where it cannot run); a project's config cannot. With it on, tools that keep state
   under `~` (NuGet's packages, Cargo's registry, the MSBuild server) or read a config on the
   credential list (`~/.nuget/NuGet/NuGet.Config`, `~/.yarnrc.yml`) fail, as do changes to
   `.git/config` and hooks (`git config`, `git push -u`) and commits in a linked worktree: list a
   tool's directory in the privileged `sandbox.writable` (a credential location stays unreadable), or
   run such steps without the sandbox.
5. **Credentials for commands** (T138): choose them in host config (`env.variables`), which needs no
   consent; a project's environment spec that asks for one is withdrawn without a terminal, so grant it
   beforehand in the project's directory: `fx grants allow env.credential GH_TOKEN --scope repo` (or
   `secret.read <name>` for a `secret:` reference).


## Chosen variables (T138)

The host resolves the variables environments' commands get (environments.md, "Variables"): `EnvVariableConsent.resolver`, built in `Host.start` with the person's `env.variables` and `env.deny`, the host's variables, its secret sources (service credentials start before environments for this) and the grants log. A project's credential asks the person: the TUI shows a card, `fx run` prompts at a terminal (`[r] always for this project`), and without a terminal the request is withdrawn and the run names `fx grants allow env.credential <NAME> --scope repo`, to run in the project's directory. A variable left out is a warning on `fx run`'s env line, which names the variables passed (`vars: GH_TOKEN`). Model requests are cleaned of every live credential value (`Host.redactingLlm`) before the egress hooks.

To give `gh` and `git push` a token: `{"variants": {"env.variables": {"GH_TOKEN": "host:GH_TOKEN"}}}` in `~/.fx/config.json`. Approvals are unchanged: `gh **`, `git push **` and `git fetch **` still ask under `auto`.

## Native AOT

`fx` carries the whole host, Kestrel included (`Fx.Transport.Http`). Scenarios: `host-composition`
(paths, config, trust, the static list, parsing `auth`, the index's JSON, the host's slots),
`host-embedded` (on the JIT and natively: `fx auth` login, list and status on the file store, a host
started with its lock, built-ins, SQLite stores and service credentials, the stored key resolving,
`fx auth` refused beside it, `fx grants` allow, list and revoke and refused beside a running host,
sign-in, a headless run in an embedded environment, a restart that continues the session, `fx replay`
of it), `host-protocol-codecs` (the protocol's JSON, least roles) and `host-protocol`
(a client in process and over loopback with the token file: sign-in, a session created, prompted and
followed, the typed errors, environments, a wrong token). The diff also runs `fx variants list`,
`fx variants schema`, `fx replay`'s usage error, `fx grants --help` and a scope it does not offer. CI
smoke-tests the published binary (`.github/scripts/aot-smoke.sh host`): `fx variants list`; `fx run -p`
in an empty project without credentials, which stops before installing anything; with a key that is
never sent, which stops at the first install's approval (no terminal), withdraws it and names
`fx grants allow component.install bun@…`; and `fx grants` list, allow and revoke.

## Testing

- `UnitTests`: the data directory, config files, trust, the static list against `extensions/`, the probe environment, activity lines, the index's golden file, the outcome's JSON.
- `HostTests` (no network): start, lock and machine id; config errors; trust and project config
  (a trusted project cannot select `codeact.grants.mode = auto`; host config can; a policy file that
  forbids it stops the start and denies `approvals.auto`);
  `fx variants`; `fx auth` (a pasted key stored in the file store, listed, resolved by the host's
  model access, refused beside a running host, logged out); a session in an embedded environment whose turn fails at its hands (no interpreter),
  continued after a restart; installs with downloads that fail (no network): headless first (the
  request withdrawn, no person's denial, the `fx grants allow` command shown), then a terminal's deny
  once and allow once, each asked again, then headless again; a grant made with `fx grants` that a
  headless run installs by without asking, listed and revoked; `fx grants`' refusals (a bad or unknown
  action, an unknown session or grant, a running host); `fx run` failing on a missing model or missing
  credentials before anything is asked or installed; a request left pending by a host that stopped
  while it asked, put to the next run's person; the sandbox (T137): off by default (`env: … · sandbox:
  off`, no warning), `auto` from host config confining or warning, a driver the command line names that
  cannot run failing the run, a trusted project's config refused for selecting `sandbox.driver`.
- `ProtocolTests`: in process, create, prompt, subscribe, reconnect after a sequence number, a restart;
  least roles with a viewer's identity provider, grants answered over the protocol; over a Unix socket
  or named pipe and over loopback: the token file, a wrong token, a session token as the bearer, an
  extension's method, revocation ending connections; with the bun-ts kernel (network): a client answers
  the first install's grant request while `session/create` waits, then a turn finishes; the generated
  types; the least roles; `session/approvals` and `session/setApprovals` (a person's switch is a session
  grant and its revocation, without a sandbox too; with the host's mode `auto`, off is a session deny
  grant; with `sandbox.driver = auto`, the confining driver and why; a policy that forbids it; viewers
  read, cannot switch).
- `LiveTests` (the network for Bun; cached in `artifacts/host-tests/downloads`; skipped offline,
  required in CI): `fx run -p` with TypeScript cells on bun-ts, Bun installed by the host's installer
  after the person approves; a restart that continues the session without asking again; a turn the
  host stopped during a model call, abandoned at the next start; a cell the host stopped during, a
  decision at the next start that `fx run` cannot make without a terminal, then a person's `failed`
  and the turn going on; a cell's command that asks the grants log and runs once a person allows it;
  then another command with nobody at a terminal (withdrawn, the model reads that it was not answered,
  the run names `fx grants allow shell.exec …`), the same command in a later run from a terminal
  (asked again, allowed once, run) and in a run after that without one (asked again); `fx run -p` in two host
  processes recorded and `fx replay` matching it, and nothing journaled with `session.record` off; a
  long turn compacting (spec §5) beside a cell when the provider's count reached the threshold of the
  catalog's window and when a cell asks (`fx.history.compact`): two `◇ compacted` lines, each summary
  request the call before it on the Messages API's wire plus the instruction, and `fx replay` matching;
  one kernel for a session's turns (with `sandbox.driver = auto`, sandboxed where a driver confines): a name declared in turn 1 used
  in turn 2 after the host idled longer than its thread pool keeps idle threads (`ThreadTimeoutMs` is 1 s
  in this project, so a kernel that died with the thread that started it would die here, T135), then a
  kernel that kills itself: its cell's receipt and the journal say how (SIGKILL, exit code 137), the next
  cell's receipt and `fx run`'s activity say why a new kernel started, and `fx replay` matches; auto
  approvals (T134) without a sandbox and with `sandbox.driver = auto`: a build's commands and a script in
  the workspace unasked (journaled as auto approvals, `fx run`'s `✓ auto-approved` line and its one
  `approvals: auto` line, `· unsandboxed` without a driver), a download, a write outside the workspace and
  the first install asked; unsandboxed the script writes outside the workspace with the user's rights,
  sandboxed it cannot.
- `GrantPromptTests`: what the terminal shows of a grant request, replies as answers, what a run shows
  when nobody can answer (the `fx grants allow` command for requests that offer host scope, quoted for
  the shell, the resource escaped as an exact pattern).

## Left to later tasks

| What | Task |
|---|---|
| `auth/*` over the client protocol (status, list, logout; logins forwarded to a capable client); browser logins on the command line (a loopback listener) | T509 |
| Extensions from host config (process and Bun extensions, pins), installing their components in the bootstrap's components phase | T115, with host-config extensions |
| Persisting the installer's journal in the event store; the project's lockfile (`.fx/`), frozen-lockfile checks | with T211 |
| Machine retry delays and store page sizes as slots | when tuned |
| Choosing an environment per session (the picker) | later (after T130) |
| An interactive trust prompt (trusting from the TUI, then reading the project's config without a restart); the TUI only says `fx trust` | later |
| Epoch-fenced takeover and sharing with other people | T507 |
| The HTTP/SSE offset-read mirror of the protocol (spec §17 fallbacks), binary channels for bulk data (`blobs/*`, `pty/*`) | with their method groups |
| `fx grants` over the client protocol to a host `fx serve` runs (today it needs the data directory's lock); repository-scoped grants once requests name the workspace's repository | later |
