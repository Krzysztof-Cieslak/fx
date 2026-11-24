# Spike: SQLite under Native AOT (T006)

Spec: §20 (static SQLite), §5 (the event log), §12.5–12.6 (the ledger's working store and full-text
search). Feeds T105 (event and blob store) first, then the grants store (T123) and the ledger's working
store and projections (T301).

## Verdict

**SQLite works under Native AOT, statically linked, on all eight targets.** The `fx` binary carries
SQLite inside: no shared library next to it and none from the system. The JIT loads a shared library
built from the same source with the same options, so tests and the JIT side of the JIT-vs-native diff
exercise the same SQLite. Evidence, all in CI:

- the eleven `sqlite-*` scenarios and the `fx --build-info` case of the JIT-vs-native diff are identical
  on the JIT and natively on `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64` (Alpine
  container), `osx-arm64`, `osx-x64`, `win-x64` and `win-arm64`;
- on every target, `fx` copied alone into an empty directory reports the pinned SQLite version, and no
  SQLite library is published next to it;
- the binding's tests (`tests/Fx.Sqlite.Tests`: round-trip properties, errors, transactions, WAL with
  parallel readers, FTS5, JSON, hardening, copies, interrupts) pass on Linux, macOS and Windows.

No prebuilt package provides what this needs (static libraries for every target, our options, a
current version), so SQLite is built from the pinned amalgamation during the normal build. That needs
a C compiler, which Native AOT needs anyway.

No change to the spec's design. §20 now names how SQLite is built.

## What was built

| Path | What |
|---|---|
| `native/sqlite/Sqlite.props` | The pin: version, source id, URL and digests of the amalgamation; the library name |
| `native/sqlite/library.c` | The compile-time options, then `#include "sqlite3.c"`, then a non-variadic shim for `sqlite3_db_config` |
| `native/sqlite/Sqlite.targets` | Download and verify, compile per RID (static and shared), cache, and wire into the binding and into Native AOT publishes |
| `src/Fx.Sqlite` | The binding: blittable `DllImport`s and safe wrappers (below) |
| `tests/Fx.Sqlite.Tests` | Unit tests, including FsCheck round trips of every value type |
| `tests/aot/Fx.Aot.Scenarios/Scenarios/Sqlite.fs` | The `sqlite-*` scenarios below |
| `src/Fx.Cli` | `fx --build-info`: the version and the embedded SQLite (so `fx` links SQLite now) |
| `.github/workflows/ci.yml` | Caches `artifacts/native`; checks that `fx` carries SQLite |

| Scenario | Proves |
|---|---|
| `sqlite-library` | Version, source id = pin, thread safety, every compile option |
| `sqlite-values` | Edge integers and reals (-0.0, infinities, NaN becomes NULL), text with non-ASCII and NUL characters, empty and 1 MiB blobs (SHA-256), a STRICT table, conversions, changes and rowids |
| `sqlite-errors` | Unique and foreign-key violations with extended codes, SQL errors, missing, non-database and read-only files as results; misuse (argument count, a statement inside itself, NULL read as integer, column range, finalized statement, closed connection) as exceptions |
| `sqlite-transactions` | Commit, rollback on `Error` and on exceptions, savepoints, a second writer gets `Busy` and succeeds later |
| `sqlite-wal` | WAL on and persisted, a reader's snapshot while the writer commits, passive and truncate checkpoints, WAL refused for in-memory databases |
| `sqlite-concurrency` | One writer and three reader threads never see half a transaction; one connection shared by four threads keeps their transactions apart |
| `sqlite-fts5` | bm25 ranking, phrases, prefixes, column filters, NOT, snippets, query errors, an external-content index kept by triggers (with `trusted_schema` off) |
| `sqlite-json` | `->`, `->>`, `json_extract`, `json_each`, `json_set`, JSONB round trips, aggregates, malformed JSON as a result |
| `sqlite-hardening` | No `load_extension`, no double-quoted strings, defensive mode blocks schema writes, the pragma defaults |
| `sqlite-copies` | serialize/deserialize, the online backup, `VACUUM INTO`, garbage deserialized fails at first use |
| `sqlite-interrupt` | A cancelled token interrupts a running query; a token cancelled before is `Interrupt`; an idle interrupt changes nothing |

## How SQLite is built

**Source.** The amalgamation zip from sqlite.org (3.54.1), downloaded by MSBuild (`DownloadFile`, three
retries), checked against the pinned SHA-256 (MSBuild's `GetFileHash` has no SHA3; sqlite.org's SHA3-256
is recorded next to it for bumps), extracted once into `artifacts/native/sqlite/`. A bad download is
deleted and fails the build. Offline: `-p:FxSqliteArchive=<path to the zip>`.

