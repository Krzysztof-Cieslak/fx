# Module spec: the public SDKs (`Fx.Sdk`, `Fx.Sdk.Process`, `@fx/sdk`)

Spec: §16.1 (SDKs at launch: `Fx.Sdk` and `@fx/sdk`; other languages via raw JSON-RPC), §16.2–16.4,
§4.1 (TypeScript authors use `@fx/sdk` codecs that produce the same `Schema`), §7.2, §17 (generated
types), §20 (naming). Task: T109.
Code: [`src/Fx.Sdk`](../../src/Fx.Sdk), [`src/Fx.Sdk.Process`](../../src/Fx.Sdk.Process),
[`packages/sdk`](../../packages/sdk) (`@fx/sdk`), the samples
[`extensions/Fx.Ext.Sample`](../../extensions/Fx.Ext.Sample) and
[`packages/sample-extension`](../../packages/sample-extension), and
[`scripts/packages.ts`](../../scripts/packages.ts).
Tests: `tests/Fx.Sdk.Tests`, `tests/Fx.Sdk.Process.Tests` (the protocol's messages, the generated
TypeScript, the cross-language fixtures), `tests/Fx.Extensions.Tests` (architecture, transport parity,
server conformance), `bun test` in `packages/sdk` and `packages/sample-extension`.

An extension sees the host only through a public SDK. Built-ins are bundled extension packages that use
the same SDKs as third-party ones (spec §1 principle 9, §16.4). This spec says what each SDK exposes, how
they are packaged and versioned, and how to write an extension with them. The extension system itself
(manifests, the registry, hooks, runtimes, the protocol) is [extensions.md](extensions.md).

## What is public

