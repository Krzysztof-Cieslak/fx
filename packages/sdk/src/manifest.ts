// Manifests (spec §16.1): the codec, the checks across fields and the hash, mirroring Fx.Sdk's
// `Manifest` module, so that a TypeScript extension's manifest is what the host reads, reviews and
// pins. test/fixtures/protocol.json (written by the F# tests) holds the F# schema, decoded manifests,
// their issues and their hashes.

import { createHash } from "node:crypto";
import { Codec, Validation, type Issue, issueAt } from "./codec.ts";
import type {
  ComponentRef,
  ContributionDecl,
  LaunchSpec,
  Manifest as ManifestJson,
  MethodDecl,
  RpcContract,
} from "./generated/contracts.d.ts";
import { injectionVectors, protocolVersion, sdkVersion } from "./generated/product.ts";
import { type Json, quote, serializeCanonical } from "./json.ts";

export type { ComponentRef, ContributionDecl, LaunchSpec, MethodDecl, RpcContract } from "./generated/contracts.d.ts";

/** An extension package's manifest, as JSON (the generated type). */
export type Manifest = ManifestJson;

/** The extension runtimes of the core; others are contributed (`bun`, from the Bun extension host). */
export const Runtimes = {
  /** Data only: the manifest's contributions are the extension. */
  declarative: "declarative",
  /** F# compiled into the host's binary. */
  native: "native",
  /** A child process speaking the extension protocol over JSON-RPC on stdio, in any language. */
  process: "process",
  /** TypeScript on the pinned Bun, hosted by the Bun extension host (T115). */
  bun: "bun",
} as const;

/** Patterns of the names in a manifest (ECMAScript, anchored), as `Manifest.Patterns`. */
export const Patterns = {
  extensionId: "^[a-z][a-z0-9-]*(\\.[a-z0-9][a-z0-9-]*)*$",
  version: "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\\+[0-9A-Za-z.-]+)?$",
  runtime: "^[a-z][a-z0-9-]*$",
  contributionType: "^[a-z][a-z0-9-]*([.:/][a-z0-9][a-z0-9-]*)*$",
  contributionId: "^[a-z0-9][a-z0-9._-]*$",
  capability: "^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*)*$",
  methodName: "^[a-z][A-Za-z0-9]*(/[a-z][A-Za-z0-9]*)*$",
  envName: "^[A-Za-z_][A-Za-z0-9_]*\\*?$",
} as const;

const named = (pattern: string, description: string) => Codec.string.pattern(pattern).describe(description);

const extensionId = named(Patterns.extensionId, 'An extension id: dotted lowercase segments, e.g. "acme.tools".');
const semver = named(Patterns.version, 'A semantic version, e.g. "1.2.0".');

const placement = Codec.enum(["host", "environment", "client"]).describe(
  "Where the extension runs: in the host, in an environment (envd) or in a client.",
);

const componentRef: Codec<ComponentRef> = Codec.record({ id: Codec.string.nonEmpty(), version: Codec.string.nonEmpty() })
  .strict()
  .describe("A component to install before the extension starts: its id and a version range.")
  .named("ComponentRef");

const envName = named(Patterns.envName, "An environment variable name; in deny lists, a trailing * matches any suffix.");

const launchSpec = Codec.record({
  exe: Codec.string.nonEmpty(),
  args: Codec.withDefault(Codec.array(Codec.string), []),
  envAllowlist: Codec.withDefault(Codec.array(envName), []),
  envSet: Codec.withDefault(Codec.keyValues(Codec.string), {}),
  envDeny: Codec.withDefault(Codec.array(envName), []),
  cwd: Codec.withDefault(Codec.enum(["owned", "workspace"]), "owned"),
  untrustedOverrides: Codec.optional(
    Codec.array(Codec.string).describe("Runtime flags (--name=value) added before the program while the project is not trusted."),
  ),
})
  .strict()
  .describe(
    "How to start a process: an extension process, or a runtime. exe and args are templates, e.g. ${root} (the package directory) and ${exeSuffix}.",
  )
  .named("LaunchSpec")
  // Overrides are written only when there are some, as the F# side does (adding the field changed no hash).
  .map(
    (spec) => spec,
    ({ untrustedOverrides, ...spec }) => (untrustedOverrides === undefined || untrustedOverrides.length === 0 ? spec : { ...spec, untrustedOverrides }),
  ) as Codec<LaunchSpec>;