Alternatives considered:

- **Prebuilt packages.** `SQLitePCLRaw.lib.e_sqlite3` 3.53.3 (and the identical `SourceGear.sqlite3` and
  `SQLite` packages) ships static `.a` files for Linux (glibc and musl) only; macOS gets a `.dylib` and
  Windows a `.dll`. Their options are not ours (extension loading, DQS), and they trail releases.
  `StaticLink.e_sqlite3` is a small community wrapper of the same binaries.
- **Vendoring the amalgamation.** About 10 MB per version in git history, for the same guarantee a
  pinned digest gives.

**Compiling.** `library.c` is the single translation unit. On Linux and macOS it is compiled once with
`-O2 -fPIC -ffunction-sections -fdata-sections -DNDEBUG`, and that object becomes both the static
library (`ar`) and the shared library (`-shared`, `-dynamiclib`). `-fPIC` because Native AOT links
position-independent executables; function sections so the linker drops what is unused. On Windows,
MSVC compiles it twice (`/O2 /Gy /Gw /MT /Brepro`): once plainly for the static `.lib`, once with
`SQLITE_API=__declspec(dllexport)` for the DLL, so `fx.exe` does not export SQLite's API.

Compilers are found the way ILCompiler finds its linker: `FxSqliteCC` if set, else `clang`, else `gcc`;
on Windows `vswhere` finds Visual Studio with the C++ tools for the target architecture and `vcvarsall`
sets up the environment (also for cross builds, e.g. `amd64_arm64`). macOS compiles for
`<arch>-apple-macos14.0`, the minimum the .NET 11 ILCompiler targets. Cross-compiling for another
Linux needs `FxSqliteCC` (a cross compiler and sysroot); the build says so instead of producing a
wrong library.

**Wiring.** `Directory.Build.targets` imports `Sqlite.targets` into every project; it acts on two:

- the binding (`FxSqliteBinding=true` in `src/Fx.Sqlite`) builds the libraries for its RID before
  compiling and lists the shared library as content copied to the output of every project that
  references it. Library projects build RID-less, so that is the host's RID, which is what the JIT runs;
- a Native AOT publish whose compiled assemblies include `Fx.Sqlite.dll` builds the static library for
  the publish RID, adds it as a `NativeLibrary`, binds the `DllImport` name at link time with
  `DirectPInvoke`, and removes the shared library from the files to publish (without that removal it
  lands next to the binary).

The library is called `Fx.Sqlite.Native` everywhere (`libFx.Sqlite.Native.a`, `Fx.Sqlite.Native.dll`,
...). `DllImport` searches the application directory first and the system paths after it, so a common
name such as `sqlite3` could silently load a system SQLite on the JIT (macOS always has one). On first
open, the binding also checks the loaded library's version and source id against the pin.

**Caching and reproducibility.** Builds go to
`artifacts/native/sqlite/<version>-<key>/<rid>/{static,shared}/`, where the key hashes the pin,
`library.c` and `Sqlite.targets`. A build runs only when its outputs are missing; nothing compares
timestamps, so the CI cache of `artifacts/native` (keyed by `hashFiles('native/**')`) is reused as is.
Builds compile into a fresh work directory and move the results into place, so concurrent builds never
see half a library. Outputs carry no timestamps or paths (`ar D` / `ZERO_AR_DATE`, `/Brepro`, no debug
information, `NDEBUG`), so the same toolchain gives the same bytes; different runner images have
different compilers, which `COMPILER=` in the compile options shows. A cold build of SQLite takes
about 30–60 s on a CI runner.

## Compile-time options

Set in `library.c`, checked by tests through `PRAGMA compile_options` and the pragmas' values.

