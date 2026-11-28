// Unit tests of the checker and the globals table, with the Bun that runs the tests (the pinned
// one in CI). The kernel's `cell/check` is tested through the protocol by the conformance suite and
// by the interpreter's tests (tests/Fx.Ext.InterpreterBunTs.Tests), on the pinned Bun.

import { describe, expect, test } from "bun:test";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Checker, parseDiagnostics, untypedBridge } from "../src/checker.ts";
import { contextGlobals, globalsDeclarations, kernelNames } from "../src/globals.ts";
import { hoistedNames } from "../src/positions.ts";

const transpiler = new Bun.Transpiler({ loader: "tsx", replMode: true, macro: false } as unknown as Bun.TranspilerOptions);
const declared = (code: string) => hoistedNames(transpiler.transformSync(code));

const bridge = `declare namespace host {
  const shell: { run(command: string): Promise<{ exitCode: number; stdout: string }> };
  function finish(result: { kind: "done"; summary: string }): Promise<null>;
}
`;

function withChecker(body: (checker: Checker) => Promise<void>): () => Promise<void> {
  return async () => {
    const dir = mkdtempSync(join(tmpdir(), "checker-test-"));
    try {
      await body(new Checker(process.execPath, dir, "host", bridge));
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  };
}

describe("globals", () => {
  test("every global the context gets is declared, and nothing else", () => {
    const names = [...Object.keys(contextGlobals({})), ...kernelNames];
    const declaredNames = [...globalsDeclarations.matchAll(/^declare (?:var|function|class) ([A-Za-z_$][\w$]*)/gm)].map((m) => m[1]);
    expect([...new Set(declaredNames)].sort()).toEqual([...names].sort());
  });
});

describe("diagnostics", () => {
  test("lines of the cell's file, shifted by the carried-over line", () => {
    const output = [
      "cell-c2.ts(3,7): error TS2322: Type 'string' is not assignable to type 'number'.",
      "  More detail.",
      "cell-c1.ts(2,1): error TS2304: Cannot find name 'x'.",
      "C:\\check\\cell-c2.ts(1,1): error TS1005: ';' expected.",
    ].join("\r\n");
    expect(parseDiagnostics(output, "cell-c2.ts")).toEqual([
      { line: 2, column: 7, code: "TS2322", message: "Type 'string' is not assignable to type 'number'.\nMore detail." },
      { line: 0, column: 1, code: "TS1005", message: "';' expected." },
    ]);
  });

  test("a bridge without declarations is any", () => {
    expect(untypedBridge("host")).toBe("declare const host: any;\n");
  });
});

describe("checker", () => {
  test(
    "type errors against the bridge, at the cell's lines",
    withChecker(async (checker) => {
      const code = "// intent: run a command\nconst out = await host.shell.run(42)";
      const diagnostics = await checker.check("c1", code, declared(code));
      expect(diagnostics.map((d) => [d.line, d.code])).toEqual([[2, "TS2345"]]);
    }),
    30_000,
  );

  test(
    "a correct cell, earlier cells' names and types, modules as any",
    withChecker(async (checker) => {
      const first = "type Item = { name: string }\nconst items: Item[] = [{ name: 'a' }]";
      checker.record("c1", first, declared(first));
      const second = "const { join } = await import('node:path')\nconst names: string[] = items.map((i: Item) => join(i.name))\nnames";
      expect(await checker.check("c2", second, declared(second))).toEqual([]);
      const wrong = "const n: number = items[0]!.name";
      expect((await checker.check("c3", wrong, declared(wrong))).map((d) => d.code)).toEqual(["TS2322"]);
    }),
    30_000,
  );

  test(
    "values the host bound have their declared types; a cell's own declaration wins",
    withChecker(async (checker) => {
      checker.bind("notes", "string");
      const ok = "// intent: use the text\nconst size: number = notes.length\nnotes.split('`')";
      expect(await checker.check("c1", ok, declared(ok))).toEqual([]);
      const wrong = "const n: number = notes";
      expect((await checker.check("c2", wrong, declared(wrong))).map((d) => d.code)).toEqual(["TS2322"]);
      const own = "const notes = 3\nconst m: number = notes";
      checker.record("c3", own, declared(own));
      const after = "const k: number = notes";
      expect(await checker.check("c4", after, declared(after))).toEqual([]);
    }),
    30_000,
  );

  test(
    "finish takes the result type",
    withChecker(async (checker) => {
      const ok = 'await host.finish({ kind: "done", summary: "ok" })';
      expect(await checker.check("c1", ok, declared(ok))).toEqual([]);
      const wrong = 'await host.finish({ kind: "dnoe", summary: "ok" })';
      expect((await checker.check("c2", wrong, declared(wrong))).length).toBeGreaterThan(0);
    }),
    30_000,
  );
});