| Package | Project | Who uses it | Contents |
|---|---|---|---|
| `<Name>.Sdk` (`Product.nugetPackage "Sdk"`) | `src/Fx.Sdk` | every F# extension; the core | manifests, contribution types, the hook algebra, extension methods, `Extension`, `ExtensionRuntime`, `IExtensionEnv` and `BasicExtensionEnv`, the `RemotePeer` shape, components, runtimes, interpreters (`InterpreterProvider`, [interpreters.md](interpreters.md)), environment providers (`EnvProvider`, `IProviderEnv`, specs and leases, [environments.md](environments.md)), variant slots (`VariantSlots`), the chat wire contract, the model-egress hook points and `ModelAccess` ([models.md](models.md)), content exclusions (`ContentExclusions`, [egress.md](egress.md)), the grants capability (`IGrants`), `GrantStore` and the grants' contribution types ([grants.md](grants.md)), the `IdentityProvider` contract and its contribution type ([identity.md](identity.md)) |
| `<Name>.Prelude`, `.Contracts`, `.Contracts.TypeScript`, `.Effects`, `.Variants`, `.Models`, `.Grants`, `.Identity` | exposed by `Fx.Sdk` | through `Fx.Sdk` | `Product`; JSON, `Schema` and codecs; TypeScript declarations from schemas; `Eff` and the capabilities (`IHttp` included); variant slots and `IVariants`; the canonical chat model, `ILlm` and cassettes; the grants log's model (actions, patterns, grants, requests, the effective rules); principals (ids, roles, credentials, `initialize`'s identity members) |
| `<Name>.Sdk.Process` (`Product.nugetPackage "Sdk.Process"`) | `src/Fx.Sdk.Process` | F# extensions that run as their own process; the core (the protocol's messages) | `ExtensionProtocol` (messages and codecs), `ExtensionServer`, `ExtensionProcess.run` |
| `<Name>.Rpc`, `.Effects.Live` | exposed by `Fx.Sdk.Process` | through it | the JSON-RPC engine and transports; the real interpreters a process's `main` (a composition root) uses |
| `@<name>/sdk` (`Product.npmPackage "sdk"`) | `packages/sdk` | TypeScript extensions (and T115's Bun extension host) | codecs, the generated contract types, the extension API, the protocol's server |

- **Built-ins** (`extensions/`) reference `src/Fx.Sdk` and nothing else, and their compiled assemblies
  reference only `sdkSurface` (`Fx.Sdk`, `Fx.Prelude`, `Fx.Contracts`, `Fx.Contracts.TypeScript`,
  `Fx.Effects`, `Fx.Variants`, `Fx.Models`, `Fx.Grants`, `Fx.Identity`, `FSharp.Core`) and the base class library. `ArchitectureTests` checks both, and that `Fx.Sdk` and
  `Fx.Sdk.Process` reference only their surfaces (`processSdkSurface` adds `Fx.Sdk.Process`, `Fx.Rpc`,
  `Fx.Effects.Live`). Widening a surface is a deliberate edit of those sets and of this table.
- **Not public:** `Fx.Extensions` (the registry, catalog, runtimes, bootstrap), `Fx.Machines(.Host)`,
  `Fx.Store`, `Fx.Kernel`, `Fx.Interpreters`, `Fx.Components`, `Fx.Transport.Http`, the CLI. Kernels
  are not extensions' code: an interpreter extension (`fx.interpreter.bun-ts`) contributes an
  `InterpreterProvider` (data and pure functions); its kernel is a process that speaks the kernel
  protocol, launched by the core (`Fx.Interpreters`).
  When machines become a contribution type, `Fx.Machines` (FSharp.Core only) joins `sdkSurface`.

### Decisions

- **`Fx.Sdk.Process` is its own package.** A built-in runs in the binary and must not see the RPC
  engine; a process extension needs it. Putting the protocol's server in `Fx.Sdk` would widen every
  extension's surface with `Fx.Rpc` and the live interpreters. The protocol's messages live with the
  server (the host references `Fx.Sdk.Process` for them), so both sides use one definition.
- **`Fx.Models` is exposed** (T118): wire adapters and provider definitions are extensions' contributions
  (spec §10.1, §16.2), so the canonical model they translate is public. It is pure and references only
  Prelude, Contracts and Effects.
- **`Fx.Contracts.TypeScript` is exposed** (T117): an interpreter generates its kernel's declarations
  from the bindings' schemas (`InterpreterProvider.Bindings`), host-side, and the TypeScript one needs
  the generator. It is pure and references only Contracts.
- **The bun-ts kernel keeps its own JSON-RPC peer** rather than `@fx/sdk`'s: the kernel protocol frames
  every message on a line of its own after stray bytes, and the kernel ships as a few flat TypeScript
  files carried in the binary, without dependencies.
- **`Fx.Grants` is exposed** (T123): extensions request permissions (`IGrants`), add actions to the
  vocabulary and contribute grant stores and policy (spec §13.2, §16.2), so the grants log's model is
  public. It is pure, references only Contracts and Identity (Fable: clients fold the log with it), and
  its host side (the actor, the host-local store, the service, the `grants/*` methods) stays in
  `Fx.Grants.Host`, which is not public.
- **`Fx.Identity` is exposed** (T122): extensions contribute identity providers (spec §11.2, §16.2), whose
  contract speaks principals, credentials and roles, and the grants log reads a principal's kind from its
  id. It is pure and references only Contracts (Fable: clients decode `initialize`'s identity members
  with it); session tokens and the authenticator stay in `Fx.Identity.Host`, which is not public
  ([identity.md](identity.md)).
- **`Fx.Variants` is exposed**: extensions define slots (`VariantSlots.contributionType`, `variant-slot`,
  in the core catalog) and will read them through `IVariants` (T109b). It is pure and references only
  Contracts and Effects.
- **TypeScript values are the JSON shapes.** A TypeScript record is a plain object with the record's
  fields, a union an object with its tag, so the generated types (from the F# codecs) are the values'
  types, and nothing maps between a TypeScript class and its JSON.
- **Schemas have a wire form** (`Fx.Contracts.Codecs.schema`, below), so a TypeScript codec's schema
  is compared with the F# one exactly, not through JSON Schema, which loses names, unions, maps and
  tuples.

## Packages and versions

- `bun scripts/packages.ts` (after a Release build; CI runs it on every OS before the tests) packs every
  project with `<IsPackable>true</IsPackable>` into `artifacts/packages`, checks that each package id is
  `<FxPascalName>.<project name without "Fx.">` and that the packages depend only on each other and
  FSharp.Core, packs `@fx/sdk` with `bun pm pack`, and, outside the solution from those packages alone
  (`artifacts/package-consumer`), builds every package under `extensions/` from its sources against
  `<Name>.Sdk` (built-ins need nothing else) and the F# sample as a process extension against
  `<Name>.Sdk.Process`. The extension tests run that binary through the parity and server conformance
  suites (`PACKAGE_TESTS=1` requires it).
  CI uploads the packages from Linux. Publishing to nuget.org and npm comes with releases.
- **Package ids come from the naming constant:** `Directory.Build.targets` sets `PackageId` for packable
  projects from `FxPascalName` (`Directory.Build.props`, the same derivation as `Product.pascalName`);
  `packages/sdk/package.json`'s name is `Product.npmPackage "sdk"` (checked by
  `tests/Fx.Sdk.Process.Tests`). Never set a package id by hand.
- **Versions.** Packages carry the product's version (`Version`, `Product.Version`). What an extension
  is built against is `Versions.Sdk` (`major.minor`, the manifest's `sdk`) and `Versions.Protocol` (the
  extension protocol, the manifest's `protocol`); the TypeScript SDK gets both from the generated
  `product.ts`. A host admits extensions of its SDK major version with a minor version it has.
- **Compatibility policy.**
  - Before 1.0 (now `0.1`), any minor version may break extensions: bump `Versions.Sdk`'s minor version
    when a change to `Fx.Sdk`, what it exposes, or `@fx/sdk` would break an extension built against the
    previous one (a removed or renamed member, a changed codec or JSON form, a changed hook semantics).
  - From 1.0, minor versions only add; breaking changes need a new major version, and hosts keep
    admitting the previous major for a deprecation period.
  - A change an old extension process or host would misread is a new **protocol** version
    (`Versions.Protocol`), listed in `ExtensionProtocol.versions` next to the old one while both are
    served.
  - Generated contract types change only with the F# codecs, and the F# side's JSON forms are versioned
    as events are ([store.md](store.md)): add fields and cases, never change what an old one means.

## The TypeScript SDK (`packages/sdk`)

| Path | Contents |
|---|---|
| `src/generated/contracts.d.ts` | **Generated** from the F# codecs (`SdkDeclarations` in `tests/Fx.Sdk.Process.Tests`, with T102's generator): `Schema`, `Issue`, `Manifest` and its parts, `Decision`, `TransformSettings`, `SlotDecl`, every extension protocol message and typed error, `ExtensionRequests` (method → params, result, error), `ErrorCodes`, `ExtensionConstants`, the handshake, and the canonical model's types (`ChatRequest`, `ChatEvent`, `ChatResponse`, `ChatFailure`, `ModelInfo`, `ProviderDef` and what they use; types only, no runtime codecs yet) |
| `src/generated/product.ts` | **Generated** from `Fx.Product` and `Versions`: the product name, version, SDK and protocol versions, the manifest file name, `extensionId` |
| `src/json.ts`, `src/schema.ts`, `src/codec.ts` | JSON as the F# side writes and describes it; JSON Schema emitted as `Schema.toJsonSchema` does; the codec combinators |
| `src/manifest.ts`, `src/protocol.ts` | Runtime mirrors of the F# contracts: the manifest codec, `Manifest.check` and `Manifest.hash`; decisions, slots, the protocol's messages and typed errors |
| `src/extension.ts` | The extension API: `ExtensionEnv`, contribution types and hooks, `ExtMethod`, `Activation`, `Extension`, `Declare` |
| `src/rpc.ts`, `src/server.ts`, `src/stdio.ts` | JSON-RPC on NDJSON as the host speaks it; the protocol's server (`serveExtensions`); the process entry point (`runStdio`) |

Never edit the generated files: change the F# codecs and run `UPDATE_GOLDEN=1 dotnet test --project
tests/Fx.Sdk.Process.Tests`. The protocol constants and error codes are typed with the generated literal
types, so a change on the F# side that `src/` does not follow fails `bun check`.

### Codecs

The combinators mirror Fx.Contracts' (docs/modules/contracts.md): the same schema (in the wire form), the
same JSON, the same issues (paths and messages).

| F# (`Codec.`) | TypeScript (`Codec.`) | TypeScript value |
|---|---|---|
| `unit`, `bool`, `string`, `int`, `float`, `number`, `json` | `unit`, `bool`, `string`, `int`, `float`, `number`, `json` | `null`, `boolean`, `string`, `number`, … |
| `literal`, `enum` | `literal(v)`, `enum(["a", "b"])` | the literal, the strings |
| `list`, `array`, `option`, `stringMap`, `keyValues`, `tuple2`/`tuple3` | `array` (`list`), `nullable`, `stringMap`, `keyValues`, `tuple(...)` | `T[]`, `T \| null`, `Record<string, V>`, tuples |
| `record { field / optionalField / defaultField }` | `record({ name: codec, note: Codec.optional(c), tags: Codec.withDefault(c, []) })` | an object with those fields |
| `union "kind" { case0 … case4, caseOf }` | `union("kind", { done: { summary: Codec.string }, cancelled: {}, moved: Codec.caseOf(span) })` | `{ kind: "done"; summary: string } \| …` |
| `named`, `describe`, `describeCase`, `recursive`, `strict` | `.named`, `.describe`, `.describeCase`, `Codec.recursive`, `.strict()` | |
| `refine`, `minLength` … `maxItems`, `map`, `tryMap` | `.refine`, `.minLength` … `.maxItems`, `.map`, `.tryMap` | |

- Fields are written in the order given, unions with the tag first; `stringMap` sorts keys as F#'s `Map`
  does, `keyValues` keeps member order.
- **Known differences:** numbers are JavaScript numbers (exact to 2^53; described with JavaScript's
  shortest text, so `1e3` reads as `number 1000`); `JSON.parse` keeps the last of duplicate keys where
  the F# reader rejects them; there are no previews (the host previews values with the F# codecs).
- **The proof:** `tests/Fx.Sdk.Process.Tests` writes `packages/sdk/test/fixtures/codecs.json` (a domain
  using every combinator: each codec's schema, JSON Schema, and inputs with the output or the issues)
  and `protocol.json` (the schemas of the mirrored contracts, manifests with their checks and hashes);
  `bun test` defines the same codecs and must match them exactly.

### Schemas on the wire

`Codecs.schema` (Fx.Contracts) is the JSON form of `Schema`: tagged by `kind` (`any`, `null`, `boolean`,
`integer`, `number`, `string`, `const`, `enum`, `array`, `tuple`, `map`, `object`, `union`, `anyOf`,
`nullable`, `ref`, `named`), each case with its facets and an optional `description`; fields as
`{name, schema, presence}` (`presence` is `required`, `optional` or `defaulted` with its `value`), union
cases as `{tag, description?, fields, unknownFields}`, bounds as `{kind: "inclusive" | "exclusive",
value}`. It is strict: a facet a reader does not know is an issue, never a constraint silently dropped.
Changing it changes the generated `Schema` type and every fixture.

## Writing an extension in F#

A **built-in** (or any extension compiled into the binary) is a native extension: see
[extensions.md](extensions.md) and `extensions/Fx.Ext.Sample`. The same value runs as its own
**process** with `Fx.Sdk.Process`:

```fsharp
// A project referencing only <Name>.Sdk.Process (it brings Fx.Sdk and the rest).
[<EntryPoint>]
let main _ = ExtensionProcess.run "acme-tools" "1.2.0" [ Acme.Tools.extension ]
```

```json
{ "id": "acme.tools", "version": "1.2.0", "sdk": "0.1", "protocol": "1", "runtime": "process",
  "launch": { "exe": "${root}/bin/acme-tools${exeSuffix}", "envAllowlist": ["PATH", "DOTNET_ROOT*"] },
  "contributions": [ … ], "rpc": { "methods": [ … ] } }
```

`ExtensionProcess.run` serves the extensions on standard input and output with the system clock and
returns the exit code when the host closes standard input; whatever the extension writes to
`Console.Out` goes to standard error. Tests of an extension run its `Activate` in a `BasicExtensionEnv`
(or their own `IExtensionEnv`) and its contributions with `Hooks.*`.

## Writing an extension in TypeScript

```ts
import { Codec, Declare, Decision, ExtMethod, Hook, Manifest, contribution, err, ok, runStdio } from "@fx/sdk";

const greeting = Hook.gate("sample.greeting", Codec.record({ name: Codec.string, language: Codec.string }));
const divide = ExtMethod.define("divide", Codec.record({ dividend: Codec.int, divisor: Codec.int }), Codec.int)
  .withErrors(Codec.union("kind", { divideByZero: {} }), () => "division by zero");

const manifest: Manifest = {
  ...Manifest.create("acme.tools", "1.2.0", "bun"),
  contributions: [Declare.contribution(greeting, "polite")],
  rpc: { methods: [Declare.method(divide)] },
};

await runStdio([{
  manifest,
  activate: () => ({
    contributions: [contribution(greeting, "polite", { check: (g) => (g.name ? Decision.allow : Decision.deny("no name")) })],
    methods: [divide.handle((d) => (d.divisor === 0 ? err({ kind: "divideByZero" as const }) : ok(Math.trunc(d.dividend / d.divisor))))],
  }),
}], { peer: { name: "acme-tools", version: "1.2.0" } });
```

- Hook points belong to the host (or a native extension): name them with `Hook.gate`, `transform`,
  `observe` or `contribute` and the codec of what flows through them. A TypeScript extension cannot
  define contribution types or contribute interpreter middleware (both are in-process F#).
- Code runs in `ExtensionEnv` (`extension`, `signal`: the request's cancellation, `clock`). Throwing is a
  defect: a gate denies, a transform fails the pipeline, an observer is reported, a method is `Crashed`.
  Expected failures of a method are its typed errors (`err(...)`).
- `activationMismatches(manifest, activation)` lists what an activation provides beyond or short of its
  manifest, as the host checks it; use it in the extension's tests.
- Until T115 provides the `bun` runtime, a TypeScript extension runs with the `process` runtime: its
  manifest's `launch` starts the pinned Bun on its bundle (`bun build --target=bun`) with
  `fx.runtime.bun`'s hardened launch (flags, untrusted overrides, files, environment; T114), as
  `tests/Fx.Extensions.Tests/TypeScriptSample.fs` does. Import the SDK by its package name; inside this
  repository, by a relative path, so that no source hardcodes the product name.

## Testing

- **Transport parity** (`ParitySuite`, [extensions.md](extensions.md)): the sample in the host's process,
  behind the process runtime in memory, in a child process (`Fx.Aot.Scenarios ext-peer`, which runs
  `ExtensionProcess.run`), built from the SDK packages, and in TypeScript on the pinned Bun: one
  transcript.
- **Server conformance** (`tests/Fx.Extensions.Tests/Parity/ServerConformance.fs`): a function from a way
  to reach a process serving the sample's contract to tests of the protocol as the host sees it (the
  handshake, activation, every method's typed errors, defects, deactivation). The F# server in process,
  as a child, from the packages, and the TypeScript server on Bun pass it. A server in another language
  is done when it passes it with a sample implementing `Fx.Ext.Sample`'s contract.
- **The samples' contract:** the TypeScript sample's manifest is the F# sample's, member for member and
  hash for hash (`packages/sample-extension/test/fixtures/manifest.json`).
- **The pinned Bun** comes from `tests/Fx.Kernel.Tests/PinnedBun.fs` (linked into the extension tests);
  CI caches it. Offline, those tests are skipped locally and fail in CI. The launch is
  `fx.runtime.bun`'s (`RuntimeBun.launch`), with its owned directories under the package directory.
- CI: `bun check --no-script` and `bun test` in `packages/sdk` and `packages/sample-extension` (Lint),
  `bun scripts/packages.ts` and the suites on Linux, macOS and Windows (Build and test).

## Left to later tasks

| What | Task |
|---|---|
| Host capabilities in `IExtensionEnv` beyond the clock, attenuated to the manifest's `capabilities` (`IVariants` reads of an extension's slots, …), and over the protocol for extension processes (`host/*` requests from the process, in both SDKs); `IPackages`; reading contributions from extension code | T109b |
| The `bun` extension runtime (`fx.exthost.bun`), hosting TypeScript packages written with `@fx/sdk` in one process, and contributing `ExtensionRuntime` from TypeScript | T115 |
| Publishing the packages to nuget.org and npm | with the first release |
