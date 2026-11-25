# Record and replay (`Fx.Replay`)

Spec: §15 (variants recorded, profiles, eval modes: exact replay; `fx replay`), §4.2 (no I/O outside
`Eff`, so every effect can be journaled; `ReplayEnv`), §5 (sessions, the step journal), §6.5
(durability), §18 (secrets are never journaled). Task: T131. Builds on [eff.md](eff.md),
[codeact.md](codeact.md), [sessions.md](sessions.md), [store.md](store.md), [variants.md](variants.md),
[models.md](models.md) and [kernel-protocol.md](kernel-protocol.md).
Code: [`src/Fx.Replay`](../../src/Fx.Replay) (pure apart from the capabilities it is handed: the EFF
analyzers apply to every file).
Tests: `tests/Fx.Replay.Tests` and the `replay-session` scenario of the JIT-vs-native diff.

A recorded session can be run again exactly: every turn runs again on the CodeAct strategy with an
environment that answers from a journal of what the world answered the first time (the model's
responses, the kernel process's output, files, commands, the clock, the session's inputs, people's
answers), so nothing touches the world, and what the turn writes and asks is checked against the
recording. The first difference is the report.

| Piece | Where |
|---|---|
| The journal: entries, outcomes, segments, their JSON (version 1), digests | `Journal.fs` (`JournalEntry`, `Outcome`, `Stored`, `JournalEvent`, `JournalCodecs`, `JournalLog`, `Digest`) |
| The JSON of journaled values and the inputs whose digests are kept | `Values.fs` |
| A session's journal in a host process; the recorder | `Recorder.fs` (`SessionJournal`, `Recorder`, `RecorderSettings`) |
| Recording interpreters | `Record.fs` (`Record.clock`, `ids`, `llm`, `shell`, `fsRead`, `fs`, `fsOps`, `snapshots`, `secrets`, `variants`, `log`; `Ops`, `Inputs`, `Lanes`, `Pull`) |
| The recorded strategy, `session.record` | `Recording.fs` (`Recording.recorder`, `Recording.codeact`, `RecordedCodeAct`, `Wraps`, `HostTaps`, `HandsTaps`, `HandsInfo`, `Markers`, `RecordingSlots`) |
| Replaying a segment: matching, gates, divergences | `Replayer.fs` (`ReplaySegment`, `ReplayEntry`, `Gate`, `Found`, `Divergence`, `ReplayedFailure`) |
| `ReplayEnv` and the replay interpreters | `Replay.fs` (`ReplayTurnEnv`, `ReplayHandsEnv`, `Replay.clock`, `ids`, `llm`, `shell`, `fsRead`, `fs`, `fsOps`, `snapshots`, `secrets`, `variants`, `store`) |
| Exact replay of a session, the report | `SessionReplay.fs` (`SessionReplay.run`, `segments`; `RecordedSession`, `ReplaySettings`, `ReplayReport`) |
| `fx replay <session>` | `ReplayCommand.fs` (`ReplayCommand.parse`, `run`, `output`, `reportCodec`) |

## The journal

Each session has a stream `journal/<session>` in the host's event store (version 1 of
`JournalCodecs.event`; `tests/Fx.Replay.Tests/golden/journal-events-v1.jsonl` is what it wrote, kept,
never regenerated). A **segment** is one host process recording the session: `began`, batches of entries
(`recorded`), and `ended` when the host closed cleanly (a crash leaves a segment without it).

An entry is one capability call, or a marker:

