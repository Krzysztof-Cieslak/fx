// Bounded text and value previews. Output keeps a head and a tail and counts what it dropped
// (spec §6.6: bounded output everywhere). Overflow to a blob is T201's job.

import { inspect } from "node:util";
import type { BoundedText, Preview } from "./protocol.ts";

export class OutputBuffer {
  private head = "";
  private tail = "";
  private total = 0;

  constructor(private readonly limit: number) {}

  append(text: string): void {
    this.total += text.length;
    const headLimit = Math.ceil(this.limit / 2);
    if (this.head.length < headLimit) {
      const take = Math.min(text.length, headLimit - this.head.length);
      this.head += text.slice(0, take);
      text = text.slice(take);
    }
    if (text.length > 0) {
      const tailLimit = this.limit - headLimit;
      this.tail = (this.tail + text).slice(-tailLimit);
    }
  }

  get isEmpty(): boolean {
    return this.total === 0;
  }

  /** Returns the buffered text and resets the buffer. */
  take(): BoundedText {
    const kept = this.head.length + this.tail.length;
    const truncated = this.total > kept;
    const text = truncated ? `${this.head}\n… [${this.total - kept} chars omitted] …\n${this.tail}` : this.head + this.tail;
    const result = { text, truncated, totalChars: this.total };
    this.head = "";
    this.tail = "";
    this.total = 0;
    return result;
  }
}

/** Type name that works across realms (values from the vm context fail `instanceof` checks here). */
export function typeName(value: unknown): string {
  if (value === null) return "null";
  if (Array.isArray(value)) return `Array(${value.length})`;
  if (typeof value === "object") {
    const ctor = (value as { constructor?: { name?: unknown } }).constructor;
    return typeof ctor?.name === "string" && ctor.name !== "" ? ctor.name : "Object";
  }
  return typeof value;
}

// `Bun.inspect` prints Object.prototype's methods for objects from another vm context (1.4.5);
// node:util's inspect handles them correctly.
export function formatValue(value: unknown, maxChars: number): string {
  return inspect(value, { depth: 2, maxArrayLength: 20, maxStringLength: maxChars, breakLength: 100, colors: false });
}

export function preview(value: unknown, maxChars: number): Preview {
  const text = formatValue(value, maxChars);
  const truncated = text.length > maxChars;
  return { type: typeName(value), text: truncated ? text.slice(0, maxChars) + "…" : text, truncated };
}

export function formatArgs(args: unknown[], maxChars: number): string {
  return args.map((a) => (typeof a === "string" ? a : formatValue(a, maxChars))).join(" ");
}