| Option | Why |
|---|---|
| `THREADSAFE=1` (serialized) | Finalizers release statements and connections on their own thread, and a connection may be shared; SQLite's mutexes make that safe. `THREADSAFE=0` would make a binding bug memory corruption |
| `OMIT_LOAD_EXTENSION` | No code can load native extensions into the process, whatever SQL it runs |
| `DQS=0` | `"x"` is always an identifier; a typo is an error, not a string |
| `TRUSTED_SCHEMA=0` | A database file cannot make its schema (views, triggers, defaults) call functions with side effects. FTS5 is marked innocuous, so triggers that maintain an index still work |
| `ENABLE_API_ARMOR` | Misuse (null handles, finalized statements) returns `SQLITE_MISUSE` instead of crashing |
| `DEFAULT_FOREIGN_KEYS=1` | Foreign keys are enforced without a pragma per connection |
| `TEMP_STORE=2` | Temporary tables and sort spills stay in memory, not in files in the temporary directory (pragma can override) |
| `ENABLE_FTS5` | Full-text search for the ledger (§12.6) and history. JSON and JSONB are built in |
| `DEFAULT_MEMSTATUS=0`, `LIKE_DOESNT_MATCH_BLOBS`, `OMIT_DECLTYPE`, `OMIT_DEPRECATED`, `OMIT_PROGRESS_CALLBACK`, `OMIT_SHARED_CACHE`, `STRICT_SUBTYPE=1`, `USE_ALLOCA` | From SQLite's recommended options: faster and smaller, nothing the binding uses |
| `HAVE_USLEEP`, `HAVE_LOCALTIME_R` (Unix), `HAVE_FDATASYNC` (Linux) | What a configure script would find. Without `HAVE_USLEEP`, the busy handler sleeps in whole seconds |

Each connection is also hardened when it opens: `SQLITE_DBCONFIG_DEFENSIVE` on (no `writable_schema`, no
writes to shadow tables, no `journal_mode=OFF`), and the trusted-schema and double-quoted-string
settings set again, so they hold whatever the build.

Not chosen:

- `DEFAULT_WAL_SYNCHRONOUS=1` (recommended for speed). Durability is the store's choice, per database,
  with measurements; see the recommendations.
- `MAX_EXPR_DEPTH=0` (recommended). It removes a stack-depth guard; SQL built from data (search queries)
  should keep it.
- `OMIT_AUTOINIT` (recommended). Saves a check per call but crashes if any call precedes
  `sqlite3_initialize`.
- `OMIT_UTF16` and other omissions: SQLite warns that `OMIT_*` options may not work with the
  amalgamation, and the size gain is small.

Note that `PRAGMA compile_options` lists some options by name only (`DEFAULT_FOREIGN_KEYS`, not `=1`)
and lists `DEFAULT_RECURSIVE_TRIGGERS` although it is 0 (SQLite defines it internally); the tests check
the pragmas' values for those.

## The binding (`src/Fx.Sqlite`)

```fsharp
// Opening: expected failures (missing file, not a database, no permission) are results.
let db =
    match Database.connect path OpenOptions.defaults with
    | Ok db -> db
    | Error error -> failwith (string error)

Database.useWal db |> ignore

Database.exec db "CREATE TABLE IF NOT EXISTS events (stream TEXT, seq INTEGER, body BLOB, PRIMARY KEY (stream, seq)) STRICT"
|> ignore

// An append: read the head and insert in one IMMEDIATE transaction. `string` maps BEGIN and COMMIT
// failures (e.g. Busy) to the body's error type.
let append stream expected body : Result<unit, string> =
    Database.transaction db TransactionKind.Immediate string (fun () ->
        let head =
            Database.tryFirst db "SELECT coalesce(max(seq), 0) FROM events WHERE stream = ?" [ Value.Text stream ] (fun row ->
                row.Int64 0)

        match head with
        | Error error -> Error(string error)
        | Ok(Some actual) when actual <> expected -> Error $"conflict: expected {expected}, found {actual}"
        | Ok _ ->
            Database.execute db "INSERT INTO events VALUES (?, ?, ?)" [ Value.Text stream; Value.Integer(expected + 1L); Value.Blob body ]
            |> Result.map ignore
            |> Result.mapError string)
```

- **Interop.** `Native.fs` declares the C API as `DllImport`s with blittable signatures only: handles and
  strings are pointers, text is UTF-8 that the binding encodes, results are numbers or pointers copied
  out at once. `[<assembly: DisableRuntimeMarshalling>]` guarantees it: a signature that needed a
  marshalling stub would fail on the JIT tests too. Arrays are pinned with a `GCHandle`, because the
  analyzers' typed-tree walk throws on F#'s `use p = fixed array`. `sqlite3_db_config` is variadic, and
  variadic arguments go on the stack on Apple arm64, so `library.c` exports a fixed-arity shim.
- **Values.** `Value` is SQLite's five types (`Null`, `Integer`, `Real`, `Text`, `Blob`); arguments are
  bound in order and their count must match. `Row` reads the current row (`Int64`, `Double`, `Text`,
  `Blob`, `Value`, `IsNull`, `Type`, `Name`) and is valid only inside the callback; typed readers reject
  NULL. An empty string or blob binds as empty, not NULL (a null pointer would bind NULL).
