---
id: 01M88764RSSDBSJTJW7VA4JAV0
anchor: '@file'
created: 2026-11-25T02:05:06Z
norm: '1'
sig: 19f9ce21898e0254
body_hash: 91c9670945758cdc
raw_hash: 22e7726093cd661a
lines: 1-198
---

Process.Kill(entireProcessTree = true) wraps per-process failures in an AggregateException (e.g. after the Process was disposed on exit); Proc.Kill must swallow it to stay a no-op on exited processes.
