import { describe, expect, test } from "bun:test";
import { hoistedNames, mapToSource } from "../src/kernel/positions.ts";

const transpiler = new Bun.Transpiler({ loader: "tsx", replMode: true });

/** Finds `needle` in the transpiled output and maps its position back. */
function roundTrip(source: string, needle: string, occurrence = 0) {
  const js = transpiler.transformSync(source);
  let offset = -1;
  for (let i = 0; i <= occurrence; i++) offset = js.indexOf(needle, offset + 1);
  expect(offset).toBeGreaterThanOrEqual(0);
  const before = js.slice(0, offset);
  const line = before.split("\n").length;
  const column = offset - before.lastIndexOf("\n");
  return mapToSource(source, js, line, column);
}

describe("hoistedNames", () => {
  test("lists replMode's hoisted declarations", () => {
    const js = transpiler.transformSync(`const a = 1; let { b, c: [d] } = { b: 2, c: [3] }; function f() {} class K {} import { join } from "node:path";`);
    expect(hoistedNames(js)).toEqual(expect.arrayContaining(["a", "b", "d", "f", "K", "join"]));
  });

  test("is empty for expressions", () => {
    expect(hoistedNames(transpiler.transformSync("1 + 1"))).toEqual([]);
  });
});

describe("mapToSource", () => {
  test("maps a throw on a later line", () => {
    expect(roundTrip(`const q: number = 1;\nconst w = 2;\nnull.foo`, "null.foo")).toEqual({ line: 3, column: 1 });
  });

  test("maps code inside a class method", () => {
    const source = `class K {\n  m(): never {\n    throw new TypeError('in method')\n  }\n}\nnew K().m()`;
    expect(roundTrip(source, "throw new TypeError")).toEqual({ line: 3, column: 5 });
  });

  test("maps string literals whatever quotes the source used", () => {
    const source = `const s = 'a';\nfoo('x', s)`;
    expect(roundTrip(source, `foo("x"`)).toEqual({ line: 2, column: 1 });
  });

  test("uses statement order for repeated code", () => {
    const source = `let n = 0;\nn = n + 1;\nn = n + 1;\nn.toFixed(1);\nn.toFixed(1);`;
    expect(roundTrip(source, "n.toFixed(1)", 1)).toEqual({ line: 5, column: 1 });
  });

  test("returns undefined rather than guessing", () => {
    const js = transpiler.transformSync("1");
    expect(mapToSource("1", js, 999, 1)).toBeUndefined();
  });
});
