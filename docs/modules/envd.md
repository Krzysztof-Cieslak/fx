# envd core (`Fx.Envd`, `Fx.Envd.Live`)

Spec: §3 (host = brain, envd = hands, one binary; embedded mode), §9 (environments, bootstrap), §5
(snapshots per step), §6.5–6.6 (shell, values in envd), §17 (environment protocol), §18. Task: T111.
Builds on [eff.md](eff.md) (capabilities), [rpc.md](rpc.md) (JSON-RPC, channels), [store.md](store.md)
(blobs) and [docs/spikes/pty.md](../spikes/pty.md) (terminals).
Code: [`src/Fx.Envd`](../../src/Fx.Envd) (pure: capabilities, protocol, envd's state and handlers, the
host's proxies) and [`src/Fx.Envd.Live`](../../src/Fx.Envd.Live) (interpreters and the composition
root). Entry point: `fx envd --stdio` (hidden from the usage; hosts start it).
Tests: `tests/Fx.Envd.Tests` (over fakes), `tests/Fx.Envd.Live.Tests` (real files, processes,
terminals and git; in process and as an `fx envd --stdio` child), and the `envd-*` scenarios of the
JIT-vs-native diff.

envd is a mode of the single binary that runs inside an environment and serves its capabilities over
the **environment protocol**. The host uses the same capability records whether the environment is in
its own process (embedded mode, in-process transport) or remote (stdio of a child, later WebSocket and
Dialback): `EnvClient` implements them over a connection, and they pass the same conformance suites as
the interpreters envd runs.

```
host code ── Fs / FsOps / Shell / Terminal / Blobs / Snapshots ──┐
                                                                 │ EnvClient (proxies)
                                     Fx.Rpc: in process | stdio  │ JSON-RPC + binary channels
                                                                 ▼
                                   EnvdServer handlers ── Envd (processes, output logs, watches)
                                                                 │
                LocalFs, LocalFsOps, LocalShell, LocalTerminal, FileBlobStore, GitSnapshots
```

## Capabilities

| Capability | Record | Interpreters |
|---|---|---|
| Files (existing) | `FsRead`, `Fs` | `LocalFs`; `InMemoryFs`; proxy |
| envd's file operations | `FsOps`, `IFsOps` | `LocalFsOps` (Fx.Envd.Live); `FsOpsOver.create` (generic, over any `FsRead`/`Fs`: the fake); proxy |
| Processes (existing) | `Shell` (pipes) | `LocalShell`; `ScriptedShell`; proxy |
| Terminals (existing) | `Terminal` (`Fx.Pty`) | `LocalTerminal` (wrapped by `TerminalSetup`); proxy |
| Blobs (existing) | `Blobs` | `FileBlobStore`; `InMemoryBlobs`; proxy; `BlobCache.over` |
| Workspace snapshots | `Snapshots`, `ISnapshots`, `SnapshotRef` | `GitSnapshots` (over `Shell` + `Fs`); `Snapshot.unavailable`; proxy |
| Spill files | `Spill`, `SpillFile` (envd-internal) | `LocalSpill`; `Spill.inMemory` |

`FsOps` (paths absolute, in the environment's syntax, like `FsRead`):

- `StatMany`: one result per path, in order.
- `ReadRange path offset count`: at most `count` bytes, with the file's size (a size-limited read is
  offset 0 and a limit; `FileChunk.AtEnd` says whether it was cut).
- `WriteAtomic`: a temporary file `.<name>.<random>.tmp` in the same directory, then a rename over the
  target, so readers see the old or the new content; the Unix mode of the old file is kept. Not synced
  to disk (a crash of the OS can lose the last write, never mix two).
