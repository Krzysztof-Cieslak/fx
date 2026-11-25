# Event and blob stores (`Fx.Store`, `Fx.Store.Live`)

Spec: §5 (with §3, §4.4, §6.6, §12.5, §15 and §18). Task: T105. Builds on the event-log abstraction
of [machines.md](machines.md), the codecs of [contracts.md](contracts.md) and the SQLite binding of
[docs/spikes/sqlite.md](../spikes/sqlite.md).
Code: [`src/Fx.Store`](../../src/Fx.Store) (pure: typed events, snapshots, the blob capability, fakes)
and [`src/Fx.Store.Live`](../../src/Fx.Store.Live) (the real stores: SQLite and files).
Tests: `tests/Fx.Store.Tests`, `tests/Fx.Store.Live.Tests` (with the child process in
`tests/support/Fx.Store.Writer`), and the `store-*` scenarios of the JIT-vs-native diff.

| Piece | Pure (`Fx.Store`) | Interpreters |
|---|---|---|
| Event log | `EventLog<'Evt>` (Fx.Machines.Host); `Payload`, `EventSchema`, `EventLog.typed` | `SqliteEventStore.Log`; `InMemoryEventLog` |
| Snapshots | `SnapshotStore`, `SnapshotSchema`, `Snapshots`, `Projection`, `ActorSnapshots` | `SqliteEventStore.Snapshots`; `InMemorySnapshots` |
| Blobs | `Blobs`, `IBlobs`, `BlobHash`, `BlobRef`, `BlobWriter` | `FileBlobStore`; `InMemoryBlobs` |
| Wire | `Codecs.recorded`, `payload`, `attribution`, `eventRef`, `blobRef`, `instant` | |

Only composition roots (the host, envd, tests, tools) reference `Fx.Store.Live`. They decide where
data lives and pass absolute paths; deriving the data directory from `Fx.Product` is the host's job
(T129).

## Typed events

```fsharp
type Payload = { Version: int; Data: Json }          // what a store keeps: canonical JSON + schema version
type Upcaster = Json -> Result<Json, string>          // version n → n + 1, pure
type EventSchema<'Evt> = { Version: int; Codec: Codec<'Evt>; Upcasters: Upcaster list }

let schema =
    EventSchema.create Note.codecV1                   // version 1
    |> EventSchema.evolve renameTextToBody Note.codec // version 2: v1 JSON → v2 JSON, then the new codec

let log: EventLog<Note> = EventLog.typed schema store.Log
```

- **Stores keep payloads.** A store is an `EventLog<Payload>`; `EventLog.typed` gives the typed view
  an actor or a projection uses. Appends encode with the current version as canonical JSON
  (`Json.canonicalize`). Reads upcast from the payload's version, one upcaster at a time, then decode.
- **Committed events are never rewritten** (spec §12.5). Every read of an old event runs the
  upcasters again, so they must be pure and total over what the old version wrote.
- **When to bump the version.** When the JSON of an existing event changes shape or meaning. Adding a
  union case or an optional field needs no new version: the codec reads old JSON as it is. The event
  type may change with the version (`evolve` takes the new codec; the old one is no longer needed).
- **An event no version can read is a defect**: the read fails with `UnreadableEventException`
  (stream, sequence number and why: a newer version, an upcaster's error, or the codec's issues).
  `Evolve` must accept every event `Handle` ever decided ([machines.md](machines.md)); this is the
  same rule one level down.
- **Test upcasters** by keeping payloads that old versions wrote and reading them with the current
  schema (`EventSchemaTests`, the `store-events` scenario). Never delete such a test.

## The SQLite event store (`SqliteEventStore`)

```fsharp
match SqliteEventStore.openFile path EventStoreOptions.defaults with
| Ok store -> ...   // store.Log : EventLog<Payload>, store.Snapshots : SnapshotStore
| Error error -> ... // OpenError: the database (a SqliteError), a newer schema, an invalid path
```

One database file holds every stream of a host and their snapshots. It passes `EventLogConformance`
and `SnapshotsConformance`, the suites the in-memory interpreters pass.

### Schema (version 1, `PRAGMA user_version`)