- **Statements.** `Database.execute/query/tryFirst/fold` take SQL text and prepare it once per
  connection (`SQLITE_PREPARE_PERSISTENT`, up to 128 statements, then per use); the same SQL may run
  inside its own row callback. `Database.prepare` gives a statement to keep. Statements are reset and
  their bindings cleared after every run, whatever happens. `execute` returns the rows changed, 0 for
  statements other than INSERT, UPDATE and DELETE. SQL with more than one statement is rejected
  (`Database.exec` runs scripts).
- **Errors.** Expected failures are `Result<_, SqliteError>`: `Code` (the primary code as a union:
  `Busy`, `Constraint`, `ReadOnly`, `Full`, `IoError`, `Corrupt`, `NotADatabase`, `CantOpen`,
  `Interrupt`, ...), the extended code (e.g. 2067 `SQLITE_CONSTRAINT_UNIQUE`; `SqliteError.isUniqueViolation`)
  and SQLite's message. SQL errors are results too: SQL may come from data (search queries). Misuse is
  an exception: a wrong argument count, a statement used inside itself, NULL read as a number, a closed
  connection, a binding SQLite rejects.
- **Transactions.** `Database.transaction db kind ofError body` commits on `Ok`, rolls back on `Error`
  or an exception, and nests as savepoints. `ofError` maps BEGIN/COMMIT failures (e.g. `Busy`) into
  the body's error type.
- **Threads.** Every operation takes the connection's lock, and a transaction keeps it until it ends: a
  connection can be shared, and another thread's statement never lands in someone's transaction. For
  parallel reads, open more connections; in WAL mode readers do not block the writer or each other.
  `Database.interrupt` works from any thread, and `Database.cancellable db token f` turns a
  cancellation into `Interrupt`.
- **Copies.** `serialize`/`deserialize` (the file format as bytes), `backup` (the online backup API),
  and `VACUUM INTO ?` for a compact copy on disk.

**No capability.** The binding is not an `Eff` capability. A connection is a stateful handle with
transactions, not data in and data out, and journaling SQL calls would record storage internals instead
of domain effects. The capabilities are the stores (`EventLog` in `Fx.Machines.Host`, the blob store,
the grants and ledger stores); their real interpreters use `Fx.Sqlite`, and their fakes are in memory.
The EFF analyzers do not see P/Invoke, so `src/Fx.Sqlite` needs no `analyzers.json` entry; AGENTS.md
records the gap and the rule (only storage interpreters and composition roots reference `Fx.Sqlite`).

## Per-OS notes

- **Linux (glibc).** Ubuntu runners use clang 18. The linker's `--gc-sections` keeps only reachable
  SQLite code: `fx`, which calls only the version functions, grew by 33 KB on `linux-x64`.
- **Linux (musl).** The Alpine AOT image has gcc 15 and no clang; ILCompiler links with gcc there too.
  Nothing musl-specific was needed.
- **macOS.** Apple's `ar` cannot write deterministic archives (`D`); `ZERO_AR_DATE=1` does it. `fsync` on
  macOS does not flush the drive's cache; real durability needs `PRAGMA fullfsync=ON` (and
  `checkpoint_fullfsync`), which costs a lot per commit.
- **Windows.** MSVC through `vswhere`/`vcvarsall` on both x64 and arm64 runners. `/MT` matches Native
  AOT's hybrid C runtime (static VC runtime, dynamic UCRT: ILCompiler links with
  `/NODEFAULTLIB:libucrt.lib /DEFAULTLIB:ucrt.lib`). Open database files are locked: they cannot be
  deleted or renamed while open, and antivirus scanners may hold them briefly; tests and scenarios
  close connections before deleting their directory.
