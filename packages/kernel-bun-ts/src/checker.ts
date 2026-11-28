// Type-checking cells (`cell/check`, and `cell/run` with `check`) with Bun's built-in checker,
// `bun check` (TypeScript 7 in Bun 1.4; no `typescript` package), as the Bun kernel spike designed it
// (docs/spikes/bun-kernel.md, "Type-checking").
//
// The check directory holds a tsconfig, the host bridge's declarations (`<global>.d.ts`, generated
// by the host from the bindings' codecs), the kernel's globals (`kernel-globals.d.ts`) and one module
// per executed cell that exports the cell's top-level names. A cell being checked starts with one line
// that re-declares the earlier names it mentions:
//   declare let items: typeof import("./cell-c3.ts").items; type Item = import("./cell-c3.ts").Item;
// `declare let` (not `import`) keeps them assignable, as they are at run time, where replMode turns
// every declaration into a `var` on the context. Only names the cell mentions are carried, so the
// checker loads the cells it needs, not the whole history. Values the host bound (`value/decode`, e.g.
// a cell's texts) are carried the same way with their declared type: `declare let notes: string;`.

import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { globalsDeclarations } from "./globals.ts";
import type { Diagnostic } from "./protocol.d.ts";

const GLOBALS_FILE = "kernel-globals.d.ts";

const tsconfig = (bridgeFile: string) => ({
  compilerOptions: {
    // No DOM: see globals.ts.
    lib: ["ESNext"],
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
  // Only the declarations; the cell and the earlier cells it refers to load on demand
  // (`include: ["*.ts"]` would load every cell ever written: about twice as slow after 200 cells).
  files: [GLOBALS_FILE, bridgeFile],
});

interface Binding {
  file: string;
  value: boolean;
  type: boolean;
  /** The declared type of a value the host bound (`value/decode`), which no cell's module exports. */
  declared?: string;
}

const TYPE_DECL = /^(?:export\s+)?(?:declare\s+)?(?:interface|type)\s+([A-Za-z_$][\w$]*)/gm;
const VALUE_AND_TYPE_DECL = /^(?:export\s+)?(?:declare\s+)?(?:abstract\s+)?(?:class|enum)\s+([A-Za-z_$][\w$]*)/gm;

export class Checker {
  private readonly visible = new Map<string, Binding>();
  private readonly recorded = new Set<string>();

  /**
   * @param bun The pinned Bun (the kernel's own executable).
   * @param dir An owned directory for the check files.
   * @param globalName The host bridge's global.
   * @param bridge Its declarations (`<global>.d.ts`).
   */
  constructor(
    private readonly bun: string,
    private readonly dir: string,
    globalName: string,
    bridge: string,
  ) {
    mkdirSync(dir, { recursive: true });
    const bridgeFile = `${globalName}.d.ts`;
    writeFileSync(join(dir, "tsconfig.json"), JSON.stringify(tsconfig(bridgeFile), null, 2));
    writeFileSync(join(dir, bridgeFile), bridge);
    writeFileSync(join(dir, GLOBALS_FILE), globalsDeclarations);
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
      if (own.has(name) || !identifiers.has(name)) continue;
      if (b.declared !== undefined) header.push(`declare let ${name}: ${b.declared};`);
      else if (b.value) header.push(`declare let ${name}: typeof import("./${b.file}").${name};`);
      if (b.type) header.push(`type ${name} = import("./${b.file}").${name};`);
    }
    const exported = [...new Set([...values, ...types])];
    const body = `${header.join(" ")}\n${source}\nexport { ${exported.join(", ")} };\n`;
    writeFileSync(join(this.dir, file), body);
    return { file, values, types };
  }

  /** Records a cell that ran, so later cells see its names and types. */
  record(cellId: string, source: string, valueNames: string[]): void {
    const { file, values, types } = this.write(cellId, source, valueNames);
    this.recorded.add(cellId);
    const typeSet = new Set(types);
    for (const name of new Set([...values, ...types])) {
      this.visible.set(name, { file, value: values.includes(name), type: typeSet.has(name) });
    }
  }

  /** Records a value the host bound (`value/decode`), so later cells see it with this type. */
  bind(name: string, type: string): void {
    this.visible.set(name, { file: "", value: true, type: false, declared: type });
  }

  /** The diagnostics of a cell, at its own lines (line 0: the names carried over from earlier cells). */
  async check(cellId: string, source: string, valueNames: string[]): Promise<Diagnostic[]> {
    const { file } = this.write(cellId, source, valueNames);
    const proc = Bun.spawn(
      // --no-script: otherwise a `check` script in any package.json above the directory runs instead.
      // --no-pretty: the one-line `tsc --pretty false` format, whatever the environment says.
      [this.bun, "--no-orphans", "check", "--no-script", "--no-pretty", "-p", join(this.dir, "tsconfig.json"), join(this.dir, file)],
      { cwd: this.dir, stdout: "pipe", stderr: "pipe", stdin: "ignore" },
    );
    const [stdout, stderr, exitCode] = await Promise.all([new Response(proc.stdout).text(), new Response(proc.stderr).text(), proc.exited]);
    if (!this.recorded.has(cellId)) rmSync(join(this.dir, file), { force: true });
    const diagnostics = parseDiagnostics(stdout, file);
    // Diagnostics of other files (an earlier cell that ran with type errors) are not this cell's.
    if (exitCode !== 0 && !ANY_DIAGNOSTIC.test(stdout)) {
      diagnostics.push({ line: 0, column: 0, code: "check", message: (stderr || stdout).trim() || `bun check exited with ${exitCode}` });
    }
    return diagnostics;
  }
}

/** `tsc --pretty false` lines: `file(line,column): error TSxxxx: message`. */
const DIAGNOSTIC = /^(.*?)\((\d+),(\d+)\): error (TS\d+): (.*)$/;
const ANY_DIAGNOSTIC = /^.*?\(\d+,\d+\): error TS\d+: /m;

/** The diagnostics of one file; line 1 of the file is the carried-over names, so lines shift by one. */
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

/** Declarations of a host bridge the host gave no file for: every binding is `any`. */
export function untypedBridge(globalName: string): string {
  return `declare const ${globalName}: any;\n`;
}

/** Removes the check directory (at exit). */
export function removeCheckDir(dir: string): void {
  rmSync(dir, { recursive: true, force: true });
}