| Table | Columns | Why |
|---|---|---|
| `streams` | `id`, `name` UNIQUE, `head` | the head check reads one row; events store a small integer instead of the name |
| `commits` | `(stream, first_seq)` PK, `count`, `key`, `at` (µs), `principal`, `via`, `cause_stream`, `cause_seq`; UNIQUE `(stream, key)` | what the events of one command share is stored once; the unique key makes deduplication survive restarts. `WITHOUT ROWID`: small rows |
| `events` | `position` (rowid), `stream`, `seq`, `commit_seq`, `version`, `data` (canonical JSON text); UNIQUE `(stream, seq)` | a rowid table because event rows are mostly larger than 1/20 of a page, where SQLite advises against `WITHOUT ROWID`; `position` is the order of appends across streams, kept for a future all-streams feed |
| `snapshots` | `(stream, name)` PK, `seq`, `version`, `state` (canonical JSON) | one snapshot per stream and view |

- Tables are `STRICT`. JSON is text, so a person can read the database with any SQLite shell.
- Opening migrates an empty database in one `BEGIN IMMEDIATE` transaction (checking the version again
  inside, in case another process migrates at the same time). A database of a newer schema version
  is refused with `OpenError.NewerSchema`: it may hold what this code would misread.

### Writing: one writer, group commit

- A dedicated thread owns the only connection that writes. `Append` checks its arguments, serializes
  the events on the caller's thread, and queues the commit; `Snapshots.Save` and `Checkpoint` queue
  too. Nothing else writes the database.
- The writer takes everything queued (up to `MaxBatch`, 256) and commits it in **one `BEGIN IMMEDIATE`
  transaction** (group commit): one sync for the whole batch. Each append in it runs its checks in
  order, so two appends to one stream in a batch behave as if sequential.
- **Checks, in order**: a commit with the same key in the stream → `Duplicate (key, its events)`;
  otherwise a head other than `expected` → `Conflict`; otherwise insert the commit, its events, and
  move the head. A rejected append writes nothing; the others in the batch are unaffected.
- **Failures.** If a batch fails as a whole (a full disk, an I/O error, `Busy` past the timeout), each
  request runs again in its own transaction, so one bad request fails only itself. A failure is a
  defect: the caller's task faults, and an actor stops and rebuilds from the log (its retry with the
  same key is deduplicated).
- Replies complete with `RunContinuationsAsynchronously`: callers continue on the thread pool, never on
  the writer thread, so no caller can block the writer.
- An append whose token is cancelled before the writer takes it is not written; once in a transaction
  it completes.
- **Size.** An event's JSON may not exceed `MaxEventBytes` (1 MiB): large content belongs in the blob
  store, and the event carries a `BlobRef` (`Codecs.blobRef`). JSON nested deeper than the reader's
  limit (128, `ReadOptions.defaults`) is refused too, for events and snapshots alike: everything
  committed must read back, or a stream could never be replayed again.

### Reading and waiting

- `Read` and `Head` run on the caller's thread with a pooled reader connection (`query_only`). In WAL
  mode readers never wait for the writer or each other. Idle readers are kept up to `Readers` (4);
  more are opened when needed. Cancellation interrupts a running query.
- `WaitPast` registers the waiter **before** it reads the head: a commit after the read wakes it, a
  commit before it shows in the read. The writer wakes the waiters of each stream whose head moved,
  after the commit.
- `EventLog.subscribe` (spec §5 `subscribe(afterSeq)`) is built on `Read` and `WaitPast`: it replays
  in pages and then follows live, without gaps or repeats; a property test appends from another
  thread while subscribers start at random positions.

### One process per store

A store is meant to be owned by one process (the host). Two connections or processes writing one file
stay correct (the head and key checks run inside the write lock, as the two-process test shows), but
commits from another process wake no waiters here: they are seen only by the next read. Following
another process's writes would need `PRAGMA data_version` polling.

### Durability

`Durability` is a store-wide choice by the composition root (to become a variant slot, read by the host, T129):

| | `Full` (default) | `Normal` |
|---|---|---|
| SQLite | `synchronous=FULL`: the WAL is synced on every commit | `synchronous=NORMAL`: synced at checkpoints |
| Blob files | synced before the rename | not synced |
| Process crash | nothing committed is lost | nothing committed is lost |
| OS crash, power loss | nothing committed is lost (see macOS) | the last commits may be lost; the database stays consistent |

- `Full` is the default because the spec's promise is "never loses work", and group commit recovers
  most of its cost under load (see Performance).
