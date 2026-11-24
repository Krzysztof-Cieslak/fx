// The bun-ts kernel: cells are transpiled with `Bun.Transpiler({ loader: "tsx", replMode: true })`
// and evaluated in one persistent `node:vm` context. replMode hoists top-level declarations to
// `var`s on the context (so they persist across cells), rewrites `import` to `await import()` and
// wraps the cell in an (async) IIFE that returns `{ value }` (so top-level await works).
//
// Each cell runs inside its own `Bun.ModuleGraph` when available: timers, sockets, fetches and
// child processes the cell starts belong to it, uncaught errors are attributed to it, and
// cancelling the cell disposes them without touching the values stored in the context.

import { join } from "node:path";
import vm from "node:vm";
import { OutputBuffer, formatArgs, preview } from "./bounded.ts";
import { Checker } from "./checker.ts";
import { type CellSource, hoistedNames, rewriteStack } from "./positions.ts";
import type {
  CellCancelParams,
  CellCheckParams,
  CellCheckResult,
  CellError,
  CellLateParams,
  CellRunParams,
  CellRunResult,
  FinishResult,
  InitializeParams,
  InitializeResult,
  Preview,
  ValuePreviewParams,
} from "./protocol.ts";
import { protocolVersion } from "./protocol.ts";
import { ErrorCodes, RpcError, type RpcPeer } from "../rpc.ts";

type ModuleGraphLike = { run<R>(fn: () => R): R; dispose(): void };
type ModuleGraphCtor = {
  new (options: { onError?: (error: unknown, kind: string) => void }): ModuleGraphLike;
  readonly current: ModuleGraphLike | undefined;
};
const ModuleGraph = (Bun as unknown as { ModuleGraph?: ModuleGraphCtor }).ModuleGraph;

class Interrupt extends Error {
  constructor(readonly kind: "timeout" | "cancelled", message: string) {
    super(message);
  }
}

class Cell {
  readonly output: OutputBuffer;
  graph: ModuleGraphLike | undefined;
  done = false;
  finished: unknown;
  interrupt!: (reason: Interrupt) => void;
  readonly interrupted: Promise<never>;

  constructor(
    readonly id: string,
    outputChars: number,
  ) {
    this.output = new OutputBuffer(outputChars);
    this.interrupted = new Promise<never>((_, reject) => (this.interrupt = reject));
    this.interrupted.catch(() => {});
  }
}

const IDENTIFIER = /^[A-Za-z_$][\w$]*$/;
const CELL_ID = /^[A-Za-z0-9_-]{1,64}$/;

function isErrorLike(e: unknown): e is { name?: unknown; message?: unknown; stack?: unknown; code?: unknown } {
  return typeof e === "object" && e !== null && "message" in e;
}

export class Kernel {
  private readonly context: vm.Context;
  private readonly transpiler = new Bun.Transpiler({ loader: "tsx", replMode: true, macro: false } as unknown as Bun.TranspilerOptions);
  private readonly cells = new Map<string, Cell>();
  private readonly sources = new Map<string, CellSource>();
  private readonly byGraph = new Map<ModuleGraphLike, Cell>();
  private readonly contextJsonParse: (text: string) => unknown;
  private readonly ContextError: ErrorConstructor;
  private active: Cell | undefined;
  private initialized = false;
  private moduleBase = process.cwd();
  private outputChars = 16_000;
  private previewChars = 2_000;
  private checker: Checker | undefined;

  constructor(
    private readonly globalName: string,
    private readonly peer: RpcPeer,
  ) {
    if (!IDENTIFIER.test(globalName)) throw new Error(`invalid global name: ${globalName}`);
    const console = this.makeConsole();
    this.context = vm.createContext({
      console,
      setTimeout,
      clearTimeout,
      setInterval,
      clearInterval,
      queueMicrotask,
      structuredClone,
      TextEncoder,
      TextDecoder,
      URL,
      URLSearchParams,
      AbortController,
      AbortSignal,
      atob,
      btoa,
      fetch,
      Request,
      Response,
      Headers,
      Blob,
      FormData,
      performance,
      crypto,
    });
    this.contextJsonParse = vm.runInContext("JSON.parse", this.context);
    this.ContextError = vm.runInContext("Error", this.context);
  }

