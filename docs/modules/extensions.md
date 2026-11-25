# Module spec: the extension system (`src/Fx.Sdk`, `src/Fx.Extensions`)

Spec: §16 (with §1 principle 9, §3, §4.2–4.4, §11.5, §13, §15, §17, §18). Task: T108.
Code: [`src/Fx.Sdk`](../../src/Fx.Sdk) (the public F# SDK), [`src/Fx.Sdk.Process`](../../src/Fx.Sdk.Process)
(the extension protocol and its F# server), [`src/Fx.Extensions`](../../src/Fx.Extensions) (the core:
registry, catalog, runtimes, the host's side of the protocol, bootstrap), [`extensions/`](../../extensions)
(extension packages; [`Fx.Ext.Sample`](../../extensions/Fx.Ext.Sample) is the reference) and
`ProcTransport` in `src/Fx.Rpc`. The SDKs themselves (what is public, packages, versions, `@fx/sdk`) are
[sdk.md](sdk.md).
Tests: `tests/Fx.Sdk.Tests`, `tests/Fx.Extensions.Tests` (registry, bootstrap, launch, protocol,
transport parity, architecture) and the `extensions-*` scenarios of the JIT-vs-native diff.

Everything beyond a minimal core is an extension, and built-ins are bundled extension packages that use
only the public SDK. This module is the machinery: what an extension declares, how the host decides to
trust it, how it runs (in the binary, as data, or in another process), and how its contributions combine.

## The boundary: the public SDK

| Assembly | Who references it | Contents |
|---|---|---|
| `Fx.Sdk` | extensions, the core, composition roots | manifests, contribution types, hooks and their algebra, extension methods, `Extension`, `ExtensionRuntime`, `IExtensionEnv`, `BasicExtensionEnv`, the `RemotePeer` shape, variant slots, interpreters ([interpreters.md](interpreters.md)), the wire contract and `ModelAccess`, the grants capability and contribution types ([grants.md](grants.md)) |
| `Fx.Prelude`, `Fx.Contracts`, `Fx.Contracts.TypeScript`, `Fx.Effects`, `Fx.Variants`, `Fx.Models`, `Fx.Grants` | exposed by `Fx.Sdk` | `Product`, JSON and codecs, TypeScript declarations from schemas, `Eff` and capabilities, variant slots, the canonical model ([models.md](models.md)), the grants log's model ([grants.md](grants.md)) |
| `Fx.Sdk.Process` (with `Fx.Rpc`, `Fx.Effects.Live`) | F# extension processes; the core, for the protocol's messages | `ExtensionProtocol`, `ExtensionServer`, `ExtensionProcess.run` |
| `Fx.Extensions`, `Fx.Machines(.Host)`, … | the core and composition roots only | — |

- **The rule:** a package under `extensions/` has exactly one project reference, `src/Fx.Sdk`, and no
  package references. Its compiled assembly references only the SDK surface (`Fx.Sdk`, `Fx.Prelude`,
  `Fx.Contracts`, `Fx.Contracts.TypeScript`, `Fx.Effects`, `Fx.Variants`, `Fx.Models`, `Fx.Grants`, `FSharp.Core`) and the base class library
  (`System.*`).
  `ArchitectureTests` checks both, from the `.fsproj` and from the assembly's metadata
  (`System.Reflection.Metadata`), proves the check fails on an assembly that uses the core, checks that
  `Fx.Sdk` exposes nothing else, and that the core references no extension package.
- The analyzers' AOT and EFF rules apply to `extensions/**` as to `src/**`: extensions do no I/O outside
  `Eff`.
- Widening the surface (e.g. `Fx.Machines`, when machines become a contribution type) is a deliberate
  edit of `ArchitectureTests.sdkSurface` and of this table.
- An F# extension that runs as its own process (not a built-in) references `Fx.Sdk.Process`, which adds
  the protocol's server and what it runs on; the packages and the TypeScript SDK are [sdk.md](sdk.md).

## Manifests

```json
{ "id": "acme.tools", "version": "1.2.0", "description": "…",
  "sdk": "0.1", "protocol": "1", "placement": "host", "runtime": "process",
  "launch": { "exe": "${root}/bin/tools${exeSuffix}", "args": ["--stdio"], "envAllowlist": ["PATH"],
              "envSet": { "MODE": "fast" }, "envDeny": ["SECRET_*"], "cwd": "owned" },
  "requires": [ { "id": "bun", "version": ">=1.4 <2" } ],
  "capabilities": [ "clock", "fs.read" ],
  "contributions": [ { "type": "gate:model-egress", "id": "no-secrets" } ],
  "rpc": { "methods": [ { "name": "tools/list", "params": { … }, "result": { … } } ] },
  "replaces": [], "hash": "sha256:…" }
```

- The file is `Manifest.fileName` (`fx.extension.json`, from the product name). `Manifest.codec` is
  strict: unknown members are issues. `Manifest.decode`/`parse` add the checks across fields
  (`Manifest.check`), each issue with a JSON path, worded for a model: values only in declarative
  manifests, `launch` only and always for `process`, no duplicates, no extension replacing itself, no
  injection vector in `envSet`, a matching `hash`.
- **Names:** ids are dotted lowercase (`Product.extensionId "sample"` for ours, a reverse domain for
  others); versions are semver; contribution types `kind:point` or dotted (`gate:model-egress`,
  `extension-runtime`, `sample.word`); method names camelCase segments. The patterns are in
  `Manifest.Patterns` and in the JSON Schema (`tests/Fx.Sdk.Tests/golden/manifest.schema.json`).
- **Versions:** `sdk` is `major.minor` (`Versions.Sdk`, `0.1`); a host admits extensions of its major
  version with a minor version it has (before 1.0, the same minor). `protocol` is the extension protocol
  (`Versions.Protocol`, `1`).
- **Hash:** `Manifest.hash` is `sha256:` over the canonical JSON (`Json.serializeCanonical`) of the
  manifest without `hash`. Host config pins extensions by it. A package's payload (executables,
  scripts) is a component (`requires`), whose digests the component installer checks
  ([components.md](components.md)).
- **Capabilities** are names; the host builds the extension's environment from them (below). Their
  syntax is checked; which names exist grows with the host capabilities (T109b).

## Contribution types and the registry

A **contribution type** (`ContributionType<'T>`) is a typed key: a unique name, a kind, whether it is
privileged, and how its values cross runtimes. Anyone defines one without touching the core:

```fsharp
let commands  = ContributionType.data "command" "Slash commands." Command.codec       // data (Contribute)
let egress    = Hook.transform "model-egress" "…" ModelRequest.codec |> ContributionType.privileged
let fsMiddle  : ContributionType<Middleware<Fs>> = Hook.interpret "fs" "…"            // in process only
```

| Field | Meaning |
|---|---|
| `ToJson` | the value's JSON form: a data value, or code's settings (a transform's priority) |
| `OfJson` | builds a value from its JSON form and, for code, a `Remote` (how to reach the code in an extension process). Without a remote (a declarative manifest), code is an issue |
| `Serve` | how an extension process runs the value's code for the host: JSON in, JSON out |

- `Contribution.create t id value` erases the type (a `Contribution`); `Contribution.tryValue t` gets it
  back. Two types with one name and different value types are a defect.
- **Catalog:** the host's table of types by name: the core's (`extension-runtime`,
  `contribution-type`, `variant-slot`, `component`, `runtime`, `interpreter`), the host's own hook points (`HostSettings.Catalog`), and types native
  extensions define by contributing a `ContributionTypes.contributionType` (so later extensions,
  declarative manifests and extension processes can contribute to them). Admission refuses types the
  catalog does not know.
- **The registry is a machine** (`Registry.machine`, spec §4.4) whose state is a fold of its events
  ("replayed transforms"): `Registry.replay events` rebuilds it, and which contributions are in effect,
  and in what order, follows from it.

| Command | Events | Rejections |
|---|---|---|
| `Admit request` (before any code runs) | `Disabled(replaced, ReplacedBy id)`…, `Admitted` | `extension.duplicate`, `extension.replaced`, `extension.placement`, `extension.sdk`, `extension.protocol`, `trust.untrusted-project`, `trust.hash-mismatch`, `trust.unpinned`, `contribution.unknown-type`, `trust.privileged`, `trust.replace`, `contribution.conflict` |
| `Activate(id, provided, methods)` | `Activated` | `extension.not-admitted`, `extension.mismatch` (not exactly what the manifest declares) |
| `Fail(id, reason)` | `ActivationFailed` | `extension.not-admitted` |
| `Disable(id, reason)` | `Disabled(id, Requested)` | `trust.policy`, `extension.not-enabled` |

- `Handle` decides from the command alone: the host puts in it every fact from the world (the hash it
  computed, the pin from host config, whether the project is trusted, the catalog's view of each type).
- Contribution keys (type, id) are unique among admitted and active extensions; admission reserves
  them, failure and disabling release them.
- Events and state use only FSharp.Core types, like every machine's.

## The hook algebra (spec §16.3)

Hooks are contribution types whose values are code. `Hooks` runs them over the contributions in effect
(`ExtensionHost.contributions`), each in its extension's environment with the caller's scope.

| Kind | Value | Runner | Combination | Failure |
|---|---|---|---|---|
| Gate | `Gate<'I> = { Check: 'I -> Eff<IExtensionEnv, Decision> }` | `Hooks.decide` | concurrent; `Verdict.combine`: max over Deny > Ask > Allow, reasons of the winning level sorted and deduplicated; commutative, associative, idempotent, `Allow` the identity | the gate denies (`the gate failed`) |
| Transform | `{ Priority; Apply: 'T -> Eff<_, 'T> }` | `Hooks.transform` | in ascending priority, ties in registration order | the pipeline fails (`ContributionFailedException`): nothing goes out untransformed |
| Contribute | data values `'T` | `Hooks.collect` | concatenation in registration order | — |
| Observe | `{ Observe: 'E -> Eff<_, unit> }` | `Hooks.observe` | concurrent; waits for all; cannot change the event or the result | isolated, returned as `ObserverFailure`s |
| Interpret | `{ Priority; Wrap: 'I -> 'I }` over a capability record | `Hooks.interpret` | middleware stack, lowest priority outermost | — (in process only) |

- Cancelling the caller's scope cancels running hooks and fails with the cancellation; that is not a
  gate's failure.
- Failure details never carry exception messages (they differ between the JIT and Native AOT, and
  between runtimes): a reason names the contribution, `ContributionFailedException.Detail` has the
  exception type or the remote failure for diagnostics.
- Laws are FsCheck properties in `tests/Fx.Sdk.Tests/HookTests.fs`.
- Hook point names are prefixed by kind (`gate:model-egress`, `transform:model-egress`), so one point
  can have hooks of several kinds (T124 needs a transform and a gate on model egress).
- Interpreter middleware wraps records of functions, which live in one process: only native extensions
  contribute it. A process extension intercepts calls through the gates, transforms and observers of
  the call's hook points instead.

## The extension environment

`IExtensionEnv` is the environment of all extension code: its extension's id, the capabilities the
host grants it (today a clock), and the caller's scope. The host builds one per extension
(`HostSettings.EnvFor manifest`), attenuated to the manifest's `capabilities`: an extension's authority
is that value, never the caller's environment. `BasicExtensionEnv` (in `Fx.Sdk`) is the environment
every extension gets today; T109b adds capabilities (each checked against the manifest), also over the
protocol for extension processes.

## Extensions, methods and activation

```fsharp
let manifest = { Manifest.create (Product.extensionId "sample") "1.0.0" Runtimes.Native with
                   Contributions = [ Declare.contribution Points.greeting "polite"; … ]
                   Rpc = { Methods = [ Declare.method Methods.echo ] } }
let extension = { Manifest = manifest
                  Activate = eff { …; return Activation.create [ Contribution.create … ] [ ExtMethod.handle … ] } }
```

- The manifest declares; activation provides; the host checks that the two match exactly (types and
  ids, method names). The manifest is what is reviewed, hashed and pinned, so code cannot add
  undeclared contributions.
- `Activate` runs in the extension's environment and may allocate state its contributions share (the
  sample's observer records notes that a method returns). `Deactivate` releases it.
- **Methods** (`ExtMethod`): name and codecs of params, result and typed errors; clients call
  `ext/<id>/<name>` (T129 maps the client protocol to `ExtensionHost.call`). A method that throws is
  `MethodFailure.Crashed`, in process and out.

## Runtimes

| Runtime | Phase | What runs |
|---|---|---|
| `native` | in-binary | an `Extension` value from a static list compiled into the binary. No assembly loading or reflection (Native AOT) |
| `declarative` | in-binary | nothing: the manifest's values, decoded by the catalog |
| `process` | process | a child process in any language, speaking the extension protocol over JSON-RPC on stdio |
| contributed (`bun`, …) | hosted | an `ExtensionRuntime` contribution (privileged) loads the package; the contributor may itself be a process extension |

- **Process runtime:** `LaunchSpec` (data, the type runtimes use too: [runtimes.md](runtimes.md))
  becomes the process to start (`Launch.procSpec`):
  - `${root}` and `${exeSuffix}` expand;
  - the environment is exactly the allowlisted host variables plus `envSet`, minus `envDeny` and the
    host's deny list of runtime injection vectors (`Manifest.deniedEnvironment`, which is
    `InjectionVectors.patterns`: `NODE_OPTIONS`, `BUN_OPTIONS`, `DOTNET_STARTUP_HOOKS`, `LD_PRELOAD`,
    …), compared case-insensitively on Windows;
  - while the project is not trusted, the launch's `untrustedOverrides` go before the first argument
    that is not a flag.

  The process starts through the shell capability (`Shell.spawn`), so a test can hand it a fake
  process, and `ProcTransport` (in `Fx.Rpc`) turns the `Proc` into a JSON-RPC transport; standard
  error is kept (bounded) for diagnostics. Stopping closes the connection (the child's stdin), waits
  `StopTimeout`, then kills it. The connection lives in the bootstrap's scope.
- **Contributed runtimes:** the host passes the package's manifest and directory and its catalog's
  `Load`. A runtime contributed by an extension process is a proxy: activation goes to that process
  (`extension/activate` with the manifest), and the package's code then runs there. This is how
  `fx.exthost.bun` (T115, a process extension) provides `bun`; `ServerTests` runs the same path with an
  `echo` runtime.

### The extension protocol

The host is the JSON-RPC client; the extension process serves (`ExtensionServer` in `Fx.Sdk.Process`
for F#, `serveExtensions` in `@fx/sdk` for TypeScript; any language can implement it and pass the server
conformance suite, `tests/Fx.Extensions.Tests/Parity/ServerConformance.fs`). `initialize` carries `"extensionProtocol": "1"` (refused otherwise); the result lists
the ids the process serves (`"serves"`).

| Method | Params | Result |
|---|---|---|
| `extension/activate` | `{extension, manifest?, root?}`: an extension it serves, or a package to load with a runtime it contributes | `{manifest, contributions: [{type, id, value?}], methods}` |
| `extension/invoke` | `{extension, type, id, input}` | `{output}` |
| `extension/call` | `{extension, method, params}` | `{output}` |
| `extension/deactivate` | `{extension}` | `null` |

Typed errors (`ExtensionProtocolError`, by `kind`; a contribution's `address` is
`<extension>/<type>/<id>`):

| `kind` | Fields | When |
|---|---|---|
| `unknownExtension` | `extension` | `activate`: the process neither serves the extension nor can load it |
| `notActive` | `extension` | `invoke`, `call`, `deactivate`: never activated, or deactivated |
| `activationFailed` | `extension`, `reason` | `activate`: no runtime for the package, or the runtime refused it |
| `unknownContribution` | `address` | `invoke`: the active extension has no such contribution |
| `inProcessOnly` | `address` | `invoke`: the contribution's code cannot leave its process (interpreter middleware) |
| `unknownMethod` | `method` | `call`: the active extension has no such method |
| `invalid` | `issues` | `invoke`, `call`: the input or params do not decode |
| `methodFailed` | `message`, `data` | `call`: the method failed with one of its typed errors |

- Every request names the extension, so one process can serve several and load more.
- Activating twice answers the same. The host checks that the answer's id and version are the
  admitted manifest's and that it provides what the manifest declares.
- Defects in the process are JSON-RPC `InternalError`s: a gate denies, a transform fails, a method is
  `Crashed`. A process that is gone makes its contributions `Unreachable`.
- Calls from the extension to the host (host capabilities over RPC) come with T109b.

## Bootstrap (spec §16.1)

`ExtensionHost.bootstrap settings candidates` loads `Candidate`s (`Source`, `Package`, pin) in phases:

1. **in-binary:** native extensions, then declarative ones, each in the order given. Each is admitted,
   then activated, before the next, so types a native extension defines are known to those after it.
2. **components:** process and hosted extensions are admitted (all trust checks), then the components
   each admitted one requires are installed (`HostSettings.InstallComponents`: `Components.installFor`
   from `Fx.Components`, [components.md](components.md); the default settings have no installer, so
   extensions that require components fail closed).
3. **process:** extension processes start and activate.
4. **hosted:** extensions whose runtime an extension contributes (now available) activate.

- Candidates for another placement, and those disabled in host config, are skipped (policy extensions
  cannot be disabled).
- The result (`Host`) has the registry's state and events, the activated extensions, the catalog and a
  report per candidate (`activated`, `rejected` with the rejection, `failed` with the reason, `skipped`).
  The same candidates give the same events.
- `ExtensionHost.contributions`, `call`, `callTyped`, `disable` (returns a new `Host`) and `stop`
  (deactivates, the last admitted first) work on it.

## Trust (spec §16.5, §18)

| Rule | Where it holds |
|---|---|
| Nothing project-local runs before the project is trusted | admission (`trust.untrusted-project`) happens before activation, and before components are installed; `BootstrapTests` checks the extension's code never ran |
| Pinned hashes | a pin must match `Manifest.hash`; policy can require pins for everything outside the binary (`trust.unpinned`) |
| Privileged contributions only from host config, policy or the binary | `ContributionType.Privileged` comes from the catalog, never from the manifest; project extensions may not contribute such types (`trust.privileged`) |
| Policy extensions are unremovable | they cannot be disabled (in config or by command) or replaced (`trust.policy`, `trust.replace`) |
| An extension replaces only extensions from a source trusted as much (project < builtin < host config < policy) | `trust.replace` |
| Runtime launch specs deny injection vectors | `Manifest.check` refuses them in `envSet`; `Launch.environment` always removes them (`InjectionVectors`); a runtime's spec must list them all (`LaunchSpec.validate`, [runtimes.md](runtimes.md)) |
| Project files do not configure a runtime before trust | a launch's `untrustedOverrides` apply until the project is trusted (`LaunchContext.ProjectTrusted`, `RuntimeSpec.launch`) |
| Code cannot add undeclared contributions | activation must match the manifest (`extension.mismatch`) |

Left to later tasks: deciding project trust and reading pins from host config (T106/T129), bundled
packages extracted with hash checks (T115; `fx.runtime.bun` is declarative, so its package is its
manifest, a value in the binary, with nothing to extract), sandboxing extension
processes (T113; Windows v1 runs them unsandboxed), restarting crashed extension processes.

## Left to later tasks

| What | Task |
|---|---|
| The static list of built-ins in the host's composition root, bootstrap at startup, `ext/<id>/*` on the client protocol, recording the registry's events in the event store | T129 (T105 for the store) |
| Host capabilities in `IExtensionEnv` (and over the protocol, `host/*`) | T109b |
| Wiring the installer into the host: planning the lockfile from the component definitions of builtin and host-config candidates before the bootstrap, `Components.installFor` as `InstallComponents` | T129 |
| The `bun` runtime (`fx.exthost.bun`, a process extension contributing `ExtensionRuntime "bun"`) | T115 |
| Disabling and pinning from host config and policy; variants selecting extensions (`IVariants`) and profiles recording extension versions (spec §15) | T106 |

## Transport parity

- `ParitySuite.tests name load` (`tests/Fx.Extensions.Tests/Parity`) is a function from a way to load
  the sample into a host to tests: one transcript of every hook and method
  (`SampleHost.transcript`), the registry's events and replay, contributions with their data, separate
  state per activation, and disabling.
- It runs on the sample loaded natively, behind the process runtime over in-memory pipes, in a child
  process (`Fx.Aot.Scenarios ext-peer`, on the JIT in tests and natively in the diff), built from the SDK
  packages, and on the TypeScript sample (`packages/sample-extension`) on the pinned Bun
  ([sdk.md](sdk.md)). T115 runs it on the TypeScript sample under the `bun` runtime: supply a loader.
- The JSON-RPC conformance suite also runs over `ProcTransport` on a child started with `LocalShell`.

## Native AOT

- No assembly loading or reflection: built-ins are values in a static list, type erasure is a sealed
  generic box tested with `:?`.
- Scenarios: `extensions-manifest` (round trip, hash, issues), `extensions-hooks` (the algebra),
  `extensions-registry` (a script of commands, replay), and `extensions-native`,
  `extensions-process-memory`, `extensions-process-child`, whose transcripts are identical.

## Adding a contribution type or a hook point

1. Define it next to the code that uses it, with `ContributionType.data`, `Hook.gate`, `Hook.transform`,
   `Hook.observe` or `Hook.interpret`; mark it `ContributionType.privileged` if spec §16.5 lists it.
   Types that extensions contribute to must be in `Fx.Sdk` (or defined by a native extension).
2. Add it to the host's catalog (`HostSettings.Catalog`), or have a native extension contribute its
   `ContributionType.info` under `ContributionTypes.contributionType`.
3. Run its hooks with `Hooks.*` over `ExtensionHost.contributions`.
4. Give it a conformance suite (a function from contributions to tests) that built-ins and third-party
   extensions pass.
