# Machines and the machine host (`Fx.Machines`, `Fx.Machines.Host`)

Spec: §4.4 (with §1 principles 2 and 4, §3, §5, §8.1, §17 and §19.3). Task: T104.
Code: [`src/Fx.Machines`](../../src/Fx.Machines) (pure, shared with clients) and [`src/Fx.Machines.Host`](../../src/Fx.Machines.Host) (the runner).
Tests: `tests/Fx.Machines.Tests`, `tests/Fx.Machines.Host.Tests`, and the `machines-*` scenarios of the JIT-vs-native diff.

## Machines

```fsharp
type Machine<'Cmd, 'Evt, 'S, 'Intent> =
    { Initial : 'S
      Evolve  : 'S -> 'Evt -> 'S
      Handle  : 'S -> 'Cmd -> Result<'Evt list, Rejection>
      React   : 'S -> 'Evt -> 'Intent list }
```

- **`Initial`** is the state of an empty log.
- **`Evolve`** applies one event.
  - It is pure and total: it must accept every event `Handle` ever decided, because events are facts that stay in logs forever.
  - Clients share it (§17, §19.3), so it, the events and the state use only FSharp.Core.
- **`Handle`** decides what a command means in a state: the events to append, or a `Rejection` (`{ Code; Message }`, e.g. `chat.closed`).
  - It is pure. Anything it needs from the world, such as the time, an id or a result, arrives in the command.
  - A rejection is an expected outcome, not a defect.
- **`React`** says what should happen after an event, as data (intents). It gets the state *after* the event.
- **State is a fold of the log.** `Machine.replay machine events` is the state of a log. A host that rebuilds an actor and a client that follows the log compute exactly this.

| Function | What it does |
|---|---|
| `Machine.fold`, `replay` | the state after events |
| `Machine.apply` | evolves events and collects what `React` returns for each, in order |
| `Machine.step` | `Handle`, then `apply`: what a command does in a state, without appending |
| `Machine.run` | runs commands as a host would (rejected ones change nothing), for tests and simulations |
| `Machine.mapIntent`, `mapCommand` | adapt a machine's intents or commands |

**Fable.** `Fx.Machines` references only FSharp.Core: no tasks, threads, I/O or reflection. `ArchitectureTests` reads the compiled assembly's metadata and fails on any other reference. The effectful runner is a separate project, `Fx.Machines.Host`.

## What travels to and from an actor

| Type | Meaning |
|---|---|
| `StreamId` | names one stream, the log of one actor: `kind/id`, e.g. `session/01J…` |
| `IdempotencyKey` | identifies one command. Clients make keys unique (an `Id` is a good key) |
| `Attribution` | who sent an input: `{ Principal; Via }`. `Principal` is a principal id (principals are T122); the host's own inputs use `system`. `Via` names the client, interpreter or process manager that delivered it |
| `EventRef` | an event's position: stream and sequence number, printed `stream#seq` |
| `Envelope<'Cmd>` | a command with its key, its attribution, and the event that caused it, if any |
| `Outcome` | `Accepted of Appended` (the sequence numbers appended; possibly none), `Rejected of Rejection`, or `Stopped` (the actor stopped before handling it, so nothing happened: send it again with the same key) |

## Event logs

`EventLog<'Evt>` is what an actor needs from an event store. It is a record of functions with data in and out and the cancellation token last, like a capability:

| Function | Contract |
|---|---|
| `Append stream expected meta events` | appends one **commit** (the events of one command) atomically if the head is `expected`, and returns the new head. If the stream already has a commit with the key in `meta`, `Error (Duplicate (key, appended))` with that commit's events (checked first, whatever `expected` is); otherwise, if the head is not `expected`, `Error (Conflict (expected, actual))`. Never called with an empty list |
| `Read stream after max` | at most `max` events after a sequence number, in order |
| `Head stream` | the last sequence number, 0 for an empty stream |
| `WaitPast stream after` | completes with the head once the head is past `after` |

- Sequence numbers start at 1, with no gaps. Streams are independent.
- **A stream never holds two commits with one key.** So a command takes effect at most once even when the writer does not know the key (a retry after a lost reply, an actor that did not read the whole log), and the caller learns where its events went.
- Every event carries `EventMeta`: when the actor handled the command (`At`, on `IClock`), who sent it (`By`), its key (`Key`) and its cause (`Cause`). Events of one commit share it.
- **Interpreters.** `InMemoryEventLog` is for tests, simulations and `fx test`. Its calls complete synchronously, and an append wakes waiting readers inline, before it returns. The durable store is `SqliteEventStore` ([store.md](store.md)). Every interpreter passes `EventLogConformance`, a function from a log to a `Test`.
- **Events are typed values here.** The durable store keeps them as codec JSON with a schema version; `EventLog.typed` (Fx.Store) gives the typed view ([store.md](store.md)).
- `EventLog.next`, `readAll` and `subscribe` are built on the four functions.
  - **`subscribe log stream after handle`** is spec §5's `subscribe(afterSeq)`. It replays the events after `after`, then follows new commits, in order, without gaps or repeats.
  - It reads in pages, so a slow subscriber never holds up writers. It stops when `handle` returns false, or when its scope is cancelled.

