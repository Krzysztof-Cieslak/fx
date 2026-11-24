// LaunchSpec as data (spec §6.2) and its resolution into a command line, environment and cwd.
//
// `resolveLaunch` is pure: the host's process runner applies the result verbatim, replacing the
// child's environment instead of merging into the parent's. T114 ports this to F#.

import { join } from "node:path";

export type CwdPolicy = "owned" | "workspace";

export interface LaunchSpec {
  /** Executable template, e.g. `${bun.exe}`. */
  exe: string;
  /** Argument templates. Runtime flags come before the first non-flag argument (the entry point). */
  args: string[];
  /** Names (or `*` patterns) inherited from the parent environment. Everything else is dropped. */
  envAllowlist: string[];
  /** Variables the launcher sets (templates). Must not match `envDeny`. */
  envSet: Record<string, string>;
  /** Runtime injection vectors (`*` patterns). Required; never present in the child environment. */
  envDeny: string[];
  cwdPolicy: CwdPolicy;
  /** Runtime flags added while the project is untrusted, inserted before the entry point. */
  untrustedOverrides: string[];
}

/** Directories the launcher owns; nothing project-local lives here. */
export interface OwnedLayout {
  root: string;
  cwd: string;
  home: string;
  config: string;
  cache: string;
  transpilerCache: string;
  installCache: string;
  tmp: string;
  bunfig: string;
  tsconfig: string;
  check: string;
}

export function ownedLayout(root: string): OwnedLayout {
  return {
    root,
    cwd: join(root, "cwd"),
    home: join(root, "home"),
    config: join(root, "home", "config"),
    cache: join(root, "cache"),
    transpilerCache: join(root, "cache", "transpiler"),
    installCache: join(root, "cache", "install"),
    tmp: join(root, "tmp"),
    bunfig: join(root, "config", "bunfig.toml"),
    tsconfig: join(root, "config", "tsconfig.json"),
    check: join(root, "check"),
  };
}

/** The fx-owned bunfig: same switches as the flags, so either one alone is enough. */
export const ownedBunfig = `# Owned by the launcher. Project bunfig.toml files are never read.
telemetry = false
env = false

[install]
auto = "disable"

[run]
noOrphans = true
`;

/** Used through --tsconfig-override until the project is trusted. */
export const ownedTsconfig = JSON.stringify({ compilerOptions: {} }, null, 2) + "\n";

/** The hardened launch of the bun-ts kernel (spec §6.3). */
export const bunKernelLaunch: LaunchSpec = {
  exe: "${bun.exe}",
  args: [
    "--config=${owned.bunfig}",
    "--no-env-file",
    "--no-install",
    "--no-orphans",
    "--no-macros",
    "${kernel.entry}",
    "--global=${kernel.global}",
  ],
  untrustedOverrides: ["--tsconfig-override=${owned.tsconfig}"],
  envAllowlist: [
    "PATH",
    "LANG",
    "LC_ALL",
    "LC_CTYPE",
    "TZ",
    // Windows: the OS loader, Winsock and CNG need these.
    "SystemRoot",
    "SystemDrive",
    "windir",
    "ComSpec",
    "PATHEXT",
    "NUMBER_OF_PROCESSORS",
    "PROCESSOR_ARCHITECTURE",
    "OS",
  ],
  envSet: {
    DO_NOT_TRACK: "1",
    NO_COLOR: "1",
    HOME: "${owned.home}",
    USERPROFILE: "${owned.home}",
    XDG_CONFIG_HOME: "${owned.config}",
    XDG_CACHE_HOME: "${owned.cache}",
    TMPDIR: "${owned.tmp}",
    TEMP: "${owned.tmp}",
    TMP: "${owned.tmp}",
    BUN_RUNTIME_TRANSPILER_CACHE_PATH: "${owned.transpilerCache}",
    BUN_INSTALL_CACHE_DIR: "${owned.installCache}",
  },
  envDeny: [
    "BUN_OPTIONS",
    "BUN_INSPECT*",
    "BUN_CONFIG_*",
    "BUN_JSC_*",
    "BUN_FEATURE_FLAG_*",
    "BUN_DEBUG*",
    "NODE_OPTIONS",
    "NODE_PATH",
    "NODE_CHANNEL_*",
    "NODE_REPL_EXTERNAL_MODULE",
    "NODE_TLS_REJECT_UNAUTHORIZED",
    "NPM_CONFIG_*",
    "LD_PRELOAD",
    "LD_LIBRARY_PATH",
    "LD_AUDIT",
    "DYLD_*",
  ],
  cwdPolicy: "owned",
};

