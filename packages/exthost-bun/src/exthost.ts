// The Bun extension host (`fx.exthost.bun`, spec §6.3, §16.1): a process extension, launched on the pinned
// Bun with `fx.runtime.bun`'s hardened launch, that contributes the extension runtime `bun`. The host
// activates a package whose manifest names `bun` through this process (`extension/activate` with the
// manifest and the package directory); the runtime imports the package's module and activates its
// extension in the extension's own environment, and the process serves it from then on. One process
// hosts every Bun package of a host. See README.md and docs/modules/extensions.md.

import { join } from "node:path";
import { pathToFileURL } from "node:url";
import {
  type Activation,
  Declare,
  type Extension,
  type ExtensionRuntime,
  ExtensionRuntimes,
  Manifest,
  type MethodResult,
  Runtimes,
  contribution,
  err,
  extensionId,
  ok,
} from "../../sdk/src/index.ts";

/** The extension's id: `fx.exthost.bun`. */
export const id = extensionId("exthost.bun");

/** The runtime it contributes. */
export const runtimeName = Runtimes.bun;

/**
 * The module of a package for the `bun` runtime, in its package directory next to its manifest: one ES
 * module whose default export is the extension (`export default extension`), bundled with its
 * dependencies (`bun build --target=bun`), since the hardened launch installs nothing.
 */
export const entryFile = "extension.js";

/** The manifest the process serves; the host's (Fx.Ext.ExthostBun) adds how to launch it. */
export const manifest: Manifest = {
  ...Manifest.create(id, "1.0.0", Runtimes.process),
  description: "The Bun extension host: the extension runtime bun, which runs TypeScript extensions on the pinned Bun.",
  requires: [{ id: "bun", version: "1.4.5" }],
  contributions: [Declare.contribution(ExtensionRuntimes.contributionType, runtimeName)],
};

/** The name of what was thrown, never its message (messages are not part of a failure's contract). */
function nameOf(thrown: unknown): string {
  return thrown instanceof Error ? thrown.name : typeof thrown;
}

function isExtension(value: unknown): value is Extension {
  if (typeof value !== "object" || value === null) return false;
  const candidate = value as { manifest?: unknown; activate?: unknown };
  const m = candidate.manifest as { id?: unknown; version?: unknown } | undefined;
  return typeof candidate.activate === "function" && typeof m === "object" && m !== null && typeof m.id === "string" && typeof m.version === "string";
}

/**
 * Loads a package's extension: imports its module (each package's once per process) and checks that its
 * default export is the extension the manifest describes (the same id and version). The host checks
 * that the activation provides exactly what the manifest declares.
 */
export async function load(request: { manifest: Manifest; root: string | undefined }): Promise<MethodResult<Extension, string>> {
  if (request.root === undefined) return err("a package for the bun runtime needs its package directory");
  const entry = join(request.root, entryFile);
  if (!(await Bun.file(entry).exists())) return err(`the package has no ${entryFile}`);
  let module: { default?: unknown };
  try {
    module = await import(pathToFileURL(entry).href);
  } catch (thrown) {
    console.error(`loading ${entry} failed: ${thrown instanceof Error ? (thrown.stack ?? thrown.message) : String(thrown)}`);
    return err(`its module does not load (${nameOf(thrown)})`);
  }
  const extension = module.default;
  if (!isExtension(extension)) return err(`${entryFile} does not export an extension as its default export`);
  const { id: found, version } = extension.manifest;
  if (found !== request.manifest.id || version !== request.manifest.version) {
    return err(`its module is ${found} ${version}, not ${request.manifest.id} ${request.manifest.version}`);
  }
  return ok(extension);
}

/** The `bun` runtime: loads a package, then activates its extension in that extension's environment. */
export const runtime: ExtensionRuntime = {
  async activate(request): Promise<MethodResult<Activation, string>> {
    const loaded = await load(request);
    if (!loaded.ok) return loaded;
    return ok(await loaded.value.activate(request.environment));
  },
};

/** The Bun extension host as an extension: it contributes the runtime and nothing else. */
export const extension: Extension = {
  manifest,
  activate: () => ({ contributions: [contribution(ExtensionRuntimes.contributionType, runtimeName, runtime)], methods: [] }),
};
