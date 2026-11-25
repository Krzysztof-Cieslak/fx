// The reference kernel of the kernel protocol (docs/modules/kernel-protocol.md), for TypeScript on
// the pinned Bun. Cells are transpiled with `Bun.Transpiler({ loader: "tsx", replMode: true })` and
// evaluated in one persistent `node:vm` context: replMode hoists top-level declarations to `var`s on
// the context (they persist across cells) and wraps the cell in an async IIFE returning `{ value }`
// (top-level await works). See docs/spikes/bun-kernel.md for the semantics.
//
// Each cell runs in its own `Bun.ModuleGraph` when Bun has it: the timers, sockets and processes it
// starts belong to the cell, errors from its background work are attributed to it, and interrupting
// the cell disposes them without touching the values it stored.

import { join } from "node:path";
import vm from "node:vm";
import { OutputBuffer, OutputStream } from "./output.ts";
import { type CellSource, hoistedNames, rewriteStack } from "./positions.ts";
import type {
  Activity,
  CellOutcome,
  CellRef,
  CellResult,
  CellRun,
  DecodeParams,
  EncodeParams,
  HostCallError,
  KernelError,
  KernelInfo,
  KernelLimits,
  KernelSetup,
  PreviewParams,
  RecallParams,
  Value,
  ValueInfo,
  ValueRef,
} from "./protocol.d.ts";
import { Cancelled, RpcError, type RpcPeer } from "./rpc.ts";
import * as values from "./values.ts";

/** The kernel protocol versions this kernel speaks. */
export const kernelVersions = ["1"];

type ModuleGraphLike = { run<R>(fn: () => R): R; dispose(): void };
type ModuleGraphCtor = {
  new (options: { onError?: (error: unknown, kind: string) => void }): ModuleGraphLike;
  readonly current: ModuleGraphLike | undefined;
};
const ModuleGraph = (Bun as unknown as { ModuleGraph?: ModuleGraphCtor }).ModuleGraph;

const IDENTIFIER = /^[A-Za-z_$][\w$]*$/;
const CELL_ID = /^[A-Za-z0-9_-]{1,64}$/;
/** Names of the global that are the kernel's own, not host bindings. */
const RESERVED = new Set(["finish", "activity"]);

function failure(error: KernelError, message: string): RpcError {
  return RpcError.typed(error, message);
}

class Interrupt extends Error {
  constructor(readonly outcome: CellOutcome) {
    super(outcome.kind);
  }
}

class Cell {
  readonly output: OutputBuffer;
  readonly stream: OutputStream;
  /** Aborts the cell's host calls. */
  readonly calls = new AbortController();
  graph: ModuleGraphLike | undefined;
  done = false;
  finished = false;
  activities = 0;
  interrupt!: (reason: Interrupt) => void;
  readonly interrupted: Promise<never>;

  constructor(
    readonly id: string,
    limit: number,
    send: (text: string) => void,
  ) {
    this.output = new OutputBuffer(limit);
    this.stream = new OutputStream(limit, send);
    this.interrupted = new Promise<never>((_, reject) => (this.interrupt = reject));
    this.interrupted.catch(() => {});
  }
}

function isErrorLike(e: unknown): e is { name?: unknown; message?: unknown; stack?: unknown; code?: unknown } {
  return typeof e === "object" && e !== null && "message" in e;
}

export class Kernel {
  private readonly context: vm.Context;
  private readonly transpiler = new Bun.Transpiler({ loader: "tsx", replMode: true, macro: false } as unknown as Bun.TranspilerOptions);
  private readonly cells = new Map<string, Cell>();
  private readonly sources = new Map<string, CellSource>();
  private readonly byGraph = new Map<ModuleGraphLike, Cell>();
  private readonly realm: values.Realm;
  private readonly outputs: Record<string, unknown>;
  private readonly ContextError: ErrorConstructor;
  private active: Cell | undefined;
  private setup: KernelSetup | undefined;
  private limits: KernelLimits = { outputChars: 16_000, preview: { maxChars: 1000, maxItems: 10, maxDepth: 3, maxStringChars: 120 }, inlineBytes: 65_536 };
  private moduleBase = process.cwd();

