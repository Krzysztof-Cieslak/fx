import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { existsSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { KernelHost, pinnedBun } from "../src/host.ts";
import { kernelGlobal } from "../src/product.ts";
import type { CellCheckResult, CellError, Diagnostic } from "../src/kernel/protocol.ts";

const g = kernelGlobal;
const SLOW = 180_000;

describe("type-checking cells with bun check", () => {
  let k: KernelHost;
  let n = 0;
  const check = async (code: string): Promise<Diagnostic[]> => (await k.request<CellCheckResult>("cell/check", { cellId: `chk${++n}`, code })).diagnostics;
  const codes = (ds: Diagnostic[]) => ds.map((d) => d.code);

  beforeAll(async () => {
    k = await KernelHost.launch();
  }, SLOW);
  afterAll(async () => {
    await k.close();
  });

  test("checks a cell against the generated typings of the global", async () => {
    expect(await check(`const r = await ${g}.shell.run("ls"); r.lines.map((l) => l.trim())`)).toEqual([]);
    const ds = await check(`const r = await ${g}.shell.run(42)\nconst code: string = r.exitCode`);
    expect(ds.map(({ line, column, code }) => ({ line, column, code }))).toEqual([
      { line: 1, column: `const r = await ${g}.shell.run(`.length + 1, code: "TS2345" },
      { line: 2, column: 7, code: "TS2322" },
    ]);
    expect(ds[0]!.message).toContain("'number' is not assignable to parameter of type 'string'");
  });

  test("knows exactly the globals the kernel provides", async () => {
    const ok = `const u = new URL("https://example.com/a?b=1"); const t = setTimeout(() => console.log(u.searchParams.get("b")), 1); clearTimeout(t); const res: Promise<Response> = fetch(u); new TextEncoder().encode("x").length`;
    expect(await check(ok)).toEqual([]);
    expect(codes(await check(`document.title`))).toEqual(["TS2584"]);
    expect(codes(await check(`process.env.HOME`))).toEqual(["TS2591"]);
  });

  test("validates finish results structurally", async () => {
    const ds = await check(`await ${g}.finish({ kind: "done", summry: "typo" })`);
    expect(codes(ds)).toEqual(["TS2561"]);
    expect(ds[0]!.message).toContain("Did you mean to write 'summary'?");
  });

  test("sees the names and types of cells that ran earlier", async () => {
    await k.run(`interface Item { name: string; size: number }\nconst items: Item[] = [{ name: "a", size: 1 }]\nlet total = 0\nclass Box { constructor(readonly item: Item) {} }`);
    expect(await check(`total += items[0]!.size; const b: Box = new Box(items[0]!); b.item.name.toUpperCase()`)).toEqual([]);
    expect(codes(await check(`items[0]!.nme`))).toEqual(["TS2551"]);
    expect(codes(await check(`total = "lots"`))).toEqual(["TS2322"]);
    expect(codes(await check(`const it: Item = { name: "x", size: "big" }`))).toEqual(["TS2322"]);
  });

  test("a later declaration of the same name wins", async () => {
    await k.run(`const shadow = 1`);
    await k.run(`const shadow = "now a string"`);
    expect(await check(`shadow.toUpperCase()`)).toEqual([]);
    expect(codes(await check(`shadow.toFixed(1)`))).toEqual(["TS2551"]);
  });

  test("module imports need bundled type declarations (none in the spike)", async () => {
    const ds = await check(`import { join } from "node:path"; join("a", "b")`);
    expect(codes(ds)).toEqual(["TS2591"]);
  });

  test("cell/run with check does not run a cell with type errors", async () => {
    await k.run(`let sideEffect = 0`);
    const r = await k.run(`sideEffect = 1; const n: number = "not a number"`, { check: true });
    expect(r.ok).toBe(false);
    const error = r.error as Extract<CellError, { kind: "type" }>;
    expect(error.kind).toBe("type");
    expect(error.diagnostics.map((d) => [d.line, d.code])).toEqual([[1, "TS2322"]]);
    expect((await k.run(`sideEffect`)).value?.text).toBe("0");
    expect((await k.run(`sideEffect = 2; sideEffect`, { check: true })).value?.text).toBe("2");
  });

  test("a package.json check script above the check directory is never run", async () => {
    const marker = join(k.layout.root, "check-script-ran");
    writeFileSync(join(k.layout.root, "package.json"), JSON.stringify({ scripts: { check: `bun -e "require('fs').writeFileSync('${marker.replaceAll("\\", "/")}', 'x')"` } }));
    expect(await check(`1 + 1`)).toEqual([]);
    expect(existsSync(marker)).toBe(false);
    // Positive control: without --no-script, `bun check` runs the script instead of the checker.
    const bun = await pinnedBun();
    const env = { ...k.env, PATH: `${join(bun.exe, "..")}${process.platform === "win32" ? ";" : ":"}${k.env.PATH ?? ""}` };
    Bun.spawnSync([bun.exe, "check", join(k.layout.check, "tsconfig.json")], { cwd: k.layout.check, env });
    expect(existsSync(marker)).toBe(true);
  });
});
