---
id: 01M87NRG9ABGSCTPCEKDR9GK0C
anchor: '@file'
created: 2026-11-24T21:00:33Z
norm: '1'
---

Never use expect(promise).rejects/.resolves on promises that wait for kernel I/O: on Windows bun test 1.4.5 waits in a nested event loop that does not read subprocess pipes, and the test deadlocks.
