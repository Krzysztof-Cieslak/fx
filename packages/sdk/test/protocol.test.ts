// The runtime codecs of the contracts against the F# ones (test/fixtures/protocol.json, written by
// tests/Fx.Sdk.Process.Tests): the same schemas, and manifests read, checked and hashed alike.

import { describe, expect, test } from "bun:test";
import type { Codec } from "../src/codec.ts";
import { Manifest } from "../src/manifest.ts";
import {
  activateParamsCodec,
  activatedCodec,
  callParamsCodec,
  decisionCodec,
  extensionRefCodec,
  invokeParamsCodec,
  issueCodec,
  outputCodec,
  protocolErrorCodec,
  protocolErrorMessage,
  slotDeclCodec,
  transformSettings,
} from "../src/protocol.ts";
import fixtures from "./fixtures/protocol.json";

const codecs: Record<string, Codec<any>> = {
  issue: issueCodec,
  manifest: Manifest.codec,
  decision: decisionCodec,
  transformSettings,
  slotDecl: slotDeclCodec,
  activateParams: activateParamsCodec,
  activated: activatedCodec,
  invokeParams: invokeParamsCodec,
  callParams: callParamsCodec,
  extensionRef: extensionRefCodec,
  output: outputCodec,
  protocolError: protocolErrorCodec,
};

describe("the contracts mirror the F# codecs", () => {
  for (const [name, schema] of Object.entries(fixtures.schemas)) {
    test(name, () => {
      expect(codecs[name]?.schema).toEqual(schema as never);
    });
  }

  test("every mirrored contract has a fixture", () => {
    expect(Object.keys(codecs).sort()).toEqual(Object.keys(fixtures.schemas).sort());
  });
});

describe("manifests are read, checked and hashed as the host does", () => {
  fixtures.manifests.forEach((c, index) => {
    test(`manifest ${index}`, () => {
      const decoded = Manifest.decode(c.input);
      if ("output" in c) {
        if (!decoded.ok) throw new Error(JSON.stringify(decoded.issues));
        expect(JSON.stringify(Manifest.encode(decoded.value))).toBe(JSON.stringify(c.output));
        expect(Manifest.hash(decoded.value)).toBe(c.hash as string);
      } else {
        expect(decoded.ok ? [] : decoded.issues).toEqual(c.issues as never);
      }
    });
  });

  test("a manifest with its own hash checks", () => {
    const manifest = Manifest.create("acme.tools", "1.0.0", "bun");
    expect(Manifest.check({ ...manifest, hash: Manifest.hash(manifest) })).toEqual([]);
  });
});

describe("typed errors", () => {
  test("their wire format and messages are the host's", () => {
    const cases: [Parameters<typeof protocolErrorMessage>[0], string, string][] = [
      [{ kind: "unknownExtension", extension: "x" }, '{"kind":"unknownExtension","extension":"x"}', "unknown extension x"],
      [{ kind: "notActive", extension: "x" }, '{"kind":"notActive","extension":"x"}', "x is not active"],
      [{ kind: "activationFailed", extension: "x", reason: "no runtime" }, '{"kind":"activationFailed","extension":"x","reason":"no runtime"}', "no runtime"],
      [{ kind: "unknownContribution", address: "x/t/i" }, '{"kind":"unknownContribution","address":"x/t/i"}', "unknown contribution x/t/i"],
      [{ kind: "inProcessOnly", address: "x/t/i" }, '{"kind":"inProcessOnly","address":"x/t/i"}', "x/t/i runs only in its process"],
      [{ kind: "unknownMethod", method: "m" }, '{"kind":"unknownMethod","method":"m"}', "unknown method m"],
      [{ kind: "invalid", issues: [{ path: ["a"], message: "bad" }] }, '{"kind":"invalid","issues":[{"path":["a"],"message":"bad"}]}', "invalid:\n$.a: bad"],
      [{ kind: "methodFailed", message: "too big", data: 3 }, '{"kind":"methodFailed","message":"too big","data":3}', "too big"],
    ];
    for (const [error, json, text] of cases) {
      expect(protocolErrorCodec.encodeText(error)).toBe(json);
      expect(protocolErrorMessage(error)).toBe(text);
    }
  });
});
