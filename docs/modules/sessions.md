# Sessions (`Fx.Sessions`, `Fx.Sessions.Host`)

Spec: §5 (with §4.4–4.5, §8.1, §12.1–12.2, §13, §15, §17 and §19.2, §19.5). Task: T125. Builds on
[machines.md](machines.md), [store.md](store.md), [variants.md](variants.md) and [contracts.md](contracts.md).
Code: [`src/Fx.Sessions`](../../src/Fx.Sessions) (the machine and its JSON; shared with clients) and
[`src/Fx.Sessions.Host`](../../src/Fx.Sessions.Host) (actors on the event store, the turn-runner boundary,
profiles, subscriptions).
Tests: `tests/Fx.Sessions.Tests`, `tests/Fx.Sessions.Host.Tests`, and the `sessions-*` scenarios of the
JIT-vs-native diff.

A session is a conversation with the agent: inputs from any number of clients, turns that a strategy runs
(T127), and the requests the agent or the host makes of people. Its state is the fold of its event log
(spec §1, principle 4), and every client folds the same events with the same code.

| Piece | Where |
|---|---|
| Commands, events, state, intents | `Model.fs` (`Command`, `SessionCommand`, `Event`, `SessionState`, `Intent`) |
| Request kinds | `RequestKinds.fs` (`RequestKind<'P, 'A>`, `RequestKindInfo`, `RequestKinds`, built-ins `confirm`, `choice`) |
| The machine | `Session.fs` (`Session.evolve`, `handle`, `react`, `recover`, `machine`, `offers`, `nextInputs`; `Rejections`) |
| JSON | `Codecs.fs` (every type; the event codec is version 1 of the session's event schema) |
| Storage and subscriptions | `SessionLog` (event and snapshot schemas, the projection, `feed`, input lookups) |
| The boundary to turn strategies | `TurnContext`, `TurnRunner`, `TurnContext.ask` |
| Profiles | `ProfileBinder` (`ofVariants`, `constant`), `BoundProfile` |
| The host | `SessionHost` (`create`, `send`, `interrupt`, `start`, `state`, `offers`, `setOverrides`, `subscribe`, `stop`), `SessionSettings` |
| Test double | `Fakes.ScriptedTurnRunner`, `TurnStep` |

## The machine

`Session.machine kinds : Machine<SessionCommand, Event, SessionState, Intent>`. `Fx.Sessions` references only
FSharp.Core, Fx.Machines and Fx.Contracts (Fable; `ArchitectureTests` reads the compiled metadata).

- **Commands carry their sender and time.** `Handle` is pure and sees only the command, so a
  `SessionCommand` is `{ Command; By; At }`. The host builds it from the envelope's attribution and the clock,
  so events record the same sender as their metadata, and clients see who did what by folding events alone.
- **Times** are microseconds since the Unix epoch (`int64`), as in the store's envelopes.
- **Commands are read through their codec first, canonical.** The JSON they carry (payloads, answers,
  overrides, evidence) goes into events exactly as the store reads it back, so a live actor's state equals the
  fold of the stored log whatever member order or number notation a client sent; what the codec refuses is
  refused `session.invalid`.
- **Inputs** (a prompt, a request settled as input, a finished job) are numbered by the sequence number of the
  event that received them. Later events refer to them by that id (`TurnStarted`, `InputsDelivered`), so a
  prompt is stored once, and `Session.inputOf event` gives the input an event received.
- `state.Seq` counts the events folded: `Handle` knows the sequence number each new event gets.
- **Rejections** have stable codes (`Rejections.*`, e.g. `session.closed`, `session.stale-turn`,
  `session.not-open`); clients match on them.
- **Invariant:** after every accepted command the session never sits idle with work it could start
  (`nextInputs` is `None`); commands that add inputs or end a wait call `settle`. A property test checks it.

### Prompts: steer and queue (spec §5, §19.2)

| | No turn running | A turn running |
|---|---|---|
| `Prompt(text, Steer)` | starts a turn with it at once (and with anything else in the inbox) | goes to the inbox; delivered at the turn's next step boundary |
| `Prompt(text, Queue)` | starts a turn, oldest queued prompt first, unless the session waits for requests or jobs | waits in the queue; starts a turn of its own after the running one |

- When a turn ends, the next one starts with the **inbox** if it holds anything (steers the turn did not take,
  late answers, job notices), else with the **oldest queued prompt**. One queued prompt per turn, so each
  turn's result answers one prompt from one principal.
- **Step boundaries.** The runner calls `TakeInputs(turn, step)` at each boundary; the session appends
  `InputsDelivered(turn, step, ids)` with everything in the inbox (nothing if it is empty). Delivered inputs
  join the running turn's inputs.
- `Withdraw id` takes back a queued prompt or a steer not delivered yet.
- Prompts are at most `Session.MaxPromptChars` (131072) UTF-16 units, so events stay far below the store's
  1 MiB; larger content belongs in attachments (blobs, later).

### Turns

- `TurnStarted(turn, inputs, profile, at)` starts a turn; `react` asks the host to run it
  (`Intent.RunTurn`). The turn runs under the profile bound at its start.
- The runner's result comes back as `EndTurn(turn, TurnResult)` (spec §4.5: `Done | NeedInput | Waiting`) or
  `FailTurn(turn, reason)`. `TurnEnded(turn, TurnEnd, at)` records `Completed result | Interrupted |
  Failed reason | Abandoned reason`.
- **`NeedInput requests`**: the session waits until every one of those requests that is open and replies as
  input is settled, then starts the next turn with their resolutions. **`Waiting(jobs, note)`**: it waits for
  `NotifyJob` of each job. Queued prompts wait too; a steer takes over at once (and ends the wait).
- **Held.** After an interrupt, a failure or an abandoned turn the session is held: nothing starts from the
  queue or inbox until someone sends a prompt (spec §5: nothing auto-resumes).
- Commands about a turn that is not running are refused `session.stale-turn` (a late result after an
  interrupt, a step of an ended turn).

### Interrupts: the control path

Send `Interrupt` with `Actor.interrupt` (`SessionHost.send` does so for `Command.Interrupt`): it cancels the
actor's work in flight at once (the running turn's effect, request timers, fork starts) and goes before
queued commands ([machines.md](machines.md)). Because the work is already cancelled when the command is
handled, requesting and applying the interrupt are one commit: `Interrupted(Some turn, by)`, the turn's
turn-scoped requests cancelled, `TurnEnded(turn, Interrupted)`. An interrupt with no turn running is recorded
too (`Interrupted(None, by)`) and holds the session. Either way `react` asks again for the background work
the interrupt cancelled (`Session.background`: request timers, forks being started), as `recover` does.

