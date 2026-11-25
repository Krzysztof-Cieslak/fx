// The TypeScript sample declares exactly the F# sample's manifest (test/fixtures/manifest.json, written
// by tests/Fx.Sdk.Process.Tests): the same contributions, the same methods with the same JSON Schemas,
// so the same hash. Its behaviour is checked by the F# host's parity suite on the pinned Bun.

import { describe, expect, test } from "bun:test";
import { Manifest, activationMismatches } from "../../sdk/src/index.ts";
import { activate, manifest } from "../src/sample.ts";
import fixture from "./fixtures/manifest.json";

describe("the TypeScript sample", () => {
  test("declares the F# sample's manifest, member for member", () => {
    expect(JSON.stringify(Manifest.encode(manifest))).toBe(JSON.stringify(fixture.manifest));
  });

  test("has the F# sample's hash", () => {
    expect(Manifest.hash(manifest)).toBe(fixture.hash);
    expect(Manifest.check(manifest)).toEqual([]);
  });

  test("provides exactly what it declares", () => {
    expect(activationMismatches(manifest, activate())).toEqual([]);
  });
});
