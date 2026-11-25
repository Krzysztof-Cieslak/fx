#!/usr/bin/env bun
// Decides what a CI run runs (.github/workflows/ci.yml, the `plan` job).
//
// Two tiers:
//   fast  every pull request: lint, and one `os` job per OS (linux-x64, osx-arm64, win-x64) that
//         builds, tests, publishes with Native AOT and runs the JIT-vs-native diff. Each area's own
//         steps (keyring, sandbox, the network Bun test on macOS and Windows, the spike) run only when
//         the area changes.
//   full  the same jobs with every area's steps, plus the other targets (`targets` job: TARGETS).
//         Pushes to main, the nightly schedule, manual runs (unless `full` is false) and pull
//         requests labelled `ci:full`. Changed paths never pick the full tier: a pull request that
//         touches platform-sensitive files (PLATFORM_PATHS) gets a notice asking for the label.
// A pull request that changes only Markdown runs lint only, without its .NET steps.
//
//   bun scripts/ci-plan.ts                     in CI: reads the event from the environment
//   bun scripts/ci-plan.ts --files a.fs b.md   prints the plan a pull request changing those files gets

import { appendFileSync } from "node:fs";

// A target of the `targets` job and what it runs besides Native AOT: the areas its runner covers
// and the os jobs cannot.
export type Target = {
  rid: string;
  os: string;
  alpine: boolean; // in the Alpine container (musl)
  keyring: boolean; // the live keyring: the probe's headless states and the keyring scenarios
  sandbox: boolean; // the sandbox scenarios with this OS's confining driver
  bun: boolean; // the pinned Bun's musl build, the kernel conformance and the interpreter on it
  // Builds and runs the areas' test suites too (on the JIT). Without it, an area runs only what needs
  // no build beyond the scenarios.
  suites: boolean;
};

export type Input = {
  event: string;
  fullInput?: string;
  labels: string[];
  files: string[];
};

export type Plan = {
  full: boolean;
  code: boolean;
  keyring: boolean;
  sandbox: boolean;
  bun: boolean;
  spike: boolean;
  targets: Target[];
  // Changed platform-sensitive files, for the notice (pull requests in the fast tier only).
  platform: string[];
};

// The os jobs' runners (fixed in ci.yml; scripts/ci-plan.test.ts checks the two agree).
export const OS_JOBS = [
  { rid: "linux-x64", os: "ubuntu-24.04" },
  { rid: "osx-arm64", os: "macos-26" },
  { rid: "win-x64", os: "windows-2025" },
];

const target = (rid: string, os: string, extra: Partial<Target> = {}): Target => ({
  rid,
  os,
  alpine: false,
  keyring: false,
  sandbox: false,
  bun: false,
  suites: false,
  ...extra,
});

// The full tier's other targets, natively. Besides Native AOT, each runs what only it can show:
// bubblewrap's arm64 seccomp program (linux-arm64), gnome-keyring in Alpine and the pinned Bun's musl
// builds with the bun-ts kernel on them (musl), the Keychain and Seatbelt on Intel (osx-x64). osx-x64
// runs only their native scenarios: building the suites took ten minutes on the Intel runner, and
// they run on osx-arm64 already.
export const TARGETS: Target[] = [
  target("linux-arm64", "ubuntu-24.04-arm", { sandbox: true, suites: true }),
  target("linux-musl-x64", "ubuntu-24.04", { alpine: true, keyring: true, bun: true, suites: true }),
  target("linux-musl-arm64", "ubuntu-24.04-arm", { alpine: true, bun: true, suites: true }),
  target("osx-x64", "macos-26-intel", { keyring: true, sandbox: true }),
  target("win-arm64", "windows-11-arm"),
];

// Files whose effect can differ per architecture or runner: native code and P/Invoke, build and
// package settings, the workflows themselves, and the pinned runtime's per-target archives. Pull
// requests run three targets only, so a change here asks for the `ci:full` label before merging.
const PLATFORM_PATHS = [
  ".github/**",
  "global.json",
  "nuget.config",
  "Directory.Build.props",
  "Directory.Build.targets",
  "Directory.Packages.props",
  "native/**",
  "src/Fx.Pty/**",
  "src/Fx.Keyring/**",
  "src/Fx.Sqlite/**",
  "src/Fx.Transport.Http/**",
  "extensions/Fx.Ext.RuntimeBun/**",
  // Sandbox drivers: what confines a process differs per OS, kernel and architecture (bwrap's
  // namespaces, the seccomp program per architecture, Seatbelt).
  "extensions/Fx.Ext.Sandbox*/**",
  "src/Fx.Sdk/Sandbox.fs",
  "src/Fx.Environments/Sandbox.fs",
  "scripts/ci-plan.ts",
];
// The keyring steps also run the credential-store conformance suite on the keyring store (T121).
const KEYRING_PATHS = [
  "**/*Keyring*/**",
  "**/*Keyring*",
  "**/*Credential*",
  "src/Fx.Auth/**",
  "tests/Fx.Auth.Tests/**",
  "extensions/Fx.Ext.Store*/**",
  "src/Fx.Effects/PrivateFiles.fs",
  "src/Fx.Effects/Secrets.fs",
  "docs/spikes/keychains.md",
];
// The network Bun test off Linux: the runtime, the installer and the kernel.
const BUN_PATHS = [
  "extensions/Fx.Ext.RuntimeBun/**",
  "extensions/Fx.Ext.InterpreterBunTs/**",
  "packages/kernel-bun-ts/**",
  "src/Fx.Components/**",
  "src/Fx.Components.Live/**",
  "src/Fx.Kernel/**",
  "src/Fx.Interpreters/**",
  "tests/Fx.Ext.RuntimeBun.Tests/**",
  "tests/Fx.Kernel.Tests/**",
  "tests/Fx.Ext.InterpreterBunTs.Tests/**",
];
// The sandbox steps: the drivers, how environments use them, and their tests.
const SANDBOX_PATHS = [
  "extensions/Fx.Ext.Sandbox*/**",
  "**/*Sandbox*",
  "src/Fx.Environments/**",
  "src/Fx.Envd/**",
  "src/Fx.Envd.Live/**",
  "tests/Fx.Environments.Tests/**",
  "tests/Fx.Ext.Sandbox*/**",
  "docs/modules/sandbox.md",
];
const SPIKE_PATHS = ["spikes/**"];

