#!/usr/bin/env bun
// Type-checks the TypeScript declarations that Fx.Contracts.TypeScript generates, with Bun's built-in
// checker (`bun check`, TypeScript 7; CI pins Bun 1.4.5 as for the kernel). The golden files in
// tests/Fx.Contracts.TypeScript.Tests/golden are the generator's output (the F# tests keep them
// current), so checking them checks the generator.
//
//   bun scripts/declarations.ts
//
// 1. typescript/tsconfig.json: every golden file, the shared fixtures (JSON the F# codecs write, and
//    JSON they reject under `@ts-expect-error`) and usage.ts must type-check under strict options.
// 2. typescript/must-fail: must fail with exactly the errors its `// expect: TSxxxx` comments list,
//    which proves that the checker runs and that the types are not `any`.

import { readFileSync, readdirSync } from "node:fs";
import { basename, join, relative } from "node:path";

const ROOT = join(import.meta.dir, "..");
const TESTS = join(ROOT, "tests", "Fx.Contracts.TypeScript.Tests");
const GOLDEN = join(TESTS, "golden");
const PROJECT = join(TESTS, "typescript");
const MUST_FAIL = join(PROJECT, "must-fail");
const DIAGNOSTIC = /^(.*?)\((\d+),(\d+)\): error (TS\d+): (.*)$/;
const EXPECT = /\/\/\s*expect:\s*(TS\d+)\s*$/;

interface Diagnostic {
  file: string;
  line: number;
  code: string;
  text: string;
}

function fail(message: string): never {
  console.error(message);
  process.exit(1);
}

// `--no-script`: otherwise `bun check` runs a "check" script from a package.json above the project
// instead of checking (docs/spikes/bun-kernel.md). `--no-pretty` pins the one-line format.
function check(project: string): { exitCode: number; output: string; diagnostics: Diagnostic[] } {
  const command = [process.execPath, "check", "--no-script", "--no-pretty", "-p", project];
  console.log(`$ bun ${command.slice(1).join(" ")}`);
  const result = Bun.spawnSync(command, { cwd: project });
  const output = result.stdout.toString() + result.stderr.toString();
  const diagnostics = output
    .split(/\r?\n/)
    .map((line) => DIAGNOSTIC.exec(line))
    .filter((match) => match !== null)
    .map((match) => ({ file: basename(match[1]!), line: Number(match[2]), code: match[4]!, text: match[0] }));
  return { exitCode: result.exitCode ?? 1, output, diagnostics };
}

console.log(`Bun ${Bun.version}, TypeScript ${process.versions.typescript ?? "(unknown)"}`);

// Every golden file is in the project: a new one cannot go unchecked.
const tsconfig = readFileSync(join(PROJECT, "tsconfig.json"), "utf8");
const unlisted = readdirSync(GOLDEN).filter(
  (name) => (name.endsWith(".d.ts") || name.endsWith(".ts")) && !tsconfig.includes(`"../golden/${name}"`),
);
if (unlisted.length > 0)
  fail(`Add these golden files to ${relative(ROOT, join(PROJECT, "tsconfig.json"))}: ${unlisted.join(", ")}`);

const ok = check(PROJECT);
if (ok.exitCode !== 0) fail(`The generated declarations do not type-check:\n${ok.output}`);

const source = readFileSync(join(MUST_FAIL, "must-fail.ts"), "utf8").split(/\r?\n/);
const expected = source.flatMap((line, i) => {
  const match = EXPECT.exec(line);
  return match ? [`must-fail.ts:${i + 1} ${match[1]}`] : [];
});
if (expected.length === 0) fail("must-fail.ts has no `// expect: TSxxxx` comments");

const failing = check(MUST_FAIL);
if (failing.exitCode === 0) fail("must-fail.ts type-checks, but it must not");
const actual = failing.diagnostics.map((d) => `${d.file}:${d.line} ${d.code}`);
const missing = expected.filter((e) => !actual.includes(e));
const unexpected = failing.diagnostics.filter((d) => !expected.includes(`${d.file}:${d.line} ${d.code}`));
if (missing.length > 0 || unexpected.length > 0 || actual.length !== expected.length)
  fail(
    [
      "must-fail.ts did not fail exactly as its `expect:` comments say.",
      ...missing.map((m) => `  expected, not reported: ${m}`),
      ...unexpected.map((d) => `  reported, not expected: ${d.text}`),
      failing.output,
    ].join("\n"),
  );

console.log(`The declarations type-check, and must-fail.ts fails with its ${expected.length} expected errors.`);