  constructor(private readonly peer: RpcPeer) {
    this.context = vm.createContext({
      console: this.makeConsole(),
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
    const fromJson = vm.runInContext("JSON.parse", this.context) as (text: string) => unknown;
    const ContextObject = vm.runInContext("Object", this.context) as ObjectConstructor;
    this.realm = { fromJson, object: () => new ContextObject() as Record<string, unknown> };
    this.ContextError = vm.runInContext("Error", this.context);
    this.outputs = this.realm.object();
    this.context.Out = this.outputs;
  }

  // ---- Handshake ---------------------------------------------------------------------------------

  /** Takes the host's `kernel` member of `initialize`; returns the kernel's. */
  initialize(setup: KernelSetup | undefined): KernelInfo {
    if (!setup) throw new RpcError(-32602, "initialize has no kernel member");
    const version = setup.versions.find((v) => kernelVersions.includes(v));
    if (version === undefined) throw new RpcError(-32004, `no common kernel protocol version; supported: ${kernelVersions.join(", ")}`, { supported: kernelVersions });
    if (!IDENTIFIER.test(setup.global)) throw new RpcError(-32602, `invalid global name: ${setup.global}`);
    this.setup = setup;
    this.limits = setup.limits;
    if (setup.moduleBase) this.moduleBase = setup.moduleBase;
    this.context[setup.global] = this.makeGlobal(setup.bindings);
    return {
      version,
      language: "typescript",
      caps: { snapshot: false, asyncHostCalls: "await", deterministic: false },
      details: {
        bun: Bun.version,
        typescript: (process.versions as Record<string, string | undefined>).typescript ?? null,
        moduleGraph: ModuleGraph !== undefined,
      },
    };
  }

  /** The global: `shell.run` becomes `<global>.shell.run(...args)`, plus `finish` and `activity`. */
  private makeGlobal(bindings: string[]): object {
    const root: Record<string, unknown> = Object.create(null);
    for (const binding of bindings) {
      const path = binding.split(".");
      if (!path.every((p) => IDENTIFIER.test(p)) || RESERVED.has(path[0]!)) throw new RpcError(-32602, `invalid binding: ${binding}`);
      let node = root;
      for (const segment of path.slice(0, -1)) node = (node[segment] ??= Object.create(null)) as Record<string, unknown>;
      node[path.at(-1)!] = (...args: unknown[]) => this.hostCall(binding, args);
    }
    root.finish = (result: unknown) => this.finish(result);
    root.activity = (kind: unknown, title: unknown, detail?: unknown) => this.activity(kind, title, detail);
    const freeze = (node: Record<string, unknown>) => {
      for (const value of Object.values(node)) if (typeof value === "object" && value !== null) freeze(value as Record<string, unknown>);
      Object.freeze(node);
    };
    freeze(root);
    return root;
  }

  // ---- Output and errors -------------------------------------------------------------------------

  /** The cell that the calling code belongs to (async-context aware when ModuleGraph exists). */
  private currentCell(): Cell | undefined {
    const graph = ModuleGraph?.current;
    return (graph && this.byGraph.get(graph)) ?? this.active;
  }

  private emit(cell: Cell | undefined, kind: "text" | "error", text: string): void {
    if (cell && !cell.done) {
      cell.output.append(text);
      cell.stream.append(text);
      return;
    }
    this.peer.notify("cell/output", { cellId: cell?.id, kind, text: text.slice(0, this.limits.outputChars), late: true });
  }

  /** Text written by cells through process.stdout/stderr or the main realm's console. */
  capture(text: string): void {
    this.emit(this.currentCell(), "text", text);
  }

  /** Uncaught errors that escaped a cell (process-level backstop). */
  backgroundError(kind: string, error: unknown): void {
    this.emit(this.currentCell(), "error", `[${kind}] ${this.describe(error)}\n`);
  }

  private makeConsole(): Record<string, (...args: unknown[]) => void> {
    const format = (args: unknown[]) => args.map((a) => (typeof a === "string" ? a : values.preview(a, this.limits.preview).text)).join(" ");
    const write = (prefix: string) => (...args: unknown[]) => this.capture(prefix + format(args) + "\n");
    return { log: write(""), info: write(""), debug: write(""), warn: write("[warn] "), error: write("[error] "), trace: write("[trace] "), dir: write("") };
  }

  private describe(error: unknown): string {
    if (isErrorLike(error)) {
      return typeof error.stack === "string" ? rewriteStack(error.stack, this.sources).stack : `${String(error.name ?? "Error")}: ${String(error.message)}`;
    }
    return `Uncaught ${values.preview(error, this.limits.preview).text}`;
  }

  // ---- The host bridge ---------------------------------------------------------------------------

  private contextError(message: string, extra: Record<string, unknown>): Error {
    return Object.assign(new this.ContextError(message), extra);
  }

  private async hostCall(binding: string, args: unknown[]): Promise<unknown> {
    const cell = this.currentCell();
    let encoded: Value[];
    try {
      encoded = args.map((arg) => values.encode(arg, this.limits.inlineBytes));
    } catch (e) {
      throw this.contextError(`cannot pass an argument of ${binding}: ${e instanceof Error ? e.message : String(e)}`, { code: "invalidArguments" });
    }
    try {
      const result = (await this.peer.request("host/call", { cellId: cell?.id, binding, args: encoded }, cell?.calls.signal)) as Value;
      return values.decode(this.realm, result);
    } catch (e) {
      if (e instanceof RpcError) {
        const typed = e.data as HostCallError | undefined;
        throw this.contextError(e.message, { code: typed?.kind ?? String(e.code), data: e.data === undefined ? undefined : this.realm.fromJson(JSON.stringify(e.data)) });
      }
      if (e instanceof Cancelled) throw this.contextError(`${binding} was cancelled`, { code: "cancelled" });
      throw e;
    }
  }

  private async finish(result: unknown): Promise<void> {
    const cell = this.currentCell();
    let encoded: Value;
    try {
      encoded = values.encode(result ?? null, this.limits.inlineBytes);
    } catch (e) {
      throw this.contextError(`cannot finish with this result: ${e instanceof Error ? e.message : String(e)}`, { code: "finishRejected" });
    }
    try {
      await this.peer.request("finish", { cellId: cell?.id, result: encoded }, cell?.calls.signal);
    } catch (e) {
      if (e instanceof RpcError) throw this.contextError(e.message, { code: "finishRejected", data: e.data === undefined ? undefined : this.realm.fromJson(JSON.stringify(e.data)) });
      throw e;
    }
    if (cell) cell.finished = true;
  }

  private activity(kind: unknown, title: unknown, detail?: unknown): void {
    if (typeof kind !== "string" || kind === "" || typeof title !== "string") throw new this.ContextError("activity(kind, title, detail?) takes a non-empty kind and a title");
    const cell = this.currentCell();
    const activity: Activity = { kind, title };
    if (detail !== undefined) {
      const encoded = values.encode(detail, this.limits.inlineBytes);
      if (encoded.kind === "inline") activity.detail = encoded.value;
    }
    const late = !cell || cell.done;
    if (cell && !late) {
      cell.stream.flush();
      cell.activities++;
    }
    this.peer.notify("cell/activity", { cellId: cell?.id, activity, late });
  }

  // ---- Cells -------------------------------------------------------------------------------------

  private transpile(code: string): { js: string } | { error: CellOutcome } {
    try {
      return { js: this.transpiler.transformSync(code) };
    } catch (e) {
      const first = (e as { errors?: unknown[] }).errors?.[0] ?? e;
      const pos = (first as { position?: { line?: number; column?: number; lineText?: string } }).position;
      const message = isErrorLike(first) ? String(first.message) : String(first);
      return { error: { kind: "syntaxError", message, line: pos?.line, column: pos?.column, lineText: pos?.lineText } };
    }
  }

  private runtimeError(error: unknown): CellOutcome {
    if (error instanceof Interrupt) return error.outcome;
    if (!isErrorLike(error)) {
      const text = values.preview(error, this.limits.preview).text;
      return { kind: "runtimeError", name: "Uncaught", message: text, stack: `Uncaught ${text}` };
    }
    const mapped = typeof error.stack === "string" ? rewriteStack(error.stack, this.sources) : { stack: String(error.message), top: undefined };
    const outcome: CellOutcome = { kind: "runtimeError", name: String(error.name ?? "Error"), message: String(error.message), stack: mapped.stack };
    if (typeof error.code === "string") outcome.code = error.code;
    if (mapped.top) outcome.location = mapped.top;
    return outcome;
  }

  async runCell(params: CellRun, signal: AbortSignal): Promise<CellResult> {
    const cellId = params.cellId;
    if (typeof cellId !== "string" || !CELL_ID.test(cellId)) throw failure({ kind: "invalidCellId", cellId: String(cellId) }, `invalid cell id ${cellId}`);
    if (this.cells.has(cellId)) throw failure({ kind: "duplicateCell", cellId }, `cell ${cellId} already ran`);
    if (this.active) throw failure({ kind: "busy", running: this.active.id }, `cell ${this.active.id} is running`);
    const t0 = performance.now();
    const cell = new Cell(cellId, this.limits.outputChars, (text) => this.peer.notify("cell/output", { cellId, kind: "text", text, late: false }));
    this.cells.set(cellId, cell);
    this.active = cell;
    const onAbort = () => this.interrupt(cell, new Interrupt({ kind: "cancelled" }));
    signal.addEventListener("abort", onAbort, { once: true });
    const result = (outcome: CellOutcome, declared: string[]): CellResult => {
      cell.stream.flush();
      cell.done = true;
      signal.removeEventListener("abort", onAbort);
      if (this.active === cell) this.active = undefined;
      return {
        cellId,
        outcome,
        output: cell.output.take(),
        declared,
        finished: cell.finished,
        notified: cell.stream.sent + cell.activities,
        durationMs: Math.round(performance.now() - t0),
      };
    };

    const transpiled = this.transpile(params.code);
    if ("error" in transpiled) return result(transpiled.error, []);
    const { js } = transpiled;
    const declared = hoistedNames(js);
    const filename = join(this.moduleBase, `cell-${cellId}.ts`);
    this.sources.set(filename, { cellId, source: params.code, js });
    if (ModuleGraph) {
      cell.graph = new ModuleGraph({ onError: (error, kind) => this.emit(cell, "error", `[${kind}] ${this.describe(error)}\n`) });
      this.byGraph.set(cell.graph, cell);
    }

    let timer: ReturnType<typeof setTimeout> | undefined;
    const timedOut = (): Interrupt => new Interrupt({ kind: "timedOut", afterMs: params.timeoutMs ?? 0 });
    try {
      const script = new vm.Script(js, { filename, importModuleDynamically: vm.constants.USE_MAIN_CONTEXT_DEFAULT_LOADER } as vm.ScriptOptions);
      // displayErrors: false keeps Node-style source excerpts of the transpiled code out of `stack`.
      const options = { displayErrors: false, ...(params.timeoutMs ? { timeout: params.timeoutMs } : {}) };
      const execute = () => script.runInContext(this.context, options) as { value: unknown } | Promise<{ value: unknown }>;
      const returned = cell.graph ? cell.graph.run(execute) : execute();
      if (params.timeoutMs) {
        const remaining = Math.max(0, params.timeoutMs - (performance.now() - t0));
        timer = setTimeout(() => this.interrupt(cell, timedOut()), remaining);
      }
      const completion = await Promise.race([Promise.resolve(returned), cell.interrupted]);
      const value = completion?.value;
      if (value === undefined) return result({ kind: "completed" }, declared);
      this.outputs[cellId] = value;
      return result({ kind: "completed", value: values.info(value, this.limits.preview) }, declared);
    } catch (e) {
      if (isErrorLike(e) && e.code === "ERR_SCRIPT_EXECUTION_TIMEOUT") {
        this.interrupt(cell, timedOut());
        return result(timedOut().outcome, declared);
      }
      return result(this.runtimeError(e), declared);
    } finally {
      if (timer) clearTimeout(timer);
    }
  }

  /** Disposes what the cell started, cancels its host calls and rejects its completion. */
  private interrupt(cell: Cell, reason: Interrupt): void {
    cell.interrupt(reason);
    cell.calls.abort();
    if (cell.graph) {
      cell.graph.dispose();
      this.byGraph.delete(cell.graph);
      cell.graph = undefined;
    }
  }

  cancelCell(params: CellRef): { wasRunning: boolean } {
    const cell = this.cells.get(params.cellId);
    if (!cell) throw failure({ kind: "unknownCell", cellId: params.cellId }, `unknown cell ${params.cellId}`);
    const wasRunning = !cell.done;
    // Cancelling a finished cell still stops what it left running in the background.
    this.interrupt(cell, new Interrupt({ kind: "cancelled" }));
    return { wasRunning };
  }

  // ---- Values ------------------------------------------------------------------------------------

  private lookup(ref: ValueRef): unknown {
    if (ref.kind === "variable") {
      if (IDENTIFIER.test(ref.name) && ref.name in this.context && ref.name !== this.setup?.global) return this.context[ref.name];
    } else if (Object.hasOwn(this.outputs, ref.cellId)) {
      return this.outputs[ref.cellId];
    }
    throw failure({ kind: "notFound", ref }, ref.kind === "variable" ? `no variable ${ref.name}` : `no output of cell ${ref.cellId}`);
  }

  previewValue(params: PreviewParams): ValueInfo {
    return values.info(this.lookup(params.ref), params.limits ?? this.limits.preview);
  }

  encodeValue(params: EncodeParams): Value {
    const value = this.lookup(params.ref);
    try {
      return values.encode(value, params.maxBytes ?? this.limits.inlineBytes);
    } catch (e) {
      if (e instanceof values.TooLarge) throw failure({ kind: "tooLarge", bytes: e.bytes, limit: e.limit }, `the value has ${e.bytes} bytes of JSON, more than ${e.limit}`);
      if (e instanceof values.NotEncodable) throw failure({ kind: "notEncodable", type: e.typeName, reason: e.message }, `a ${e.typeName} cannot be encoded: ${e.message}`);
      throw e;
    }
  }

  decodeValue(params: DecodeParams): ValueInfo {
    if (!IDENTIFIER.test(params.name) || params.name === this.setup?.global) throw failure({ kind: "invalidName", name: params.name }, `invalid name ${params.name}`);
    const value = values.decode(this.realm, params.value);
    this.context[params.name] = value;
    return values.info(value, this.limits.preview);
  }

  recall(params: RecallParams): ValueInfo {
    const value = values.decode(this.realm, params.value);
    this.outputs[params.cellId] = value;
    return values.info(value, this.limits.preview);
  }
}
