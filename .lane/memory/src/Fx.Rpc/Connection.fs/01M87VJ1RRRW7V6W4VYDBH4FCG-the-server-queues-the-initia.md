---
id: 01M87VJ1RRRW7V6W4VYDBH4FCG
anchor: '@file'
created: 2026-11-24T22:41:53Z
norm: '1'
sig: b70dad81299f621a
body_hash: 630166c0d86f669a
raw_hash: 64e9268aabba42d1
vouched: 2026-11-27T11:48:28Z
lines: 1-1218
---

The server queues the initialize result before completing the handshake task, and the client opens the connection inside the reader as the result arrives; otherwise a request from the other side races the handshake and is refused NotInitialized.
