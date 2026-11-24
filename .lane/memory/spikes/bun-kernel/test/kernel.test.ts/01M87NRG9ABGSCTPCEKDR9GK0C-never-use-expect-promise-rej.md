---
id: 01M87NRG9ABGSCTPCEKDR9GK0C
anchor: '@file'
created: 2026-11-24T21:00:33Z
norm: '1'
sig: 08c30549ee5e8d4e
body_hash: 86aed5fae55e45ab
raw_hash: 9285b0d2391885d9
lines: 1-274
---

Never use expect(promise).rejects/.resolves on promises that wait for kernel I/O: on Windows bun test 1.4.5 waits in a nested event loop that does not read subprocess pipes, and the test deadlocks.
