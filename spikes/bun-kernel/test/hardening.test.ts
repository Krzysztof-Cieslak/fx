// Every hardening measure is tested twice: a positive control shows the injection works against a
// plain `bun` launch of the pinned runtime, then the hardened kernel launch shows it does not.

import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { existsSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { KernelHost, pinnedBun, processAlive } from "../src/host.ts";
import { bunKernelLaunch, matchesPattern } from "../src/launch-spec.ts";
import type { PinnedBun } from "../src/pinned-bun.ts";
import { spikeArtifacts } from "../src/product.ts";

const SLOW = 180_000;
const hostile = join(spikeArtifacts, "hostile", String(process.pid));
const marker = (name: string) => join(hostile, `ran-${name}`);
const writeMarker = (name: string) => `require("node:fs").writeFileSync(${JSON.stringify(marker(name))}, "x");`;

/** A workspace that tries every project-local and environment injection we know of. */
function createHostileWorkspace(): Record<string, string> {
  rmSync(hostile, { recursive: true, force: true });
  mkdirSync(join(hostile, "node_path", "probe-node-path"), { recursive: true });
  writeFileSync(join(hostile, ".env"), "HOSTILE_DOTENV=loaded\n");
  writeFileSync(join(hostile, "bunfig.toml"), `preload = ["./evil-bunfig.ts"]\n`);
  writeFileSync(join(hostile, "evil-bunfig.ts"), writeMarker("bunfig"));
  writeFileSync(join(hostile, "evil-options.ts"), writeMarker("bun-options"));
  writeFileSync(join(hostile, "tsconfig.json"), JSON.stringify({ compilerOptions: { paths: { "probe-paths": ["./evil-paths.ts"] } } }));
  writeFileSync(join(hostile, "evil-paths.ts"), `export const who = "tsconfig paths";`);
  writeFileSync(join(hostile, "node_path", "probe-node-path", "index.js"), `module.exports = "NODE_PATH";`);
  writeFileSync(
    join(hostile, "probe.ts"),
    `const out: Record<string, unknown> = { dotenv: process.env.HOSTILE_DOTENV ?? null };
try { out.paths = (await import("probe-paths")).who } catch { out.paths = null }
try { out.nodePath = require("probe-node-path") } catch { out.nodePath = null }
console.log(JSON.stringify(out));`,
  );
  return {
    BUN_OPTIONS: "--preload=./evil-options.ts",
    NODE_OPTIONS: "--require=./evil-options.ts",
    NODE_PATH: join(hostile, "node_path"),
    BUN_JSC_dumpOptions: "1",
    BUN_CONFIG_VERBOSE_FETCH: "curl",
    NPM_CONFIG_REGISTRY: "https://registry.invalid/",
    SECRET_TOKEN: "do-not-leak",
  };
}

describe("hardened launch", () => {
  let bun: PinnedBun;
  let injected: Record<string, string>;
  beforeAll(async () => {
    bun = await pinnedBun();
    injected = createHostileWorkspace();
  }, SLOW);
  afterAll(() => {
    rmSync(hostile, { recursive: true, force: true });
  });

  test("positive control: a plain launch of the pinned Bun is injectable", () => {
    const r = Bun.spawnSync([bun.exe, "probe.ts"], { cwd: hostile, env: { ...process.env, ...injected } });
    expect(JSON.parse(r.stdout.toString().trim().split("\n").at(-1)!)).toEqual({ dotenv: "loaded", paths: "tsconfig paths", nodePath: "NODE_PATH" });
    expect(existsSync(marker("bun-options"))).toBe(true);
    expect(existsSync(marker("bunfig"))).toBe(true);
    expect(r.stderr.toString()).toContain("Modified JSC options");
    rmSync(marker("bun-options"));
    rmSync(marker("bunfig"));
  });

  test("the kernel ignores project files and injected variables, even with the workspace as cwd", async () => {
    const k = await KernelHost.launch({ workspace: hostile, parentEnv: { ...process.env, ...injected } });
    try {
      const env = await k.run(`const proc = await import("node:process"); Object.keys(proc.env).sort()`);
      const names = (await k.run(`Object.keys(proc.env).sort().join(",")`)).value!.text.slice(1, -1).split(",");
      expect(env.ok).toBe(true);
      // On POSIX, Bun sets this itself for --no-orphans so that the kernel's children inherit it.
      // On Windows, libuv's spawn (used by Bun.spawn in this harness, not by .NET's Process) copies
      // a few variables it considers required from the parent when the given environment lacks them.
      const added = process.platform === "win32" ? ["HOMEDRIVE", "HOMEPATH", "LOGONSERVER", "USERDOMAIN", "USERNAME"] : ["BUN_FEATURE_FLAG_NO_ORPHANS"];
      if (process.platform !== "win32") expect(names).toContain("BUN_FEATURE_FLAG_NO_ORPHANS");
      const allowed = [...bunKernelLaunch.envAllowlist, ...Object.keys(bunKernelLaunch.envSet)].map((n) => n.toUpperCase());
      for (const name of names.filter((n) => !added.includes(n.toUpperCase()))) {
        expect(allowed).toContain(name.toUpperCase());
        expect(bunKernelLaunch.envDeny.some((p) => matchesPattern(name, p))).toBe(false);
      }
      expect(names).not.toContain("HOSTILE_DOTENV");
      expect(names).not.toContain("SECRET_TOKEN");
      expect((await k.run(`proc.cwd() === ${JSON.stringify(hostile)}`)).value?.text).toBe("true");
      const paths = await k.run(`await import("probe-paths")`);
      expect(paths.ok).toBe(false);
      const nodePath = await k.run(`const { createRequire } = await import("node:module"); createRequire(${JSON.stringify(join(hostile, "x.js"))})("probe-node-path")`);
      expect(nodePath.ok).toBe(false);
      expect((await k.run(`proc.execArgv.some((a) => a.includes("preload"))`)).value?.text).toBe("false");
      expect(existsSync(marker("bun-options"))).toBe(false);
      expect(existsSync(marker("bunfig"))).toBe(false);
    } finally {
      await k.close();
    }
    expect(k.stderr).not.toContain("Modified JSC options");
  }, SLOW);

  test("the kernel's home, caches and temp are owned", async () => {
    const k = await KernelHost.launch();
    try {
      const r = await k.run(`const os = await import("node:os"); [os.homedir() === ${JSON.stringify(k.layout.home)}, os.tmpdir() === ${JSON.stringify(k.layout.tmp)}]`);
      expect(r.value?.text).toBe("[ true, true ]");
    } finally {
      await k.close();
    }
  }, SLOW);

  test("positive control: BUN_INSPECT opens a debugger listener", async () => {
    const server = Bun.listen({ hostname: "127.0.0.1", port: 0, socket: { data() {} } });
    const port = server.port;
    server.stop(true);
    const proc = Bun.spawn([bun.exe, "-e", "await Bun.sleep(5000)"], { env: { ...process.env, BUN_INSPECT: `127.0.0.1:${port}` }, stdout: "ignore", stderr: "ignore" });
    try {
      let connected = false;
      for (let i = 0; i < 50 && !connected; i++) {
        try {
          const socket = await Bun.connect({ hostname: "127.0.0.1", port, socket: { data() {} } });
          socket.end();
          connected = true;
        } catch {
          await Bun.sleep(100);
        }
      }
      expect(connected).toBe(true);
    } finally {
      proc.kill();
    }
    const k = await KernelHost.launch({ parentEnv: { ...process.env, BUN_INSPECT: `127.0.0.1:${port}` } });
    try {
      expect(k.env.BUN_INSPECT).toBeUndefined();
      let refused = false;
      try {
        (await Bun.connect({ hostname: "127.0.0.1", port, socket: { data() {} } })).end();
      } catch {
        refused = true;
      }
      expect(refused).toBe(true);
    } finally {
      await k.close();
    }
  }, SLOW);
});

describe("--no-orphans", () => {
  const cleanups: (() => void)[] = [];
  afterAll(() => cleanups.forEach((c) => c()));
  async function killParent(mode: "hardened" | "control" | "busy"): Promise<{ kernel: number; child: number }> {
    const parent = Bun.spawn([process.execPath, join(import.meta.dir, "fixtures", "orphan-parent.ts"), mode], { stdout: "pipe", stderr: "inherit" });
    const reader = parent.stdout.getReader();
    let text = "";
    while (!text.includes("\n")) {
      const { value, done } = await reader.read();
      if (done) throw new Error(`orphan parent exited early: ${text}`);
      text += new TextDecoder().decode(value);
    }
    const pids = JSON.parse(text.trim()) as { kernel: number; child: number };
    expect(processAlive(pids.kernel)).toBe(true);
    expect(processAlive(pids.child)).toBe(true);
    parent.kill("SIGKILL");
    await parent.exited;
    cleanups.push(() => rmSync(join(spikeArtifacts, "owned", `orphans-${mode}-${parent.pid}`), { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }));
    return pids;
  }

  async function waitDead(pid: number, ms: number): Promise<boolean> {
    const until = Date.now() + ms;
    while (Date.now() < until) {
      if (!processAlive(pid)) return true;
      await Bun.sleep(50);
    }
    return !processAlive(pid);
  }

  test("positive control: without it, a cell's child outlives a killed host (except on Windows)", async () => {
    const { kernel, child } = await killParent("control");
    try {
      // The kernel itself exits on stdin EOF. On POSIX it leaves its descendants behind; on Windows
      // libuv puts every spawned process in a kill-on-close job object, so they go with it.
      expect(await waitDead(kernel, 5000)).toBe(true);
      expect(await waitDead(child, 1000)).toBe(process.platform === "win32");
    } finally {
      try {
        process.kill(child, "SIGKILL");
      } catch {}
    }
  }, SLOW);

  test("with it, killing the host takes down the kernel and everything it started", async () => {
    const { kernel, child } = await killParent("hardened");
    expect(await waitDead(kernel, 5000)).toBe(true);
    expect(await waitDead(child, 5000)).toBe(true);
  }, SLOW);

  test("with it, a kernel stuck in a busy loop still dies with the host", async () => {
    const { kernel, child } = await killParent("busy");
    try {
      expect(await waitDead(kernel, 5000)).toBe(true);
      expect(await waitDead(child, 5000)).toBe(true);
    } finally {
      for (const pid of [kernel, child]) {
        try {
          process.kill(pid, "SIGKILL");
        } catch {}
      }
    }
  }, SLOW);
});
