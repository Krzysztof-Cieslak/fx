# `fx.runtime.bun`

The bundled Bun runtime (spec §6.3, task T114). A **declarative** extension (`Product.extensionId
"runtime.bun"`): its manifest carries two values and nothing of it runs.

| Contribution | Id | Value |
|---|---|---|
| `component` (privileged) | `bun-1.4.5` | `RuntimeBun.bunComponent`: Bun 1.4.5, one `Archive` method per product target, digests pinned |
| `runtime` (privileged) | `bun` | `RuntimeBun.runtime`: the component, the hardened `LaunchSpec`, and the files written before every launch (`bunfig.toml`, `tsconfig.json`) |

The host's planner reads the component; whoever starts Bun (the bun-ts kernel, the extension host,
the workflow worker) installs it with `IComponents` and launches it with `RuntimeSpec.prepare` and
`RuntimeSpec.launch` (docs/modules/runtimes.md). Nothing may start any other Bun: never the user's.

Code: `Pins.fs` (the pin table), `RuntimeBun.fs` (component, launch, manifest). Tests:
`tests/Fx.Ext.RuntimeBun.Tests` (the runtime conformance suite, the data, plan → install → launch
offline, and the real Bun with `NETWORK_TESTS=1`), and the bootstrap and architecture tests in
`tests/Fx.Extensions.Tests`.

## The pins

Bun **1.4.5** (`1.4.5+a7733435c`). Every release archive for a desktop or server target is pinned
(twelve), each with the archive's SHA-256 and the SHA-256 of the executable inside.

The digests were checked three ways:

- they are the release's `SHASUMS256.txt`;
- its clear-signed copy `SHASUMS256.txt.asc` verifies with Bun's release key, "Robobun
  <robobun@oven.sh>", `F3DC C08A 8572 C074 9B3E 1888 8EAB 4D40 A7B2 2B59` (keys.openpgp.org);
- they equal GitHub's asset digests and the hashes of the downloaded files.

The network tests check the table against `SHASUMS256.txt` again on every CI run.

| Target | Archive |
|---|---|
| `linux-x64-glibc` | `bun-linux-x64-baseline.zip` |
| `linux-x64-musl` | `bun-linux-x64-musl-baseline.zip` |
| `linux-arm64-glibc` | `bun-linux-aarch64.zip` |
| `linux-arm64-musl` | `bun-linux-aarch64-musl.zip` |
| `macos-x64` | `bun-darwin-x64-baseline.zip` |
| `macos-arm64` | `bun-darwin-aarch64.zip` |
| `windows-x64` | `bun-windows-x64-baseline.zip` |
| `windows-arm64` | `bun-windows-aarch64.zip` |

Each archive holds `bun-<target>/bun` (`bun.exe` on Windows), so the method strips one directory.
The installer probes `bun --revision` and expects exactly `1.4.5+a7733435c`.

### Baseline builds on x64

Targets have no CPU-feature dimension (T110), so the x64 targets need one archive each: the default
build, or the `-baseline` build for CPUs without AVX2.

**Measured:** in 1.4.5 the two are the same program. The executable inside
`bun-linux-x64.zip` and `bun-linux-x64-baseline.zip` is byte for byte identical, and the same holds
for linux-musl, macOS and Windows: `Pins.assets` records the executable digests, and a test checks
each pair. The archives differ only in the top directory's name.

**Decision:** always the baseline build on x64. It costs nothing, and it is the safe choice where AVX2
is missing or masked: old servers, some VMs and emulators. A CPU probe would add a moving part for
no gain. If a later release splits the builds again, measure the difference before choosing the
default build, or add a feature dimension to targets.

## The hardened launch

`--config=<owned bunfig.toml> --no-env-file --no-install --no-orphans --no-macros`, plus
`--tsconfig-override=<owned tsconfig.json>` until the project is trusted.

- Each flag is listed by the pinned `bun --help`; the network tests check it. Bun ignores unknown
  flags silently, so a flag that a version lacks would otherwise go unnoticed.
- Each one has a positive control (below).
- The owned `bunfig.toml` repeats the flags (`env = false`, `[install] auto = "disable"`,
  `[run] noOrphans = true`) and sets `telemetry = false`, so the file or the flag alone is enough.

