// The kernel's side of JSON-RPC 2.0 as the host speaks it (docs/modules/rpc.md): newline-delimited
// messages on stdio, the host's `initialize` handshake, `$/cancelRequest` in both directions, and the
// protocol's error codes. Kernels never open binary channels and never send batches.
//
// Every message is written as `{"jsonrpc":"2.0",…}` (the key first) on a line of its own, after a
// newline that ends any partial line a cell wrote to fd 1: the host recovers messages after stray
// bytes (docs/modules/kernel-protocol.md, "Framing").

import type { Capabilities, ErrorCodes, InitializeParams, InitializeResult, PeerInfo } from "./protocol.d.ts";

export const errorCodes: ErrorCodes = {
  parseError: -32700,
  invalidRequest: -32600,
  methodNotFound: -32601,
  invalidParams: -32602,
  internalError: -32603,
  applicationError: -32000,
  requestCancelled: -32001,
  notInitialized: -32002,
  overloaded: -32003,
  unsupportedVersion: -32004,
  responseTooLarge: -32005,
  shuttingDown: -32006,
};

/** The JSON-RPC protocol versions this kernel speaks (`Fx.Rpc.Protocol.versions`). */
export const rpcVersions = ["1"];

/** An error answer. `ApplicationError` carries the method's typed error in `data`. */
export class RpcError extends Error {
  constructor(
    readonly code: number,
    message: string,
    readonly data?: unknown,
  ) {
    super(message);
  }

  /** A method's typed error (an expected failure), e.g. `{ kind: "busy", running: "c1" }`. */
  static typed(error: { kind: string }, message: string): RpcError {
    return new RpcError(errorCodes.applicationError, message, error);
  }
}

/** The request was cancelled: by `$/cancelRequest`, or locally. */
export class Cancelled extends Error {
  constructor() {
    super("cancelled");
  }
}

type Id = number | string;
export type RequestHandler = (params: any, signal: AbortSignal) => unknown;
export type NotificationHandler = (params: any) => void;

interface Pending {
  resolve: (value: unknown) => void;
  reject: (error: unknown) => void;
}

export interface Server {
  /** This side, as it introduces itself. */
  peer: PeerInfo;
  /** Decides on the host's `initialize` params; returns the members to add to the result. */
  accept: (params: InitializeParams & Record<string, unknown>) => Record<string, unknown>;
}

export interface PeerOptions {
  write: (frame: string) => void;
  /** Lines that are not JSON-RPC (diagnostics only). */
  onStray?: (text: string) => void;
}

const FRAME_PREFIX = '{"jsonrpc":"2.0",';
/** The largest message this kernel reads (16 MiB), announced in `initialize`. */
const maxMessageSize = 16 * 1024 * 1024;

export class RpcPeer {
  private nextId = 1;
  private initialized = false;
  private hostMaxMessageSize = Number.MAX_SAFE_INTEGER;
  private readonly pending = new Map<Id, Pending>();
  private readonly running = new Map<Id, AbortController>();
  private readonly requests = new Map<string, RequestHandler>();
  private readonly notifications = new Map<string, NotificationHandler>();
  private server: Server | undefined;

  constructor(private readonly options: PeerOptions) {}

  onRequest(method: string, handler: RequestHandler): this {
    this.requests.set(method, handler);
    return this;
  }

  onNotification(method: string, handler: NotificationHandler): this {
    this.notifications.set(method, handler);
    return this;
  }

  /** Serves the host's `initialize`; nothing else is handled before it. */
  serve(server: Server): this {
    this.server = server;
    return this;
  }