  /** The cell that the calling code belongs to (async-context aware when ModuleGraph exists). */
  private currentCell(): Cell | undefined {
    const graph = ModuleGraph?.current;
    return (graph && this.byGraph.get(graph)) ?? this.active;
  }

  private emit(cell: Cell | undefined, kind: "output" | "error", text: string): void {
    if (cell && !cell.done) {
      cell.output.append(text);
      return;
    }
    const late: CellLateParams = { cellId: cell?.id ?? null, kind, text: text.slice(0, this.outputChars) };
    this.peer.notify("cell/late", late);
  }

  /** Text written by cells through process.stdout/stderr or the main realm's console. */
  capture(text: string): void {
    this.emit(this.currentCell(), "output", text);
  }

  /** Uncaught errors that escaped a cell (process-level backstop). */
  backgroundError(kind: string, error: unknown): void {
    this.emit(this.currentCell(), "error", `[${kind}] ${this.describe(error)}\n`);
  }

  private makeConsole(): Record<string, (...args: unknown[]) => void> {
    const write = (prefix: string) => (...args: unknown[]) => this.capture(prefix + formatArgs(args, this.previewChars) + "\n");
    return { log: write(""), info: write(""), debug: write(""), warn: write("[warn] "), error: write("[error] "), trace: write("[trace] "), dir: write("") };
  }

  initialize(params: InitializeParams): InitializeResult {
    if (this.initialized) throw new RpcError(ErrorCodes.invalidRequest, "already initialized");
    if (params.protocolVersion !== protocolVersion) throw new RpcError(ErrorCodes.invalidParams, `unsupported protocol version ${params.protocolVersion}`);
    this.initialized = true;
    if (params.moduleBase) this.moduleBase = params.moduleBase;
    this.outputChars = params.limits?.outputChars ?? this.outputChars;
    this.previewChars = params.limits?.previewChars ?? this.previewChars;
    this.context[this.globalName] = this.makeGlobal(params.bindings);
    if (params.check) this.checker = new Checker(process.execPath, params.check.dir, this.globalName, params.check.typings);
    return {
      protocolVersion,
      global: this.globalName,
      bun: Bun.version,
      typescript: (process.versions as Record<string, string | undefined>).typescript ?? null,
      moduleGraph: ModuleGraph !== undefined,
    };
  }

  /** Builds the global from binding names: `shell.run` becomes `global.shell.run(...args)`. */
  private makeGlobal(bindings: string[]): object {
    const root: Record<string, unknown> = Object.create(null);
    for (const binding of bindings) {
      const path = binding.split(".");
      if (path.length < 2 || !path.every((p) => IDENTIFIER.test(p))) throw new RpcError(ErrorCodes.invalidParams, `invalid binding: ${binding}`);
      let node = root;
      for (const segment of path.slice(0, -1)) node = (node[segment] ??= Object.create(null)) as Record<string, unknown>;
      node[path.at(-1)!] = (...args: unknown[]) => this.hostCall(binding, args);
    }
    root.finish = (result: unknown) => this.finish(result);
    for (const value of Object.values(root)) if (typeof value === "object") Object.freeze(value);
    return Object.freeze(root);
  }

  /** Values handed to cells are created in the context's realm, so `instanceof Array` etc. hold. */
  private toContext(value: unknown): unknown {
    return value === undefined ? undefined : this.contextJsonParse(JSON.stringify(value));
  }

  private contextError(message: string, extra?: Record<string, unknown>): Error {
    return Object.assign(new this.ContextError(message), extra);
  }