const contributionDecl: Codec<ContributionDecl> = Codec.record({
  type: named(Patterns.contributionType, 'A contribution type, e.g. "extension-runtime".'),
  id: named(Patterns.contributionId, "The contribution's id, unique for its type."),
  value: Codec.optional(Codec.json),
})
  .strict()
  .describe("A contribution: its type and id. Declarative extensions give its value; other runtimes provide it when they activate.")
  .named("ContributionDecl");

const methodDecl: Codec<MethodDecl> = Codec.record({
  name: named(Patterns.methodName, "The method's name; clients call ext/<extension id>/<name>."),
  description: Codec.optional(Codec.string),
  params: Codec.optional(Codec.json),
  result: Codec.optional(Codec.json),
})
  .strict()
  .describe("A method of the extension's RPC contract; params and result are JSON Schemas.")
  .named("MethodDecl");

const rpcContract = Codec.record({ methods: Codec.withDefault(Codec.array(methodDecl), []) })
  .strict()
  .named("RpcContract") as Codec<RpcContract>;

/** The manifest codec: unknown members are issues. `Manifest.decode` adds the checks across fields. */
const manifestCodec = Codec.record({
  id: extensionId,
  version: semver,
  description: Codec.optional(Codec.string),
  sdk: Codec.string.describe('The SDK API version the extension is built against, e.g. "0.1".'),
  protocol: Codec.string.nonEmpty().describe('The extension protocol version it speaks, e.g. "1".'),
  placement: Codec.withDefault(placement, "host"),
  runtime: named(Patterns.runtime, "The extension runtime: declarative, native, process, or a contributed one."),
  launch: Codec.optional(launchSpec),
  requires: Codec.withDefault(Codec.array(componentRef), []),
  capabilities: Codec.withDefault(Codec.array(named(Patterns.capability, 'A capability name, e.g. "fs.read".')), []),
  contributions: Codec.withDefault(Codec.array(contributionDecl), []),
  rpc: Codec.withDefault(rpcContract, { methods: [] }),
  replaces: Codec.withDefault(Codec.array(extensionId), []),
  hash: Codec.optional(Codec.string),
})
  .strict()
  .describe("An extension package's manifest.")
  .named("Manifest") as Codec<Manifest>;

/**
 * Runtime injection vectors (spec §18): variables that make a runtime load code before the program
 * does. The host always removes them from an extension process's environment; a manifest may not set
 * them (`Manifest.deniedEnvironment`, generated from `InjectionVectors`).
 */
export const deniedEnvironment: readonly string[] = injectionVectors;

/** Whether a variable name matches a deny pattern (exact, or a prefix ending in `*`). */
export function matchesEnvPattern(ignoreCase: boolean, pattern: string, name: string): boolean {
  const norm = (s: string) => (ignoreCase ? s.toUpperCase() : s);
  return pattern.endsWith("*") ? norm(name).startsWith(norm(pattern.slice(0, -1))) : norm(name) === norm(pattern);
}

function duplicates(names: readonly string[]): string[] {
  const counts = new Map<string, number>();
  for (const name of names) counts.set(name, (counts.get(name) ?? 0) + 1);
  return [...counts.entries()].filter(([, n]) => n > 1).map(([name]) => name);
}

const sdkVersionPattern = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/;

/** The manifest's content hash: `sha256:<hex>` of the canonical JSON of the manifest without `hash`. */
function hash(manifest: Manifest): string {
  const { hash: _, ...rest } = manifest;
  const canonical = serializeCanonical(manifestCodec.encode(rest as Manifest));
  return "sha256:" + createHash("sha256").update(canonical, "utf8").digest("hex");
}