The environment is built from nothing: the variables below, never the launcher's environment.

| Variables | Value | Why |
|---|---|---|
| `PATH` | the launcher's | Programs Bun starts (a cell's child process) resolve commands with it. Bun itself is started by its absolute path |
| `LANG`, `LC_ALL`, `LC_CTYPE`, `TZ` | the launcher's | The user's locale and time zone, for text and dates (child processes too) |
| `SystemRoot`, `windir`, `SystemDrive` | the launcher's (Windows) | Winsock, CNG and other system DLLs find their files through them: sockets fail to initialise without `SystemRoot` |
| `ComSpec`, `PATHEXT` | the launcher's (Windows) | How child processes are started (`shell: true`) and found without an extension |
| `NUMBER_OF_PROCESSORS`, `PROCESSOR_ARCHITECTURE`, `OS` | the launcher's (Windows) | Facts about the machine that build tools and scripts started by Bun read. They configure nothing |
| `DO_NOT_TRACK=1`, `BUN_ENABLE_CRASH_REPORTING=0` | set | No telemetry, and no crash-report uploads, which are on by default on macOS and Windows |
| `NO_COLOR=1` | set | Output goes to a protocol or a model, not a terminal |
| `HOME`, `USERPROFILE`, `XDG_CONFIG_HOME` | owned home | The global `.bunfig.toml`, `.npmrc` and anything else under home are owned, out of reach of the user's own and of other launches |
| `XDG_CACHE_HOME`, `BUN_RUNTIME_TRANSPILER_CACHE_PATH`, `BUN_INSTALL_CACHE_DIR` | owned cache | Caches are per launch |
| `TMPDIR`, `TEMP`, `TMP` | owned temp | Temporary files too |

Every injection vector in `InjectionVectors.all` is denied. The deny list matters for allowlisted
patterns and for `EnvSet`, since nothing else is inherited.

Two variables appear that the spec does not name: Bun sets `BUN_FEATURE_FLAG_NO_ORPHANS` for its
children on Linux and macOS (for `--no-orphans`).

The working directory is owned (`cwd = owned`). The live tests also run Bun with the workspace as
its working directory, to show that the flags alone keep the project's files out.

### Positive controls

Each row works on a plain launch of the pinned binary (the launcher's environment plus the
injection, in the hostile workspace) and fails on the hardened one (`LiveTests`, Linux, macOS,
Windows and musl).

| Injection | Plain launch | Hardened launch |
|---|---|---|
| `BUN_OPTIONS=--preload=…` | the preload runs | nothing runs |
| `BUN_INSPECT=127.0.0.1:<port>` | a debugger listens | nothing listens |
| `BUN_JSC_dumpOptions=1` | JSC prints its options | nothing |
| `NODE_PATH` | resolves modules | does not |
| `NODE_OPTIONS` | reaches the runtime (and its Node children) | absent |
| `.env` in the working directory | loaded | not loaded |
| `bunfig.toml` with `preload` in the working directory | runs | not read |
| `tsconfig.json` `paths` in the working directory | redirect imports | ignored until the project is trusted, then honoured |

## Bumping Bun

1. Download the release's `SHASUMS256.txt` and `SHASUMS256.txt.asc`. Verify the signature with the
   Robobun key above, and that the signed text equals `SHASUMS256.txt`.
2. Download the twelve archives. Check their hashes against the file and against GitHub's asset
   digests (`gh api repos/oven-sh/bun/releases/tags/bun-v<version>`). Record each archive's size,
   and the SHA-256 and size of its executable.
3. Update `Pins.fs`: `Version`, `Revision` (from `bun --revision`), and the table.
4. Compare the `-baseline` executables with the default ones. If they differ, revisit the baseline
   decision above.
5. Check every hardening flag against the new `bun --help`. Read the release notes for new
   environment variables (`strings bun | grep -o 'BUN_[A-Z_]*'` lists the ones the binary reads) and
   add new injection vectors to `InjectionVectors` (src/Fx.Sdk).
6. Run the tests with `NETWORK_TESTS=1` and `UPDATE_GOLDEN=1` (the manifest and lockfile goldens
   change), and bump the extension's version.
