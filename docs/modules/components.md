# Module spec: components (`Fx.Sdk` model, `src/Fx.Components`, `src/Fx.Components.Live`)

Spec: §6.1 (with §1 principle 12, §6.2–6.3, §9.2, §13, §15, §16.1, §18). Task: T110.
Code: the model in [`src/Fx.Sdk`](../../src/Fx.Sdk) (`Semver.fs`, `Component.fs`); the planner,
lockfile, archive unpacking, installer machine, pipeline, capabilities and fakes in
[`src/Fx.Components`](../../src/Fx.Components) (pure); the real fetch and file interpreters in
[`src/Fx.Components.Live`](../../src/Fx.Components.Live).
Tests: `tests/Fx.Sdk.Tests/ComponentTests.fs`, `tests/Fx.Components.Tests` (archives, planner and
lockfile, machine, the installer conformance suite on the fakes), `tests/Fx.Components.Live.Tests`
(the same suite on a real disk, the live interpreters, the pinned Bun over the network), and the
`components-*` scenarios of the JIT-vs-native diff.

fx installs the programs it runs itself (the pinned Bun first), in its own directory, never through
`PATH` or user-global installs. A **component** is one version of such a program; the **planner**
(host side) resolves what extensions require into the lockfile `fx.components.lock`; the
**installer** (environment side) installs lockfile entries safely and recoverably.

## The model (in `Fx.Sdk`)

Extensions contribute components, so the model is part of the public SDK.

```fsharp
type Component =
  { Id: string                      // dotted lowercase, e.g. "bun"
    Version: SemVer                 // exact: one definition per version
    Methods: MethodChoice list      // in order of preference
    DependsOn: ComponentRef list    // { Id; Version = range text }
    Layout: InstallLayout           // { Executables }
    Exports: Exports                // { Bin: name -> path; Env: name -> value with ${dir} }
    Probe: Probe option             // { Argv; Expect }
    Platforms: TargetTriple list    // empty: wherever a method applies
    Size: SizeHint }                // { Download; Installed }, informational
and MethodChoice = { Platforms: TargetTriple list; Method: InstallMethod }
and InstallMethod =
  | Archive of ArchiveSpec          // { Url (https, ${version}); Digest; Format; Strip }
  | ReleaseIndex of ReleaseIndexSpec | Script of ScriptSpec | Package of PackageSpec
  | System of SystemSpec | Custom of CustomSpec
```

