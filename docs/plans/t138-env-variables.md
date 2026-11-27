# T138 Environments: chosen variables

A plan decided with the person (the project owner) on 2026-11-27, during dogfooding (T132). Implement
it as task T138 in lane `t138-env-variables`, from `main`. Once it lands, its rules belong in
`spec.md`, the module docs and `AGENTS.md`. Keep this file as the record of why.

## Why

fx's host strips variables that look like credentials (`*_TOKEN`, `*SECRET*`, …) before it starts an
environment's processes (`HostEnvironment.scrub`, spec §18). That is the right default, but it means
an agent working in fx cannot use `gh` or push with git: on this machine both authenticate through
`GH_TOKEN`, exported in the person's shell profile. The first dogfooding session asked the person for
GitHub access and could not open its own PR.

With the sandbox off (the default since T137), the scrub is a guardrail, not a boundary: the agent's
commands can read `~/.zshrc` (asking first, as a read outside the workspace). Making a credential
reachable on purpose, chosen by a person, visible and kept out of the model's view, is better than
an agent hunting for it.

## Decisions (fixed)

1. **A person chooses which variables an environment's commands get**, including credential-like
   ones. Two sources of a value: the host's own variable (`host:<NAME>`), or an fx secret
   (`secret:<name>`, resolved by the host's secret sources, `SecretResolver` in `src/Fx.Auth`). Plain
   literal values are allowed only for names that do not look like credentials. A credential is
   never written in config, as AGENTS.md already requires: "a reader that takes a credential takes a
   `SecretRef` and refuses a literal".
2. **Two places to configure:**
   - **The person's settings:** the privileged variant slot `env.variables` (host config or the
     command line, never a project). Applies to every environment of the host, without asking.
   - **The project's requests:** a new member `variables` of an environment spec
     (`environments.<name>.variables` in host or trusted project config). From host config it needs
     no consent. From a project, plain variables apply, while credential-like names and `secret:`
     references need the person's consent (decision 3).
   - **Syntax:** both use one map from variable name to value:
     ```json
     { "variants": { "env.variables": { "GH_TOKEN": "host:GH_TOKEN", "NPM_TOKEN": "secret:npm" },
                     "env.deny": ["AWS_*"] },
       "environments": { "default": { "provider": "local",
         "variables": { "GH_TOKEN": "host:GH_TOKEN", "RUST_LOG": "debug" } } } }
     ```
     `host:` and `secret:` are references (prefixes compared without case); any other string is a
     literal.
   - **Precedence:** for the same name, the person's slot wins over the spec. `env.deny` (privileged:
     glob patterns, without case) removes a name from both. Runtime injection vectors
     (`InjectionVectors`: `BUN_OPTIONS`, `NODE_OPTIONS`, `DOTNET_STARTUP_HOOKS`, …) are refused
     everywhere, with an issue that names the file.
   - **Merge:** variant layers and `EnvSpecs.merge` both replace rather than combine. The host
     combines the slot (the person's) with the winning environment spec's `variables`, and it
     remembers which file that spec came from. That file decides whether consent is needed.
3. **The project asks, a person consents.** A project's credential-like name or `secret:`
   reference takes effect only with a grant:
   - **Actions:** for a host variable, a new action `env.credential` (resource kind `Name`: the
     variable's name). For an fx secret, the existing action `secret.read` (resource: the secret's
     name).
   - **What the request names:** the repository, meaning the workspace's project. This makes the
     `Repo` scope work for these actions; T132a noted that no request named a repository yet, so a
     repo grant never applied.
   - **What is offered:** `once` (this environment's start), `repo` (always for this project) and
     `deny`. Not `host`, so that a yes for one project never hands a credential to another. A person
     who wants that can still grant it beforehand:
     `fx grants allow env.credential 'GH_TOKEN' --scope host`, or the like.
   - **Key:** derived from what asks: the environment's id, its attempt and the variable's name, as
     installs are keyed (T132a, `Approvals.key`), so an answer never stands for a later attempt.
   - **Asking:** the TUI shows a card; `fx run` prompts at a terminal. Headless, the request is
     withdrawn and the run names the exact `fx grants allow …` command. A denied or withdrawn
     variable is left out, with a note for the person and the model.
4. **What gets the variables:** the environment's commands (`shell.run`, the `bash` tool, terminals,
   setup commands). Not the kernel process, since cells reach credentials only through commands,
   and not envd itself, beyond what it needs to pass them on. On the host's machine they extend the
   scrubbed base (`EnvdOptions.Environment`, `BaseEnvironment` in `src/Fx.Envd`). Keep the way they
   reach envd provider-neutral: the host resolves values and hands them over at connection, never
   persisted, so a remote provider (T501) can carry them over the environment protocol later.
5. **Redaction, exact match.** The host replaces every injected credential value with
   `[redacted NAME]` in everything that flows from the environment to the host's turn:
   - process output (shell, terminals, the kernel's stdout and stderr);
   - file contents read through bindings (`fs.read`);
   - host-call results.

   That covers what the model, the context log, the replay journal, the TUI and `fx run` see. Do it
   in one place: wrappers of the hands' capability records, below T131's recording taps
   (`Recording.codeact` `Wraps` in `Host.start`), so the journal only ever holds redacted output and
   replay stays exact.
   - **Split values:** a value can arrive split across two reads. Hold back the last
     `max length − 1` bytes until more data or the end.
   - **Short values:** credential values shorter than 8 characters are not injected, since redacting
     them would mangle output. The note says why.
   - **Defence in depth:** also give the values to the model-egress redaction
     (`Fx.Ext.EgressExclusions` or a transform beside it), so a request containing one is cleaned
     before any wire sees it.
   - **Limit:** encoded copies (base64, URL-encoded, JSON-escaped) are not caught. Document this as a
     limit and point to T601 (egress redaction).
   - **Scope:** only credential-like and `secret:` values are redacted; plain variables are not
     secrets.
6. **Visible:**
   - The environment line in the TUI's info line and in `fx run` lists the names passed in, never
     the values, for example `vars: GH_TOKEN`. `EnvSummary` from `env/get`/`env/list` carries the
     names.
   - The prompt gets a section, built like `Instructions.sandbox` in `src/Fx.CodeAct/Instructions.fs`,
     naming what is available ("`GH_TOKEN` is set for commands: `gh` is authenticated") and what was
     asked for but not granted.
   - The environment's notes say which names came from where.
7. **Recorded:** which names an environment got, from which source (person or project) and with
   which consent: an event of the environment's lifecycle, or the readied event's record. Add a case
   or an optional field; never change old JSON, because environment events are kept forever. The
   replay journal keeps variables by name only (`Values.procInput` already does) and secret lookups
   by reference and outcome. `SecretTests` (tests/Fx.Replay.Tests) must show that no injected value
   reaches the journal or the blob store, not even base64-encoded.
8. **Approvals unchanged:** `gh **`, `git push **` and `git fetch **` stay in
   `codeact.grants.askCommands`, so in `auto` mode every network action still asks.

## Code map (starting points)

- **Scrub and credential names:** `src/Fx.Environments/HostEnvironment.fs` (`credentialPatterns`,
  `isCredential`, `scrub`). Reuse `isCredential` for "credential-like".
- **Environment specs:** `src/Fx.Sdk/Environment.fs` (`EnvSpec`, `SetupSpec` with its unimplemented
  `Secrets`) and `src/Fx.Environments/EnvSpecs.fs` (`EnvSpec.codec`: strict; `merge`; `check`, which
  refuses `setup.secrets` today).
- **Environments host:** `src/Fx.Environments/Envs.fs`, the lifecycle (`Lifecycle.fs`, golden
  `tests/Fx.Environments.Tests/golden/events-v1.jsonl`, never regenerated), and the local provider
  `extensions/Fx.Ext.EnvLocal` (piped and embedded envd).
- **envd:** `src/Fx.Envd/BaseEnvironment.fs`, `EnvdOptions.Environment`.
- **Slots:** register the new privileged slots (`env.variables`, `env.deny`) like `SandboxSlots` in
  `src/Fx.Environments/Sandbox.fs`, through `HostSlots.core` in `src/Fx.Host/HostSlots.fs`. Adding a
  core slot changes golden files; see AGENTS.md.
- **Secrets:** `src/Fx.Auth/SecretResolver.fs` and `AuthService`; the host resolves, nothing else
  does.
- **Grants:**
  - `src/Fx.Grants/Actions.fs`: add `env.credential`; `secret.read` exists.
  - The request's repository field; `GrantPrompts` (`src/Fx.Host`).
  - The TUI's permission card, which offers exactly the request's `Offer`.
  - `fx grants`, from T132a.
- **Hands and recording:**
  - `src/Fx.CodeAct/Environment.fs` (`EnvHands.ofHandle`).
  - `src/Fx.Host/Host.fs` (`hands`, the `Wraps` of `Recording.codeact`).
  - `src/Fx.Replay` (`Values.procInput`).
- **Prompt:** `src/Fx.CodeAct/Instructions.fs`.
- **Clients:** `src/Fx.Host/ClientProtocol.fs` (`EnvSummary`; regenerate `golden/client.d.ts` with
  `UPDATE_GOLDEN=1`), `src/Fx.Tui` (the info line, the card), `src/Fx.Host/Headless.fs` (`fx run`).

## Tests

- **Pure:**
  - parsing `host:`, `secret:` and literals;
  - literal credential values refused;
  - injection vectors refused;
  - `env.deny`;
  - precedence of the person's slot over the spec;
  - provenance of a host spec versus a project spec;
  - a project cannot set the privileged slots.
- **Redaction** (a pure function over chunks, plus the capability wrappers):
  - a value split across reads;
  - several values, including one that contains another;
  - a value inside kernel protocol JSON keeps the JSON valid;
  - `fs.read`;
  - short values not injected.
- **Grants:**
  - a project's credential asks once per attempt;
  - `repo` answers apply to that project only;
  - no `host` offer;
  - headless withdrawn, naming the command;
  - deny leaves the variable out, with notes.

  Golden renders of the TUI card at the usual widths.
- **Host:** with the scripted model and live envd, a run where a command sees `GH_TOKEN`
  (`test -n "$GH_TOKEN"`), `echo $GH_TOKEN` shows `[redacted GH_TOKEN]` to the model, and the
  model's request never contains the value. Also a project request with consent, and one without.
- **Replay:** a recorded session with injected variables replays exactly, and `SecretTests` covers it.
- **Native AOT:** a JIT-vs-native scenario for the redaction function and the variable merge. Both
  are pure.

## Docs and spec

- **Spec:**
  - §9.4: the environment spec's `variables`.
  - §11: secrets used by environments.
  - §13.1: the `env.credential` action and repository-scoped requests.
  - §18: "person-chosen credentials": host config or consent, redacted, recorded by name only.
- **Module docs:**
  - `environments.md`: variables, scrub exemptions, notes.
  - `host.md`: config and "Running unattended" (grant `env.credential` beforehand).
  - `grants.md`.
  - `auth.md`.
  - `codeact.md`: the prompt section.
  - `tui.md`.
  - `replay.md`.
- **AGENTS.md:** the Environments and Security bullets and the Commands table's "Run a prompt
  headless" row.

## Out of scope (later)

- Limiting a variable to some commands (only `gh`, `git push`): a command line holds several
  commands, so this needs the guard's split first.
- `setup.secrets` (still refused by `EnvSpecs.check`). The same injection and redaction could make it
  work; it is not part of T138.
- Remote environments (T501): the values travel over the environment protocol.
- Encoded copies of values: T601 (egress redaction).
- Releasing a credential only to the command a person approved (the credential stays in fx's store
  and is handed to that one process). This is a stronger model than an environment variable;
  consider it as its own task once T138 is in use.

## Done when

- `~/.fx/config.json` with `"env.variables": {"GH_TOKEN": "host:GH_TOKEN"}` lets fx run
  `gh pr list` and `git push` (each still asking under `auto`), and the model never sees the token.
- A project that asks for `GH_TOKEN` in its environment spec gets it only after the person says yes
  for that project.
- CI is green: fast tier, plus `ci:full` if platform paths change.

## Notes for the implementer (fx working on fx)

- Until T138 lands and the person rebuilds `fx`, the agent cannot push or open the PR from inside
  fx. Finish the lane (`lane check`, commit) and ask the person to push and open the PR.
- `fs.write` cannot pass more than 16 KiB (`values.inlineMaxBytes`) from a cell. Edit large files
  with the `edit` tool or in smaller pieces; fixing that limit is a separate task.