| Field | |
|---|---|
| `seq` | its place in the segment's order: when it **completed** for calls that bring something from the world (reads, elapsed delays, answers), when it **started** for calls that act on the world (`Proc.Write`, `CloseStdin`, `Kill`), since the world may react before the call is seen to complete |
| `lane`, `index` | which capability instance it called, and in which order calls of that lane started |
| `op` | the operation (`Ops`: `Clock.Now`, `Llm.Stream`, `Proc.Read`, `Log.Append`, `Turn.Boundary`, …) |
| `input` | a digest of its arguments (the first 128 bits of the SHA-256 of their canonical JSON), never the arguments |
| `summary` | what the call was, bounded (160 characters), for reports |
| `outcome` | `value` (inline JSON up to 1 KiB) or `blob` (a `BlobRef`: larger values go to the blob store, so equal values are stored once), `threw` (the exception's type, never its message), `cancelled`, `pending` (running when the host closed) |

Lanes of a session: `turn/<n>` (the turn's clock and model, and its calls to the session:
`Turn.Boundary`, `Turn.Open`, `Turn.Await`), `turn/<n>/stream/<i>` (a model response's pulls, one entry:
`Stream.Pulls`), `hands` (the environment's clock, files, envd's file operations, commands, spawns,
snapshots, `Hands.Guard`), `hands/proc/<i>` (each spawned process: writes, reads, wait, kill), `store`
(the event store as the strategy uses it), `variants` (`Profile`, each slot's first read under each
profile), `hands.open` (the hands each turn got), `strategy`, `turns` (`Turn.Began`, `Turn.Cancelled`,
`Turn.Ended`) and `host` (`Host.Closing`, `Host.Closed`).

The recorder buffers entries and appends them in batches of at most 256 entries (events stay well under
1 MiB): at the end of every turn, before a call once the buffer holds 2048 entries or 8 MiB, and when
the host closes. A crash loses what was not flushed: that turn replays up to the last flush.

## Recording

The composition root (T129) builds the strategy with `Recording.codeact` instead of `CodeAct.create`:

```fsharp
let recorder = Recording.recorder store.Log blobs.Blobs SystemClock.clock    // journals, flushes
let strategy =
    Recording.codeact recorder
        { Host  = fun taps env -> { env with Clock = taps.Clock env.Clock; Llm = taps.Llm env.Llm }
          Hands = fun taps env -> { env with Clock = taps.Clock env.Clock; FsRead = taps.FsRead env.FsRead
                                             Fs = taps.Fs env.Fs; FsOps = taps.FsOps env.FsOps
                                             Shell = taps.Shell env.Shell } }
        codeActConfig                                  // as for CodeAct.create
let! host = SessionHost.create sessions snapshots strategy.Runner settings
…
do! SessionHost.stop host                              // turns end (their cancellation is journaled)
do! strategy.Close()                                   // kernels close, journals end their segments
```

- `Wraps` puts the recording interpreters (`HostTaps`, `HandsTaps`) into the composition root's own
  environment types: **every capability the strategy's environments have must be replaced**, or replay
  meets calls it has no journal for (the replay tests catch that). Register `RecordingSlots.all` with the
  host's registry, as `PromptSlots.all`.
- Each session gets its own CodeAct instance, with the session's journal in its configuration: the
  store's logs (`Record.log`: appends by the digest of what they wrote, reads by how much they saw),
  `Profile` (each slot's first read), `Hands` (the hands' description, `HandsInfo`: workspace, OS,
  language, whether there are snapshots, the commands' variables by name; their environment, guard and
  snapshots wrapped).
- The runner journals each turn: `Turn.Began` with its `TurnStart`, its host environment (lane
  `turn/<n>`), its calls to the session, **its cancellation before the turn sees it** (an interrupt or
  the host stopping: `Turn.Cancelled`, then the turn's own token is cancelled), and how it ended
  (`Turn.Ended`); then a flush.
- Kernels are processes of the hands' shell (`Kernel.start` through `Shell.Spawn`), so their protocol is
  journaled as the bytes written to and read from the process. An interpreter whose kernel is not a
  process of the shell is not recorded (the tests run the script kernel as one: `ScriptProc`).
- **`session.record`** (`on` | `off`, default `on`) is read at a session's first turn in a host process
  (its default when the host did not register the slot); a session not recorded runs on a plain CodeAct
  instance. Exact replay needs every segment recorded.

### Secrets are never journaled (spec §18)

- Inputs are digests, and a process's variables enter even those by **name only**: the values commands
  and kernels get may hold credentials, so neither they nor their digests are journaled (`HandsInfo` keeps
  names too).
- `Record.llm` sits above `ModelAccess`: it sees the canonical request (a digest) and the events, never
  the wire's headers or credentials.
- `Record.secrets` journals a resolution's reference and outcome, never the value; replay resolves to an
  empty value (nothing replayed sends it anywhere).
- `IHttp`, `IKeyring`, `IPrivateFiles` and `IHostVariables` are not capabilities of a session's
  environments and have no recorders.
- `SecretTests` proves it: a credential the wire adds, a secret in commands' variables and a resolved
  secret reach the provider and the command, and none of them (nor its base64 or digest) is in the
  journal or the blob store.

Outputs are journaled as they were: what a command printed or a file held is what the model saw too.
Protect journals like the logs they sit next to.

## Replay

`SessionReplay.run settings recording session` reads the journal's segments and, for each, runs a fresh
CodeAct instance (`CodeAct<ReplayTurnEnv, ReplayHandsEnv>`) whose configuration answers from the segment:

- **`ReplayEnv`**: `ReplayTurnEnv` (clock, model) and `ReplayHandsEnv` (clock, files, envd's file
  operations, shell) implement the capabilities from the journal (`Replay.*`). Kernels start as they did
  (`ReplaySettings.Interpreter`, by the recorded language), through the replayed shell: the replayed
  process gives back what the kernel wrote, so no kernel runs.
- **Matching.** A call matches the first entry of its lane, in start order, with the same operation and
  input digest, so concurrent calls of a lane (host calls of one cell) replay whatever their order. A
  call the journal does not have is a **divergence**: the report names the lane, the entry the recording
  has there and the call replay made. A segment that did not end cleanly is cut where the host stopped:
  calls past its end wait until replay of the segment ends.
- **Order** (`Gate`). Calls whose value does not depend on when it arrives (files, commands, the model's
  events, the store) answer at once. Calls that completed when something else happened (a delay that
  elapsed, a process's output or exit, the session's inputs and answers) complete only once everything
  recorded before them has happened again, so replay keeps the recorded interleaving of concurrent work.
  Calls that were cancelled or still running do not hold anything up: they wait for their own
  cancellation. A delay the recording does not have is a timer that never fired (a timeout's timer may
  or may not start, depending on timing); it waits for its cancellation.
- **External events** are entries the driver acts on once everything recorded before them happened: a
  turn's start (`Turn.Began`: `Run` or `Recover`, with its recorded `TurnStart`), its cancellation
  (`Turn.Cancelled`: replay cancels the turn's scope there, so interrupts and host stops land where they
  did) and the host closing (`Host.Closing`: replay closes the strategy's kernels).
- **The session's log is the input.** Reads of the session's stream show exactly what the recorded reads
  saw (`Log.Read` keeps how many events); the turn's calls to the session (`Boundary`, `Open`, `Await`)
  answer as recorded and their arguments are checked, and each turn's result is checked against
  `Turn.Ended`. The session machine itself is not run again: it is pure, and its own tests replay it.
- **What replay writes is checked.** Context and step events are written again into a fresh store and
  checked as they are appended (the digest of what the recorded append wrote: the report shows the
  recorded event with the same key and the replayed one), and once more at the end, event by event (seq,
  time, sender, key, cause, payload). Values replay keeps go to a fresh blob store over the recorded one
  and must exist in the recorded one. Model requests are checked by digest: byte for byte, canonical.

`ReplayReport` gives the counts (segments, turns, entries, model requests, context and step events), the
first `Divergence` (`Segment`, `Lane`, `Expected`, `Actual`, `Reason`) and the journal entries replay
matched last (`Trace`). A replay that makes no progress for `StallAfter` reports the first entry it never
reached.

### What code must do so that it replays

Replay re-runs the code; it can only take the recorded path if the code's path depends on what the
capabilities answered, not on timing:

- **Decide from data, not from which task finished first.** CodeAct's interrupt (T128's code) used to
  decide whether a killed kernel was closed from whether its connection had already ended; it now decides
  from what the interrupt did (`SessionKernel.Run`).
- **Calls that act on the world are numbered when they start** (`SessionJournal.StartActing`,
  `RunActing`): a process can answer a write before the write is seen to complete.
- **Every capability call goes through a recorder.** A new capability is recordable before a session
  uses it: a `Record.x`/`Replay.x` pair, its operations in `Ops`, its values' codecs, and a test that
  records and replays it.

### Limits

- Exact replay is of the strategy: the session machine is not re-run, and the session's own log is the
  input (above).
- A crash loses the journal's unflushed tail; that turn replays up to the last flush, and the logs it
  wrote after it then show as a divergence.
- `FsOps.Watch` and terminals (`ITerminal`) are not recorded: no session uses them yet (a recorded
  `Watch` refuses).
- Two concurrent calls of one lane with the same input and different outcomes may swap: two delays of
  the same length, one elapsed and one cancelled, or two callers that resume at the same moment and read
  the clock (`Clock.Now` has no input). Replay keeps the order calls completed in, not which caller made
  them.
- Replaying elsewhere needs the same launches: a kernel's launch spec (its program, directory and
  variables' names) is part of the spawn's input. The script kernel's (`ScriptProc`) is the same on every
  machine; a Bun kernel's names its install root.

## The replay test harness

- `Harness.recordsAndReplays name fixture record check` (tests/Fx.Replay.Tests) records a session on the
  real session host with the recorded strategy, closes the host and replays it exactly;
  `Harness.replays name recording session` replays a recording given. `REPLAY_DUMP=<dir>` writes a
  diverging test's journal there.
- `SessionTests`: cells-only and hybrid turns, a steer, an interrupted cell whose kernel lives on, a
  killed cell and a new kernel, a question answered into the next turn, model retries, and a host stopped
  mid-command whose turn is recovered after a restart, decided (`rerun`) and followed by a recall.
- **The golden recorded session** (`golden/session-v1.jsonl`: every stream's events and every blob of a
  two-turn hybrid session, exported by `Exported.write`) must keep replaying with today's code, on every
  OS. When it diverges, the code changed what fx does with the same world. If that is intended, record it
  again deliberately — `RERECORD_GOLDEN=1 dotnet test --project tests/Fx.Replay.Tests` — and review the
  diff and the reason in the pull request. `UPDATE_GOLDEN=1` does not touch it.

## `fx replay <session>`

`ReplayCommand.run settings recording args` parses `replay <session> [--json] [--stall <seconds>]`,
replays, and gives what to print and the exit code: 0 matched, 1 diverged (or not recorded), 2 invalid
arguments; `--json` writes the report with `ReplayCommand.reportCodec`. The host's entry point (T129)
dispatches `replay`, opens the host's stores read-only (`SqliteEventStore`, `FileBlobStore`), builds
`ReplaySettings` (the interpreters by language from the extension registry, the model catalog) and prints
the output.

## Partial replay and counterfactual forks (design)

Not built yet (T217); the journal supports both:

- **Partial replay**: replay up to a turn or a step, then go live. A replay interpreter that answers from
  the journal until its lane's entries are used and then calls a live one (`Replay.orLive`), and a
  driver that stops firing markers at the chosen point; the logs written so far are checked as now.
- **Counterfactual forks**: fork the session at an event (`Command.Fork`), then run the child live under
  another profile (`Profile.layer` for selections, variants.md); the parent's prefix is replayed (or read)
  rather than run.

## Cost

Measured with `REPLAY_MEASURE=<file> dotnet test --project tests/Fx.Replay.Tests` (2-core Linux x64 VM,
in-memory stores): three turns of twenty cells, each reading a 4 KiB file and printing 2 KiB.

| | `session.record` off | on |
|---|---|---|
| Recording the session (after warm-up) | 145 ms | 350–385 ms |
| The session's logs (session, context, step events) | 697 KB | 697 KB |
| Journal events | — | 395 KB, about 2,230 entries (37 a cell) |
| Journal blobs | — | 894 KB in about 235 blobs |
| Exact replay | — | 345 ms, matched |

- About 4 ms and 20 KB a cell here, against in-memory stores and a kernel in the process: nothing next to a
  model call (seconds) and a real kernel's work. So **`session.record` is on by default**; switch it off
  per session or in host config where journals are not wanted.
- The journal is about twice the session's logs: mostly model responses and process output over 1 KiB,
  stored by hash, so equal values are kept once (the sixty reads of the same file are one blob). Model
  requests cost a digest each: they are rendered from the logs (prompt.md).

## Native AOT

`replay-session` records a CodeAct turn (the script kernel as a process of the shell), replays it
exactly, replays it with a changed prompt (it diverges) and reads the report back through its JSON, on
the JIT and as a native binary on every target. The module uses no reflection.

## Left for later

| What | Task |
|---|---|
| Partial replay, counterfactual forks, eval runs over recorded sessions | T217 |
| Re-running the session machine with its inputs re-delivered (full-host replay) | T217 |
| `fx replay` in the binary (stores, interpreters, catalog) | T129 |
| Recording terminals and watches | when sessions use them |
| Replaying a run (goals, workflows) from its journal | T212 |
