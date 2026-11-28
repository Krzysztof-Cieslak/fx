// The extension protocol's server over an in-memory connection: the handshake, every method's typed
// errors, defects, cancellation and deactivation. The F# host runs the same server against the
// transport-parity and server conformance suites (tests/Fx.Extensions.Tests) on the pinned Bun.

import { describe, expect, test } from "bun:test";
import { Codec } from "../src/codec.ts";
import {
  type Activation,
  Declare,
  Decision,
  type Extension,
  ExtMethod,
  type ExtensionRuntime,
  ExtensionRuntimes,
  Hook,
  Transform,
  activationMismatches,
  basicEnv,
  contribution,
  err,
  ok,
} from "../src/extension.ts";
import { Manifest } from "../src/manifest.ts";
import { RpcPeer, errorCodes } from "../src/rpc.ts";
import { serveExtensions } from "../src/server.ts";

const greeting = Hook.gate("test.greeting", Codec.record({ name: Codec.string }));
const text = Hook.transform("test.text", Codec.string);
const words = Hook.contribute("test.word", Codec.string);
const notes = Hook.observe("test.note", Codec.string);
const divide = ExtMethod.define("divide", Codec.record({ dividend: Codec.int, divisor: Codec.int }), Codec.int).withErrors(
  Codec.union("kind", { divideByZero: {} }),
  () => "division by zero",
);
const fail = ExtMethod.define("fail", Codec.unit, Codec.unit);
const wait = ExtMethod.define("wait", Codec.unit, Codec.unit);

const seen: string[] = [];
let deactivated = 0;

const extension: Extension = {
  manifest: {
    ...Manifest.create("test.ext", "1.0.0", "bun"),
    contributions: [
      Declare.contribution(greeting, "polite"),
      Declare.contribution(text, "shout"),
      Declare.contribution(words, "alpha"),
      Declare.contribution(notes, "recorder"),
    ],
    rpc: { methods: [Declare.method(divide), Declare.method(fail), Declare.method(wait)] },
  },
  activate: (env): Activation => ({
    contributions: [
      contribution(greeting, "polite", { check: (g) => (g.name === "" ? Decision.deny("no name") : Decision.allow) }),
      contribution(text, "shout", Transform.ofFunction(20, (s: string) => s.toUpperCase())),
      contribution(words, "alpha", "alpha"),
      contribution(notes, "recorder", { observe: (n) => void seen.push(`${env.extension}: ${n}`) }),
    ],
    methods: [
      divide.handle((d) => (d.divisor === 0 ? err({ kind: "divideByZero" as const }) : ok(Math.trunc(d.dividend / d.divisor)))),
      fail.handle(() => {
        throw new TypeError("the method failed");
      }),
      wait.handle(
        (_, env) =>
          new Promise((_resolve, reject) => {
            env.signal.addEventListener("abort", () => reject(new Error("aborted")));
          }),
      ),
    ],
    deactivate: () => void deactivated++,
  }),
};

/** A host on the other end of an in-memory connection. */
function connect(served: readonly Extension[] = [extension]) {
  const frames: any[] = [];
  const waiting = new Map<number, (message: any) => void>();
  const peer = new RpcPeer({
    write: (frame) => {
      const message = JSON.parse(frame);
      frames.push(message);
      waiting.get(message.id)?.(message);
    },
  });
  serveExtensions(peer, served, { peer: { name: "test", version: "1.0.0" } });
  let next = 1;
  const send = (method: string, params: unknown): Promise<any> => {
    const id = next++;
    const answer = new Promise<any>((resolve) => waiting.set(id, resolve));
    peer.handleLine(JSON.stringify({ jsonrpc: "2.0", id, method, params }));
    return answer;
  };
  const initialize = (members: Record<string, unknown>) =>
    send("initialize", { protocolVersions: ["1"], peer: { name: "host", version: "0" }, capabilities: { maxMessageSize: 1 << 20 }, ...members });
  return { peer, frames, send, initialize };
}

async function ready() {
  const connection = connect();
  const answer = await connection.initialize({ extensionProtocol: "1" });
  expect(answer.result.serves).toEqual(["test.ext"]);
  return connection;
}