  private async hostCall(method: string, args: unknown[]): Promise<unknown> {
    const cell = this.currentCell();
    try {
      const result = await this.peer.request("host/call", { cellId: cell?.id ?? null, method, args });
      return this.toContext(result);
    } catch (e) {
      if (e instanceof RpcError) throw this.contextError(e.message, { code: e.code, data: this.toContext(e.data) });
      throw e;
    }
  }

  private async finish(result: unknown): Promise<void> {
    const cell = this.currentCell();
    const json = JSON.parse(JSON.stringify(result ?? null));
    const response = (await this.peer.request("finish", { cellId: cell?.id ?? null, result: json })) as FinishResult;
    if (!response.accepted) throw this.contextError(`finish rejected: ${(response.errors ?? []).join("; ")}`, { errors: this.toContext(response.errors ?? []) });
    if (cell) cell.finished = json;
  }

  private cellFile(cellId: string): string {
    return join(this.moduleBase, `cell-${cellId}.ts`);
  }

  private transpile(code: string): { js: string } | { error: CellError } {
    try {
      return { js: this.transpiler.transformSync(code) };
    } catch (e) {
      const first = (e as { errors?: unknown[] }).errors?.[0] ?? e;
      const pos = (first as { position?: { line?: number; column?: number; lineText?: string } }).position;
      const message = isErrorLike(first) ? String(first.message) : String(first);
      return { error: { kind: "syntax", message, line: pos?.line, column: pos?.column, lineText: pos?.lineText } };
    }
  }

  private describe(error: unknown): string {
    if (isErrorLike(error)) {
      const mapped = typeof error.stack === "string" ? rewriteStack(error.stack, this.sources).stack : `${String(error.name ?? "Error")}: ${String(error.message)}`;
      return mapped;
    }
    return `Uncaught ${preview(error, this.previewChars).text}`;
  }

  private runtimeError(error: unknown): CellError {
    if (error instanceof Interrupt) return { kind: error.kind, message: error.message };
    if (isErrorLike(error) && error.code === "ERR_SCRIPT_EXECUTION_TIMEOUT") return { kind: "timeout", message: String(error.message) };
    if (!isErrorLike(error)) {
      const text = preview(error, this.previewChars).text;
      return { kind: "runtime", name: "Uncaught", message: text, stack: `Uncaught ${text}` };
    }
    const mapped = typeof error.stack === "string" ? rewriteStack(error.stack, this.sources) : { stack: String(error.message) };
    return { kind: "runtime", name: String(error.name ?? "Error"), message: String(error.message), location: mapped.top, stack: mapped.stack };
  }

  private validateCellId(cellId: unknown): string {
    if (typeof cellId !== "string" || !CELL_ID.test(cellId)) throw new RpcError(ErrorCodes.invalidParams, "cellId must match [A-Za-z0-9_-]{1,64}");
    if (this.cells.has(cellId)) throw new RpcError(ErrorCodes.invalidParams, `cell ${cellId} already ran`);
    return cellId;
  }

  async checkCell(params: CellCheckParams): Promise<CellCheckResult> {
    if (!this.checker) throw new RpcError(ErrorCodes.invalidRequest, "type-checking was not enabled in initialize");
    const t0 = performance.now();
    const transpiled = this.transpile(params.code);
    if ("error" in transpiled) {
      const e = transpiled.error as { message: string; line?: number; column?: number };
      return { diagnostics: [{ line: e.line ?? 0, column: e.column ?? 0, code: "syntax", message: e.message }], durationMs: performance.now() - t0 };
    }
    const diagnostics = await this.checker.check(params.cellId, params.code, hoistedNames(transpiled.js));
    return { diagnostics, durationMs: performance.now() - t0 };
  }

