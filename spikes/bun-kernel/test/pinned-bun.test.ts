import { describe, expect, test } from "bun:test";
import { join } from "node:path";
import { bunPin, detectTarget, ensurePinnedBun, verifyArchive } from "../src/pinned-bun.ts";
import { spikeArtifacts } from "../src/product.ts";

describe("pinned Bun", () => {
  test("pins a digest for every supported target", () => {
    const expected = [
      "bun-linux-x64",
      "bun-linux-x64-baseline",
      "bun-linux-x64-musl",
      "bun-linux-x64-musl-baseline",
      "bun-linux-aarch64",
      "bun-linux-aarch64-musl",
      "bun-darwin-x64",
      "bun-darwin-x64-baseline",
      "bun-darwin-aarch64",
      "bun-windows-x64",
      "bun-windows-x64-baseline",
      "bun-windows-aarch64",
    ];
    expect(Object.keys(bunPin.targets).sort()).toEqual(expected.sort());
    for (const digest of Object.values(bunPin.targets)) expect(digest).toMatch(/^[0-9a-f]{64}$/);
  });

  test("detects a pinned target for this machine", () => {
    expect(Object.keys(bunPin.targets)).toContain(detectTarget());
  });

  test("rejects an archive whose digest does not match", () => {
    expect(() => verifyArchive("bun-linux-x64", new TextEncoder().encode("not bun"))).toThrow(/digest mismatch/);
  });

  test(
    "downloads, verifies and probes the pinned release",
    async () => {
      const bun = await ensurePinnedBun(join(spikeArtifacts, "runtimes"));
      const revision = Bun.spawnSync([bun.exe, "--revision"], { env: { DO_NOT_TRACK: "1" } }).stdout.toString().trim();
      expect(revision).toBe(bunPin.revision);
      const ts = Bun.spawnSync([bun.exe, "-p", "process.versions.typescript"], { env: { DO_NOT_TRACK: "1" } }).stdout.toString().trim();
      expect(ts).toBe(bunPin.typescript);
    },
    180_000,
  );
});
