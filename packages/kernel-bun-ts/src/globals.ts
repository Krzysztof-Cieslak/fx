// The globals cells get besides ECMAScript's own, the host bridge and `Out`: one table, from which
// the kernel builds the cell context and the checker its declarations (`kernel-globals.d.ts`), so
// that what type-checks is what runs. Deliberately small: `lib: ["DOM"]` would declare far more
// than cells get and roughly triple `bun check`'s start-up (docs/spikes/bun-kernel.md).
//
// `process`, `Bun` and `require` are not globals: cells import them (`await import("node:fs")`).
// Modules have no declarations here (shipping bun-types would add megabytes to the binary), so
// `node:*` and `bun` imports type-check as `any` (the shorthand ambient modules below).

/** The context's globals; `console` is the kernel's own (it captures output per cell). */
export function contextGlobals(console: Record<string, (...args: unknown[]) => void>): Record<string, unknown> {
  return {
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
  };
}

/** The kernel's own names on the context, next to the globals. */
export const kernelNames = ["Out"];

/** Declarations of exactly `contextGlobals`, `kernelNames` and the modules cells may import. */
export const globalsDeclarations = `// The globals of the bun-ts kernel's cells (written by the kernel; see globals.ts).
declare var console: {
  log(...data: any[]): void; info(...data: any[]): void; debug(...data: any[]): void;
  warn(...data: any[]): void; error(...data: any[]): void; trace(...data: any[]): void; dir(item?: any): void;
};
type TimerHandle = { ref(): TimerHandle; unref(): TimerHandle; hasRef(): boolean; refresh(): TimerHandle };
declare function setTimeout<A extends any[]>(handler: (...args: A) => void, timeout?: number, ...args: A): TimerHandle;
declare function setInterval<A extends any[]>(handler: (...args: A) => void, timeout?: number, ...args: A): TimerHandle;
declare function clearTimeout(id: TimerHandle | undefined): void;
declare function clearInterval(id: TimerHandle | undefined): void;
declare function queueMicrotask(callback: () => void): void;
declare function structuredClone<T>(value: T): T;
declare function atob(data: string): string;
declare function btoa(data: string): string;
declare class TextEncoder { encode(input?: string): Uint8Array }
declare class TextDecoder { constructor(label?: string); decode(input?: ArrayBufferView | ArrayBuffer): string }
declare class URLSearchParams implements Iterable<[string, string]> {
  constructor(init?: string | Record<string, string> | [string, string][]);
  get(name: string): string | null; getAll(name: string): string[]; has(name: string): boolean;
  set(name: string, value: string): void; append(name: string, value: string): void; delete(name: string): void;
  toString(): string; [Symbol.iterator](): Iterator<[string, string]>;
}
declare class URL {
  constructor(url: string | URL, base?: string | URL);
  href: string; origin: string; protocol: string; host: string; hostname: string; port: string;
  pathname: string; search: string; hash: string; readonly searchParams: URLSearchParams; toString(): string;
}
declare class AbortSignal { readonly aborted: boolean; readonly reason: any; static timeout(ms: number): AbortSignal; throwIfAborted(): void }
declare class AbortController { readonly signal: AbortSignal; abort(reason?: any): void }
declare class Blob { constructor(parts?: (string | ArrayBuffer | ArrayBufferView | Blob)[], options?: { type?: string }); readonly size: number; readonly type: string; text(): Promise<string>; arrayBuffer(): Promise<ArrayBuffer>; bytes(): Promise<Uint8Array> }
declare class FormData { append(name: string, value: string | Blob): void; get(name: string): string | Blob | null }
declare class Headers implements Iterable<[string, string]> {
  constructor(init?: Record<string, string> | [string, string][] | Headers);
  get(name: string): string | null; set(name: string, value: string): void; has(name: string): boolean; [Symbol.iterator](): Iterator<[string, string]>;
}
type BodyInit = string | Blob | ArrayBuffer | ArrayBufferView | URLSearchParams | FormData;
interface RequestInit { method?: string; headers?: Record<string, string> | Headers; body?: BodyInit | null; signal?: AbortSignal; redirect?: "follow" | "error" | "manual" }
declare class Request { constructor(input: string | URL | Request, init?: RequestInit); readonly url: string; readonly method: string; readonly headers: Headers }
declare class Response {
  constructor(body?: BodyInit | null, init?: { status?: number; headers?: Record<string, string> | Headers });
  readonly ok: boolean; readonly status: number; readonly statusText: string; readonly headers: Headers; readonly url: string;
  text(): Promise<string>; json(): Promise<any>; arrayBuffer(): Promise<ArrayBuffer>; bytes(): Promise<Uint8Array>; blob(): Promise<Blob>;
}
declare function fetch(input: string | URL | Request, init?: RequestInit): Promise<Response>;
declare var performance: { now(): number; readonly timeOrigin: number };
declare var crypto: { randomUUID(): string; getRandomValues<T extends ArrayBufferView>(array: T): T };
/** The completion value of every cell that had one, by cell id. */
declare var Out: Record<string, unknown>;
declare module "node:*";
declare module "bun";
`;
