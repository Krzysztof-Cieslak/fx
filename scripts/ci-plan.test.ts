import { describe, expect, test } from "bun:test";
import { YAML } from "bun";
import { join } from "node:path";
import { OS_JOBS, TARGETS, plan } from "./ci-plan";

const pr = (...files: string[]) => plan({ event: "pull_request", labels: [], files });
const rids = (targets: { rid: string }[]) => targets.map((t) => t.rid);

describe("ci plan", () => {
  test("an ordinary pull request gets the fast tier", () => {
    const p = pr("src/Fx.Sessions/Machine.fs", "tests/Fx.Sessions.Tests/Tests.fs");
    expect(p.full).toBe(false);
    expect(p.code).toBe(true);
    expect(p.targets).toEqual([]);
    expect(p.keyring).toBe(false);
    expect(p.sandbox).toBe(false);
    expect(p.bun).toBe(false);
    expect(p.spike).toBe(false);
    expect(p.platform).toEqual([]);
  });

  test("main, the schedule and manual runs get every target and every area", () => {
    for (const event of ["push", "schedule", "workflow_dispatch"]) {
      const p = plan({ event, labels: [], files: [] });
      expect(p.full).toBe(true);
      expect(p.code).toBe(true);
      expect(rids(p.targets)).toEqual(["linux-arm64", "linux-musl-x64", "linux-musl-arm64", "osx-x64", "win-arm64"]);
      expect([p.keyring, p.sandbox, p.bun, p.spike]).toEqual([true, true, true, true]);
    }
    const manual = plan({ event: "workflow_dispatch", fullInput: "false", labels: [], files: [] });
    expect(manual.full).toBe(false);
    expect(manual.targets).toEqual([]);
  });

  test("the ci:full label gets every target, even for Markdown", () => {
    const p = plan({ event: "pull_request", labels: ["ci:full"], files: ["README.md"] });
    expect(p.full).toBe(true);
    expect(p.code).toBe(true);
    expect(p.targets).toHaveLength(5);
    expect(p.keyring).toBe(true);
  });

  test("platform-sensitive files ask for the label instead of picking the full tier", () => {
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
      const p = pr(f, "src/Fx.Sessions/Machine.fs");
      expect(p.full).toBe(false);
      expect(p.targets).toEqual([]);
      expect(p.platform).toEqual([f]);
    }
    expect(pr("src/Fx.Sqlite.Tests.fs").platform).toEqual([]);
    expect(plan({ event: "pull_request", labels: ["ci:full"], files: ["global.json"] }).platform).toEqual([]);
  });

  test("an area's steps run when the area changes", () => {
    for (const f of [
      "src/Fx.Auth/KeyringStore.fs",
      "tests/Fx.Auth.Tests/CredentialStoreConformance.fs",
      "extensions/Fx.Ext.StoreKeyring/StoreKeyring.fs",
      "src/Fx.Effects/Keyring.fs",
    ]) {
      expect(pr(f).keyring).toBe(true);
    }
    expect(pr("src/Fx.Effects/Clock.fs").keyring).toBe(false);
    for (const f of ["src/Fx.Environments/Envs.fs", "tests/Fx.Environments.Tests/SandboxTests.fs", "src/Fx.CodeAct/SandboxGuard.fs"]) {
      expect(pr(f).sandbox).toBe(true);
    }
    expect(pr("src/Fx.Sessions/Machine.fs").sandbox).toBe(false);
    expect(pr("packages/kernel-bun-ts/src/kernel.ts").bun).toBe(true);
    expect(pr("src/Fx.Components/Planner.fs").bun).toBe(true);
    expect(pr("src/Fx.Sessions/Machine.fs").bun).toBe(false);
    expect(pr("spikes/bun-kernel/src/kernel.ts").spike).toBe(true);
    // Areas never add jobs on a pull request.
    expect(pr("src/Fx.Auth/KeyringStore.fs", "src/Fx.Environments/Envs.fs").targets).toEqual([]);
  });

  test("a Markdown-only pull request runs no builds", () => {
    const p = pr("docs/modules/sessions.md", "AGENTS.md");
    expect(p.code).toBe(false);
    expect(p.targets).toEqual([]);
    expect(pr("docs/spikes/keychains.md").keyring).toBe(false);
    expect(pr("docs/spikes/pty-probe/probe.fsx").code).toBe(true);
  });

  test("the os jobs and the targets cover every target once, natively", () => {
    const all = ["linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64", "osx-arm64", "osx-x64", "win-x64", "win-arm64"];
    const covered = [...rids(OS_JOBS), ...rids(TARGETS)];
    expect([...covered].sort()).toEqual([...all].sort());
    for (const t of TARGETS) expect(t.alpine).toBe(t.rid.startsWith("linux-musl-"));
    // No cross-compilation: each target runs on a runner of its architecture.
    for (const t of [...OS_JOBS, ...TARGETS]) {
      const arm = t.rid.endsWith("-arm64");
      if (t.rid.startsWith("osx-")) expect(t.os.endsWith("-intel")).toBe(!arm);
      else expect(t.os.endsWith("-arm")).toBe(arm);
    }
  });

  test("ci.yml's os job runs on the plan's runners", async () => {
    const workflow = YAML.parse(await Bun.file(join(import.meta.dir, "..", ".github", "workflows", "ci.yml")).text()) as {
      jobs: Record<string, { strategy?: { matrix?: { include?: { rid: string; os: string }[] } } }>;
    };
    const include = workflow.jobs.os?.strategy?.matrix?.include ?? [];
    expect(include.map(({ rid, os }) => ({ rid, os }))).toEqual(OS_JOBS);
  });
});