// Glob subset: `**` spans directories, `*` stays within one segment.
const glob = (pattern: string) =>
  new RegExp(
    "^" +
      pattern
        .split("**")
        .map((part) => part.split("*").map((s) => s.replace(/[.+?^${}()|[\]\\]/g, "\\$&")).join("[^/]*"))
        .join(".*") +
      "$",
  );
const matching = (patterns: string[], files: string[]) => {
  const res = patterns.map(glob);
  return files.filter((f) => res.some((re) => re.test(f)));
};
const anyMatch = (patterns: string[], files: string[]) => matching(patterns, files).length > 0;

export function plan(input: Input): Plan {
  const pr = input.event === "pull_request";
  const full =
    input.event === "push" ||
    input.event === "schedule" ||
    (input.event === "workflow_dispatch" && input.fullInput !== "false") ||
    (pr && input.labels.includes("ci:full"));
  const code = !pr || full || input.files.length === 0 || !input.files.every((f) => f.endsWith(".md"));
  // Lane's notes (.lane/memory/<path>/…) name the files they are about but change nothing in them.
  const files = input.files.filter((f) => !f.startsWith(".lane/"));
  const area = (paths: string[]) => code && (full || (pr && anyMatch(paths, files)));
  return {
    full,
    code,
    keyring: area(KEYRING_PATHS),
    sandbox: area(SANDBOX_PATHS),
    bun: area(BUN_PATHS),
    spike: full || (pr && anyMatch(SPIKE_PATHS, files)),
    targets: full ? TARGETS : [],
    platform: pr && !full ? matching(PLATFORM_PATHS, files) : [],
  };
}

async function changedFiles(base: string): Promise<string[]> {
  const out = await Bun.$`git diff --name-only ${base}...HEAD`.quiet().text();
  return out.split("\n").filter((l) => l.length > 0);
}

function summary(p: Plan, changed: number): string {
  const areas = [
    p.keyring && "keyring",
    p.sandbox && "sandbox",
    p.bun && "the network Bun test on macOS and Windows",
    p.spike && "the Bun kernel spike",
  ].filter(Boolean);
  const lines = [
    `### CI plan: ${p.full ? "full" : p.code ? "fast" : "docs only"}`,
    `- changed files: ${changed}`,
    `- os jobs: ${p.code ? OS_JOBS.map((t) => t.rid).join(", ") : "none"}`,
    `- other targets: ${p.targets.map((t) => t.rid).join(", ") || "none"}`,
    `- area steps: ${areas.join(", ") || "none"}`,
  ];
  if (p.platform.length > 0) {
    const shown = p.platform.slice(0, 10).map((f) => `\`${f}\``);
    if (p.platform.length > shown.length) shown.push(`and ${p.platform.length - shown.length} more`);
    lines.push(
      "",
      `> [!WARNING]`,
      `> This pull request touches platform-sensitive code (${shown.join(", ")}); label it \`ci:full\` to run every target before merging.`,
    );
  }
  lines.push("", "Label a pull request `ci:full`, or run `gh workflow run CI --ref <branch>`, for every target.");
  return lines.join("\n");
}

if (import.meta.main) {
  const args = process.argv.slice(2);
  if (args[0] === "--files") {
    console.log(JSON.stringify(plan({ event: "pull_request", labels: [], files: args.slice(1) }), null, 2));
  } else {
    const event = process.env.EVENT ?? "";
    const base = process.env.BASE_SHA ?? "";
    const files = event === "pull_request" && base ? await changedFiles(base) : [];
    const labels: string[] = JSON.parse(process.env.LABELS || "[]") ?? [];
    const p = plan({ event, fullInput: process.env.FULL_INPUT, labels, files });
    const outputs = {
      full: String(p.full),
      code: String(p.code),
      keyring: String(p.keyring),
      sandbox: String(p.sandbox),
      bun: String(p.bun),
      spike: String(p.spike),
      targets: JSON.stringify({ include: p.targets }),
    };
    const lines = Object.entries(outputs).map(([k, v]) => `${k}=${v}`);
    if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, lines.join("\n") + "\n");
    const text = summary(p, files.length);
    if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, text + "\n");
    console.log(text);
    // An annotation, so the notice shows on the pull request's checks too.
    if (p.platform.length > 0)
      console.log(
        "::warning title=Platform-sensitive change::This pull request touches platform-sensitive code; label it ci:full to run every target before merging.",
      );
  }
}