- `ApplyPatch`: a **structured edit list**, not a unified diff. `FilePatch` is `Create(path, text)`,
  `Edit(path, digest?, edits)` or `Delete(path, digest?)`; a `TextEdit` replaces `Count` lines from
  1-based `Line` (`Count = 0` inserts; `Line = count + 1` appends). Edits refer to the original
  numbering and must not overlap. The digest (SHA-256, `BlobHash`) of the file as the patch saw it
  makes a stale edit fail (`Stale`). Every file is read and checked before any is written, then each is
  written atomically. Line endings: only `\n` and `\r\n` end lines; untouched lines keep theirs, new
  lines take the file's (its first line's, else `\n`); a missing final newline stays missing; a byte
  order mark stays; content that is not UTF-8 is `NotText`. Why not unified diffs: models get line
  numbers wrong and context matching is fuzzy; ranges plus a digest are exact and are what anchored
  edits (spec §6.8, T204) need. A unified-diff parser that produces this list can come later.
- `Search`: a depth-first walk in name order from `Root`; `.git` directories are skipped, symlinks not
  followed. `Include`/`Exclude` globs (git-style: `*`, `**`, `?`, `[a-z]`, `[!a]`, `{a,b}`; a glob
  without `/` matches the name at any depth) over paths relative to the root with `/`. With a pattern
  (.NET regex on the linear-time `NonBacktracking` engine: no backreferences or lookaround), one match
  per matching line with its 1-based line and column, lines cut to 500 characters; binary files (a zero
  byte in the first 8000) and files over `MaxFileBytes` are skipped; `MaxResults` cuts (`Truncated`).
  ripgrep semantics (`.gitignore`, file types, multiline) are T202's.
- `Watch`: `FileSystemWatcher` (inotify, FSEvents, ReadDirectoryChangesW) into a `ChangeQueue`: the
  first change starts a `Debounce` on the clock, then everything pending is one `WatchBatch`, coalesced
  per path (added then deleted drops out, deleted then added is modified). Renames are a deletion and
  an addition. An OS buffer overflow or more than 16384 pending changes sets `Overflow`: rescan.

`ChangeKind`/`FileChange` serve both watch batches (absolute paths) and snapshot diffs (relative, `/`).

## The protocol

The host is the JSON-RPC client. `initialize` carries `environmentProtocol: "1"` both ways; envd
refuses another version (`UnsupportedVersion`) and adds `platform` to its result:

```json
{"os":"linux","arch":"x64","libc":"glibc","shell":{"program":"/bin/sh","args":["-c"]},
 "pathSeparator":"/","caseSensitive":true,"workspace":"/src/app","version":"0.0.0",
 "features":["pty","watch","snapshots"]}
```

`features` lists the optional groups that work here (`snapshots` needs git). Methods (typed errors in
brackets; all definitions in `EnvProtocol`):

| Group | Methods |
|---|---|
| Files | `fs/stat {paths}` → `{entries: [{entry}|{error}]}`; `fs/list`, `fs/read {path, offset, count?, channel}`, `fs/write {path, channel, atomic}`, `fs/createDirectory`, `fs/delete`, `fs/move` [`FsError`]; `fs/patch` [`PatchError`]; `fs/search` [`SearchError`] |
| Watching | `watch/start {watch, root, recursive, debounceMs}` [`FsError`], `watch/stop {watch}`; envd → host: `watch/changed {watch, changes, overflow, closed}` |
| Processes | `exec/start {program, cwd?, env, terminal?}` → `{id, pid}`; `exec/attach {id, out, err, channel}`; `exec/write {id, data}` (base64, at most 64 KiB); `exec/closeStdin`, `exec/resize`, `exec/kill`, `exec/wait` → `{exitCode}`, `exec/status`, `exec/list`, `exec/release` [`ExecError`: `notStarted`, `invalidSize`, `unknownProcess`] |
| Blobs | `blobs/put {channel}` → `BlobRef`; `blobs/get {hash, offset?, count?, channel}` [`BlobError`]; `blobs/stat {hash}` → `{blob?}` |
| Snapshots | `snapshot/take {message}` → `SnapshotRef`; `snapshot/diff {from, to}` → `{changes}`; `snapshot/patch {from, to, paths?, channel}`; `snapshot/restore {to, paths?}` [`SnapshotError`] |

