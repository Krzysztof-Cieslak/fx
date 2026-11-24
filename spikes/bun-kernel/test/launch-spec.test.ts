import { describe, expect, test } from "bun:test";
import { type LaunchSpec, bunKernelLaunch, matchesPattern, ownedLayout, ownedVars, resolveLaunch } from "../src/launch-spec.ts";

const layout = ownedLayout("/owned");
const vars = { ...ownedVars(layout), "bun.exe": "/runtimes/bun", "kernel.entry": "/kernel/kernel.js", "kernel.global": "g" };

const hostileParent = {
  PATH: "/usr/bin",
  HOME: "/home/user",
  SystemRoot: "C:\\Windows",
  BUN_OPTIONS: "--preload=./evil.ts",
  bun_options: "--preload=./evil.ts",
  NODE_OPTIONS: "--require=./evil.js",
  NODE_PATH: "/evil",
  BUN_INSPECT: "127.0.0.1:9229",
  BUN_INSPECT_CONNECT_TO: "ws://127.0.0.1:1",
  BUN_JSC_useJIT: "0",
  BUN_CONFIG_REGISTRY: "https://evil.example",
  NPM_CONFIG_USERCONFIG: "/evil/.npmrc",
  LD_PRELOAD: "/evil.so",
  DYLD_INSERT_LIBRARIES: "/evil.dylib",
  AWS_SECRET_ACCESS_KEY: "secret",
  GITHUB_TOKEN: "secret",
};

describe("matchesPattern", () => {
  test("is a case-insensitive glob", () => {
    expect(matchesPattern("BUN_INSPECT_CONNECT_TO", "BUN_INSPECT*")).toBe(true);
    expect(matchesPattern("node_options", "NODE_OPTIONS")).toBe(true);
    expect(matchesPattern("Path", "PATH")).toBe(true);
    expect(matchesPattern("PATHEXT", "PATH")).toBe(false);
    expect(matchesPattern("X.Y", "X*Y")).toBe(true);
  });
});

describe("resolveLaunch", () => {
  const resolved = resolveLaunch(bunKernelLaunch, vars, hostileParent, { trusted: false });

  test("keeps only allowlisted variables and the launcher's own", () => {
    const names = Object.keys(resolved.env);
    const allowed = [...bunKernelLaunch.envAllowlist, ...Object.keys(bunKernelLaunch.envSet)].map((n) => n.toUpperCase());
    for (const name of names) expect(allowed).toContain(name.toUpperCase());
    expect(resolved.env.PATH).toBe("/usr/bin");
    expect(resolved.env.SystemRoot).toBe("C:\\Windows");
    expect(resolved.env.AWS_SECRET_ACCESS_KEY).toBeUndefined();
    expect(resolved.env.GITHUB_TOKEN).toBeUndefined();
  });

  test("removes every injection vector", () => {
    for (const name of Object.keys(resolved.env)) {
      expect(bunKernelLaunch.envDeny.some((p) => matchesPattern(name, p))).toBe(false);
    }
  });

  test("redirects home, caches and temp into owned directories", () => {
    expect(resolved.env.HOME).toBe(layout.home);
    expect(resolved.env.USERPROFILE).toBe(layout.home);
    expect(resolved.env.TMPDIR).toBe(layout.tmp);
    expect(resolved.env.BUN_RUNTIME_TRANSPILER_CACHE_PATH).toBe(layout.transpilerCache);
    expect(resolved.env.DO_NOT_TRACK).toBe("1");
  });

  test("puts the hardened flags and the untrusted override before the entry point", () => {
    expect(resolved.cmd).toEqual([
      "/runtimes/bun",
      `--config=${layout.bunfig}`,
      "--no-env-file",
      "--no-install",
      "--no-orphans",
      "--no-macros",
      `--tsconfig-override=${layout.tsconfig}`,
      "/kernel/kernel.js",
      "--global=g",
    ]);
    expect(resolved.cwd).toBe(layout.cwd);
  });

  test("drops the tsconfig override once the project is trusted", () => {
    const trusted = resolveLaunch(bunKernelLaunch, vars, hostileParent, { trusted: true });
    expect(trusted.cmd.some((a) => a.startsWith("--tsconfig-override"))).toBe(false);
  });

  test("rejects specs that set or allow denied variables", () => {
    const sets: LaunchSpec = { ...bunKernelLaunch, envSet: { ...bunKernelLaunch.envSet, NODE_OPTIONS: "--x" } };
    expect(() => resolveLaunch(sets, vars, {}, { trusted: true })).toThrow(/denied variable NODE_OPTIONS/);
    const allows: LaunchSpec = { ...bunKernelLaunch, envAllowlist: [...bunKernelLaunch.envAllowlist, "BUN_OPTIONS"] };
    expect(() => resolveLaunch(allows, vars, {}, { trusted: true })).toThrow(/denied variable BUN_OPTIONS/);
    const noDeny: LaunchSpec = { ...bunKernelLaunch, envDeny: [] };
    expect(() => resolveLaunch(noDeny, vars, {}, { trusted: true })).toThrow(/envDeny is required/);
  });

  test("deny wins over a wildcard allowlist", () => {
    const wide: LaunchSpec = { ...bunKernelLaunch, envAllowlist: ["*"] };
    const env = resolveLaunch(wide, vars, hostileParent, { trusted: true }).env;
    expect(env.BUN_OPTIONS).toBeUndefined();
    expect(env.bun_options).toBeUndefined();
    expect(env.GITHUB_TOKEN).toBe("secret");
  });

  test("fails on unknown template variables", () => {
    expect(() => resolveLaunch(bunKernelLaunch, { "bun.exe": "/bun" }, {}, { trusted: true })).toThrow(/unknown variable/);
  });
});
