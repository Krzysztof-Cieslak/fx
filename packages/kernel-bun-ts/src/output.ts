// Bounded output (spec §6.6 "bounded output everywhere"): a cell's result keeps the head and the
// tail of what it printed, with one marker line for what was left out, and the kernel streams at
// most `outputChars` of it as `cell/output` notifications. Overflow into a blob is T201's.

import type { BoundedText } from "./protocol.d.ts";

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
      this.tail = tailLimit > 0 ? (this.tail + text).slice(-tailLimit) : "";
    }
  }

  take(): BoundedText {
    const kept = this.head.length + this.tail.length;
    const truncated = this.total > kept;
    const text = truncated ? `${this.head}\n… [${this.total - kept} chars omitted] …\n${this.tail}` : this.head + this.tail;
    return { text, truncated, totalChars: this.total };
  }
}

/** Coalesces output into notifications: flushed after the current task, and before the result. */
export class OutputStream {
  private pending = "";
  private streamed = 0;
  private scheduled = false;
  /** Notifications sent. */
  sent = 0;

  constructor(
    private readonly limit: number,
    private readonly send: (text: string) => void,
  ) {}

  append(text: string): void {
    const room = this.limit - this.streamed - this.pending.length;
    if (room <= 0) return;
    this.pending += text.slice(0, room);
    if (!this.scheduled) {
      this.scheduled = true;
      queueMicrotask(() => this.flush());
    }
  }

  flush(): void {
    this.scheduled = false;
    if (this.pending.length === 0) return;
    const text = this.pending;
    this.pending = "";
    this.streamed += text.length;
    this.sent++;
    this.send(text);
  }
}
