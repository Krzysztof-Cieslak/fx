// Type-checking cells with `bun check` (Bun 1.4 ships a TypeScript 7 checker; no `typescript`
// package is needed).
//
// Each executed cell is written to the check directory as a module that exports its top-level
// names. A later cell starts with one line that re-declares the names visible to it:
//   declare let items: typeof import("./cell-1.ts").items; type Item = import("./cell-1.ts").Item;
// `declare let` (not `import`) keeps them assignable, as they are at runtime, where replMode turns
// every declaration into a `var` on the context.

import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import type { Diagnostic } from "./protocol.ts";

const tsconfig = (typingsFile: string) => ({
  compilerOptions: {
    lib: ["ESNext", "DOM"],
    target: "ESNext",
    module: "Preserve",
    moduleDetection: "force",
    moduleResolution: "bundler",
    allowImportingTsExtensions: true,
    noEmit: true,
    strict: true,
    skipLibCheck: true,
    types: [],
  },
  // Only the typings; the named cell and the earlier cells it references are loaded on demand.
  // (`include: ["*.ts"]` would load every cell ever written: ~2x slower after 200 cells.)
  files: [typingsFile],
});

interface Binding {
  file: string;
  value: boolean;
  type: boolean;
}

const TYPE_DECL = /^(?:export\s+)?(?:declare\s+)?(?:interface|type)\s+([A-Za-z_$][\w$]*)/gm;
const VALUE_AND_TYPE_DECL = /^(?:export\s+)?(?:declare\s+)?(?:abstract\s+)?(?:class|enum)\s+([A-Za-z_$][\w$]*)/gm;

export class Checker {
  private readonly visible = new Map<string, Binding>();
  private readonly recorded = new Set<string>();

  constructor(
    private readonly bunExe: string,
    private readonly dir: string,
    globalName: string,
    typings: string,
  ) {
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, "tsconfig.json"), JSON.stringify(tsconfig(`${globalName}.d.ts`), null, 2));
    writeFileSync(join(dir, `${globalName}.d.ts`), typings);
  }

  private fileName(cellId: string): string {
    return `cell-${cellId}.ts`;
  }

  /** Writes the cell's check module; `valueNames` are the names replMode hoisted. */
  private write(cellId: string, source: string, valueNames: string[]): { file: string; values: string[]; types: string[] } {
    const file = this.fileName(cellId);
    const identifiers = new Set(source.match(/[A-Za-z_$][\w$]*/g) ?? []);
    // replMode also hoists names it synthesizes for imports (e.g. `import_node_path`).
    const values = valueNames.filter((n) => identifiers.has(n));
    const types = [...source.matchAll(TYPE_DECL), ...source.matchAll(VALUE_AND_TYPE_DECL)].map((m) => m[1]!);
    const own = new Set([...values, ...types]);
    const header: string[] = [];
    for (const [name, b] of this.visible) {
      if (own.has(name)) continue;
      if (b.value) header.push(`declare let ${name}: typeof import("./${b.file}").${name};`);
      if (b.type) header.push(`type ${name} = import("./${b.file}").${name};`);
    }
    const exported = [...new Set([...values, ...types])];
    const body = `${header.join(" ")}\n${source}\nexport { ${exported.join(", ")} };\n`;
    writeFileSync(join(this.dir, file), body);
    return { file, values, types };
  }

  /** Records an executed cell so later cells see its names and types. */
  record(cellId: string, source: string, valueNames: string[]): void {
    const { file, values, types } = this.write(cellId, source, valueNames);
    this.recorded.add(cellId);
    const typeSet = new Set(types);
    for (const name of new Set([...values, ...types])) {
      this.visible.set(name, { file, value: values.includes(name), type: typeSet.has(name) });
    }
  }

  async check(cellId: string, source: string, valueNames: string[]): Promise<Diagnostic[]> {
    const { file } = this.write(cellId, source, valueNames);
    const proc = Bun.spawn(
      // --no-script: otherwise a `check` script in any package.json above the directory runs instead.
      // (--no-follow-imports from the online docs does not exist in 1.4.5; errors are filtered by file.)
      [this.bunExe, "--no-orphans", "check", "--no-script", "--no-pretty", "-p", join(this.dir, "tsconfig.json"), join(this.dir, file)],
      { cwd: this.dir, stdout: "pipe", stderr: "pipe", stdin: "ignore" },
    );
    const [stdout, stderr, exitCode] = await Promise.all([new Response(proc.stdout).text(), new Response(proc.stderr).text(), proc.exited]);
    if (!this.recorded.has(cellId)) rmSync(join(this.dir, file), { force: true });
    const diagnostics = parseDiagnostics(stdout, file);
    if (exitCode !== 0 && !DIAGNOSTIC.test(stdout)) {
      diagnostics.push({ line: 0, column: 0, code: "check", message: (stderr || stdout).trim() });
    }
    return diagnostics;
  }
}

/** Parses `tsc --pretty false` lines for one file; line 1 of the file is the carried-over bindings. */
const DIAGNOSTIC = /^(.*?)\((\d+),(\d+)\): error (TS\d+): (.*)$/m;

export function parseDiagnostics(output: string, file: string): Diagnostic[] {
  const diagnostics: Diagnostic[] = [];
  let last: Diagnostic | undefined;
  for (const line of output.split(/\r?\n/)) {
    const m = DIAGNOSTIC.exec(line);
    if (m) {
      last = undefined;
      const path = m[1]!.replaceAll("\\", "/");
      if (path !== file && !path.endsWith("/" + file)) continue;
      last = { line: Math.max(0, Number(m[2]) - 1), column: Number(m[3]), code: m[4]!, message: m[5]! };
      diagnostics.push(last);
    } else if (last && line.startsWith("  ")) {
      last.message += "\n" + line.trim();
    }
  }
  return diagnostics;
}