## Actors

An actor runs one machine on one stream and is the stream's **single writer** (§3).

```fsharp
let spec = ActorSpec.create stream machine log interpret   // Interpret: 'Intent -> Eff<'R, 'Cmd list>
let! actor = Actor.start spec                              // 'R :> IClock, ITrace, IScopeWith<'R>
let! outcome = Actor.send actor (Envelope.create key by command)
```

### The shell loop

Each input goes through the shell loop of §4.4, one at a time, in mailbox order:

1. **Duplicate check.** A key the actor knows gets the outcome it had the first time. Nothing else happens.
2. **`Handle`** runs in the current state.
   - A rejection is remembered and returned.
   - An empty list is an `Accepted` with no events.
3. **Append.** The events go to the log as one commit, with `expected` = the actor's head and the metadata of the envelope.
4. **`Evolve` and `React`** run for each event. The new state and head are published.
5. **Intents** start, then the sender gets its outcome.
6. **Results.** When an intent finishes, the commands it returned go back to the mailbox, like any input:
   - attributed to `system via interpreter`;
   - caused by the intent's event;
   - keyed `<event>/<intent index>.<command index>`, e.g. `chat/1#4/0.0`. The same in every run.

### Inputs are serialized and attributed

- The mailbox is the only way in. Commands from any number of clients and threads are handled one at a time, in arrival order: first in, first out per sender.
- Each commit records its sender's attribution, so every event says who caused it.
- `Actor.snapshot` returns the state and the sequence number of the last event in it, as one consistent pair.
- A client can start from a snapshot and `Actor.subscribe` after its `Seq` (§5: "snapshot + tail"). It then sees every later event exactly once.

### Idempotency keys

| A key that… | is answered with the first outcome… |
|---|---|
| was accepted with events | always: the actor rebuilds its key index from the log's metadata, so this survives restarts. A key the log has but the actor does not know (another writer appended it) gets `Duplicate` from the log, and the actor answers with that commit, its state unchanged |
| was rejected, or accepted with no events | by the same actor. A new actor handles the command again |
| was answered `Stopped` | never: the command was not handled |

- Either way, **a command takes effect at most once**. Only a command that changed nothing can be handled again, and handling it again cannot duplicate an effect.
- A key identifies a command. Sending another command with a known key returns the first command's outcome. The host does not compare commands.
- The key index grows with the log, and so does the in-memory index of rejections. Bounding them is future work.

### The control path

`Actor.interrupt` is the control path of §5:

- On the caller's thread, it cancels the actor's work in flight, before anything is queued.
- It puts the command before every queued command. Queued commands keep their order.
- When the actor handles the command, the actor cancels the work again. That covers intents started meanwhile by the command that was in progress.
- An interrupt whose key the actor already knows cancels nothing. So a retried interrupt cannot stop work that started after the first one.
- Intents that must finish anyway shield themselves with `Eff.shield`.

### Intents

- **`Interpret`** runs intents in the actor's environment:
  - the scope is the actor's work scope, cancelled by `interrupt` and by `stop`;
  - the span is the actor's span.
- **Concurrent or sequential.** `IntentOrder.Concurrent` (the default) starts each intent as soon as `React` returns it. `IntentOrder.Sequential` runs them one at a time, in order.
- **Not durable.**
  - `React` runs when an event is appended, never when a log is replayed.
  - Work in flight when an actor stops is lost unless the state records it.
  - **`Recover: 'S -> 'Intent list`** runs once when an actor starts, with the rebuilt state. Its results are keyed `<stream>#<head>/recover/<i>.<j>`.
  - Nothing auto-resumes (§5). A session's `Recover` turns an interrupted tool call into `OutcomeUnknown` (T128); it does not re-run the call.

### Failures

| What fails | What happens |
|---|---|
| `Handle` throws, for a command with a sender | that command's task faults; the actor goes on (its state did not change) |
| `Handle` throws, for an interpreter's result | the actor stops: nobody else would learn of it |
| the append throws | the sender's task faults and the actor stops. The commit may or may not be in the log, so a new actor rebuilds from the log, and the sender's retry with the same key is deduplicated |
| the append conflicts | `ConcurrencyException`: another writer appended, e.g. after a takeover (§17). The actor stops |
| `Evolve` or `React` throws | the sender's task faults and the actor stops |
| an interpreter throws | the actor stops (expected failures are commands). Cancellation by `interrupt` or `stop` is not a failure |

When an actor stops:

- queued and later commands are answered `Stopped`;
- its work is cancelled and awaited;
- `Completion` faults with the defect;
- `Actor.wait` rethrows the defect, and `Actor.stop` never throws.

### Lifecycle

- **`Actor.start`** rebuilds the state, the head and the key index from the log, starts the loop, then runs `Recover`.
  - The actor outlives the scope that started it.
  - Its loop runs without cancellation, so a stopping actor never abandons an append half-way.