/** Checks across fields that the codec cannot express; each issue has a path (`Manifest.check`). */
function check(manifest: Manifest): Issue[] {
  const issues: Issue[] = [];
  const contributions = manifest.contributions ?? [];
  const methods = manifest.rpc?.methods ?? [];
  const capabilities = manifest.capabilities ?? [];
  const replaces = manifest.replaces ?? [];
  if (!sdkVersionPattern.test(manifest.sdk)) issues.push(issueAt(["sdk"], `expected a version major.minor, e.g. "${sdkVersion}"`));
  const declarative = manifest.runtime === Runtimes.declarative;
  contributions.forEach((c, i) => {
    if (c.value === undefined && declarative) {
      issues.push(issueAt(["contributions", i], 'a declarative extension gives each contribution\'s value; add "value"'));
    } else if (c.value !== undefined && !declarative) {
      issues.push(
        issueAt(
          ["contributions", i, "value"],
          `only declarative extensions give values in the manifest; a ${manifest.runtime} extension provides them when it activates`,
        ),
      );
    }
  });
  for (const dup of duplicates(contributions.map((c) => c.type + "/" + c.id))) {
    issues.push(issueAt(["contributions"], `the contribution ${quote(dup)} is declared twice`));
  }
  for (const dup of duplicates(methods.map((m) => m.name))) issues.push(issueAt(["rpc", "methods"], `the method ${quote(dup)} is declared twice`));
  if (declarative && methods.length > 0) issues.push(issueAt(["rpc", "methods"], "a declarative extension has no code, so no methods"));
  for (const dup of duplicates(capabilities)) issues.push(issueAt(["capabilities"], `the capability ${quote(dup)} is listed twice`));
  for (const dup of duplicates(replaces)) issues.push(issueAt(["replaces"], `${quote(dup)} is listed twice`));
  if (replaces.includes(manifest.id)) issues.push(issueAt(["replaces"], "an extension cannot replace itself"));
  const launch = manifest.launch;
  if (launch === undefined && manifest.runtime === Runtimes.process) {
    issues.push(issueAt(["launch"], 'a process extension needs "launch": how to start it'));
  } else if (launch !== undefined && (declarative || manifest.runtime === Runtimes.native)) {
    issues.push(issueAt(["launch"], `a ${manifest.runtime} extension is not started as a process; remove "launch"`));
  } else if (launch !== undefined) {
    Object.keys(launch.envSet ?? {}).forEach((name, i) => {
      const denied = [...deniedEnvironment, ...(launch.envDeny ?? [])].some((pattern) => matchesEnvPattern(true, pattern, name));
      if (denied) issues.push(issueAt(["launch", "envSet", i], `${name} is denied: it can inject code into the runtime`));
      if (name.includes("*")) issues.push(issueAt(["launch", "envSet", i], "a variable name cannot contain *"));
    });
    (launch.untrustedOverrides ?? []).forEach((flag, i) => {
      if (!flag.startsWith("-")) issues.push(issueAt(["launch", "untrustedOverrides", i], "an override is a runtime flag, written --name=value"));
    });
  }
  if (manifest.hash !== undefined && manifest.hash !== hash(manifest)) {
    issues.push(issueAt(["hash"], `does not match the manifest's content; its hash is ${quote(hash(manifest))}`));
  }
  return issues;
}

/** Manifests: building, reading, checking and hashing them, as Fx.Sdk's `Manifest` module does. */
export const Manifest = {
  /** The manifest codec (strict). */
  codec: manifestCodec,
  launchSpec,
  contributionDecl,
  methodDecl,
  componentRef,
  /** A manifest with the required fields, this SDK's versions, and nothing else. */
  create(id: string, version: string, runtime: string): Manifest {
    return {
      id,
      version,
      sdk: sdkVersion,
      protocol: protocolVersion,
      placement: "host",
      runtime,
      requires: [],
      capabilities: [],
      contributions: [],
      rpc: { methods: [] },
      replaces: [],
    };
  },
  check,
  hash,
  /** Decodes and checks a manifest: every issue, with its path. */
  decode(json: unknown): Validation<Manifest> {
    const decoded = manifestCodec.decode(json);
    if (!decoded.ok) return decoded;
    const issues = check(decoded.value);
    return issues.length === 0 ? decoded : Validation.fail(issues);
  },
  /** The manifest's JSON, with every defaulted member written. */
  encode(manifest: Manifest): Json {
    return manifestCodec.encode(manifest);
  },
};