### Pending requests (spec §5, §12.2, §13)

A request is opened with `OpenRequest spec` (a `RequestSpec`):

| Field | Meaning |
|---|---|
| `Id` | unique among the session's open requests; openers derive it from what they do, so a retry opens the same one |
| `Kind`, `Payload` | a registered kind and a payload its codec accepts (`session.unknown-kind`, `session.invalid-payload`) |
| `Origin` | `Turn n` (the running turn; forced by `TurnContext.Open`), `Session` (the host or an extension: installs, auth flows), `Stream ref` (a follower of the log, e.g. a run step at a gate) |
| `Reply` | `ToOpener`: the opener waits for the resolution in the log (a tool call waiting for a grant). `AsInput`: the resolution becomes an input for the agent (a decision card) |
| `Lifetime` | `Turn`: cancelled when its turn ends (only a turn's requests). `Session`: open until settled, across turns and restarts |
| `Expiry` | `{ At; Then = Cancel \| Answer json }`: the host's timer sends `ExpireRequest` at `At` |

- **Kinds** are typed and extensible: `RequestKind<'Payload, 'Answer>` has the payload and answer codecs, a
  check of an answer against its payload (`choice`: one of the options) and the **client capabilities** that
  can answer it (its id unless `answerableBy` says otherwise). `RequestKind.info` erases it for the machine;
  `RequestKinds.builtIn` has `confirm` and `choice`. Decision cards (T301), grant requests (T123), gates
  (T306), auth flows (T121) and installs are kinds their owners register, as extensions do theirs.
- **Answered exactly once.** `Answer(id, response)` is checked against the kind (`session.invalid-answer`) and
  the deadline (`session.expired`); a second answer is refused `session.not-open`, and a resent answer (same
  key) gets its first outcome. `CancelRequest` withdraws one. `RequestResolved` records the resolution
  (`Answered | Cancelled | Expired default`), who and when, and the input id when it replies as input.
- **Delivered once to its origin.** `ToOpener` resolutions are followed in the log by the opener
  (`SessionLog.resolution`, `TurnContext.Await`); `AsInput` ones are inputs, delivered at the next step boundary
  or as the next turn's input, exactly once. Cancellations the session makes itself (turn ended, session
  closed) are not inputs.