describe("the extension protocol's server", () => {
  test("refuses hosts that do not speak the extension protocol, and stays in the handshake", async () => {
    const { initialize } = connect();
    const refused = await initialize({});
    expect(refused.error.code).toBe(errorCodes.invalidRequest);
    expect(refused.error.message).toBe("the host must speak extension protocol 1 (initialize member extensionProtocol)");
    const accepted = await initialize({ extensionProtocol: "1" });
    expect(accepted.result.capabilities.methods).toEqual(["extension/activate", "extension/call", "extension/deactivate", "extension/invoke"]);
  });

  test("activation describes the contributions; activating twice answers the same", async () => {
    const { send } = await ready();
    const unknown = await send("extension/activate", { extension: "nope" });
    expect(unknown.error).toEqual({
      code: errorCodes.applicationError,
      message: "unknown extension nope",
      data: { kind: "unknownExtension", extension: "nope" },
    });
    const first = await send("extension/activate", { extension: "test.ext" });
    const second = await send("extension/activate", { extension: "test.ext" });
    expect(second.result).toEqual(first.result);
    expect(first.result.contributions).toEqual([
      { type: "gate:test.greeting", id: "polite" },
      { type: "transform:test.text", id: "shout", value: { priority: 20 } },
      { type: "test.word", id: "alpha", value: "alpha" },
      { type: "observe:test.note", id: "recorder" },
    ]);
    expect(first.result.methods).toEqual(["divide", "fail", "wait"]);
    expect(first.result.manifest).toEqual(Manifest.encode(extension.manifest) as never);
  });

  test("a package for a runtime the process does not contribute fails to activate", async () => {
    const { send } = await ready();
    const manifest = Manifest.create("acme.other", "1.0.0", "lua");
    const answer = await send("extension/activate", { extension: "acme.other", manifest });
    expect(answer.error.data).toEqual({ kind: "activationFailed", extension: "acme.other", reason: "this process has no extension runtime lua" });
  });

  test("invocations: not active, unknown, invalid input, data, gates, transforms and observers", async () => {
    const { send } = await ready();
    const invoke = (type: string, id: string, input: unknown) => send("extension/invoke", { extension: "test.ext", type, id, input });
    expect((await invoke("gate:test.greeting", "polite", { name: "x" })).error.data).toEqual({ kind: "notActive", extension: "test.ext" });
    await send("extension/activate", { extension: "test.ext" });
    expect((await invoke("gate:test.greeting", "nope", {})).error.data).toEqual({
      kind: "unknownContribution",
      address: "test.ext/gate:test.greeting/nope",
    });
    expect((await invoke("gate:test.greeting", "polite", { name: 1 })).error.data).toEqual({
      kind: "invalid",
      issues: [{ path: ["name"], message: "expected string, got number 1" }],
    });
    expect((await invoke("test.word", "alpha", null)).error.data).toEqual({ kind: "inProcessOnly", address: "test.ext/test.word/alpha" });
    expect((await invoke("gate:test.greeting", "polite", { name: "" })).result).toEqual({ output: { decision: "deny", reason: "no name" } });
    expect((await invoke("transform:test.text", "shout", "hi")).result).toEqual({ output: "HI" });
    expect((await invoke("observe:test.note", "recorder", "seen")).result).toEqual({ output: null });
    expect(seen).toContain("test.ext: seen");
  });

  test("calls: results, typed errors, invalid params, unknown methods and defects", async () => {
    const { send } = await ready();
    await send("extension/activate", { extension: "test.ext" });
    const call = (method: string, params: unknown) => send("extension/call", { extension: "test.ext", method, params });
    expect((await call("divide", { dividend: 7, divisor: 2 })).result).toEqual({ output: 3 });
    expect((await call("divide", { dividend: 1, divisor: 0 })).error).toEqual({
      code: errorCodes.applicationError,
      message: "division by zero",
      data: { kind: "methodFailed", message: "division by zero", data: { kind: "divideByZero" } },
    });
    expect((await call("divide", { dividend: 1 })).error.data).toEqual({
      kind: "invalid",
      issues: [{ path: ["divisor"], message: "missing required field" }],
    });
    expect((await call("nope", null)).error.data).toEqual({ kind: "unknownMethod", method: "nope" });
    const crashed = await call("fail", null);
    expect(crashed.error).toEqual({ code: errorCodes.internalError, message: "internal error (TypeError)" });
  });

  test("a cancelled request aborts the extension's signal and is answered RequestCancelled", async () => {
    const { send, peer } = await ready();
    await send("extension/activate", { extension: "test.ext" });
    const answer = send("extension/call", { extension: "test.ext", method: "wait", params: null });
    peer.handleLine(JSON.stringify({ jsonrpc: "2.0", method: "$/cancelRequest", params: { id: 3 } }));
    expect((await answer).error.code).toBe(errorCodes.requestCancelled);
  });

  test("deactivation releases the activation; the extension is then not active", async () => {
    const { send } = await ready();
    await send("extension/activate", { extension: "test.ext" });
    const before = deactivated;
    expect((await send("extension/deactivate", { extension: "test.ext" })).result).toBeNull();
    expect(deactivated).toBe(before + 1);
    expect((await send("extension/deactivate", { extension: "test.ext" })).error.data).toEqual({ kind: "notActive", extension: "test.ext" });
  });

  test("requests before initialize, malformed lines and unknown methods follow JSON-RPC", async () => {
    const { send, peer, frames } = connect();
    expect((await send("extension/activate", { extension: "test.ext" })).error.code).toBe(errorCodes.notInitialized);
    peer.handleLine("not json");
    expect(frames.at(-1)).toEqual({ jsonrpc: "2.0", id: null, error: { code: errorCodes.parseError, message: "parse error: not JSON" } });
    await send("initialize", { protocolVersions: ["1"], peer: { name: "h", version: "0" }, capabilities: { maxMessageSize: 4096 }, extensionProtocol: "1" });
    expect((await send("nope", {})).error.code).toBe(errorCodes.methodNotFound);
    expect((await send("extension/invoke", { extension: 1 })).error.code).toBe(errorCodes.invalidParams);
  });

  test("activationMismatches lists what an activation provides beyond or short of its manifest", async () => {
    const activation = await extension.activate(basicEnv("test.ext", new AbortController().signal, { now: () => new Date(0) }));
    expect(activationMismatches(extension.manifest, activation)).toEqual([]);
    expect(activationMismatches({ ...extension.manifest, contributions: [], rpc: { methods: [] } }, activation)).toHaveLength(7);
  });

  test("a runtime an extension contributes loads packages, which the process then serves", async () => {
    const requests: string[] = [];
    const echo: ExtensionRuntime = {
      activate: async (request, env) => {
        requests.push(`${env.extension} loads ${request.manifest.id} from ${request.root} for ${request.environment.extension}`);
        if (request.manifest.id === "acme.broken") return err("its code does not load");
        if (request.manifest.id === "acme.defect") throw new TypeError("the runtime broke");
        return ok({
          contributions: [contribution(words, "w", request.manifest.id)],
          methods: [divide.handle((d) => ok(d.dividend))],
        });
      },
    };
    const host: Extension = {
      manifest: { ...Manifest.create("test.host", "1.0.0", "process"), contributions: [Declare.contribution(ExtensionRuntimes.contributionType, "echo")] },
      activate: () => ({ contributions: [contribution(ExtensionRuntimes.contributionType, "echo", echo)], methods: [] }),
    };
    const { send, initialize } = connect([host]);
    await initialize({ extensionProtocol: "1" });
    const manifest = (id: string) => ({ ...Manifest.create(id, "1.0.0", "echo"), contributions: [Declare.contribution(words, "w")], rpc: { methods: [Declare.method(divide)] } });

    const before = await send("extension/activate", { extension: "acme.tools", manifest: manifest("acme.tools"), root: "/pkg" });
    expect(before.error.data).toEqual({ kind: "activationFailed", extension: "acme.tools", reason: "this process has no extension runtime echo" });

    const runtimeHost = await send("extension/activate", { extension: "test.host" });
    expect(runtimeHost.result.contributions).toEqual([{ type: "extension-runtime", id: "echo" }]);
    expect((await send("extension/invoke", { extension: "test.host", type: "extension-runtime", id: "echo", input: null })).error.data).toEqual({
      kind: "inProcessOnly",
      address: "test.host/extension-runtime/echo",
    });

    const loaded = await send("extension/activate", { extension: "acme.tools", manifest: manifest("acme.tools"), root: "/pkg" });
    expect(loaded.result.contributions).toEqual([{ type: "test.word", id: "w", value: "acme.tools" }]);
    expect(loaded.result.manifest.id).toBe("acme.tools");
    expect((await send("extension/activate", { extension: "acme.tools", manifest: manifest("acme.tools"), root: "/pkg" })).result).toEqual(loaded.result);
    expect(requests).toEqual(["test.host loads acme.tools from /pkg for acme.tools"]);
    expect((await send("extension/call", { extension: "acme.tools", method: "divide", params: { dividend: 6, divisor: 3 } })).result).toEqual({ output: 6 });

    const broken = await send("extension/activate", { extension: "acme.broken", manifest: manifest("acme.broken") });
    expect(broken.error.data).toEqual({ kind: "activationFailed", extension: "acme.broken", reason: "its code does not load" });
    expect((await send("extension/call", { extension: "acme.broken", method: "divide", params: {} })).error.data).toEqual({ kind: "notActive", extension: "acme.broken" });
    const defect = await send("extension/activate", { extension: "acme.defect", manifest: manifest("acme.defect") });
    expect(defect.error).toEqual({ code: errorCodes.internalError, message: "internal error (TypeError)" });

    expect((await send("extension/deactivate", { extension: "acme.tools" })).result).toBeNull();
    expect((await send("extension/call", { extension: "acme.tools", method: "divide", params: {} })).error.data).toEqual({ kind: "notActive", extension: "acme.tools" });
  });
});
