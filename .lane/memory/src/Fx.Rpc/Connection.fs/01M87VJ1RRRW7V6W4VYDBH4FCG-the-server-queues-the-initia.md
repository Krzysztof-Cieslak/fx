---
id: 01M87VJ1RRRW7V6W4VYDBH4FCG
anchor: '@file'
created: 2026-11-24T22:41:53Z
norm: '1'
sig: b70dad81299f621a
body_hash: c2274c4a7aa9c0f2
raw_hash: 161c36c0d5182c98
lines: 1-1218
---

The server queues the initialize result before completing the handshake task, and the client opens the connection inside the reader as the result arrives; otherwise a request from the other side races the handshake and is refused NotInitialized.
