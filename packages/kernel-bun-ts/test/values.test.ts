// Unit tests of the kernel's pure parts. The kernel's behaviour is tested by the conformance suite
// (tests/Fx.Kernel.Tests), which runs this kernel on the pinned Bun.

import { describe, expect, test } from "bun:test";
import vm from "node:vm";
import { OutputBuffer, OutputStream } from "../src/output.ts";
import type { Limits, Value } from "../src/protocol.d.ts";
import * as values from "../src/values.ts";

const context = vm.createContext({});
const realm: values.Realm = {
  fromJson: vm.runInContext("JSON.parse", context),
  object: () => new (vm.runInContext("Object", context) as ObjectConstructor)() as Record<string, unknown>,
};

const limits: Limits = { maxChars: 100, maxItems: 5, maxDepth: 2, maxStringChars: 20 };

describe("previews", () => {
  const samples: [string, unknown][] = [
    ["a long string", "x".repeat(5000)],
    ["a long array", Array.from({ length: 1000 }, (_, i) => i)],
    ["a deep object", { a: { b: { c: { d: { e: [1, [2, [3]]] } } } } }],
    ["many keys", Object.fromEntries(Array.from({ length: 50 }, (_, i) => [`key${i}`, i]))],
    ["a map", new Map(Array.from({ length: 50 }, (_, i) => [`k${i}`, i]))],
    ["astral characters", "𝄞".repeat(200)],
  ];

  for (const [name, value] of samples) {
    test(`${name} stays within maxChars and is marked truncated`, () => {
      const preview = values.preview(value, limits);
      expect(preview.text.length).toBeLessThanOrEqual(limits.maxChars);
      expect(preview.truncated).toBe(true);
      // Never half a surrogate pair at the cut.
      expect(preview.text).not.toMatch(/[\ud800-\udbff](?![\udc00-\udfff])/);
    });
  }

  test("small values preview whole", () => {
    expect(values.preview(42, limits)).toEqual({ text: "42", truncated: false });
    expect(values.preview("hi", limits)).toEqual({ text: '"hi"', truncated: false });
    expect(values.preview([1, 2], limits)).toEqual({ text: "[1, 2]", truncated: false });
    expect(values.preview({ a: 1 }, limits)).toEqual({ text: '{"a": 1}', truncated: false });
  });

  test("lists show maxItems and count the rest", () => {
    expect(values.preview([1, 2, 3, 4, 5, 6, 7], limits).text).toBe("[1, 2, 3, 4, 5, …+2]");
  });

  test("objects from the cells' realm preview like local ones", () => {
    const fromCell = vm.runInContext('({ a: [1, 2], b: "c" })', context);
    expect(values.preview(fromCell, limits).text).toBe('{"a": [1, 2], "b": "c"}');
    expect(values.typeName(vm.runInContext("[1]", context))).toBe("Array");
  });
});

describe("encoding", () => {
  test("plain data encodes inline", () => {
    expect(values.encode({ a: [1, "x", null, true] }, 1000)).toEqual({ kind: "inline", value: { a: [1, "x", null, true] } });
  });

  test("values without a JSON form are refused", () => {
    for (const value of [undefined, () => 1, 10n, Symbol("s"), NaN, Infinity, { f: () => 1 }, [undefined], new Map()]) {
      expect(() => values.encode(value, 1000)).toThrow(values.NotEncodable);
    }
    const cycle: Record<string, unknown> = {};
    cycle.self = cycle;
    expect(() => values.encode(cycle, 1000)).toThrow(values.NotEncodable);
  });

  test("arguments leave out members that are undefined, and nothing else", () => {
    expect(values.encode({ a: 1, b: undefined, c: { d: undefined } }, 1000, true)).toEqual({ kind: "inline", value: { a: 1, c: {} } });
    expect(() => values.encode({ b: undefined }, 1000)).toThrow(values.NotEncodable);
    expect(() => values.encode([undefined], 1000, true)).toThrow(values.NotEncodable);
    expect(() => values.encode({ f: () => 1 }, 1000, true)).toThrow(values.NotEncodable);
  });

  test("dates encode as JSON.stringify writes them", () => {
    expect(values.encode(new Date(0), 1000)).toEqual({ kind: "inline", value: "1970-01-01T00:00:00.000Z" });
  });

  test("values over the limit are too large", () => {
    expect(() => values.encode("x".repeat(200), 100)).toThrow(values.TooLarge);
  });

  test("references round trip, and plain data never becomes one", () => {
    const handle: Value = { kind: "handle", handle: "h-1", type: "Thing", preview: { text: "a thing", truncated: false } };
    const decoded = values.decode(realm, handle) as Record<string, unknown>;
    expect(Object.isFrozen(decoded)).toBe(true);
    expect(Object.values(decoded)).not.toContain("h-1");
    expect(values.encode(decoded, 10)).toEqual(handle);
    expect(String(decoded)).toBe("Thing(a thing)");
    const lookalike = { ...decoded };
    expect(values.encode(lookalike, 1000).kind).toBe("inline");
    expect(() => values.encode({ inside: decoded }, 1000)).toThrow(values.NotEncodable);
  });

  test("inline values are created in the cells' realm", () => {
    const decoded = values.decode(realm, { kind: "inline", value: [1, 2] });
    expect(vm.runInContext("(v) => Array.isArray(v) && v instanceof Array", context)(decoded)).toBe(true);
  });
});

describe("output", () => {
  test("keeps the head and the tail within the limit", () => {
    const buffer = new OutputBuffer(10);
    buffer.append("0123456789abcdefghij");
    const text = buffer.take();
    expect(text.truncated).toBe(true);
    expect(text.totalChars).toBe(20);
    expect(text.text.startsWith("01234")).toBe(true);
    expect(text.text.endsWith("fghij")).toBe(true);
  });

  test("streams at most the limit, coalesced", async () => {
    const sent: string[] = [];
    const stream = new OutputStream(5, (text) => sent.push(text));
    stream.append("abc");
    stream.append("defgh");
    await Promise.resolve();
    expect(sent).toEqual(["abcde"]);
    expect(stream.sent).toBe(1);
  });
});