- **`Actor.stop`**: the command in progress finishes, then the actor stops as above.
- **`Actor.run spec body`** is the structured form. It stops the actor when `body` ends, also on failure or cancellation.
- **`ActorRegistry`** keeps one actor per stream in a process.
  - It starts an actor on first use, and again after one stopped (rebuilding from the log).
  - Concurrent first uses start it once.
  - `ActorRegistry.stop` stops them all.

### Concurrency and determinism

- Inputs are handed to a waiting actor on the sending thread. Appends to `InMemoryEventLog` complete inline, and so do replies and fake-clock delays.
- So with the in-memory log and a `FakeClock`, everything an actor does happens in an order fixed by its inputs, and a script gives the same log, outcomes and trace on every run.
- **The rule that comes with it: never block a thread inside a continuation of an actor's reply.** Senders continue on the actor's thread. A blocking wait for the same actor deadlocks.
- With a durable store, appends complete on other threads. The invariants hold, but timing decides how inputs from different senders interleave.

## Process managers

A process manager follows events of other streams and sends commands to actors in reaction, e.g. "a run's step finished, so the session continues" (§8.1). Its logic is pure:

```fsharp
type ProcessManager<'In, 'S, 'Out> =
    { Initial: 'S; Evolve: 'S -> 'In -> 'S; React: 'S -> 'In -> 'Out list }
```

`ProcessManager.start` runs it with:

- **sources** (`Source.ofLog log stream select`), which map events to inputs;
- **its own stream**;
- **`Deliver`**, which sends an output to an actor with the delivery's key (`Delivery.envelope`).

How it works:

1. **Its own actor.** The process manager is a machine (`ProcessManager.machine`) run as an actor on its own stream.
   - `Observe (source event, input)` appends `Observed`. Its evolution folds the input, advances the source's checkpoint, and puts the commands `React` returns in an outbox.
   - `Acknowledge key` appends `Delivered` and clears the delivery.
2. **One pump per source.** A pump reads its source after the checkpoint and sends each input to the actor.
   - The actor records inputs in arrival order, so the state is a fold of one log even across several sources.
   - A source event already observed changes nothing: its position is at or below the checkpoint, and its key is known.
3. **Deliveries** run one at a time, in outbox order.
   - Each is keyed `<process stream>:<source event>/<n>`, the same on every attempt.
   - `Accepted` and `Rejected` acknowledge it. `Stopped` is retried after `RetryDelay` (100 ms, doubling up to 30 s).
4. **On start**, the actor's `Recover` redelivers the outbox, and the pumps resume after their checkpoints.

**Delivery guarantee: at least once, and effectively once.**

- A delivery leaves the outbox only once its target has handled it.
- After a crash, everything still in the outbox is delivered again, with the same key, so the target answers with its first outcome and appends nothing.
- Deliveries keep their order, except that a delivery waits for an earlier one that is being retried.

| Crash | Recovery |
|---|---|
| after a source event, before `Observed` | the pump reads it again after the checkpoint |
| after `Observed`, before the delivery | the outbox still holds the delivery; it is delivered on start |
| after the delivery, before `Delivered` | delivered again; the target deduplicates it by key |

## Testing

- `tests/Fx.Machines.Tests`:
  - the pure helpers, with property tests (the state after any commands is the replay of their events);
  - the Fable architecture test.
- `tests/Fx.Machines.Host.Tests`:
  - `EventLogConformance`;
  - the actor's guarantees, one test each, on a session-like `Chat` machine;
  - process managers, and the registry.
- **Determinism and replay** (`DeterminismTests`):
  - FsCheck generates scripts of sends from several clients, resends of earlier keys, interrupts, closes, clock advances and restarts;
  - each script runs twice, with instant appends and with appends that take a second (so inputs queue up behind them);
  - the two runs must give identical logs, outcomes and traces;
  - every run is checked: sequence numbers count from 1, each command's events stay together and are attributed to its sender, the actor's state is the fold of its log, and duplicate keys get the first outcome as in the table above.
- **Native AOT.**
  - `machines-actor` covers the shell loop, duplicates, rejections, the control path, a crash and a rebuild with `Recover`, and a subscription.
  - `machines-process-manager` covers a process manager across two streams, with a restart.
  - Both run on the JIT and as a native binary on every target.

## Left for later

| What | Task |
|---|---|
| The durable event store (SQLite and blobs) passing `EventLogConformance`; snapshots, so a long stream does not replay from 1 | T105 |
| The session machine on this host: steer/queue, durable pending requests, the client protocol | T125 |
| Principals behind `Attribution.Principal` | T122 |
| `OutcomeUnknown` through `Recover` | T128 |
| Retry delays and page sizes as variants | T106 |
| Journaling intents for exact replay of a session | T131 |
| Epoch-fenced takeover leases; the actor already stops on a conflicting append | T507 |
| Bounding the key index; evicting idle actors from a registry | when a long-lived stream needs it |
