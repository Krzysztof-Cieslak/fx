// Newline-delimited JSON-RPC 2.0 peer, used by both the kernel and the host harness.
//
// Every frame starts with the literal `{"jsonrpc":"2.0",` (the key is serialized first). A reader
// can therefore recover a frame that follows stray bytes on the same line (e.g. something a cell
// wrote to fd 1 without a newline) and treat everything else as stray output.

export const FRAME_PREFIX = '{"jsonrpc":"2.0",';

export class RpcError extends Error {
  constructor(
    readonly code: number,
    message: string,
    readonly data?: unknown,
  ) {
    super(message);
  }
}

export const ErrorCodes = {
  parseError: -32700,
  invalidRequest: -32600,
  methodNotFound: -32601,
  invalidParams: -32602,
  internalError: -32603,
  busy: -32001,
  cancelled: -32800,
} as const;

type Handler = (params: any) => unknown;

interface Pending {
  resolve: (value: unknown) => void;
  reject: (error: unknown) => void;
}

export interface RpcPeerOptions {
  write: (frame: string) => void;
  onStray?: (text: string) => void;
}

export class RpcPeer {
  private nextId = 1;
  private readonly pending = new Map<number, Pending>();
  private readonly handlers = new Map<string, Handler>();

  constructor(private readonly options: RpcPeerOptions) {}

  on(method: string, handler: Handler): this {
    this.handlers.set(method, handler);
    return this;
  }

  request(method: string, params?: unknown): Promise<unknown> {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.send({ jsonrpc: "2.0", id, method, params });
    });
  }

  notify(method: string, params?: unknown): void {
    this.send({ jsonrpc: "2.0", method, params });
  }

  /** Rejects every outstanding request, e.g. when the transport closes. */
  failAll(error: Error): void {
    for (const p of this.pending.values()) p.reject(error);
    this.pending.clear();
  }

  get outstanding(): number {
    return this.pending.size;
  }

  handleLine(raw: string): void {
    const line = raw.endsWith("\r") ? raw.slice(0, -1) : raw;
    if (line.length === 0) return;
    const at = line.indexOf(FRAME_PREFIX);
    if (at < 0) {
      this.options.onStray?.(line);
      return;
    }
    if (at > 0) this.options.onStray?.(line.slice(0, at));
    let msg: any;
    try {
      msg = JSON.parse(line.slice(at));
    } catch {
      this.options.onStray?.(line.slice(at));
      return;
    }
    if (typeof msg.method === "string") {
      void this.dispatch(msg);
    } else if (msg.id !== undefined) {
      const pending = this.pending.get(msg.id);
      if (!pending) return;
      this.pending.delete(msg.id);
      if (msg.error) pending.reject(new RpcError(msg.error.code, msg.error.message, msg.error.data));
      else pending.resolve(msg.result);
    }
  }

  private async dispatch(msg: { id?: number | string; method: string; params?: unknown }): Promise<void> {
    const handler = this.handlers.get(msg.method);
    const isRequest = msg.id !== undefined;
    if (!handler) {
      if (isRequest) this.send({ jsonrpc: "2.0", id: msg.id, error: { code: ErrorCodes.methodNotFound, message: `method not found: ${msg.method}` } });
      return;
    }
    try {
      const result = await handler(msg.params);
      if (isRequest) this.send({ jsonrpc: "2.0", id: msg.id, result: result ?? null });
    } catch (e) {
      if (!isRequest) return;
      const error =
        e instanceof RpcError
          ? { code: e.code, message: e.message, data: e.data }
          : { code: ErrorCodes.internalError, message: e instanceof Error ? e.message : String(e) };
      this.send({ jsonrpc: "2.0", id: msg.id, error });
    }
  }

  private send(message: object): void {
    this.options.write(JSON.stringify(message));
  }
}

/** Splits a byte stream into lines and feeds them to `onLine`; calls `onEnd` at EOF. */
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
