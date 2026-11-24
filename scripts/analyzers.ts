#!/usr/bin/env bun
// Runs the custom F# analyzers (tools/Fx.Analyzers) with the fsharp-analyzers tool pinned in
// dotnet-tools.json. Run `dotnet tool restore` and `dotnet restore Fx.slnx` first.
//
//   bun scripts/analyzers.ts             analyze every F# project in Fx.slnx; fail on any finding
//   bun scripts/analyzers.ts samples     analyze the failing samples and fail unless the findings are
//                                        exactly their `// expect: <codes>` markers (proves the gate fails)
//
//   --no-build   use the analyzers already built in artifacts/ instead of building them first
//
// Where each rule applies and which files may break it is decided by analyzers.json alone, so inline
// `fsharpanalyzer: ignore-…` suppression comments are rejected.

import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, join } from "node:path";

const ROOT = join(import.meta.dir, "..");
const ANALYZERS_PROJECT = join("tools", "Fx.Analyzers", "Fx.Analyzers.fsproj");
const ANALYZERS_PATH = join("artifacts", "bin", "Fx.Analyzers", "release");
const SAMPLES = join("tests", "Fx.Analyzers.Tests", "Samples");
const SUPPRESSION = /fsharpanalyzer:/i;
const EXPECT = /\/\/\s*expect:\s*([A-Z0-9, ]+)$/;

function run(command: string[]): number {
  console.log(`$ ${command.join(" ")}`);
  const result = Bun.spawnSync(command, { cwd: ROOT, stdout: "inherit", stderr: "inherit" });
  return result.exitCode ?? 1;
}

// Runs without echoing the output, which is returned for error messages.
function runQuiet(command: string[]): { exitCode: number; output: string } {
  console.log(`$ ${command.join(" ")}`);
  const result = Bun.spawnSync(command, { cwd: ROOT });
  return { exitCode: result.exitCode ?? 1, output: result.stdout.toString() + result.stderr.toString() };
}

function fail(message: string): never {
  console.error(message);
  process.exit(1);
}

const analyzerArgs = ["dotnet", "fsharp-analyzers", "--analyzers-path", ANALYZERS_PATH, "--fail-on-type-errors"];

function checkNoSuppressions() {
  const listed = Bun.spawnSync(
    ["git", "ls-files", "--cached", "--others", "--exclude-standard", "--", "*.fs", "*.fsi", "*.fsx"],
    { cwd: ROOT },
  );
  if (listed.exitCode !== 0) fail("git ls-files failed");
  const offenders = listed.stdout
    .toString()
    .split("\n")
    // Untracked files under dot-directories (e.g. a NuGet cache in CI) are not ours.
    .filter((file) => file && !file.startsWith(".") && existsSync(join(ROOT, file)))
    .filter((file) => SUPPRESSION.test(readFileSync(join(ROOT, file), "utf8")));
  if (offenders.length > 0)
    fail(
      `Inline analyzer suppressions are not allowed; add an \`allow\` entry with a reason to analyzers.json:\n  ${offenders.join("\n  ")}`,
    );
}

function analyzeSolution(): number {
  const projects = [...readFileSync(join(ROOT, "Fx.slnx"), "utf8").matchAll(/Path="([^"]+\.fsproj)"/g)].map((m) => m[1]);
  if (projects.length === 0) fail("No F# projects found in Fx.slnx");
  const command = [...analyzerArgs, "--configuration", "Release", ...projects.flatMap((p) => ["--project", p])];
  let exitCode: number;
  if (process.env.GITHUB_ACTIONS === "true") {
    // Findings become annotations on the pull request. The tool colours every line, and the runner
    // only recognises `::error` at the start of a line, so strip the colour codes.
    const result = runQuiet([...command, "--output-format", "github"]);
    console.log(result.output.replace(/\x1b\[[0-9;]*m/g, ""));
    exitCode = result.exitCode;
  } else exitCode = run(command);
  if (exitCode === 0) console.log(`No analyzer findings in ${projects.length} projects.`);
  return exitCode;
}

// Findings as "<file>:<line> <code>", so expected and actual compare as sets.
function expectedFindings(scripts: string[]): Set<string> {
  const expected = new Set<string>();
  for (const script of scripts)
    readFileSync(join(ROOT, script), "utf8")
      .split(/\r?\n/)
      .forEach((line, i) => {
        const codes = line.match(EXPECT)?.[1] ?? "";
        for (const code of codes.split(/[\s,]+/).filter(Boolean)) expected.add(`${basename(script)}:${i + 1} ${code}`);
      });
  return expected;
}

function analyzeSamples(): number {
  const scripts = readdirSync(join(ROOT, SAMPLES))
    .filter((f) => f.endsWith(".fsx"))
    .sort()
    .map((f) => join(SAMPLES, f));
  const dir = mkdtempSync(join(tmpdir(), "analyzers-"));
  const report = join(dir, "samples.sarif");
  try {
    const { exitCode, output } = runQuiet([...analyzerArgs, "--report", report, "--script", ...scripts]);
    if (exitCode === 0) fail(`${output}\nThe analyzers passed the failing samples: the CI gate would not fail on violations.`);
    if (!existsSync(report)) fail(`${output}\nThe analyzers exited with ${exitCode} and wrote no report.`);

    type Result = { ruleId: string; locations: { physicalLocation: { artifactLocation: { uri: string }; region: { startLine: number } } }[] };
    const results: Result[] = JSON.parse(readFileSync(report, "utf8")).runs[0].results ?? [];
    const actual = new Set(
      results.map((r) => {
        const location = r.locations[0].physicalLocation;
        return `${basename(location.artifactLocation.uri)}:${location.region.startLine} ${r.ruleId}`;
      }),
    );
    const expected = expectedFindings(scripts);
    const missing = [...expected].filter((e) => !actual.has(e)).sort();
    const unexpected = [...actual].filter((a) => !expected.has(a)).sort();
    if (missing.length > 0 || unexpected.length > 0)
      fail(`${output}\nSample findings differ from their markers.\nMissing:\n  ${missing.join("\n  ")}\nUnexpected:\n  ${unexpected.join("\n  ")}`);
    console.log(`The analyzers reported exactly the ${expected.size} expected findings in ${scripts.length} samples.`);
    return 0;
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const args = process.argv.slice(2);
const unknown = args.filter((a) => a !== "samples" && a !== "--no-build");
if (unknown.length > 0) fail(`Unknown arguments: ${unknown.join(" ")}\nUsage: bun scripts/analyzers.ts [samples] [--no-build]`);

checkNoSuppressions();
if (!args.includes("--no-build") && run(["dotnet", "build", ANALYZERS_PROJECT, "--configuration", "Release"]) !== 0)
  fail("Building the analyzers failed.");
process.exit(args.includes("samples") ? analyzeSamples() : analyzeSolution());
