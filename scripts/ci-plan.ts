#!/usr/bin/env bun
// Decides what a CI run runs (.github/workflows/ci.yml, the `plan` job).
//
// Two tiers:
//   full  every target and every platform job. Pushes to main, the nightly schedule, manual runs
//         (unless `full` is false), pull requests labelled `ci:full`, and pull requests that change
//         platform-sensitive files (FULL_PATHS).
//   fast  every other pull request: build and test on Linux, macOS and Windows, Native AOT and the
//         JIT-vs-native diff on one target per OS family (FAST_AOT), and the jobs that test one
//         area (keyring, musl Bun, the spike) only when that area changes.
// A pull request that changes only Markdown runs neither builds nor AOT jobs.
//
//   bun scripts/ci-plan.ts                     in CI: reads the event from the environment
//   bun scripts/ci-plan.ts --files a.fs b.md   prints the plan a pull request changing those files gets

import { appendFileSync } from "node:fs";

type Target = { rid: string; os: string; alpine?: boolean };

export type Input = {
  event: string;
  fullInput?: string;
  labels: string[];
  files: string[];
};

export type Plan = {
  full: boolean;
  code: boolean;
  aot: Target[];
  keyring: Target[];
  bunMusl: { arch: string; os: string }[];
  networkOnAllOs: boolean;
  spike: boolean;
};

const AOT: Target[] = [
  { rid: "linux-x64", os: "ubuntu-24.04" },
  { rid: "linux-arm64", os: "ubuntu-24.04-arm" },
  { rid: "linux-musl-x64", os: "ubuntu-24.04", alpine: true },
  { rid: "linux-musl-arm64", os: "ubuntu-24.04-arm", alpine: true },
  { rid: "osx-arm64", os: "macos-26" },
  { rid: "osx-x64", os: "macos-26-intel" },
  { rid: "win-x64", os: "windows-2025" },
  { rid: "win-arm64", os: "windows-11-arm" },
];
const FAST_AOT = ["linux-x64", "linux-musl-x64", "osx-arm64", "win-x64"];

const KEYRING: Target[] = [
  { rid: "linux-x64", os: "ubuntu-24.04" },
  { rid: "linux-musl-x64", os: "ubuntu-24.04", alpine: true },
  { rid: "osx-arm64", os: "macos-26" },
  { rid: "osx-x64", os: "macos-26-intel" },
];
const FAST_KEYRING = ["linux-x64", "linux-musl-x64", "osx-arm64"];

const BUN_MUSL = [
  { arch: "x64", os: "ubuntu-24.04" },
  { arch: "arm64", os: "ubuntu-24.04-arm" },
];

// Files whose effect differs per architecture or runner: native code and P/Invoke, build and
// package settings, the workflows themselves, and the pinned runtime's per-target archives.
const FULL_PATHS = [
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
  "scripts/ci-plan.ts",
];
// The keyring job also runs the credential-store conformance suite on the keyring store (T121).
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
const anyMatch = (patterns: string[], files: string[]) => {
  const res = patterns.map(glob);
  return files.some((f) => res.some((re) => re.test(f)));
};

export function plan(input: Input): Plan {
  const pr = input.event === "pull_request";
  const full =
    input.event === "push" ||
    input.event === "schedule" ||
    (input.event === "workflow_dispatch" && input.fullInput !== "false") ||
    input.labels.includes("ci:full") ||
    (pr && anyMatch(FULL_PATHS, input.files));
  const code = !pr || full || input.files.length === 0 || !input.files.every((f) => f.endsWith(".md"));
  const keyringChanged = pr && anyMatch(KEYRING_PATHS, input.files);
  const bunChanged = pr && anyMatch(BUN_PATHS, input.files);
  return {
    full,
    code,
    aot: code ? AOT.filter((t) => full || FAST_AOT.includes(t.rid)) : [],
    keyring: full ? KEYRING : code && keyringChanged ? KEYRING.filter((t) => FAST_KEYRING.includes(t.rid)) : [],
    bunMusl: full ? BUN_MUSL : code && bunChanged ? BUN_MUSL.slice(0, 1) : [],
    networkOnAllOs: full || bunChanged,
    spike: input.event === "schedule" || (pr && anyMatch(SPIKE_PATHS, input.files)),
  };
}

async function changedFiles(base: string): Promise<string[]> {
  const out = await Bun.$`git diff --name-only ${base}...HEAD`.quiet().text();
  return out.split("\n").filter((l) => l.length > 0);
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
      aot: JSON.stringify({ include: p.aot }),
      "aot-run": String(p.aot.length > 0),
      keyring: JSON.stringify({ include: p.keyring }),
      "keyring-run": String(p.keyring.length > 0),
      "bun-musl": JSON.stringify({ include: p.bunMusl }),
      "bun-musl-run": String(p.bunMusl.length > 0),
      "network-all-os": String(p.networkOnAllOs),
      spike: String(p.spike),
    };
    const lines = Object.entries(outputs).map(([k, v]) => `${k}=${v}`);
    if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, lines.join("\n") + "\n");
    const summary = [
      `### CI plan: ${p.full ? "full" : p.code ? "fast" : "docs only"}`,
      `- changed files: ${files.length}`,
      `- AOT: ${p.aot.map((t) => t.rid).join(", ") || "none"}`,
      `- keyring: ${p.keyring.map((t) => t.rid).join(", ") || "none"}`,
      `- pinned Bun on musl: ${p.bunMusl.map((t) => t.arch).join(", ") || "none"}`,
      `- network Bun test on: ${p.networkOnAllOs ? "every OS" : "Linux"}`,
      `- spike: ${p.spike}`,
      "",
      "Label a pull request `ci:full`, or run `gh workflow run CI --ref <branch>`, for every target.",
    ].join("\n");
    if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary + "\n");
    console.log(summary);
  }
}
