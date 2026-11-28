// Retries with backoff (a sample for the outline tests).
import { setTimeout as sleep } from "node:timers/promises";

export const MAX_ATTEMPTS = 10;

export interface RetryConfig {
  attempts: number;
  delay: number;
  decide?(attempt: number): boolean;
}

export type Outcome<T> = { ok: true; value: T } | { ok: false; attempts: number };

export enum Mode {
  Linear,
  Exponential,
}

export class Retrier {
  private count = 0;

  constructor(private readonly config: RetryConfig) {}

  async execute<T>(op: () => Promise<T>): Promise<Outcome<T>> {
    const local = (x: number) => x + 1;
    for (let i = 0; i < this.config.attempts; i++) {
      this.count++;
      await sleep(this.config.delay);
    }
    return { ok: false, attempts: this.count };
  }
}

export function retry<T>(n: number, op: () => Promise<T>): Promise<Outcome<T>> {
  return new Retrier({ attempts: n, delay: 1 }).execute(op);
}

const describe = (o: Outcome<unknown>): string => (o.ok ? "ok" : `gave up after ${o.attempts}`);

namespace Internal {
  export function jitter(ms: number): number {
    return ms;
  }
}
