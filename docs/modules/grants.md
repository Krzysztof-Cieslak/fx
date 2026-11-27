# Module spec: the grants log (`Fx.Grants`, `Fx.Grants.Host`, `Fx.Sdk/Grants.fs`)

Spec: §13 (with §1 principle 6, §4.2 `IGrants`, §4.4, §5 durable pending requests, §6.1, §8.2, §10.4,
§10.6, §11, §16.2–16.5, §17, §18, §19.4). Tasks: T123, T132a (requests nobody can answer, `fx grants`),
T134 (approval modes: `approvals.auto`), T134b (yolo approvals: `approvals.yolo`).
Code: [`src/Fx.Grants`](../../src/Fx.Grants) (the pure core, shared with clients),
[`src/Fx.Sdk/Grants.fs`](../../src/Fx.Sdk/Grants.fs) (the `IGrants` capability and the contribution
types, public), [`src/Fx.Grants.Host`](../../src/Fx.Grants.Host) (the actor, the host-local store, the
service and the client protocol), [`src/Fx.Components/Approvals.fs`](../../src/Fx.Components/Approvals.fs)
(first installs).
Tests: `tests/Fx.Grants.Tests` (patterns, rules and machine, with property tests; codecs and golden
files; the Fable architecture test), `tests/Fx.Grants.Host.Tests` (the store conformance suite in memory
and on SQLite, the service, the protocol over JSON-RPC, replay), `tests/Fx.Components.Tests/ApprovalsTests.fs`,
and the `grants-*` scenarios of the JIT-vs-native diff.

The grants log records **permissions**: tool and command approvals, first installs, budget raises, model
enablement, paid fallbacks, environment cost, secrets and `.env` access, network opening, safe-output
and workflow approvals (spec §13.1). Choices among alternatives go to the decision ledger instead
(§1 principle 6, §12). Agents only **request**; people grant, answer and revoke; policy only tightens.