- **Bulk data goes over binary channels that the caller opens** and names in the request. The handler
  accepts the channel first thing. Downloads (`fs/read`, `blobs/get`, `snapshot/patch`): envd writes
  the content and closes the channel before it answers; the caller reads concurrently with the call.
  Uploads (`fs/write`, `blobs/put`): the caller writes and closes; envd answers after the end. A caller
  whose call fails resets its channel (`Transfer.download`/`upload`). Unaccepted channels count against
  `maxChannels` until the connection ends, which is why the handler always accepts.
- `FsError` (eight cases) is one flat object `{kind, path, detail?}`: `Codec.union` takes at most four
  cases under Native AOT.
- Typed errors are expected failures; a lost connection or a crash in a handler is a defect at the
  proxy (`EnvCallException`, `TransferFailedException`), as for every capability.

### Processes and sequenced, resumable output

- envd assigns process ids (`"1"`, `"2"`, … for its lifetime). **Processes belong to the `Envd`, not
  to a connection**: they keep running when the host disconnects, and a host that connects again
  finds them (`exec/list`, `exec/status`) and re-attaches.
- Each process has one `OutputLog` per stream (`out`, and `err` for pipes; a terminal has `out`
  only). A pump reads the process continuously, so programs never block on a host that is not reading.
  **Every byte's sequence number is its offset in its stream.**
- The last `Retain` bytes (`--retain`, default 1 MiB) stay in memory. When a stream overflows them, the
  whole stream goes to a **spill file** in `<state>/output/<id>.out|err` (written from offset 0 and
  flushed as it grows, so it can be grepped meanwhile; spec §6.6), and `exec/status` reports its path.
  Without a spill (fakes), older bytes are dropped and read as a gap.
- `exec/attach {id, out, err, channel}` streams records over the channel from those offsets, until both
  streams have ended (then the channel closes) or the channel fails. Record layout (big-endian): kind
  (1 data, 2 gap), stream (0 out, 1 err), then offset (u64), length (u32) and bytes, or from and until
  (u64). A resumed attachment starts exactly where the last record the host kept ended.
- `exec/kill` kills the tree (process group and tree on Linux and macOS, job object for terminals on
  Windows). `exec/release` kills if needed and forgets the process with its output and spill files. The
  `Proc` proxy releases a process once it has read the output to its end and seen the exit code; the
  `Pty` proxy's `Close` kills, waits for the exit and releases.
- Processes envd started die with it only if it stops cleanly (end of input on stdio): a killed envd
  leaves them running. Containment is the sandbox drivers' (T113).

### Terminals

`TerminalSetup` applies what pty.md recommends to every terminal spec unless it sets them itself:
`TERM=xterm-256color`; `LC_CTYPE=C.UTF-8` (Linux) or `UTF-8` (macOS) when envd's own environment has
no locale; on Windows, PowerShell `Script`s start with UTF-8 console encodings (a console starts with
the OEM code page and loses non-ASCII input). The Windows shell is `pwsh` if it is on `PATH`, else
Windows PowerShell; `--shell pwsh|powershell|sh` overrides it (the host passes its `shell.windows`
variant there, T112). A VT responder for programs that query the terminal (pty.md, 6) is not done.

### Watches

Watches belong to their connection: the host picks the id, registers where batches go, then calls
`watch/start`, so no batch can arrive for an id it does not know. They stop with `watch/stop` or when
the connection ends; after a reconnect the host starts them again and rescans.

## Workspace snapshots (shadow git)

`GitSnapshots` keeps a bare repository in envd's state (`<state>/snapshots.git`) and runs
`git --git-dir=<shadow> --work-tree=<workspace>` through the shell capability. The user's repository
(index, refs, hooks, config of the repository) is never read or written.

- `Take`: `add --all --ignore-errors` (the workspace's `.gitignore` files and the user's global excludes
  apply; nested repositories become gitlinks), `write-tree`, `commit-tree` with fixed dates and the
  previous snapshot as parent, `update-ref refs/<product>/snapshots`. Equal workspaces have equal
  trees; equal histories, equal commits.
