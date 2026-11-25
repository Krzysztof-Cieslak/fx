import { describe, expect, test } from "bun:test";
import { plan } from "./ci-plan";

const pr = (...files: string[]) => plan({ event: "pull_request", labels: [], files });
const rids = (targets: { rid: string }[]) => targets.map((t) => t.rid);

describe("ci plan", () => {
  test("an ordinary pull request gets the fast tier", () => {
    const p = pr("src/Fx.Sessions/Machine.fs", "tests/Fx.Sessions.Tests/Tests.fs");
    expect(p.full).toBe(false);
    expect(p.code).toBe(true);
    expect(rids(p.aot)).toEqual(["linux-x64", "linux-musl-x64", "osx-arm64", "win-x64"]);
    expect(p.keyring).toEqual([]);
    expect(p.sandbox).toEqual([]);
    expect(p.bunMusl).toEqual([]);
    expect(p.networkOnAllOs).toBe(false);
    expect(p.spike).toBe(false);
  });

  test("main, the schedule and manual runs get every target", () => {
    for (const event of ["push", "schedule", "workflow_dispatch"]) {
      const p = plan({ event, labels: [], files: [] });
      expect(p.full).toBe(true);
      expect(p.aot).toHaveLength(8);
      expect(p.keyring).toHaveLength(4);
      expect(p.sandbox).toHaveLength(4);
      expect(p.bunMusl).toHaveLength(2);
      expect(p.networkOnAllOs).toBe(true);
    }
    expect(plan({ event: "workflow_dispatch", fullInput: "false", labels: [], files: [] }).full).toBe(false);
  });

  test("the ci:full label gets every target", () => {
    expect(plan({ event: "pull_request", labels: ["ci:full"], files: ["README.md"] }).aot).toHaveLength(8);
  });

  test("platform-sensitive files get every target", () => {
    for (const f of [
      ".github/workflows/ci.yml",
      "global.json",
      "Directory.Build.props",
      "native/sqlite/library.c",
      "src/Fx.Pty/UnixPty.fs",
      "src/Fx.Keyring/Native/Windows.fs",
      "src/Fx.Transport.Http/PeerCredentials.fs",
      "extensions/Fx.Ext.RuntimeBun/RuntimeBun.fs",
      "extensions/Fx.Ext.SandboxBubblewrap/Seccomp.fs",
      "extensions/Fx.Ext.SandboxSeatbelt/Profile.fs",
      "src/Fx.Sdk/Sandbox.fs",
    ]) {
      expect(pr(f).full).toBe(true);
    }
    expect(pr("src/Fx.Sqlite.Tests.fs").full).toBe(false);
  });

  test("an area's own jobs run when the area changes", () => {
    expect(rids(pr("src/Fx.Auth/KeyringStore.fs").keyring)).toEqual(["linux-x64", "linux-musl-x64", "osx-arm64"]);
    expect(rids(pr("tests/Fx.Auth.Tests/CredentialStoreConformance.fs").keyring)).toHaveLength(3);
    expect(rids(pr("extensions/Fx.Ext.StoreKeyring/StoreKeyring.fs").keyring)).toHaveLength(3);
    expect(rids(pr("src/Fx.Effects/Keyring.fs").keyring)).toHaveLength(3);
    expect(pr("src/Fx.Effects/Clock.fs").keyring).toEqual([]);
    const bun = pr("packages/kernel-bun-ts/src/kernel.ts");
    expect(bun.bunMusl.map((t) => t.arch)).toEqual(["x64"]);
    expect(bun.networkOnAllOs).toBe(true);
    expect(pr("spikes/bun-kernel/src/kernel.ts").spike).toBe(true);
    expect(rids(pr("src/Fx.Environments/Envs.fs").sandbox)).toEqual(["linux-x64", "osx-arm64"]);
    expect(rids(pr("tests/Fx.Environments.Tests/SandboxTests.fs").sandbox)).toHaveLength(2);
    expect(rids(pr("src/Fx.CodeAct/SandboxGuard.fs").sandbox)).toHaveLength(2);
    expect(pr("src/Fx.Sessions/Machine.fs").sandbox).toEqual([]);
  });

  test("a Markdown-only pull request runs no builds", () => {
    const p = pr("docs/modules/sessions.md", "AGENTS.md");
    expect(p.code).toBe(false);
    expect(p.aot).toEqual([]);
    expect(pr("docs/spikes/pty-probe/probe.fsx").code).toBe(true);
  });
});