  async runCell(params: CellRunParams): Promise<CellRunResult> {
    if (!this.initialized) throw new RpcError(ErrorCodes.invalidRequest, "not initialized");
    const cellId = this.validateCellId(params.cellId);
    if (this.active) throw new RpcError(ErrorCodes.busy, `cell ${this.active.id} is running`);
    const t0 = performance.now();
    const cell = new Cell(cellId, this.outputChars);
    this.cells.set(cellId, cell);
    this.active = cell;
    const result = (fields: Partial<CellRunResult> & { ok: boolean }): CellRunResult => {
      cell.done = true;
      if (this.active === cell) this.active = undefined;
      return { cellId, declared: [], ...fields, output: cell.output.take(), finished: cell.finished, durationMs: performance.now() - t0 };
    };

    const transpiled = this.transpile(params.code);
    if ("error" in transpiled) return result({ ok: false, error: transpiled.error });
    const { js } = transpiled;
    const declared = hoistedNames(js);

    if (params.check && this.checker) {
      const diagnostics = await this.checker.check(cellId, params.code, declared);
      if (diagnostics.length > 0) return result({ ok: false, error: { kind: "type", diagnostics } });
    }

    const filename = this.cellFile(cellId);
    this.sources.set(filename, { cellId, source: params.code, js });
    this.checker?.record(cellId, params.code, declared);
    if (ModuleGraph) {
      cell.graph = new ModuleGraph({ onError: (error, kind) => this.emit(cell, "error", `[${kind}] ${this.describe(error)}\n`) });
      this.byGraph.set(cell.graph, cell);
    }

    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      const script = new vm.Script(js, { filename, importModuleDynamically: vm.constants.USE_MAIN_CONTEXT_DEFAULT_LOADER } as vm.ScriptOptions);
      // displayErrors: false keeps Node-style source excerpts of the transpiled code out of `stack`.
      const options = { displayErrors: false, ...(params.timeoutMs ? { timeout: params.timeoutMs } : {}) };
      const execute = () => script.runInContext(this.context, options) as { value: unknown } | Promise<{ value: unknown }>;
      const returned = cell.graph ? cell.graph.run(execute) : execute();
      if (params.timeoutMs) {
        const remaining = Math.max(0, params.timeoutMs - (performance.now() - t0));
        timer = setTimeout(() => this.interrupt(cell, new Interrupt("timeout", `cell exceeded ${params.timeoutMs} ms`)), remaining);
      }
      const completion = await Promise.race([Promise.resolve(returned), cell.interrupted]);
      return result({ ok: true, value: preview(completion?.value, this.previewChars), declared });
    } catch (e) {
      if (isErrorLike(e) && e.code === "ERR_SCRIPT_EXECUTION_TIMEOUT") this.interrupt(cell, new Interrupt("timeout", String(e.message)));
      return result({ ok: false, error: this.runtimeError(e), declared });
    } finally {
      if (timer) clearTimeout(timer);
    }
  }

  /** Disposes what the cell started (timers, sockets, child processes) and rejects its completion. */
  private interrupt(cell: Cell, reason: Interrupt): void {
    cell.interrupt(reason);
    if (cell.graph) {
      cell.graph.dispose();
      this.byGraph.delete(cell.graph);
      cell.graph = undefined;
    }
  }

  cancelCell(params: CellCancelParams): { cancelled: boolean } {
    const cell = this.cells.get(params.cellId);
    if (!cell) throw new RpcError(ErrorCodes.invalidParams, `unknown cell ${params.cellId}`);
    const wasRunning = !cell.done;
    // Cancelling a finished cell still stops what it left running in the background.
    this.interrupt(cell, new Interrupt("cancelled", "cell cancelled"));
    return { cancelled: wasRunning };
  }

  previewValue(params: ValuePreviewParams): Preview {
    if (!IDENTIFIER.test(params.name) || !(params.name in this.context)) throw new RpcError(ErrorCodes.invalidParams, `no variable named ${params.name}`);
    return preview(this.context[params.name], this.previewChars);
  }
}