  /** Sends a request; `signal` cancels it at the host (`$/cancelRequest`) and rejects with `Cancelled`. */
  request(method: string, params: unknown, signal?: AbortSignal): Promise<unknown> {
    if (signal?.aborted) return Promise.reject(new Cancelled());
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const onAbort = () => {
        if (!this.pending.delete(id)) return;
        this.send({ jsonrpc: "2.0", method: "$/cancelRequest", params: { id } });
        reject(new Cancelled());
      };
      signal?.addEventListener("abort", onAbort, { once: true });
      const settle = (f: (value: any) => void) => (value: unknown) => {
        signal?.removeEventListener("abort", onAbort);
        f(value);
      };
      this.pending.set(id, { resolve: settle(resolve), reject: settle(reject) });
      this.send({ jsonrpc: "2.0", id, method, params });
    });
  }

  notify(method: string, params: unknown): void {
    this.send({ jsonrpc: "2.0", method, params });
  }

  /** Rejects every outstanding request, e.g. when the input ends. */
  failAll(error: Error): void {
    for (const p of this.pending.values()) p.reject(error);
    this.pending.clear();
  }

  handleLine(raw: string): void {
    const line = raw.endsWith("\r") ? raw.slice(0, -1) : raw;
    if (line.length === 0) return;
    let message: any;
    try {
      message = JSON.parse(line);
    } catch {
      this.options.onStray?.(line);
      this.send({ jsonrpc: "2.0", id: null, error: { code: errorCodes.parseError, message: "parse error" } });
      return;
    }
    if (Array.isArray(message) || typeof message !== "object" || message === null) {
      this.send({ jsonrpc: "2.0", id: null, error: { code: errorCodes.invalidRequest, message: "not a single message (the kernel protocol has no batches)" } });
      return;
    }
    if (typeof message.method === "string") {
      if (message.id === undefined) this.onNotificationMessage(message.method, message.params);
      else void this.onRequestMessage(message.id, message.method, message.params);
    } else if (message.id !== undefined && message.id !== null) {
      const pending = this.pending.get(message.id);
      if (!pending) return;
      this.pending.delete(message.id);
      if (message.error) pending.reject(new RpcError(message.error.code, message.error.message, message.error.data));
      else pending.resolve(message.result);
    }
  }

  private onNotificationMessage(method: string, params: any): void {
    if (method === "$/cancelRequest") {
      this.running.get(params?.id)?.abort();
      return;
    }
    if (!this.initialized) return;
    try {
      this.notifications.get(method)?.(params ?? {});
    } catch {
      // Notifications have nobody to answer.
    }
  }

  private async onRequestMessage(id: Id, method: string, params: any): Promise<void> {
    if (method === "initialize") return this.initialize(id, params);
    if (!this.initialized) return this.answerError(id, new RpcError(errorCodes.notInitialized, "not initialized"));
    const handler = this.requests.get(method);
    if (!handler) return this.answerError(id, new RpcError(errorCodes.methodNotFound, `method not found: ${method}`));
    if (this.running.has(id)) return this.answerError(id, new RpcError(errorCodes.invalidRequest, "duplicate request id"));
    const controller = new AbortController();
    this.running.set(id, controller);
    try {
      const result = await handler(params ?? {}, controller.signal);
      this.answer(id, result ?? null);
    } catch (e) {
      if (controller.signal.aborted && e instanceof Cancelled) this.answerError(id, new RpcError(errorCodes.requestCancelled, "request cancelled"));
      else this.answerError(id, e instanceof RpcError ? e : new RpcError(errorCodes.internalError, `internal error (${e instanceof Error ? e.name : typeof e})`));
    } finally {
      this.running.delete(id);
    }
  }

  private initialize(id: Id, params: any): void {
    if (this.initialized) return this.answerError(id, new RpcError(errorCodes.invalidRequest, "already initialized"));
    if (!this.server) return this.answerError(id, new RpcError(errorCodes.internalError, "not serving"));
    const offered: unknown = params?.protocolVersions;
    const version = Array.isArray(offered) ? offered.find((v) => rpcVersions.includes(v)) : undefined;
    if (version === undefined) {
      return this.answerError(id, new RpcError(errorCodes.unsupportedVersion, `no common protocol version; supported: ${rpcVersions.join(", ")}`, { supported: rpcVersions }));
    }
    let members: Record<string, unknown>;
    try {
      members = this.server.accept(params);
    } catch (e) {
      return this.answerError(id, e instanceof RpcError ? e : new RpcError(errorCodes.invalidParams, e instanceof Error ? e.message : String(e)));
    }
    const host: Capabilities | undefined = params?.capabilities;
    if (typeof host?.maxMessageSize === "number") this.hostMaxMessageSize = host.maxMessageSize;
    const capabilities: Capabilities = {
      maxMessageSize,
      maxPendingRequests: 64,
      methods: [...this.requests.keys(), ...this.notifications.keys()].sort(),
    };
    const result: InitializeResult & Record<string, unknown> = { protocolVersion: version, peer: this.server.peer, capabilities, ...members };
    // Answer first: the host may send requests as soon as it reads the result.
    this.answer(id, result);
    this.initialized = true;
  }

  private answer(id: Id, result: unknown): void {
    const frame = JSON.stringify({ jsonrpc: "2.0", id, result });
    if (Buffer.byteLength(frame) > this.hostMaxMessageSize) {
      this.answerError(id, new RpcError(errorCodes.responseTooLarge, "the response is larger than the host accepts"));
      return;
    }
    this.options.write(frame);
  }

  private answerError(id: Id, error: RpcError): void {
    const body: Record<string, unknown> = { code: error.code, message: error.message };
    if (error.data !== undefined) body.data = error.data;
    this.send({ jsonrpc: "2.0", id, error: body });
  }

  private send(message: { jsonrpc: "2.0" } & Record<string, unknown>): void {
    const frame = JSON.stringify(message);
    if (!frame.startsWith(FRAME_PREFIX)) throw new Error("a frame must start with the jsonrpc member");
    this.options.write(frame);
  }
}

/** Splits a byte stream into lines and feeds them to `onLine`. */
export async function readLines(stream: ReadableStream<Uint8Array>, onLine: (line: string) => void): Promise<void> {
  const decoder = new TextDecoder();
  let buffer = "";
  for await (const chunk of stream) {
    buffer += decoder.decode(chunk, { stream: true });
    let nl: number;
    while ((nl = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, nl);
      buffer = buffer.slice(nl + 1);
      onLine(line);
    }
  }
  buffer += decoder.decode();
  if (buffer.length > 0) onLine(buffer);
}