| Piece | Where |
|---|---|
| Resources and patterns | `Pattern.fs` (`ResourceKind`, `Pattern`, `Resource`, `Specificity`) |
| Grants, requests, answers, events, state, commands, errors | `Model.fs` |
| The vocabulary | `Actions.fs` (`ActionDef`, `Actions.builtIn`) |
| The effective rules | `Rules.fs` (`PolicyRule`, `RuleDecision`, `Rules.evaluate`) |
| The machine's functions | `GrantLog.fs` (`initial`, `evolve`, `decide`, `react`, `recover`, `request`, `checkAnswer`) |
| JSON | `Codecs.fs` (version 1 of the event schema), `Protocol.fs` (the client protocol's messages) |
| The capability, stores, contribution types | `Fx.Sdk/Grants.fs` (`Grants`, `IGrants`, `GrantStore`, `GrantStores`, `GrantActions`, `GrantPolicy`) |
| The host | `GrantMachine`, `GrantStoreLog`, `GrantsService`, `GrantsProtocol` (Fx.Grants.Host) |

## Actions

An action is a dotted name, at least two segments, each a lowercase letter then letters and digits
(`shell.exec`, `model.paidFallback`). Its `ActionDef` names the kind of its resources and the scopes an
answer may choose (always `once`). Checks and requests for unknown actions are refused, and grants for
them never apply. Extensions add actions as data (`grant-action`, `GrantActions.contributionType`); the
host builds the vocabulary with `Actions.withContributed` (a clash is an error).

| Action | Resource | Scopes | Spec |
|---|---|---|---|
| `shell.exec` | command | all | §13.1 tool/command approvals |
| `fs.read`, `fs.write` | path | all | §13.1 |
| `net.connect` | name: `host:port` | all | §13.1 network opening, §18 egress |
| `approvals.auto` | name: the sandbox driver that confines the commands (`bubblewrap`, `seatbelt`), `none` when nothing does (`*` any) | all | §13.3 auto approvals: never requested, only granted (a person's switch) and checked by the CodeAct guard; a policy deny rule on `none` forbids them only unsandboxed |
| `approvals.yolo` | name: as `approvals.auto` (first installs are checked on `none`) | all | §13.3 yolo approvals: everything runs unasked; never requested, only granted (a person's `/yolo`) and checked by the CodeAct guard and the host's installer; a policy deny rule on `none` forbids them only unsandboxed |
| `component.install` | name: `<id>@<version>` | once, repo, host | §6.1 first install |
| `budget.raise` | name: `<run>/<budget>` | once | §8.2 |
| `model.enable` | name: `<provider>/<model>` | all | §10.6 |
| `model.paidFallback` | name: `<provider>/<model>` | all | §10.4 |
| `provider.expose` | name: provider id | once, repo, host | §13.1 first-time-provider exposure |
| `secret.read` | name: secret name | all | §11 |
| `env.dotenv` | path: the `.env` file | all | §6.3 |
| `env.cost` | name: `<provider>/<spec>` | once, session, host | §9, §13.1 |
| `safeOutput.apply` | name: `<type>/<target>` | once, session | §8.5 |
| `workflow.approval` | name: `<workflow>/<gate>` | once | §8.3 `Approval` gates |
| `env.credential` | name: a host variable | once, repo, host | §9.4, §18 (T138) |

`env.credential` (T138; resource: the name of a host variable; scopes once, repo, host): a project's environment spec asks to give its commands that credential variable. The request names the project's repository (its directory) and offers once and repo only, so a yes never reaches another project; `fx grants allow env.credential <NAME> --scope repo` (in the project's directory) or `--scope host` grants it beforehand. A project's `secret:` variable asks `secret.read` on the secret's name the same way. Requests are keyed `<action>/<environment>/<attempt>/<variable>`. `fx grants --scope repo` is the project of the current directory; a request without a repository never offers `repo` (`GrantsCommand.allowing` then names a host grant, if offered).

## Resources and patterns

A **resource** is a string whose shape its action's kind fixes; requesters give it **canonical**, and
anything else is refused (`Resource.validate`), so a grant cannot be sidestepped by spelling:

| Kind | Resource | Separator |
|---|---|---|
| `path` | `/`-separated; no `\`, no `.` or `..` segment, no empty segment except the first of an absolute path (`/etc/hosts`, `C:/Users/me`, `src/a.fs`) | `/` |
| `command` | one simple command: its arguments joined by single spaces, no control characters. A requester splits compound commands (`;`, `&&`, `\|`, substitutions) and checks each part | space |
| `name` | anything without control characters (`api.github.com:443`, `bun@1.4.5`, `copilot/gpt-5`) | none |

Resources are at most 32768 characters. A grant's **pattern** is a glob over its action's resources
(`Pattern`, at most 1024 characters):

- `*` matches any run of characters, none included, that does not contain the separator; in a name
  (no separator) it matches anything.
- `?` matches exactly one character that is not the separator.
- `**` (paths and commands only) must be a whole segment and matches any number of whole segments,
  none included: `src/**` matches `src` and everything under it, `git **` any `git` command,
  `**/*.fs` any `.fs` file. At most four per pattern. In a name, write `*`.
- `\` escapes the next character (`\*`, `\?`, `\\`); every other character matches itself.
  `Pattern.exact resource` is the pattern that matches exactly one resource.
- Patterns follow the resources' rules: no empty segment (`a//b`, `a/`), no `.` or `..` segment in
  a path, single spaces in a command, no control characters.
- **Case, fail closed:** allow grants match exactly (`Pattern.matches`); deny grants, policy rules and
  repository path prefixes of deny grants match ignoring case (`Pattern.matchesIgnoringCase`,
  invariant culture). A resource spelled with other case (on a case-insensitive filesystem, a Windows
  command) is therefore never permitted more, and possibly forbidden more.

**Specificity** (`Pattern.specificity`, `Pattern.compareSpecificity`) orders patterns that match one
resource, most specific first, field by field: exact patterns (no wildcard), then more literal
characters (separators count), then fewer `**`, then fewer runs of wildcards with a `*` in them (`?*?*` is
one run), then more `?` (each constrains a character). Generalizing a pattern (a literal to `?`, `?`
to `*`, two literals to `*`, a `*` segment to `**`) keeps it matching and makes it strictly less
specific: a property test checks it.

## Grants, scopes and requests

```fsharp
type Grant =
  { Id: GrantId; Action: ActionName; Resource: string (* pattern *); Effect: Allow | Deny
    Scope: GrantScope; By: string (* a person *); At: int64; Expires: int64 option
    Reason: string option; Origin: Answer of RequestId | Direct of via: string }
and GrantScope = Once of RequestId | Session of string | Repo of { Repo; Path: string option } | Host
```

- Times are microseconds since the Unix epoch (`int64`), as in the store's envelopes.
- A **context** (`GrantContext`: session, run, `RepoPlace` = repository id and a repo-relative path) is
  what scopes are compared with. `Session s` covers contexts of session `s`; `Repo { Repo; Path }` covers
  contexts in that repository at or under the path prefix (segment by segment); `Host` covers
  everything; `Once r` covers nothing: it was spent on request `r` when it was granted.
- A **request** (`GrantRequest`) is made from an ask (`GrantAsk`): the requester's idempotency key
  (asking again with it is the same request: derive it from what asks, e.g. a tool call), the action,
  the resource, the context, the requester (principal, agent, extension), a one-line summary, evidence
  for the prompt, suggested broader patterns (each must match the resource), the scopes offered and an
  optional expiry. The offer is the action's scopes, narrowed to what the requester offers and to what
  the context allows (a session, a repository), `once` first. Requests are bounded (summary and labels
  2000 characters, 32 pieces of evidence of 16384, 16 suggestions), so events stay far below 1 MiB.
- An **answer** (`GrantAnswer`, spec §19.4) has an effect, a `ScopeChoice` (`Once | Session | Repo of
  path option | Host`, filled in from the request's context), an optional pattern (default: exactly
  the resource; it must match the resource), an optional expiry and a reason. `checkAnswer` checks it
  against the request: an offered scope, a path prefix that covers the request's path, a pattern of the
  right kind that matches, an expiry in the future. The same check fits a session request kind's
  `Check` (T125).

## Effective rules

`Rules.evaluate actions policy now grants query` (pure) decides a query. Among the grants **in force**
(active, `now` before their expiry) of the query's action whose scope covers the context and whose
pattern matches the resource (exactly for allows, ignoring case for denies):

1. a matching **policy `Deny`** rule or **`Deny` grant** denies: deny wins, however specific an allow;
2. otherwise a matching **policy `Ask`** rule makes a person decide, whatever grants allow;
3. otherwise the **most specific `Allow`** grant allows (`Rules.moreSpecific`: pattern specificity, then
   the narrower scope — session, repository path (longer first), repository, host — then the later
   grant, then the larger id);
4. otherwise a person decides (`MustAsk [NoGrant]`).

An unknown action, an invalid resource or a non-canonical context path is denied. The decision does not
depend on the order of grants or rules (reasons are sorted by id). Property tests check deny dominance,
that the allowing grant is the most specific that applies, that adding a policy rule never permits
more, that a grant at or past its expiry is as good as absent, and that scopes are honoured.

**Policy only tightens** (spec §13.2, §16.5). Policy comes from host config and policy extensions, both
privileged: data rules (`grant-policy`, `PolicyRule`: ask or deny a pattern of an action) are part of the
pure evaluation, and gates (`gate:grants`, `Gate<GrantQuery>`) run on every check and request; the service
takes the maximum of the rules' decision and the gates' verdict (Deny > Ask > Allow). Neither can allow:
a grant is the only way to `Allowed`.

## The machine

`GrantLog.decide actions state command` is the machine's `Handle` with typed errors (`GrantError`, each
with a stable `grants.*` code); `GrantMachine.machine` wraps it into a `Machine` whose rejections carry
the code and message. Commands are `{ Command: GrantOp; By; At }`: the host stamps the sender and time,
so events record them and clients see who did what by folding events alone.

| Command | Who | Events |
|---|---|---|
| `Request request` | anyone (agents, extensions, the host) | `Requested`; nothing if the key is known |
| `Answer(request, answer, grant id)` | a person | `Granted` (answering the request with its effect), or `Denied` for "deny once" |
| `Grant grant` | a person, as `By`, at the command's time; not `Once`; `Origin = Direct` | `Granted` |
| `Revoke(grant, reason)` | a person | `Revoked` |
| `Expire grant` | the host's timer, once the expiry has passed | `Expired` |
| `Close(request, why)` | the requester (`Withdrawn`), the host (`Covered g`, `Refused`, `TimedOut` once due, `SessionEnded`) | `Closed` |
| `EndSession session` | the host | `Expired` for the session's grants, `Closed SessionEnded` for its pending requests |

- **Only people grant** (spec §13.2): answers, grants and revocations from anything but a `user:`
  principal (a device, a service, `system`, an id without a kind) are refused `grants.not-a-person`; the
  machine reads the kind from the principal id (`PrincipalId.isPerson`, [identity.md](identity.md)). The
  first boundary is the capability split: agents and extensions hold only `Grants` (check, request,
  await, withdraw), and only the host server's client protocol reaches answer, grant and revoke, with the
  connection's principal.
- A request is resolved at most once: an answer, a denial or a closure; later ones are refused
  `grants.not-pending`.
- `react` asks for timers (`ExpireAt`, `TimeOutAt`); `recover` asks again for those of a rebuilt state.
  `evolve` is total: events about unknown grants or requests change only `Seq`.
- **State** (`GrantsState`): every grant with its status (`Active | Spent | Revoked | Expired`), every
  request with its status (`Pending | Answered grant | Rejected denial | Closed closure`), requests by
  key, and `Seq`. `RequestEntry.Resolution` is what a requester sees: `Allowed grant`, `Denied reasons`
  or `Cancelled reason`.
- Events are version 1 of the event schema (`GrantMachine.schema`); `tests/Fx.Grants.Tests/golden/events-v1.jsonl`
  is kept as written and must read forever.

## Stores

A **`GrantStore`** (spec §13.2, §16.2; `Fx.Sdk`) is where the log lives: the machine host's event log for
one stream, in the SDK's types (`Append expected meta events`, `Read after max`, `Head`, `WaitPast`;
`Duplicate` for a key it holds, `Conflict` for a wrong head, `ReadOnly` for a store that takes no
appends). It is a privileged, in-process contribution type (`grant-store`). Every store passes
`GrantStoreConformance` (`tests/Fx.Grants.Host.Tests/Conformance`).

- **Host-local** (built in): `GrantStoreLog.hostLocal log` is the stream `grants/host` of the host's
  event store (`SqliteEventStore` in the composition root), read and written with the event schema.
- The service runs the actor on any store through `GrantStoreLog.toEventLog`. A repository-policy store
  (grants committed in a repository, reviewed in PRs) implements the same contract; reading several
  stores into one set of effective rules is left for that store.

## The service and the capability

`GrantsService.start settings` (actions, policy rules, policy gates, store) starts one actor, the single
writer of the store's stream, rebuilds the state and restarts the timers.

- **`GrantsService.grants service : Grants`** is the `IGrants` capability (Fx.Sdk) agents, strategies and
  extensions get. Its calls run in the service's environment with the caller's cancellation.
  - `Check query`: the effective rules and the policy gates now (`GrantCheck`: `Allowed grant | MustAsk
    reasons | Denied reasons`). Records nothing.
  - `Request ask`: an invalid ask is an error; otherwise the rules settle it (`AskOutcome.Settled`) or
    a durable pending request is recorded (`AskOutcome.Pending id`). **A key is one request**, unique on
    the host (derive it from what asks and where: `session/<id>#<seq>/<call>`): asking again while it is
    pending gets it, once allowed gets that; once denied or cancelled, the rules settle the ask if they
    allow or deny now (a grant made since), otherwise it keeps its resolution. Prompting again takes a
    new key, so retrying a tool call cannot spam a person.
  - `Await id`: waits for the resolution. While it waits, it folds the log, and closes the request as
    `Covered` when a grant made since allows it, or `Refused` when the rules now deny it: answering one
    prompt with a broader pattern settles the others it covers (batched prompts, spec §19.4).
  - `Withdraw id reason`. Also what the host does with a request **nobody can answer** (a headless run
    without a terminal, [host.md](host.md)): it withdraws it (`Closed Withdrawn`, by the host), and
    never answers "deny" on a person's behalf. Nobody decided, so the log records no person's
    decision, and the next request for the same thing (a new key: another call, another install
    attempt) asks a person again.
  - `Grants.authorize ask` (Fx.Sdk) is request-then-await; cancelling the wait withdraws the request
    (shielded) and rethrows the cancellation.
  - `Grants.gate grants toQuery` is a `Gate<'I>` backed by the capability, for any hook point: an intent
    that maps to a query is decided by the rules, other gates only tighten it.
- **People** (through the protocol): `answer`, `grant` (session, repository or host; the id, person, time
  and client are the host's), `revoke`. On the command line, `fx grants allow|deny|revoke|list`
  ([host.md](host.md#fx-grants)) does the same for the OS user who runs it, signed in through
  `local-os-user` as embedded mode signs them in, and only if they own the host. `endSession` is the host's. Command keys are derived from what
  they act on, so a retry deduplicates and a different command is never swallowed by an earlier refusal:
  `request/<ask key>`, `answer/<request>/<answer JSON>`, `grant/<grant id>`, `revoke/<grant>/<person>`,
  `close/<request>/<seq>`, `withdraw/<request>`, `end-session/<session>`.
- Views: `list all`, `pending`, `snapshot`, `subscribe after handle` (`GrantNotice`s, spec §5).

## The client protocol (`grants/*`, spec §17)

`GrantsProtocol` defines each method once (Fx.Rpc), with typed errors (`GrantError`):

| Method | Params → result | |
|---|---|---|
| `grants/check` | `GrantQuery` → `GrantCheck` | would this be allowed? |
| `grants/list` | `{ all }` → `{ seq, grants }` | active grants (or all) with their status |
| `grants/pending` | → `{ seq, requests }` | the prompts to show, oldest first |
| `grants/state` | → `GrantsState` | the state to fold notices onto |
| `grants/answer` | `{ request, answer }` → `GrantResolution` | a person's answer |
| `grants/grant` | `GrantDraft` → `Grant` | a person's direct grant |
| `grants/revoke` | `{ grant, reason? }` → `null` | a person revokes |
| `grants/subscribe` | `{ after }` → last seq | sends `grants/event` (`{ seq, event }`) in order until cancelled |

`GrantsProtocol.handlers service caller` serves them; `caller` is the host server's answer to "who is on
this connection" (`Attribution`, or `NotAPerson`). The TypeScript types are generated from the codecs
(`tests/Fx.Grants.Tests/golden/grants.d.ts`, type-checked by `bun scripts/declarations.ts`).

## How the rest plugs in

| Who | How |
|---|---|
| **T125 sessions** | Grant requests live in the grants log, not in the session: a session does not open its own pending request for a grant. A thin adapter can still show them among a session's requests: a request kind `grant.request` with payload `Codecs.request`, answer `Codecs.answer`, `Check = fun r a -> GrantLog.checkAnswer actions r a now`; its answer is forwarded to `GrantsService.answer`. When a session ends, the host calls `GrantsService.endSession`. Context: `GrantContext.Session` is the session id, `Run` the run, `Repo` the workspace's repository and the path concerned |
| **T127b CodeAct** | The grants guard on host calls of cells and direct tools ([codeact.md](codeact.md#grants-spec-13-18-t127b)): `shell.exec` for each simple command of a line (compound commands split for sh and PowerShell; what the parser does not follow is the whole line), `fs.write`/`fs.read` outside the workspace and `fs.write` to protected paths, `net.connect` for network bindings. It checks first, then requests a call's needs at once with keys `<turn's start event>/<cell or tool call>/<action>/<digest>` (a retried call is the same request), and waits; an interrupt or a steer withdraws the requests, a denial is the call's `denied` error. Requests name their session and say which turn and call asked: clients show a turn's prompts from `grants/pending` by session |
| **T134 approval modes** | Auto approvals ([codeact.md](codeact.md#approval-modes-spec-133-t134)): for a command written plainly in the workspace that nothing but "no grant applies" stands in the way of, the guard checks `approvals.auto` on the hands' sandbox driver (`none` when nothing confines them: auto approvals hold sandbox or not) in the session's context. A grant allows them for its scope, a deny grant or policy forbids them, no grant leaves them to `codeact.grants.mode`. A person's switch (`session/setApprovals`, owner: host.md) is a session grant, or its revocation and a session deny grant; the host makes a grants policy rule from a policy file that forbids the mode's `auto` (on `*`); a policy rule on `none` forbids them only unsandboxed. What runs on them records nothing in the grants log: the guard reports it (step journal, receipts, replay journal) |
| **T134b yolo approvals** | Yolo approvals ([codeact.md](codeact.md#approval-modes-spec-133-t134)): for whatever a call needs that would ask (or, in `deny` mode, be refused for want of a grant), the guard checks `approvals.yolo` on the hands' driver in the session's context: a grant allows everything for its scope (deny grants and policy still refuse), a deny grant or policy forbids it, no grant leaves it to `codeact.grants.mode` (`yolo`, which replaces `off`). A person's `/yolo` (`session/setApprovals`) is a session grant; `auto` and `ask` revoke it (and make a session deny grant under the host's mode `yolo`). The host makes a deny rule on `*` from a policy file that forbids `yolo` or `auto`, or pins the mode to another value (`YoloApprovals.policyRules`). The host's installer approves first installs on them (`YoloInstalls`: the host's mode, a host grant, or a session of the environment; checked on `none`) |
| **T128 recovery** | A pending request whose turn was lost stays pending (durable); recovery withdraws it or re-awaits it with the same key. A clean stop of the host cancels the turn, which withdraws its requests |
| **T129 host server** | Composition: `GrantsService.start` on `GrantStoreLog.hostLocal` over the host's event store, with `Actions.withContributed` (`grant-action`), `GrantPolicy.rules` and `GrantPolicy.gates` from the extension host (`GrantPolicy.contributionTypes` into the catalog), and the contributed `grant-store` if host config selects one. Serves `GrantsProtocol.handlers` with the connection's principal; passes `Approvals.viaGrants` to the installer |
| **T130 TUI** | The grants view: `grants/pending` + `grants/subscribe` for prompts (once / session / repo / host, with the request's suggestions as broader patterns), `grants/list` with revoke |
| **T110 installs** | `Approvals.viaGrants` (`component.install`), keyed by what asks: the install, the holder whose `Ensure` started the attempt and the attempt (`component.install/<install>/<holder>/<attempt>`, [components.md](components.md)). An answer is final for that attempt only: "once" (or a request withdrawn because nobody could answer) never stands for a later run's environment; a host-wide grant (`fx grants allow component.install bun@*`) approves every later install at once |
| **T118/T124 egress, T208/T210 models** | A model-egress gate or model routing asks `provider.expose`, `model.enable` or `model.paidFallback` through `Grants.gate` / `Grants.authorize` |
| **T214 goals, T306 gates, T405 safe outputs** | `budget.raise`, `workflow.approval`, `safeOutput.apply` |
| **T109b** | `IGrants` in `IExtensionEnv` (attenuated by the manifest) so extension code can ask |

## Left for later

| What | Task |
|---|---|
| The installer forwarding approvals from an environment on another machine (the host's composition: [host.md](host.md)) | T501 |
| Several stores at once (a repository-policy store's grants next to the host-local ones) | when such a store exists |
| `IGrants` for extensions (`IExtensionEnv`), and over the extension protocol | T109b |
| Snapshots of the grants log, bounding the history kept in the state | when a long-lived host needs it |