- **Everywhere.** WAL needs a local filesystem with working shared memory (`-shm`), not a network share.
  A plain `dotnet build` now needs a C compiler (Visual Studio's C++ workload on Windows).

## Binary size

From the CI job summaries (bytes), before (T104) and after this change:

| Target | `fx` before | `fx` after | Scenarios before | Scenarios after | Scenarios growth |
|---|---:|---:|---:|---:|---:|
| linux-x64 | 2,216,120 | 2,248,928 | 15,375,936 | 17,008,200 | +1,632,264 |
| linux-arm64 | 2,310,208 | 2,375,784 | 15,818,208 | 17,409,440 | +1,591,232 |
| linux-musl-x64 | 2,215,880 | 2,215,880 | 15,375,696 | 16,885,296 | +1,509,600 |
| linux-musl-arm64 | 2,310,120 | 2,310,120 | 15,818,120 | 17,279,104 | +1,460,984 |
| osx-arm64 | 2,214,320 | 2,214,320 | 14,237,056 | 15,762,048 | +1,524,992 |
| osx-x64 | 2,262,336 | 2,262,336 | 14,787,928 | 16,342,864 | +1,554,936 |
| win-x64 | 2,041,856 | 2,042,880 | 15,366,144 | 16,967,168 | +1,601,024 |
| win-arm64 | 2,115,072 | 2,116,608 | 15,755,264 | 17,311,232 | +1,555,968 |

- `fx` links only what `--build-info` reaches (a few KB; segment padding absorbs it on some targets).
- The scenarios binary uses nearly the whole binding: it grew by 1.46–1.63 MB, which includes the
  binding and the scenarios' own code. The SQLite object itself is 1.39 MB (`linux-x64`, clang `-O2`,
  text and data). Expect `fx` to grow by about 1.3–1.5 MB once the event store uses SQLite.

## Risks

- **A C compiler for every build.** Contributors who never publish now need one for `dotnet build`. On
  Windows that is Visual Studio (or the Build Tools) with the C++ workload, which Native AOT already
  requires.
- **A download at build time.** A first build needs sqlite.org, three retries aside; CI caches the
  result. Offline builds pass the zip with `FxSqliteArchive`.
- **Toolchain drift.** Runner images upgrade compilers; outputs are reproducible per toolchain, not
  across them. The JIT-vs-native diff compares like with like (both libraries come from one build).
- **Cross-compiling Linux targets** needs `FxSqliteCC`; CI builds every target natively.
- **Blocking calls.** SQLite calls are synchronous and a commit may `fsync`. Calling them on thread-pool
  threads under load can starve the pool; see the recommendations.
- **The lock per connection** serializes everything on a shared connection, including long reads.
  Readers belong on their own connections.
- **Durability defaults.** `synchronous=FULL` in WAL mode costs an `fsync` per commit; `NORMAL` loses
  the last commits on power loss (never on a process crash). On macOS neither flushes the drive cache
  without `fullfsync`.

## Recommendations for T105 (event and blob store)

- **Connections.** One writer connection per database file, owned by the store and used from one
  dedicated thread or a single-consumer queue (commits block); a small pool of reader connections for
  `Read`, `Head` and subscribers. `Database.useWal` once at creation; `PRAGMA user_version` for schema
  migrations, run in a transaction.
- **Durability.** Decide `synchronous` per database and measure: `FULL` for the event log is the safe
  default (the spec's "never loses work"); `NORMAL` is crash-safe and much faster if power-loss windows
  are acceptable. Record the choice as a variant if it can change.
- **Schema.** STRICT tables. Events keyed by `(stream, seq)` (`WITHOUT ROWID` or an
  `INTEGER PRIMARY KEY` plus a unique index; measure), the idempotency key `UNIQUE` so a duplicate is
  `SqliteError.isUniqueViolation`, metadata in columns, the event body as codec JSON (or JSONB to query
  it with `->>`). Large payloads go to the content-addressed blob store, not into rows.
- **Appends.** `TransactionKind.Immediate`, read the head and compare with the expected sequence inside
  the transaction, insert the commit's events with the cached statement, commit. Map `Busy` to a retry
  and a conflict to `AppendError.Conflict`. The store passes `EventLogConformance`.
- **WaitPast/subscribe.** Signal in-process waiters after each commit; no polling. Another process
  writing the same file would need `PRAGMA data_version` polling; the single-writer host avoids it.
- **Cancellation.** Wrap calls in `Database.cancellable db env.Scope.Cancellation`.
- **Checkpoints.** Keep auto-checkpointing (1000 pages); a periodic `CheckpointMode.Truncate` when idle
  keeps the `-wal` file small. Snapshots and exports: `VACUUM INTO` or `Database.backup`.
- **Blob index.** Files named by digest, plus a table `(digest PRIMARY KEY, size, created, refs)`.
  Write the file, then insert the row; garbage-collect rows with no references and then their files.
- **Full text** (the ledger, history search): FTS5 external-content tables kept by triggers, as in the
  `sqlite-fts5` scenario; MATCH syntax errors come back as results, so user queries are safe to pass.
- **Analyzers.** When the first store lands, ban `Fx.Sqlite` in `src/` outside storage interpreters
  (an EFF rule with an `analyzers.json` entry per store), closing the P/Invoke gap.