- **Re-offered to capable clients.** Requests are part of the state, so they survive restarts and every client
  sees them. `PendingRequest.Capabilities` is recorded when the request opens (replay never depends on
  today's kinds); `Session.offers capabilities state` (and `SessionHost.offers`) gives the open requests a
  client with those capabilities can answer, which the host server offers when a client attaches (T129).
  Checking that an answering client has a capability is the host server's job.

### Profiles (docs/modules/variants.md, spec §15)

- `Started` (the first event) carries the session's profile id and its overrides; `ProfileSelected(profile,
  overrides, changed, by)` binds a new one. Each turn records the profile it runs under.
- `SelectProfile selection` is optimistic: `BasedOn` must be the session's current profile, or the selection is
  refused `session.stale-profile`. The host resolves profiles (below); the machine never does.

### Forks, close and archive

- `Fork(child, upTo)` (default: the head) records `Forked(child, upTo, by)` with the profile in effect. `react`
  asks the host to start the child: its first event is `Started` with `Parent = Some(session/<id>#upTo)`, keyed
  by the fork event, so a retry is deduplicated. `ChildStarted` / `ChildFailed` mark the fork `Ready` or
  `Failed`. The child refers to its parent instead of copying its history (T126 renders the chain).
- `Close` (refused while a turn runs: interrupt first) cancels open requests and drops the queue and inbox;
  a closed session refuses input. `Reopen` opens it again; `Archive` closes and archives it.

## The JSON

`Codecs` has a codec for every type: `command` (named `SessionCommand`, what clients send), `event`
(`SessionEvent`), `state` (`SessionState`, also the snapshot), `intent`, and the parts. Unions are tagged by
`kind`; times are integers.

- **Version 1** of the event schema is `SessionLog.schema` (`EventSchema.create Codecs.event`).
  `tests/Fx.Sessions.Tests/golden/events-v1.jsonl` keeps what it wrote for a scenario that appends every kind of
  event; it is written once and never rewritten (`Golden.kept`). A change to an event's JSON is a new schema
  version with an upcaster, and that file must still read.
- The snapshot schema (`SessionLog.snapshot`, `session` v1): bump its version when `SessionState`'s JSON or
  `evolve`'s meaning changes.
- `Resolution.Expired(Some json)` is the tag `expiredWithAnswer`, so a default answer of `null` stays exact
  (optional fields cannot tell `null` from absent); `Evidence.Detail` is `null` for none.
- TypeScript: `tests/Fx.Sessions.Host.Tests/golden/session.d.ts`, generated from the codecs and type-checked by
  `bun scripts/declarations.ts` (T109 moves it into the SDK's generated files).

## The host

```fsharp
let! host = SessionHost.create (SessionLog.typed store.Log) store.Snapshots runner SessionSettings.defaults
let! outcome = SessionHost.send host "s1" (IdempotencyKey "k1") alice (Command.Prompt("hi", PromptMode.Steer))
```

- **One actor per session stream** (`session/<id>`) in an `ActorRegistry`, started from the latest snapshot
  (`ActorSnapshots.latest`, `Actor.startFrom`): keys of commits before it are looked up in the log, so a
  command sent again still gets its first outcome. A snapshot is saved whenever `SnapshotEvery` events were
  appended since the last one (after commands the host sends, and when an actor starts).
- **Multi-client.** Every command goes through the actor's mailbox with its key and attribution: inputs from
  any number of clients are handled one at a time, in arrival order, each attributed to its principal
  (`Attribution`; principals are T122, epoch-fenced takeover T507).
- **Intents.** `RunTurn` runs the turn runner; `Expire` sleeps until the deadline on the wall clock (looping, as
  delays run on the monotonic clock) and sends `ExpireRequest`; `StartFork` starts the child; `Abandon` (from
  recovery) ends a turn the host stopped during.
- **Keys** of the host's commands derive from events, never from the clock or ids: a turn's commands from its
  `TurnStarted` event (`<stream>#<seq>/step/<n>`, `/request/<id>`), a fork's start from its `Forked` event
  (`/fork`), intent results as in [machines.md](machines.md).
- **Recovery.** Intents are not durable. An actor that starts on a log with a running turn abandons it
  (`AbandonTurn`; T128 turns in-flight tool calls into `OutcomeUnknown` and opens a decision) and arms request
  timers and fork starts again. Actors start on first use: the host server activates sessions with work in
  flight at start (T129), e.g. with `SessionHost.state`.
- **Subscriptions** (spec §5 `subscribe(afterSeq)`): `SessionHost.subscribe host id after handle` replays the
  events after `after`, then follows live; a subscriber more than `MaxGap` events behind gets the latest
  snapshot first (`Feed.Reset`). `SessionLog.feed` is its JSON (`{"kind": "reset", "seq", "state"}` or
  `{"kind": "event", …envelope}`); T129 serves it as `session/subscribe`. Clients fold `Session.evolve`.
- **Overrides** (`setOverrides host binder id key by changes`): merges the changes into the session's
  overrides, resolves the profile with the `ProfileBinder`, sends `SelectProfile` based on the profile it read,
  and resolves again (with key `<key>/<attempt>`) when another client changed it meanwhile, up to
  `ProfileAttempts`. `ProfileBinder.ofVariants registry policies layers describe` resolves the host, project
  and explicit layers with the overrides as the `Session` layer and stores the profile in the blob store before
  it is bound; an invalid override is an `Error` with the resolution's issues.

### The turn-runner boundary (T127)

```fsharp
type TurnContext<'R> =
    { Turn: TurnStart                                   // session, turn, profile, inputs, start event
      Boundary: int -> Eff<'R, Input list>              // steers delivered at step n, exactly once
      Open: RequestSpec -> Eff<'R, Result<int64, Rejection>>
      Await: string -> int64 -> Eff<'R, Resolution> }
type TurnRunner<'R> = { Run: TurnContext<'R> -> Eff<'R, Result<TurnResult, string>> }
```

- `Boundary n` sends `TakeInputs(turn, n)` keyed by the turn and step, then reads the delivered inputs from the
  log: asked again for the same step, it returns the same inputs.
- `Open` forces the request's origin to the turn and returns the sequence number of its opening event; `Await
  id after` follows the log after it for the resolution. `TurnContext.ask` does both.
- An `Error reason` ends the turn `Failed reason`; an exception ends it `Failed` with the exception's type, never
  its message; an interrupt or a stop cancels the effect's scope, and nothing is reported.
- `ScriptedTurnRunner` plays scripts (`Think`, `Boundary`, `Ask`, `Post`, `Finish`, `Fail`, `Throw`) and keeps a
  transcript of what each turn saw.

## Testing

- `tests/Fx.Sessions.Tests`: one test per rule of the machine; properties over random scripts (the state is the
  fold of the log through its JSON, a state restored from its JSON decides alike, inputs are numbered by their
  events and delivered at most once, turns follow each other, requests are settled at most once, never idle with
  work it could start); version 1's events read back; the Fable architecture test.
- `tests/Fx.Sessions.Host.Tests`: one scenario per guarantee (steer and queue, concurrent clients serialized and
  attributed, duplicates after restarts and from before a snapshot, interrupts, requests answered once, expiry,
  restart mid-turn, forks, subscribers with snapshot + tail, overrides and their races, runner failures); replay
  tests (a conversation and random scripts give the same logs, outcomes, turns, snapshots and trace on every run;
  the recorded conversation replays to its recorded state, kept as golden files); SQLite; the declarations.
- **Native AOT**: `sessions-machine`, `sessions-host` and `sessions-sqlite` run on the JIT and as a native binary
  on every target.

## Left for later

| What | Task |
|---|---|
| `session/*` methods over JSON-RPC (send, interrupt on the control path, subscribe with `SessionLog.feed`, offers on attach), capability checks of answering clients, activating sessions with work in flight at start | T129 |
| `SnapshotEvery`, `MaxGap` and `ProfileAttempts` as variant slots | T129 |
| Principals behind `Attribution`, roles; epoch-fenced takeover | T122, T507 |
| The strategy behind `TurnRunner`, `TurnResult` validation (`Ensure`), richer `Evidence` | T127 |
| Rendering a session (and a fork's parent chain) into a prompt | T126 |
| In-flight tool calls become `OutcomeUnknown` on recovery, with a decision | T128 |
| Decision cards, grant requests, gates, auth flows, installs as request kinds | T301, T123, T306, T121, T110 |
| Attachments (blob references) in prompts | when the TUI sends files |
| Whether queued prompts run one per turn or together, and whether a session holds after an interrupt, as variants | when evals or users ask |
| Generated TypeScript for clients in the SDK package | T109 |