- **Versions** are semver 2.0 (`SemVer`: strict parsing, precedence that ignores build metadata).
  **Ranges** (`VersionRange`) are written as npm writes them: `1.4.5`, `=1.4.5`, `>=1.4 <2`, `^1.4`,
  `~1.4.5`, `1`, `1.x`, `*`, alternatives with `||`. Partial versions are X-ranges. A pre-release
  is in a range only if a comparator of the alternative names a pre-release of the same
  `major.minor.patch` (npm's rule).
- **Targets** (`TargetTriple`) are `<os>-<arch>[-<libc>]`: `linux-x64-glibc`, `linux-arm64-musl`,
  `macos-arm64`, `windows-x64`, … (`TargetTriple.all`, the product's eight targets);
  `TargetTriple.ofRid` gives a .NET runtime identifier's. There is no CPU feature level: Bun's x64
  `-baseline` builds are byte-identical to its default ones in 1.4.5, so `fx.runtime.bun` installs
  them everywhere (extensions/Fx.Ext.RuntimeBun/README.md).
- **Digests** are `sha256:<64 lowercase hex>`.
- **Archive formats**: `zip`, `tar.gz`, `tar`. `tar.xz` and `tar.zst` need decompressors .NET 11
  lacks (no LZMA, no Zstandard); adding them means a pinned, digest-checked native library like
  SQLite's, or a managed decoder. Bun ships zips, so nothing needs them yet.
- **Paths** in `Layout`, `Exports` and the probe's program are relative to the install directory,
  `/`-separated, with no `.`, `..`, `\` or `:` (`Component.isRelativePath`), and may use
  `${exeSuffix}`. URLs must be `https://`.
- **Only `Archive` is implemented.** The other methods are typed and have codecs, so definitions can
  declare them; the planner skips them (and says so when nothing else is left: "not implemented by
  this installer yet (T211)"), and the installer refuses them (`installer.unsupported-method`).
- **Codecs** for everything (`Component.codec`, strict: unknown members are issues), with paths in
  their issues. `InstallMethod`'s codec is built by `TaggedUnion` (in `Component.fs`) from one record
  codec per case: `Codec.union` refuses more than four cases under Native AOT (IL3054) in this
  version of `Fx.Contracts`. When T101b's `Codec.union { }` lands, it can replace the helper; the JSON
  stays the same.
- **Contribution type**: `ComponentDefinitions.contributionType` (`component`, data, privileged as
  spec §16.5 requires), in the host's core catalog. The contribution id is free (`bun-1.4.5`); the
  planner reads id and version from the value.

## The planner and the lockfile

```fsharp
Planner.plan : PlanSettings -> Component list -> Requirement list -> TargetTriple list -> Result<Lockfile, PlanError list>
```

- For each component, the **highest version in every range** asked for, by the requirements and by
  the dependencies of the versions chosen for other components, repeated until the choice settles.
  When the dependencies of a chosen version leave a component with no version, that version is set
  aside and a lower one tried (bounded: each step removes a definition). Otherwise errors say who
  asked for what and what exists (`NoVersion`), or name the unknown component, the invalid range, the
  dependency cycle, or two different definitions of one version.
- For each target, the **first method** of the chosen version that is for the target and that the
  installer implements (`PlanSettings.Methods`, `archive` today). URL templates are expanded.
- The **lockfile** (`Lockfile`, named `Product.componentsLockFileName`) records the requirements
  (sorted, deduplicated), the targets, and each component with its method per target, layout,
  exports, probe and size. It is self-contained: installing needs no definitions.
- **Reproducibility**: `Lockfile.render` writes canonical JSON (RFC 8785 key order) indented, with a
  final newline; the same inputs in any order give the same bytes (an FsCheck property).
  `Lockfile.hash` is `sha256:` over `Json.serializeCanonical`, so formatting does not change it; it is
  what `Profile.withLockfile` records (spec §15).
- `Lockfile.isCurrent requires targets` says whether a lockfile can be used as it is (a frozen
  lockfile); `resolve` checks requirements against it; `installs target ids` gives the install specs,
  dependencies first.

## The installer

### Install keys and the install root

An `InstallSpec` is a lockfile entry for one target. Its **key** is `<id>@<version>-<hash>`, the hash
being the first 12 hex digits of the SHA-256 of the canonical JSON of what determines the files (id,
version, target, method with its digest, layout). Exports and the probe are not in it.

```
<root>/<id>/<version>-<hash>/   an active install, with its receipt (.fx-receipt.json)
<root>/.staging/<key>/          unpacking and probing
<root>/.downloads/<key>[.partial]
<root>/.locks/<key>.lock, root.lock
<root>/.roots/<installer>.json  the installs each installer's holders use
<root>/.trash/                  what a prune or a repair is deleting
```

The root is given by the composition root: `InstallRoot.defaultFor` is `~/.fx/runtimes`, or
`%LOCALAPPDATA%\fx\runtimes` on Windows (names from `Fx.Product`).

### The machine

`Installer.machine` (spec §4.4) runs as an actor per install root and process
(`ComponentInstaller.start`), on stream `components/<installer name>`, with intents one at a time.

| Command | Events | Intents |
|---|---|---|
| `Ensure(holder, spec)` | `Held` (if new for the holder), `Requested(attempt)` unless installed or in progress | `Publish`, `Inspect` |
| `Hold(holder, keys)` | `Held` (if changed) | `Publish` |
| `Prune` | `PruneStarted(round)` (unless one runs) | `Sweep` |
| `Inspected(key, found)` | `Found`, or `Missing` (+ `ApprovalRequested` the first time for the component) | `Fetch` or `Ask` |
| `Answered(key, answer)` | `Approved(id, by)` or `Failed(Denied)` | `Fetch` for every install of the component that waited |
| `Fetched(key, result)` | `Fetched` or `Failed` | `Build` |
| `Built(key, result)` | `Installed` or `Failed` | — |
| `Swept(round, removed)` | `Pruned` | — |

- Results for another step are rejected (`installer.stale`), so a late result changes nothing.
- A failed install is retried by the next `Ensure` (a new attempt).
- `Recover` (intents are not durable): republish the roots, then go on with every install from its
  step (`Inspect`, `Ask`, `Fetch`, or `Build` with the fetched archive), and finish a prune. Every
  step is safe to run again.

### The pipeline (`Pipeline.interpret`)

1. **Publish**: writes the installs this installer's holders use to its roots file, atomically,
   under the root lock. It runs before the installs that rely on it (intents are sequential).
2. **Inspect**: an install directory whose receipt matches the key is installed (perhaps by another
   installer): `Found`, with no approval and no download.
3. **Ask**: the first install of a component is a **grant** (spec §13). `InstallerSettings.Approve`
   answers it: the caller asks a person or applies a policy (default "ask"); tests approve. Approval
   is per component id and journaled (`Approved`), so a restart does not ask again and upgrades
   need none. `Approvals.viaGrants grants requester context` is the callback over the grants log
   ([grants.md](grants.md)): it asks `component.install` on `<id>@<version>` (keyed by the install,
   suggesting `<id>@*`) and waits for a person if the rules say ask; the composition root passes it.
4. **Fetch** (under the install's lock): an install that appeared meanwhile is `Found`. Otherwise
   an archive left by an earlier attempt is reused if its digest matches; else it downloads to
   `.partial`, checks the SHA-256 computed while writing, and renames it into place. Network errors,
   HTTP 5xx, 408 and 429 are retried with a doubling delay (`DownloadAttempts`, `RetryDelay`); a
   wrong digest never is.
5. **Build** (under the install's lock): an install that appeared meanwhile is reused. Otherwise it
   clears what a crash left in staging, **verifies** the archive again, **unpacks** it into staging
   (`Archive`, below), checks that every exported and executable path exists, writes the receipt,
   **probes** the program in staging (exit 0 and, if set, exactly the expected output; with a
   timeout, and the installer's environment without runtime injection vectors), then **activates**
   it with one rename under the root lock. A broken directory in the way (no matching receipt) is
   moved to the trash first: that is the repair. On success the archive is deleted; on failure
   staging and the archive are, and the active installs are untouched.
6. **Sweep** (under the root lock): reads every installer's roots file and removes the install
   directories none of them names (moved to the trash, then deleted), the staging and download
   leftovers whose install lock is free, and the trash. A roots file it cannot read stops the prune
   (nothing is removed).

**Probe before activation.** Spec §6.1 lists the steps as stage → activate → probe; the installer
probes in staging and activates after. Activation is then the single commit point: an install
directory exists only complete and probed, a failing probe never exposes anything, and "rollback" is
deleting staging. The probe runs the program from the staging path, so components must be
relocatable (Bun is). A component that needs its final path to probe would need a second step
(activate, probe, deactivate on failure); none does yet.

**Between processes** (two envds, or a host and an envd, sharing a root): the install's lock keeps
one downloading and unpacking it while the others wait, then find it installed; the root lock
serializes roots, activation and pruning, so a prune never removes what any installer's holders use
or what is being activated. Locks are files opened exclusively (`flock` on Linux and macOS, sharing
modes on Windows), released by the OS when a process dies, and never deleted.

**Holders and pruning.** `Ensure` records that a holder (`extension:<id>`, a lockfile's user) uses an
install; `Hold` sets a holder's installs exactly, so moving a holder to a new lockfile releases the
old versions, and the next prune removes them unless another holder, in any installer, uses them.
These are the spec's refcounts: a holder set per install, persisted in the journal and published in
the root.

### Archives (`Archive.extract`)

Pure: it reads a stream (`System.Formats.Tar`, `GZipStream`, `ZipArchive`) and writes through an
`ExtractSink`, so the live interpreter (to disk) and the fake (to memory) share every check.

- Entry names: no absolute paths (`/`, `\`, drive letters), no `..`; `\` separates like `/`; no
  names unsafe on Windows (device names such as `NUL` or `COM1.txt`, `:` and the other forbidden
  characters, a trailing dot or space); no two entries that differ only in case; no duplicates.
- Only files, directories, symbolic links and (tar) hard links: devices, FIFOs, sparse and
  multi-volume entries are refused.
- Symbolic links are created **after every file**, so nothing is written through one (an entry under
  a link's path is refused); a link's target must stay inside without passing through another link.
  Hard links become copies of a file written before. On Windows, symbolic links are refused for now
  (creating them needs a privilege).
- Modes are not copied: a file is executable (0755, else 0644, minus the umask) if the archive marks
  any `x` bit or the layout lists it; setuid, setgid and sticky bits are dropped.
- Limits (`ExtractLimits`: entries, bytes per file, bytes in all, path length, depth) are counted on
  what is read, not on what headers claim.
- Corrupt archives are `Corrupt` errors, never exceptions.

### Capabilities

| Interface | Record | Operations | Interpreters |
|---|---|---|---|
| `IFetch` | `Fetch` | download a URL to a file, hashing it (`FileHash`), with a byte limit; `FetchError.Transient` | `HttpFetch` (HttpClient; redirects, no https→http downgrade, a stall timeout, errors without exception messages); `FakeFetch` (served from memory, scripted failures, held downloads) |
| `IInstallFiles` | `InstallFiles` | hash a file, unpack an archive, delete a tree without following links, replace a file atomically, try a lock | `LocalInstallFiles`; `InMemoryInstallFiles` (over any `Fs`) |
| `IComponents` | `Components` | `Ensure { Holder; Requires }`: install from the lockfile and say where (`InstalledComponent`, in `Fx.Sdk`: dir, bins, env; runtimes launch from it) | `ComponentInstaller.components actor lockfile target newKey` |

The installer also uses `IFs` (receipts, roots, renames), `IShell` (probes), `IClock` (retries, lock
waits, the probe timeout) and `ITrace`.

### The extension bootstrap

`Components.installFor` fits `HostSettings.InstallComponents` (the components phase of spec §16.1):
it ensures an admitted extension's `requires`, held by `extension:<id>`. The composition root (T129)
plans the lockfile before the bootstrap from the component definitions of builtin and host-config
candidates (components are privileged, so project extensions cannot contribute them), or uses the
existing lockfile if `Lockfile.isCurrent`; it records `Lockfile.hash` in the profile.

## Conformance suite v1

`InstallerConformance.tests name create` (`tests/Fx.Components.Tests/Conformance`) is a function from
an installer environment (filesystem, install files, fetch, shell, clock) to tests. The in-memory
fakes and the live interpreters on a temporary directory both pass it, on Linux, macOS and Windows:

- an archive is fetched, verified, staged, probed and activated, with the journal and no leftovers;
- a digest mismatch is rejected and nothing is installed;
- unsafe archives (traversal, absolute paths, devices, escaping links, writes through links,
  Windows device names, case collisions) are rejected and nothing is written outside the root;
- a failing probe (wrong output, error exit) rolls back, and the active version stays;
- a crash mid-install leaves nothing active, and a restart on the same journal finishes it, reusing
  the verified archive;
- installing again is idempotent, and another installer finds the install without approval;
- an upgrade keeps the old version until a prune, and a prune keeps what any installer uses;
- concurrent installers share one download and one install;
- installs from a lockfile come dependencies first, with their bins and environment;
- a first install needs approval, and a rejection installs nothing;
- transient download failures are retried, others are not;
- methods other than `archive` are refused with an error that names T211.

`tests/Fx.Ext.RuntimeBun.Tests` (`NETWORK_TESTS=1`; its own CI step on Linux, macOS and Windows, and
an Alpine job for musl) installs the pinned Bun for the runner's platform from GitHub through the
planner, the lockfile and the installer, and launches it (runtimes.md).

## Native AOT

Scenarios `components-model`, `components-planner`, `components-archive` (tar, tar.gz and zip,
refusals, limits), `components-installer` (the machine on the fakes: install, retry, a second
installer, upgrade and prune, an unsafe archive), `components-local` (`LocalInstallFiles`: modes,
locks, deletion) and `components-http` (`HttpFetch` against a loopback server) run on the JIT and as a
native binary on every target. Digests are printed only of uncompressed or fixed bytes: compressed
bytes may differ between zlib builds.

## Left for later

| What | Task |
|---|---|
| The other install methods (`ReleaseIndex` with vendor-index trust, sandboxed `Script`, `Package`, read-only `System`, `Custom`); placement `host-push` and `prebaked`, mirrors, `fx runtime bundle\|import` | T211, T503 |
| Forwarding the approval request of an installer on another machine to the host's grants log (the host's own installer uses `Approvals.viaGrants`: [host.md](host.md)) | T501 |
| Persisting the installer's stream in `SqliteEventStore` (an `EventSchema` for `InstallerEvent`; easiest with T101b's `Codec.union { }`), serving `IComponents` to the host over the environment protocol, envd's composition | T111, T211 |
| Writing the lockfile next to the project (`.fx/`), the frozen-lockfile check (the host plans at startup and records the hash in profiles) | T211 |
| Symbolic links on Windows (Developer Mode, or copies) | when a component needs them |
| `tar.xz` / `tar.zst` | when a component needs them |
| Resumable downloads; showing download progress | when large components arrive |
