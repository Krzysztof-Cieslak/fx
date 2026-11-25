// The TypeScript codecs against the F# ones: the same domain as tests/Fx.Sdk.Process.Tests/Fixtures.fs,
// written with this SDK's combinators, must have the same schema (the wire form) and JSON Schema, and
// decode every input of test/fixtures/codecs.json to the same output or the same issues.

import { describe, expect, test } from "bun:test";
import { Codec, type Infer } from "../src/codec.ts";
import { describe as describeJson, renderPath, serializeCanonical } from "../src/json.ts";
import { toJsonSchema } from "../src/schema.ts";
import fixtures from "./fixtures/codecs.json";

const span = Codec.record({ start: Codec.int, end: Codec.int }).describe("A range of lines, end exclusive.").named("Span");

const priority = Codec.enum(["low", "medium", "high"]);

const match = Codec.record({
  path: Codec.string.nonEmpty().describe("Workspace-relative."),
  line: Codec.int.atLeast(1),
  note: Codec.optional(Codec.string),
  tags: Codec.withDefault(Codec.array(Codec.string).maxItems(3), []),
  score: Codec.float.between(0, 1),
  span: Codec.nullable(span),
  priority: Codec.withDefault(priority, "medium"),
  counts: Codec.stringMap(Codec.int),
}).named("Match");

const turnResult = Codec.union("kind", {
  done: {
    summary: Codec.string,
    evidence: Codec.optional(Codec.array(match)),
    verify: Codec.optional(Codec.string.pattern("^[a-z]")),
  },
  waiting: { on: Codec.array(Codec.string).minItems(1), note: Codec.string.maxLength(20) },
  moved: Codec.caseOf(span),
  cancelled: {},
})
  .describeCase("done", "The work is complete.")
  .named("TurnResult");

type Tree = { kind: "leaf"; value: number } | { kind: "node"; label: string; children: Tree[] };

const tree = Codec.recursive<Tree>("Tree", (self) =>
  Codec.union("kind", {
    leaf: { value: Codec.int },
    node: { label: Codec.string, children: Codec.array(self) },
  }),
);

const settings = Codec.record({ name: Codec.string, retries: Codec.optional(Codec.int.between(0, 5)) })
  .strict()
  .describe("Strict settings: unknown members are issues.")
  .named("Settings");

const shape = Codec.union("type", { circle: { radius: Codec.float }, square: { side: Codec.float } }).strict();

const header = Codec.tuple(Codec.literal("v1"), Codec.int, Codec.keyValues(Codec.bool));

const misc = Codec.record({
  any: Codec.json,
  nothing: Codec.unit,
  host: Codec.string.format("hostname").minLength(3),
  ratio: Codec.float.atMost(2.5),
});

const codecs: Record<string, Codec<any>> = { span, match, turnResult, tree, settings, shape, header, misc };

describe("the F# codecs' fixtures", () => {
  for (const fixture of fixtures.codecs) {
    const codec = codecs[fixture.name];
    describe(fixture.name, () => {
      test("the same schema", () => {
        expect(codec?.schema).toEqual(fixture.schema as never);
      });

      test("the same JSON Schema", () => {
        expect(toJsonSchema(codec!.schema)).toEqual(fixture.jsonSchema as never);
      });

      fixture.cases.forEach((c, index) => {
        test(`input ${index}: ${JSON.stringify(c.input).slice(0, 60)}`, () => {
          const decoded = codec!.decode(c.input);
          if ("output" in c) {
            if (!decoded.ok) throw new Error(decoded.issues.map((i) => renderPath(i.path) + ": " + i.message).join("\n"));
            // Encoded exactly, members in the same order.
            expect(JSON.stringify(codec!.encode(decoded.value))).toBe(JSON.stringify(c.output));
          } else {
            expect(decoded.ok ? [] : decoded.issues).toEqual(c.issues as never);
          }
        });
      });
    });
  }

  test("every codec has fixtures", () => {
    expect(fixtures.codecs.map((c) => c.name).sort()).toEqual(Object.keys(codecs).sort());
  });
});

describe("values are the JSON shapes", () => {
  test("records and unions infer their types", () => {
    const value: Infer<typeof turnResult> = { kind: "waiting", on: ["ci"], note: "soon" };
    expect(turnResult.encode(value)).toEqual({ kind: "waiting", on: ["ci"], note: "soon" });
    const m: Infer<typeof match> = { path: "a", line: 1, tags: [], score: 1, span: null, priority: "low", counts: {} };
    expect(match.encodeText(m)).toBe('{"path":"a","line":1,"tags":[],"score":1,"span":null,"priority":"low","counts":{}}');
  });

  test("defaulted fields are written when left out, optional ones are not", () => {
    const encoded = Codec.record({ a: Codec.optional(Codec.int), b: Codec.withDefault(Codec.int, 7) }).encode({ b: undefined as never });
    expect(encoded).toEqual({ b: 7 });
  });

  test("maps are written in key order", () => {
    expect(JSON.stringify(Codec.stringMap(Codec.int).encode({ b: 1, a: 2 }))).toBe('{"a":2,"b":1}');
  });

  test("decodeText reports invalid JSON at the root", () => {
    const decoded = Codec.int.decodeText("{");
    expect(decoded.ok).toBe(false);
    if (!decoded.ok) expect(decoded.issues[0]?.message.startsWith("invalid JSON: ")).toBe(true);
  });

  test("defects throw: unknown union tags, enum values out of the codec, bad type names", () => {
    expect(() => turnResult.encode({ kind: "nope" } as never)).toThrow(TypeError);
    expect(() => priority.encode("urgent" as never)).toThrow(TypeError);
    expect(() => Codec.int.named("not a name")).toThrow(TypeError);
    expect(() => Codec.union("kind", { a: { kind: Codec.string } })).toThrow(TypeError);
    expect(() => Codec.int.strict()).toThrow(TypeError);
  });
});

describe("JSON helpers", () => {
  test("descriptions as the F# side writes them", () => {
    expect(describeJson(42)).toBe("number 42");
    expect(describeJson("x".repeat(50))).toBe(`string "${"x".repeat(40)}…"`);
    expect(describeJson([])).toBe("empty array");
    expect(describeJson([1])).toBe("array of 1 item");
    expect(describeJson({})).toBe("empty object");
    expect(describeJson(true)).toBe("boolean true");
  });

  test("paths in accessor syntax", () => {
    expect(renderPath([])).toBe("$");
    expect(renderPath(["items", 2, "a key", "$ok"])).toBe('$.items[2]["a key"].$ok');
  });

  test("canonical text sorts members by UTF-16 code units", () => {
    expect(serializeCanonical({ b: [2, { d: 1, c: null }], a: "é", "\u{1F600}": 1, "\uFFFF": 2 })).toBe(
      '{"a":"é","b":[2,{"c":null,"d":1}],"\u{1F600}":1,"\uFFFF":2}',
    );
  });
});
