---
id: 01M88764RSSDBSJTJW7VA4JAV0
anchor: '@file'
created: 2026-11-25T02:05:06Z
norm: '1'
sig: 19f9ce21898e0254
body_hash: 60f7fd6d3b31aee9
raw_hash: 9a962a16c95411dd
vouched: 2026-11-26T23:31:33Z
lines: 1-198
---

Process.Kill(entireProcessTree = true) wraps per-process failures in an AggregateException (e.g. after the Process was disposed on exit); Proc.Kill must swallow it to stay a no-op on exited processes.