- **macOS**: `fsync` leaves data in the drive's cache, so power loss can still take the last commits.
  SQLite's `fullfsync` would close that gap at a large cost per commit; it is not set (left for a
  variant).
- Checkpoints: SQLite checkpoints every 1000 pages on its own; `Dispose` runs a `Truncate` checkpoint,
  and `store.Checkpoint mode` lets the host run one when idle.

### Crash consistency

`CrashTests` start `tests/support/Fx.Store.Writer` as a child process, let it append commits of one to
four events in a loop, and kill it wherever it is, three times (with both durabilities), reopening the
database in between. Every time, `PRAGMA integrity_check` passes, sequence numbers count from 1
without gaps, every commit is whole and they follow each other, every commit the writer reported is
there, and the last key is still known (`Duplicate`). A second test runs two writer processes against
one stream at once: every commit lands once, whole, in each writer's order.

## Snapshots

```fsharp
type StoredSnapshot = { Seq: int64; Version: int; State: Json }
type SnapshotStore = { Save: StreamId -> string -> StoredSnapshot -> CancellationToken -> Task<unit>
                       Load: StreamId -> string -> CancellationToken -> Task<StoredSnapshot option> }
type SnapshotSchema<'S> = { Name: string; Version: int; Codec: Codec<'S> }
```

- A snapshot is a cache of a fold: the log stays the truth, and any snapshot can be dropped. One is
  kept per stream and **name** (the actor's state, a client's view, …).
- **Versions.** Bump a snapshot schema's version whenever an old snapshot would be read wrongly: the
  state's shape changed, or its meaning (`Evolve` changed). A snapshot of another version is ignored
  (`SnapshotLoad.OtherVersion`) and the state is rebuilt from the log, never mis-decoded. One the codec
  cannot read is ignored too (`Unreadable`).
- `Save` keeps the snapshot with the later sequence number for the same version (concurrent savers
  never go back); a snapshot of another version always replaces the one kept.
