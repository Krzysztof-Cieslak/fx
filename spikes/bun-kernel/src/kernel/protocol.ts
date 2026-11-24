// Wire types of the spike kernel protocol (newline-delimited JSON-RPC 2.0 over stdio).
// A preview of what T116 specifies; see docs/spikes/bun-kernel.md for the method table.

export const protocolVersion = 1;

// host -> kernel

export interface InitializeParams {
  protocolVersion: number;
  /** Host methods exposed under the kernel global, e.g. `shell.run` becomes `<global>.shell.run(...)`. */
  bindings: string[];
  /** Directory that cell file names live in; relative imports in cells resolve against it. */
  moduleBase?: string;
  limits?: { outputChars?: number; previewChars?: number };
  /** Enables type-checking: the generated typings for the global and an owned directory to check in. */
  check?: { dir: string; typings: string };
}

export interface InitializeResult {
  protocolVersion: number;
  global: string;
  bun: string;
  typescript: string | null;
  /** Whether cells get their own `Bun.ModuleGraph` (resource ownership, cancellation). */
  moduleGraph: boolean;
}

export interface CellRunParams {
  cellId: string;
  code: string;
  /** Type-check first and do not run on errors. */
  check?: boolean;
  /** Wall-clock limit. The synchronous part is interrupted by the VM; the rest is abandoned. */
  timeoutMs?: number;
}

export interface CellCheckParams {
  cellId: string;
  code: string;
}

export interface CellCancelParams {
  cellId: string;
}

export interface ValuePreviewParams {
  name: string;
}

// kernel -> host

export interface HostCallParams {
  cellId: string | null;
  method: string;
  args: unknown[];
}

export interface FinishParams {
  cellId: string | null;
  result: unknown;
}

export interface FinishResult {
  accepted: boolean;
  errors?: string[];
}

/** Notification: output or an error that a cell produced after its `cell/run` returned. */
export interface CellLateParams {
  cellId: string | null;
  kind: "output" | "error";
  text: string;
}

// results

export interface BoundedText {
  text: string;
  truncated: boolean;
  totalChars: number;
}

export interface Preview {
  type: string;
  text: string;
  truncated: boolean;
}

export interface Location {
  cellId: string;
  line: number;
  column: number;
}

export interface Diagnostic {
  /** 1-based line in the cell; 0 when the error is in the bindings carried over from earlier cells. */
  line: number;
  column: number;
  code: string;
  message: string;
}

export type CellError =
  | { kind: "syntax"; message: string; line?: number; column?: number; lineText?: string }
  | { kind: "type"; diagnostics: Diagnostic[] }
  | { kind: "runtime"; name: string; message: string; location?: Location; stack: string }
  | { kind: "timeout"; message: string }
  | { kind: "cancelled"; message: string };

export interface CellRunResult {
  cellId: string;
  ok: boolean;
  value?: Preview;
  error?: CellError;
  output: BoundedText;
  /** Top-level names the cell declared (they persist in the kernel). */
  declared: string[];
  /** The result accepted by `finish` during this cell, if any. */
  finished?: unknown;
  durationMs: number;
}

export interface CellCheckResult {
  diagnostics: Diagnostic[];
  durationMs: number;
}