- `Diff`: `diff-tree -r --no-renames --name-status` between trees. `Patch`: `diff --no-ext-diff
  --no-textconv` (never runs the user's diff drivers). `Restore`: stage everything, then
  `restore --source=<commit> --staged --worktree` for all paths or the given ones (from stdin,
  literal pathspecs): changed files are rewritten, files that did not exist are deleted, ignored files
  are left alone.
- Bytes are kept exactly: `info/attributes` is `* -text -filter -ident -working-tree-encoding`, which
  overrides the workspace's `.gitattributes` (no line-ending conversion, no LFS filter), and every call
  passes `core.autocrlf=false`. Hooks are off (`core.hooksPath` points nowhere), `--template=` keeps the
  user's init templates out, `GIT_DIR` and friends are unset.
- One git command at a time (the shadow index lock).

**Limits.** The first snapshot of a large workspace hashes every file that is not ignored (git's
speed: seconds for tens of thousands of files); later ones are incremental through the shadow index.
Large binaries are stored whole in the shadow repository, which is never pruned yet (nothing deletes
old snapshots; T205 decides retention). Restoring paths that are in neither the snapshot nor the
workspace fails (`GitFailed`). Needs git ≥ 2.26 (`restore --pathspec-from-file`). Two-phase undo of
agent-attributed changes (spec §5) builds on `Restore` with paths (T205).

## envd's composition root

`fx envd --stdio [--workspace <dir>] [--state <dir>] [--shell pwsh|powershell|sh] [--retain <bytes>]`
(`EnvdMain`):

- the workspace defaults to the current directory and must exist (exit 2 otherwise);
- the state defaults to `~/.<product>/envd/<hash of the workspace>` (`%LOCALAPPDATA%\<product>\envd\…`
  on Windows): `blobs/` (`FileBlobStore`), `output/` (spill files, emptied at start: one envd per state
  directory), `snapshots.git`; if the state is inside the workspace, snapshots exclude it;
- git is looked for once (`git --version`); without it the `snapshots` feature is off;
- it serves one connection on stdin/stdout, then kills its processes and exits (0, or 1 if the
  connection failed before the handshake). Nothing but the protocol is written to stdout; messages go
  to stderr.

## Testing

- `FsOpsConformance` (`tests/Fx.Envd.Tests/Conformance`) and `SnapshotsConformance`
  (`tests/Fx.Envd.Live.Tests`) are new suites; `ShellConformance` (`tests/Fx.Effects.Live.Tests`) and
  `TerminalConformance` (`tests/Fx.Pty.Tests`) turned the local tests into suites. With
  `FsConformance` and `BlobsConformance` they run against the live interpreters, the proxies over
  fakes in process, and the proxies over envd built from the live interpreters, in process and as an
  `fx envd --stdio` child (the binary next to the tests): transport parity.
- Resuming: over fakes with a process the test drives (cut the connection, the process goes on,
  re-attach from the offsets read), and with a real process in process.
- The `envd-in-process` and `envd-stdio-child` scenarios run one scripted session (files, pipes, a
  terminal, blobs, a watch, snapshots) on the JIT and natively; the child is the scenarios program's
  `envd-peer` mode. CI also starts the native binary's `envd --stdio` and checks the handshake.

## Left to later tasks

- **T112**: the provider contract, the local provider (embedded mode wiring), reconnecting transports
  (WebSocket, Dialback), and passing the host's `shell.windows` variant.
- **T113**: sandboxes; containment of escaped processes; stopping children when envd is killed.
- **T131**: journaling capability calls (process output is journaled as `Read` results); a
  deterministic `ScriptedTerminal` fake.
- **T202**: ripgrep semantics for search; **T204**: anchored edits on `FsOps.ApplyPatch`; **T205**:
  per-step snapshots, retention and two-phase undo.
- Signals other than kill and Ctrl+C (typed into a terminal), and starting pipe processes with default
  signal dispositions (pty.md, 8).
