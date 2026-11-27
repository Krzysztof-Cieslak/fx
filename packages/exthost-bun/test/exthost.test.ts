// The Bun extension host's runtime: loading packages from their directories (what fails, and how), and
// the process serving a loaded package, here over an in-memory connection with the TypeScript sample
// bundled as a package. The F# host runs the same process on the pinned Bun against the transport-parity
// and server conformance suites (tests/Fx.Extensions.Tests).

import { afterAll, describe, expect, test } from "bun:test";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Manifest, RpcPeer, Runtimes, activationMismatches, serveExtensions } from "../../sdk/src/index.ts";
import { manifest as sampleManifest } from "../../sample-extension/src/sample.ts";
import { entryFile, extension, id, load, manifest } from "../src/exthost.ts";
import fixture from "./fixtures/manifest.json";

const dirs: string[] = [];
afterAll(() => {
  for (const dir of dirs) rmSync(dir, { recursive: true, force: true });
});

/** A package directory with this module as its entry. */
function packageWith(code: string | undefined): string {
  const dir = mkdtempSync(join(tmpdir(), "exthost-"));
  dirs.push(dir);
  if (code !== undefined) writeFileSync(join(dir, entryFile), code);
  return dir;
}

const acme = Manifest.create("acme.tools", "1.2.0", Runtimes.bun);
const acmeModule = `export default { manifest: { id: "acme.tools", version: "1.2.0" }, activate: (env) => ({ contributions: [{ type: "acme.word", id: "w", kind: "data", json: env.extension, serve: undefined, value: env.extension }], methods: [] }) };`;

/** The sample bundled as a package for the `bun` runtime, as a package author builds one. */
async function samplePackage(): Promise<string> {
  const dir = packageWith(undefined);
  const built = await Bun.build({ entrypoints: [join(import.meta.dir, "../../sample-extension/src/sample.ts")], target: "bun", outdir: dir, naming: entryFile });
  expect(built.success).toBe(true);
  return dir;
}

describe("the Bun extension host", () => {
  test("declares the manifest Fx.Ext.ExthostBun ships, which adds how to launch it", () => {
    const { launch: _launch, ...served } = fixture.manifest as Record<string, unknown>;
    expect(Manifest.encode(manifest)).toEqual(served as never);
    const shipped = Manifest.decode(fixture.manifest);
    expect(shipped.ok ? [] : shipped.issues).toEqual([]);
    expect(id).toBe(fixture.manifest.id);
  });

  test("provides exactly what it declares", async () => {
    const activation = await extension.activate({ extension: id, signal: new AbortController().signal, clock: { now: () => new Date(0) } });
    expect(activationMismatches(manifest, activation)).toEqual([]);
  });

  test("a package that cannot be loaded fails with the reason", async () => {
    const reason = async (root: string | undefined, m = acme) => {
      const loaded = await load({ manifest: m, root });
      return loaded.ok ? "loaded" : loaded.error;
    };
    expect(await reason(undefined)).toBe("a package for the bun runtime needs its package directory");
    expect(await reason(packageWith(undefined))).toBe("the package has no extension.js");
    expect(await reason(packageWith("export default 1;"))).toBe("extension.js does not export an extension as its default export");
    expect(await reason(packageWith("export const extension = {};"))).toBe("extension.js does not export an extension as its default export");
    expect(await reason(packageWith("throw new RangeError('boom');"))).toBe("its module does not load (RangeError)");
    expect(await reason(packageWith(acmeModule), { ...acme, version: "2.0.0" })).toBe("its module is acme.tools 1.2.0, not acme.tools 2.0.0");
    expect(await reason(packageWith(acmeModule))).toBe("loaded");
  });

  test("the process serves the packages its runtime loads, each in its own environment", async () => {
    const waiting = new Map<number, (message: any) => void>();
    const peer = new RpcPeer({
      write: (frame) => {
        const message = JSON.parse(frame);
        waiting.get(message.id)?.(message);
      },
    });
    serveExtensions(peer, [extension], { peer: { name: id, version: "1.0.0" } });
    let next = 1;
    const send = (method: string, params: unknown): Promise<any> => {
      const n = next++;
      const answer = new Promise<any>((resolve) => waiting.set(n, resolve));
      peer.handleLine(JSON.stringify({ jsonrpc: "2.0", id: n, method, params }));
      return answer;
    };
    const init = await send("initialize", { protocolVersions: ["1"], peer: { name: "host", version: "0" }, capabilities: { maxMessageSize: 1 << 20 }, extensionProtocol: "1" });
    expect(init.result.serves).toEqual([id]);
    expect((await send("extension/activate", { extension: id })).result.contributions).toEqual([{ type: "extension-runtime", id: "bun" }]);

    const acmeAnswer = await send("extension/activate", { extension: "acme.tools", manifest: acme, root: packageWith(acmeModule) });
    expect(acmeAnswer.result.contributions).toEqual([{ type: "acme.word", id: "w", value: "acme.tools" }]);

    const sample = { ...sampleManifest, runtime: Runtimes.bun };
    const missing = await send("extension/activate", { extension: sample.id, manifest: sample, root: packageWith(undefined) });
    expect(missing.error.data).toEqual({ kind: "activationFailed", extension: sample.id, reason: "the package has no extension.js" });
    const activated = await send("extension/activate", { extension: sample.id, manifest: sample, root: await samplePackage() });
    expect(activated.result.methods).toEqual(["echo", "divide", "observed", "fail"]);
    const echo = await send("extension/call", { extension: sample.id, method: "echo", params: { text: "hi" } });
    expect(echo.result).toEqual({ output: { text: "hi" } });
  });
});
