#!/usr/bin/env bun
// Task-graph tooling for tasks.md.
//
// The task tables in tasks.md are the source of truth. This script validates them
// and regenerates the derived section (waves, critical path, Mermaid graph).
//
//   bun scripts/tasks.ts          regenerate the derived section in place
//   bun scripts/tasks.ts check    fail if the derived section is stale or the graph is invalid
//   bun scripts/tasks.ts ready    list todo tasks whose dependencies are all done

import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

type Status = "todo" | "in-progress" | "done" | "blocked";
type Task = { id: string; label: string; deps: string[]; spec: string; status: Status; phase: string; doneWhen: string };

const FILE = join(import.meta.dir, "..", "tasks.md");
const START = "<!-- tasks:generated -->";
const END = "<!-- /tasks:generated -->";
const STATUSES: Status[] = ["todo", "in-progress", "done", "blocked"];
const MILESTONE = "T132";

function parse(md: string): Task[] {
  const tasks: Task[] = [];
  let phase = "";
  for (const line of md.split("\n")) {
    const heading = line.match(/^### (.+)$/);
    if (heading) phase = heading[1].trim();
    const row = line.match(/^\| (T\d{3}[a-z]?) \|(.*)\|\s*$/);
    if (!row) continue;
    const cells = row[2].split("|").map((c) => c.trim());
    if (cells.length < 5) throw new Error(`Malformed row for ${row[1]}`);
    const [label, deps, spec, status, ...rest] = cells;
    if (!STATUSES.includes(status as Status)) throw new Error(`${row[1]}: unknown status "${status}"`);
    tasks.push({
      id: row[1],
      label,
      deps: deps === "—" || deps === "" ? [] : deps.split(",").map((d) => d.trim()),
      spec,
      status: status as Status,
      phase,
      doneWhen: rest.join("|"),
    });
  }
  return tasks;
}

function validate(tasks: Task[]): Map<string, number> {
  const byId = new Map(tasks.map((t) => [t.id, t]));
  if (byId.size !== tasks.length) throw new Error("Duplicate task ids");
  for (const t of tasks)
    for (const d of t.deps) if (!byId.has(d)) throw new Error(`${t.id}: unknown dependency ${d}`);

  // Longest-path depth ("wave"); detects cycles.
  const level = new Map<string, number>();
  const visiting = new Set<string>();
  const depth = (id: string): number => {
    const known = level.get(id);
    if (known !== undefined) return known;
    if (visiting.has(id)) throw new Error(`Dependency cycle through ${id}`);
    visiting.add(id);
    const deps = byId.get(id)!.deps;
    const l = deps.length ? 1 + Math.max(...deps.map(depth)) : 0;
    visiting.delete(id);
    level.set(id, l);
    return l;
  };
  for (const t of tasks) depth(t.id);
  return level;
}

function criticalPath(tasks: Task[], level: Map<string, number>, target: string): string[] {
  const byId = new Map(tasks.map((t) => [t.id, t]));
  if (!byId.has(target)) return [];
  const path = [target];
  for (;;) {
    const deps = byId.get(path[path.length - 1])!.deps;
    if (!deps.length) break;
    path.push(deps.reduce((a, b) => (level.get(b)! > level.get(a)! ? b : a)));
  }
  return path.reverse();
}

function generate(tasks: Task[], level: Map<string, number>): string {
  const out: string[] = [START, ""];
  out.push("## Parallel waves", "");
  out.push("Earliest wave in which each task can start (longest dependency chain). Tasks in the same wave are independent of each other.", "");
  const max = Math.max(...level.values());
  for (let l = 0; l <= max; l++) {
    const members = tasks.filter((t) => level.get(t.id) === l).map((t) => `${t.id} ${t.label}`);
    out.push(`- **Wave ${l}:** ${members.join(", ")}`);
  }
  out.push("");
  const cp = criticalPath(tasks, level, MILESTONE);
  if (cp.length) out.push(`**Critical path to dogfooding (${MILESTONE}):** ${cp.join(" → ")}`, "");

  out.push("## Graph", "");
  out.push("Green = done, yellow = in progress, red = blocked.", "");
  out.push("```mermaid", "flowchart LR");
  const phases = [...new Set(tasks.map((t) => t.phase))];
  phases.forEach((p, i) => {
    out.push(`  subgraph P${i}["${p.replace(/"/g, "'")}"]`);
    for (const t of tasks.filter((t) => t.phase === p)) out.push(`    ${t.id}["${t.id} ${t.label.replace(/"/g, "'")}"]`);
    out.push("  end");
  });
  for (const t of tasks) for (const d of t.deps) out.push(`  ${d} --> ${t.id}`);
  out.push("  classDef done fill:#c8e6c9,stroke:#2e7d32");
  out.push("  classDef inprogress fill:#fff3c4,stroke:#f9a825");
  out.push("  classDef blocked fill:#ffcdd2,stroke:#c62828");
  const cls = (s: Status, name: string) => {
    const ids = tasks.filter((t) => t.status === s).map((t) => t.id);
    if (ids.length) out.push(`  class ${ids.join(",")} ${name}`);
  };
  cls("done", "done");
  cls("in-progress", "inprogress");
  cls("blocked", "blocked");
  out.push("```", "", END);
  return out.join("\n");
}

function render(md: string): string {
  const tasks = parse(md);
  const level = validate(tasks);
  const s = md.indexOf(START);
  const e = md.indexOf(END);
  if (s < 0 || e < 0) throw new Error(`Missing ${START} / ${END} markers`);
  return md.slice(0, s) + generate(tasks, level) + md.slice(e + END.length);
}

const cmd = process.argv[2] ?? "generate";
const md = readFileSync(FILE, "utf8");

if (cmd === "generate") {
  writeFileSync(FILE, render(md));
} else if (cmd === "check") {
  if (render(md) !== md) {
    console.error("tasks.md derived section is stale — run `bun scripts/tasks.ts`.");
    process.exit(1);
  }
} else if (cmd === "ready") {
  const tasks = parse(md);
  const level = validate(tasks);
  const done = new Set(tasks.filter((t) => t.status === "done").map((t) => t.id));
  const cp = new Set(criticalPath(tasks, level, MILESTONE));
  const ready = tasks.filter((t) => t.status === "todo" && t.deps.every((d) => done.has(d)));
  for (const t of ready) console.log(`${t.id}${cp.has(t.id) ? " *" : "  "} ${t.label}  (${t.spec})`);
  if (!ready.length) console.log("No ready tasks.");
  else console.log("\n* = on the critical path");
} else {
  console.error(`Unknown command: ${cmd}`);
  process.exit(2);
}