export interface ResolvedLaunch {
  cmd: string[];
  env: Record<string, string>;
  cwd: string;
}

/** Case-insensitive `*` glob: environment names are case-insensitive on Windows, so deny them everywhere. */
export function matchesPattern(name: string, pattern: string): boolean {
  const source = pattern
    .split("*")
    .map((part) => part.replace(/[.+?^${}()|[\]\\]/g, "\\$&"))
    .join(".*");
  return new RegExp(`^${source}$`, "i").test(name);
}

const isDenied = (spec: LaunchSpec, name: string) => spec.envDeny.some((p) => matchesPattern(name, p));

export function expandTemplate(template: string, vars: Record<string, string>): string {
  return template.replace(/\$\{([A-Za-z0-9_.-]+)\}/g, (_, key: string) => {
    const value = vars[key];
    if (value === undefined) throw new Error(`launch spec: unknown variable \${${key}}`);
    return value;
  });
}

export function validateLaunchSpec(spec: LaunchSpec): void {
  if (spec.envDeny.length === 0) throw new Error("launch spec: envDeny is required");
  for (const key of Object.keys(spec.envSet)) {
    if (isDenied(spec, key)) throw new Error(`launch spec: envSet sets denied variable ${key}`);
  }
  for (const name of spec.envAllowlist) {
    if (!name.includes("*") && isDenied(spec, name)) throw new Error(`launch spec: envAllowlist allows denied variable ${name}`);
  }
}

export interface ResolveOptions {
  trusted: boolean;
  /** Required when `cwdPolicy` is `workspace`. */
  workspace?: string;
}

export function resolveLaunch(
  spec: LaunchSpec,
  vars: Record<string, string>,
  parentEnv: Record<string, string | undefined>,
  options: ResolveOptions,
): ResolvedLaunch {
  validateLaunchSpec(spec);
  const env: Record<string, string> = {};
  for (const [name, value] of Object.entries(parentEnv)) {
    if (value === undefined) continue;
    if (!spec.envAllowlist.some((p) => matchesPattern(name, p))) continue;
    if (isDenied(spec, name)) continue;
    env[name] = value;
  }
  for (const [name, template] of Object.entries(spec.envSet)) {
    for (const existing of Object.keys(env)) {
      if (existing.toUpperCase() === name.toUpperCase()) delete env[existing];
    }
    env[name] = expandTemplate(template, vars);
  }
  const args = spec.args.map((a) => expandTemplate(a, vars));
  if (!options.trusted && spec.untrustedOverrides.length > 0) {
    const entryIndex = args.findIndex((a) => !a.startsWith("-"));
    const at = entryIndex < 0 ? args.length : entryIndex;
    args.splice(at, 0, ...spec.untrustedOverrides.map((a) => expandTemplate(a, vars)));
  }
  let cwd: string;
  if (spec.cwdPolicy === "owned") cwd = expandTemplate("${owned.cwd}", vars);
  else if (options.workspace) cwd = options.workspace;
  else throw new Error("launch spec: workspace cwd requested but no workspace given");
  return { cmd: [expandTemplate(spec.exe, vars), ...args], env, cwd };
}

/** Template variables for an owned layout. */
export function ownedVars(layout: OwnedLayout): Record<string, string> {
  return {
    "owned.cwd": layout.cwd,
    "owned.home": layout.home,
    "owned.config": layout.config,
    "owned.cache": layout.cache,
    "owned.transpilerCache": layout.transpilerCache,
    "owned.installCache": layout.installCache,
    "owned.tmp": layout.tmp,
    "owned.bunfig": layout.bunfig,
    "owned.tsconfig": layout.tsconfig,
  };
}