- `Projection` (`Initial`, `Evolve`, a `SnapshotSchema`; `Projection.ofMachine` for a machine's state):
  - `load`: the latest readable snapshot (unless it is past the head), then the events after it;
  - `refresh log snapshots projection every stream`: loads, and saves a new snapshot when at least
    `every` events were folded after the last one;
  - `subscribe … maxGap stream after handle`: spec §5's snapshot + tail. A subscriber more than
    `maxGap` events behind gets `Feed.Reset snapshot` (when a snapshot past its position exists), then
    `Feed.Next` for each event after it; otherwise just the events. Then it follows live.
- `ActorSnapshots`: `latest` is the snapshot an actor starts from (the latest readable one at or below
  the head), `start` starts the actor from it (`Actor.startFrom`, [machines.md](machines.md)), and `save`
  stores `Actor.snapshot`, which is always at a commit boundary. Keys of commits before the snapshot are
  looked up in the log (`EventLog.FindCommit`: the `commits` table's `(stream, key)` index).
- In `SqliteEventStore`, snapshots live in the same database as the events they fold, and their saves
  go through the writer like appends.

## Blobs

```fsharp
type Blobs =
    { Put: byte array -> CancellationToken -> Task<BlobRef>
      Get: BlobHash -> CancellationToken -> Task<Result<byte array, BlobError>>     // checked against the hash
      Read: BlobHash -> int64 -> int -> CancellationToken -> Task<Result<byte array, BlobError>>
      Stat: BlobHash -> CancellationToken -> Task<BlobRef option>
      Create: CancellationToken -> Task<BlobWriter> }                               // chunked writes
type IBlobs = abstract Blobs: Blobs
```

- A blob is immutable content named by its **SHA-256**, written `sha256:<64 lowercase hex digits>`
  (`BlobHash`). `BlobRef` is the hash and the size; events and values (spec §6.6) refer to blobs by it.
- **Large blobs** stream in both directions without breaking the capability rules (data in, data
  out, no streams): `Create` returns a `BlobWriter` (`Write` chunks, `Commit`, `Abort`), a record of
  functions like a process's `Proc`; `Read` returns a range. `Blobs.putChunks` writes a sequence of
  chunks and aborts on failure.
- Errors: `BlobError.NotFound` and `BlobError.Corrupt` (the content no longer matches its hash).
  Disk failures are defects. `Get` of a blob larger than an array can hold is a defect; use `Read`.
- Interpreters: `InMemoryBlobs` and `FileBlobStore` pass `BlobsConformance`.

### `FileBlobStore`

```fsharp
FileBlobStore.openAt root clock ids durability : Result<FileBlobStore, OpenError>
```

| Path | What |
|---|---|
| `<root>/sha256/ab/cdef…` | the blob `sha256:abcdef…` (the first two hex digits fan out the directory) |
| `<root>/tmp/<random>.part` | blobs being written (`ids` names them) |
| `<root>/index.db` | SQLite: `blobs (hash PK, size, created µs)`, `WITHOUT ROWID` |

- **Atomic writes.** Content goes to a temporary file, is synced (`Durability.Full`), and is renamed
  into place without overwriting. So a blob file is always complete; two writers of the same content
  race harmlessly (the loser deletes its file). Content that is already stored is not written again.
- **The files are the truth.** `Get`, `Read` and `Stat` look at files; `Get` re-hashes the content and
  reports `Corrupt` if it changed. `Put` trusts an existing file of the right size, replaces one of
  the wrong size, and restores a missing one. The directory entry of a rename is not synced (.NET has
  no API for it), so after power loss a blob indexed just before may be missing: it is `NotFound` until
  it is put again.
- **The index** lists every blob with its size and when it was stored (`clock`), for enumeration
  (`store.Index()`) and the garbage collector that will need it (references from events, sessions and
  values, plus a grace period for blobs not referenced yet). Storing known content only reads it.
- **One owner per root**: opening a root deletes the temporary files that crashed writers left.
- **Paths** are built with `Path` APIs; names are lowercase hex, so case-insensitive filesystems are
  fine. A blob's path is about 75 characters longer than the root; .NET handles Windows paths over 260
  characters (tested), but keep the root short enough that `index.db`'s path stays under 260
  characters, which SQLite needs on Windows.

### What lives in SQLite, and what in files

| Data | Where | Why |
|---|---|---|
| Events, commits, heads, keys | SQLite (`events.db`) | appended in transactions with checks; small; read by ranges |
| Snapshots | SQLite, beside the events | saved and read with the log they fold |
| Blob content | files | large and streamed; immutable and named by hash, so a rename is the only commit needed; can be copied, served or hard-linked as files; survives a damaged index |
| Blob index | SQLite (`index.db`) | enumeration and garbage collection need queries; losing it loses no content |

## Wire codecs (`Fx.Store.Codecs`)

`Codecs.recorded eventCodec` writes an event with its envelope, flat:
`{"stream", "seq", "event", "at" (µs since the epoch, an integer: exact in JavaScript), "by":
{"principal", "via"}, "key", "cause"?: {"stream", "seq"}}`. With `Codecs.payload` as the event codec it
is the stored form (`{"version", "data"}`), used to export logs and to send events to clients (T107,
T125). `Codecs.blobRef` is `{"hash": "sha256:…", "size"}`.

## Testing

- **Conformance**: `EventLogConformance` (in-memory, typed over in-memory, SQLite),
  `SnapshotsConformance` and `BlobsConformance` (fakes and real stores), on Linux, macOS and Windows.
- **Schemas**: upcasting chains, unreadable events, payloads never rewritten (read back raw from
  SQLite).
- **Store**: reopening, keys surviving restarts, two stores on one file, concurrent writers, group
  commit across many streams, limits, cancellation, closing, refusing newer schemas and non-databases,
  snapshot versioning through a projection.
- **Properties**: subscribe hands over from replay to live without gaps or repeats while another
  thread appends.
- **Processes**: a writer killed mid-append, and two writer processes on one stream (above).
- **Native AOT**: `store-events` (appends, duplicates, conflicts, limits, reopening, upcasting, the
  stored envelope, an unreadable event), `store-subscribe` (a subscriber and a writer thread),
  `store-snapshots` (refresh, versions, snapshot + tail) and `store-blobs` (puts, chunked writes,
  ranges, the index, damage) run on the JIT and as a native binary on every target.

## Performance

`dotnet run -c Release --project tools/Fx.Store.Bench` measures appends (one writer waiting for each
commit; 32 writers on their own streams at once), reads of a 10,000-event stream, the time from an
append until a waiter wakes, and blob throughput, with both durabilities. Events are about 200 bytes
of JSON. The `Store benchmark` workflow runs it on idle GitHub runners whenever the stores change
and writes the tables to its job summary; a busy development VM measures mostly its scheduler.

From the workflow on GitHub-hosted runners (`ubuntu-24.04`, `macos-26`, `windows-2025`), November 2026:

| Appends: commits/s (p50 latency) | Linux | macOS | Windows |
|---|---:|---:|---:|
| one writer, 1 event per commit, full | 2,484 (408 µs) | 2,885 (261 µs) | 137 (4.7 ms) |
| one writer, 1 event per commit, normal | 10,177 (68 µs) | 7,953 (71 µs) | 1,278 (61 µs) |
| one writer, 10 events per commit, full | 2,242 (475 µs) | 2,393 (356 µs) | 171 (4.9 ms) |
| one writer, 10 events per commit, normal | 6,128 (136 µs) | 5,070 (133 µs) | 717 (98 µs) |
| 32 writers at once, full | 9,934 (660 µs) | 11,388 (422 µs) | 823 (9.7 ms) |
| 32 writers at once, normal | 18,512 (369 µs) | 25,462 (159 µs) | 2,260 (505 µs) |

| Reads: p50 (p99) | Linux | macOS | Windows |
|---|---:|---:|---:|
| head | 4 µs (5) | 2 µs (3) | 3 µs (5) |
| one event | 36 µs (68) | 20 µs (47) | 28 µs (68) |
| a page of 100 events | 354 µs (728) | 236 µs (825) | 237 µs (352) |
| all 10,000 events | 34 ms | 40 ms | 24 ms |
| from an append until a waiter wakes (normal) | 91 µs (216) | 108 µs (10,194) | 77 µs (2,141) |

| Blobs | Linux | macOS | Windows |
|---|---:|---:|---:|
| put 1 MiB blobs, full | 394 MiB/s | 225 MiB/s | 42 MiB/s |
| put 1 MiB blobs, normal | 932 MiB/s | 660 MiB/s | 475 MiB/s |
| put content already stored | 1,461 MiB/s | 1,957 MiB/s | 1,262 MiB/s |
| get (hash checked) | 798 MiB/s | 1,105 MiB/s | 756 MiB/s |
| put an 8-byte blob, p50, full / normal | 929 / 130 µs | 1,461 / 198 µs | 12.9 / 0.7 ms |

What this decides:

- **`Full` stays the default.** It costs one sync per transaction: about 0.3–0.5 ms on the Linux and
  macOS runners (macOS without `fullfsync`) and about 5 ms on Windows (`FlushFileBuffers`). A session
  appends a few events per second, so even Windows' 137 commits/s for one waiting writer leaves ample
  room, and nothing committed is lost to an OS crash.
- **Group commit pays for it under load**: 32 concurrent writers get 4 to 6 times the single-writer
  commit rate with `Full`, because one sync covers a whole batch.
- **Reads are cheap.** Replaying 10,000 events, JSON parsing and decoding included, takes 25–40 ms, so
  snapshots matter for streams far longer than that, and for clients far behind. A subscriber hears of
  an append about 0.1 ms after it commits.
- **Blob writes with `Full` sync twice** (the file, then the index), which dominates small blobs,
  especially on Windows. Code that stores many small blobs (tool output overflow) may want `Normal`
  for its blob store or batched index rows; that is a variant to measure when such code exists.
- On the shared development VM (2 cores, load average above 10 from other builds) the same benchmark
  is dominated by thread scheduling: hand-offs to the writer thread take milliseconds there.

## Left for later

| What | Task |
|---|---|
| Blob garbage collection: references from events, sessions and values; a grace period from `created` | with T201 (values) or T131 |
| Journaling capability calls by blob hash, `ReplayEnv` | T131 |
| Client subscriptions over JSON-RPC with snapshot + tail (`SessionHost.subscribe`, [sessions.md](sessions.md)) | T129b |
| `MaxBatch` and page sizes as variant slots (durability and the subscribe gap are the host's: [host.md](host.md)) | when tuned |
| Following another process's commits (`PRAGMA data_version`), if a deployment ever shares a store | when needed |
| `fullfsync` on macOS as a durability level | when measured on real machines |
| Backups and exports (`VACUUM INTO`, `Database.backup`, the envelope codec) | when `fx` exports sessions |
