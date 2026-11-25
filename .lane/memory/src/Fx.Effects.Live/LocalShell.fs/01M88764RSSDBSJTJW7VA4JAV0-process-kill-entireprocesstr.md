---
id: 01M88764RSSDBSJTJW7VA4JAV0
anchor: '@file'
created: 2026-11-25T02:05:06Z
norm: '1'
---

Process.Kill(entireProcessTree = true) wraps per-process failures in an AggregateException (e.g. after the Process was disposed on exit); Proc.Kill must swallow it to stay a no-op on exited processes.
