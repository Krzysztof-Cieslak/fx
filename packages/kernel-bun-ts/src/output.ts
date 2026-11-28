// Bounded output (spec §6.6 "bounded output everywhere"): a cell's result keeps the head and the
// tail of what it printed, with one marker line for what was left out, and the kernel streams at
// most `outputChars` of it as `cell/output` notifications. When the host asks (`overflowChars`), all of
// a cut output comes back too (`whole`, within that bound), for the host to store (T201a).

import type { BoundedText } from "./protocol.d.ts";

export class OutputBuffer {
  private head = "";
  private tail = "";
  private total = 0;
  private whole: string[] = [];

  constructor(
    private readonly limit: number,
    private readonly overflow?: number,
  ) {}

  append(text: string): void {
    this.total += text.length;
    if (this.overflow !== undefined && this.total <= this.overflow) this.whole.push(text);
    else this.whole = [];
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
    const whole = truncated && this.overflow !== undefined && this.total <= this.overflow ? this.whole.join("") : undefined;
    return whole === undefined ? { text, truncated, totalChars: this.total } : { text, truncated, totalChars: this.total, whole };
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
