import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { existsSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { KernelHost } from "../src/host.ts";
import { bunPin } from "../src/pinned-bun.ts";
import { kernelGlobal } from "../src/product.ts";
import { ErrorCodes, RpcError } from "../src/rpc.ts";
import type { CellError, CellRunResult } from "../src/kernel/protocol.ts";

const g = kernelGlobal;
const SLOW = 180_000;

function expectError<K extends CellError["kind"]>(r: CellRunResult, kind: K): Extract<CellError, { kind: K }> {
  expect(r.ok).toBe(false);
  expect(r.error?.kind).toBe(kind);
  return r.error as Extract<CellError, { kind: K }>;
}

/**
 * The rejection of `promise`. Do not use `expect(promise).rejects` for promises that wait on the
 * kernel: bun test waits for them in a nested event loop that, on Windows, does not read the
 * kernel's pipes, so the promise never settles (Bun 1.4.5).
 */
async function rejection(promise: Promise<unknown>): Promise<unknown> {
  try {
    await promise;
  } catch (e) {
    return e;
  }
  throw new Error("expected a rejection");
}

async function eventually(predicate: () => boolean, ms = 5000): Promise<void> {
  const until = Date.now() + ms;
  while (!predicate()) {
    if (Date.now() > until) throw new Error("condition not met in time");
    await Bun.sleep(10);
  }
}

describe("kernel", () => {
  let k: KernelHost;
  beforeAll(async () => {
    k = await KernelHost.launch();
  }, SLOW);
  afterAll(async () => {
    expect(await k.close()).toBe(0);
  });

  test("initialize reports the pinned runtime and the global from the launcher", () => {
    expect(k.info).toEqual({ protocolVersion: 1, global: g, bun: bunPin.version, typescript: bunPin.typescript, moduleGraph: true });
  });

  test("declarations persist across cells", async () => {
    expect((await k.run(`const base: number = 40`)).declared).toEqual(["base"]);
    await k.run(`let counter = 0; function bump(by = 1) { counter += by; return counter }`);
    await k.run(`class Point { constructor(public x: number, public y: number) {} norm() { return Math.hypot(this.x, this.y) } }`);
    await k.run(`enum Color { Red, Green }`);
    await k.run(`const { a, nested: [b] } = { a: 1, nested: [2] }`);
    const r = await k.run(`bump(base); bump(); [counter, new Point(3, 4).norm(), Color.Green, a + b]`);
    expect(r.value?.text).toBe("[ 41, 5, 1, 3 ]");
    expect(r.value?.type).toBe("Array(4)");
  });

  test("imports persist and resolve through the pinned runtime", async () => {
    await k.run(`import { join as joinPath } from "node:path"; import * as os from "node:os"`);
    const r = await k.run(`[joinPath("a", "b").length, typeof os.platform()]`);
    expect(r.value?.text).toBe("[ 3, 'string' ]");
  });

  test("top-level await", async () => {
    const r = await k.run(`const later = await new Promise<string>((resolve) => setTimeout(() => resolve("done"), 20)); later`);
    expect(r.ok).toBe(true);
    expect(r.value?.text).toBe("'done'");
    expect((await k.run(`later.toUpperCase()`)).value?.text).toBe("'DONE'");
  });

  test("an object literal is a value, not a block", async () => {
    expect((await k.run(`{ answer: 42 }`)).value?.text).toBe("{ answer: 42 }");
  });

  test("previews are bounded", async () => {
    const r = await k.run(`Array.from({ length: 10_000 }, (_, i) => ({ i, label: "x".repeat(50) }))`);
    expect(r.value?.type).toBe("Array(10000)");
    expect(r.value!.text.length).toBeLessThanOrEqual(2_001 + 1);
    expect(r.value!.text).toContain("more items");
  });

  test("console output is captured and bounded with head and tail", async () => {
    const small = await k.run(`console.log("hello", { a: 1 }); console.error("oops")`);
    expect(small.output).toEqual({ text: "hello { a: 1 }\n[error] oops\n", truncated: false, totalChars: 28 });
    const big = await k.run(`for (let i = 0; i < 5000; i++) console.log("line " + i)`);
    expect(big.output.truncated).toBe(true);
    expect(big.output.totalChars).toBeGreaterThan(40_000);
    expect(big.output.text.startsWith("line 0\nline 1\n")).toBe(true);
    expect(big.output.text.endsWith("line 4999\n")).toBe(true);
    expect(big.output.text).toContain("chars omitted");
    expect(big.output.text.length).toBeLessThan(16_100);
  });

  test("syntax errors carry the cell position", async () => {
    const e = expectError(await k.run(`const ok = 1;\nconst broken = ;`), "syntax");
    expect(e).toMatchObject({ line: 2, column: 16, lineText: "const broken = ;" });
  });

  test("runtime errors are mapped back to cell positions, across cells", async () => {
    const definer = await k.run(`function explode(n: number): never {\n  if (n > 0) throw new RangeError("too big: " + n)\n  throw new Error("unreachable")\n}`);
    const caller = await k.run(`const z = 1;\nexplode(z)`);
    const e = expectError(caller, "runtime");
    expect(e.name).toBe("RangeError");
    expect(e.message).toBe("too big: 1");
    expect(e.location).toEqual({ cellId: definer.cellId, line: 2, column: 34 });
    expect(e.stack).toBe(`RangeError: too big: 1\n    at explode (cell ${definer.cellId}:2:34)\n    at <anonymous> (cell ${caller.cellId}:2:8)`);
    expect(e.stack).not.toContain("kernel.js");
  });

  test("non-Error throws", async () => {
    const e = expectError(await k.run(`throw { reason: "nope" }`), "runtime");
    expect(e.message).toBe("{ reason: 'nope' }");
  });

  test("host calls round-trip and return values from the cell's realm", async () => {
    const r = await k.run(`const res = await ${g}.shell.run("echo hi"); [res.exitCode, res.lines instanceof Array, res.lines[0]]`);
    expect(r.value?.text).toBe("[ 0, true, 'ran: echo hi' ]");
    const e = await k.run(`await (${g} as any).shell.missing?.() ?? "absent"`);
    expect(e.value?.text).toBe("'absent'");
  });

  test("host call errors reject inside the cell", async () => {
    const host = await KernelHost.launch({
      hostCall: () => {
        throw new RpcError(-32010, "denied by policy", { grant: "shell" });
      },
    });
    try {
      // replMode captures a trailing expression statement, not the completion value of a block.
      const r = await host.run(`let caught: any; try { await ${g}.shell.run("rm -rf /") } catch (e) { caught = e }; [caught instanceof Error, caught.message, caught.code, caught.data.grant]`);
      expect(r.value?.text).toBe("[ true, 'denied by policy', -32010, 'shell' ]");
    } finally {
      await host.close();
    }
  }, SLOW);

  test("finish is validated by the host", async () => {
    const accepted = await k.run(`await ${g}.finish({ kind: "done", summary: "all good" })`);
    expect(accepted.ok).toBe(true);
    expect(accepted.finished).toEqual({ kind: "done", summary: "all good" });
    const rejected = await k.run(`await ${g}.finish({ kind: "maybe" } as any)`);
    expect(expectError(rejected, "runtime").message).toContain("finish rejected: result.kind must be");
    expect(rejected.finished).toBeUndefined();
  });

  test("value/preview reads a variable by name", async () => {
    await k.run(`const settings = { retries: 3, tags: ["a", "b"] }`);
    expect(await k.request("value/preview", { name: "settings" })).toEqual({ type: "Object", text: "{ retries: 3, tags: [ 'a', 'b' ] }", truncated: false });
    expect(String(await rejection(k.request("value/preview", { name: "nope" })))).toContain("no variable");
  });

  test("a second cell/run while one is running is refused", async () => {
    const first = k.run(`await new Promise((r) => setTimeout(r, 200)); "first"`);
    await Bun.sleep(20);
    const second = k.run(`"second"`);
    expect(await rejection(second)).toMatchObject({ code: ErrorCodes.busy });
    expect((await first).value?.text).toBe("'first'");
  });

  test("background output and errors arrive as cell/late notifications", async () => {
    const r = await k.run(`setTimeout(() => console.log("later"), 10); setTimeout(() => { throw new Error("late boom") }, 20); Promise.reject(new Error("unhandled")); "started"`);
    expect(r.ok).toBe(true);
    await eventually(() => k.late.length >= 3);
    const mine = k.late.filter((l) => l.cellId === r.cellId);
    expect(mine.map((l) => l.kind).sort()).toEqual(["error", "error", "output"]);
    expect(mine.find((l) => l.kind === "output")?.text).toBe("later\n");
    expect(mine.some((l) => l.text.includes("late boom"))).toBe(true);
    expect(mine.some((l) => l.text.includes("unhandledRejection") && l.text.includes("unhandled"))).toBe(true);
    expect((await k.run(`1 + 1`)).value?.text).toBe("2");
  });

  test("writes to the process's stdout are captured; raw fd writes do not break the protocol", async () => {
    const r = await k.run(`const proc = await import("node:process"); proc.stdout.write("via process.stdout\\n"); 1`);
    expect(r.output.text).toBe("via process.stdout\n");
    const raw = await k.run(`const bun = await import("bun"); await bun.write(bun.stdout, "raw bytes without newline"); 2`);
    expect(raw.value?.text).toBe("2");
    expect((await k.run(`3`)).value?.text).toBe("3");
    expect(k.stray.join("")).toContain("raw bytes without newline");
  });

  test("macros are disabled in the cell transpiler", async () => {
    const marker = join(k.layout.root, "macro-ran");
    const macro = join(k.layout.root, "macro.ts");
    writeFileSync(macro, `export function m() { require("node:fs").writeFileSync(${JSON.stringify(marker)}, "x"); return 1 }`);
    const e = expectError(await k.run(`import { m } from ${JSON.stringify(macro)} with { type: "macro" }; m()`), "syntax");
    expect(e.message).toContain("Macros are disabled");
    expect(existsSync(marker)).toBe(false);
  });
});

describe("cancellation and timeouts", () => {
  let k: KernelHost;
  beforeAll(async () => {
    k = await KernelHost.launch();
  }, SLOW);
  afterAll(async () => {
    await k.close();
  });

  test("cancelling an awaiting cell disposes what it started", async () => {
    await k.run(`let ticks = 0`);
    const running = k.request<CellRunResult>("cell/run", { cellId: "spin", code: `setInterval(() => ticks++, 5); await new Promise(() => {})` });
    await Bun.sleep(100);
    expect(await k.request("cell/cancel", { cellId: "spin" })).toEqual({ cancelled: true });
    expectError(await running, "cancelled");
    const before = Number((await k.run(`ticks`)).value?.text);
    expect(before).toBeGreaterThan(0);
    await Bun.sleep(100);
    expect(Number((await k.run(`ticks`)).value?.text)).toBe(before);
  });

  test("cancelling also kills child processes the cell started", async () => {
    const r = await k.run(`const { spawn } = await import("bun"); const { execPath } = await import("node:process"); spawn([execPath, "-e", "await Bun.sleep(60000)"]).pid`);
    const pid = Number(r.value?.text);
    expect(pid).toBeGreaterThan(0);
    await k.request("cell/cancel", { cellId: r.cellId });
    await eventually(() => {
      try {
        process.kill(pid, 0);
        return false;
      } catch {
        return true;
      }
    });
  });

  test("a synchronous infinite loop is interrupted by the VM timeout", async () => {
    const e = expectError(await k.run(`while (true) {}`, { timeoutMs: 200 }), "timeout");
    expect(e.message).toContain("timed out");
    expect((await k.run(`"alive"`)).value?.text).toBe("'alive'");
  });

  test("only killing the kernel stops a busy loop after an await", async () => {
    const busy = await KernelHost.launch();
    const pending = busy.run(`await 0; while (true) {}`, { timeoutMs: 100 });
    pending.catch(() => {});
    const answered = await Promise.race([pending.then(() => true, () => true), Bun.sleep(1000).then(() => false)]);
    expect(answered).toBe(false);
    busy.proc.kill("SIGKILL");
    await busy.close();
    expect(String(await rejection(pending))).toContain("stdout closed");
  }, SLOW);

  test("an async cell that never settles times out", async () => {
    expectError(await k.run(`await new Promise(() => {})`, { timeoutMs: 200 }), "timeout");
    expect((await k.run(`"still alive"`)).value?.text).toBe("'still alive'");
  });
});

describe("the global's name is a launch parameter", () => {
  test("a renamed product gets a renamed global", async () => {
    const other = `${g}Renamed`;
    const k = await KernelHost.launch({ globalName: other });
    try {
      expect(k.info?.global).toBe(other);
      const r = await k.run(`[typeof ${other}.shell.run, typeof (globalThis as any)[${JSON.stringify(g)}]]`);
      expect(r.value?.text).toBe("[ 'function', 'undefined' ]");
    } finally {
      await k.close();
    }
  }, SLOW);

  test("the kernel refuses an invalid one", async () => {
    const k = await KernelHost.launch({ initialize: false, globalName: "not-an-identifier" });
    expect(await k.close()).not.toBe(0);
  }, SLOW);
});
