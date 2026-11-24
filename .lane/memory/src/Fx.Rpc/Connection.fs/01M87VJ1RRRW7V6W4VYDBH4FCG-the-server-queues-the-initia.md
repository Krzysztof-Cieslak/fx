---
id: 01M87VJ1RRRW7V6W4VYDBH4FCG
anchor: '@file'
created: 2026-11-24T22:41:53Z
norm: '1'
---

The server queues the initialize result before completing the handshake task, and the client opens the connection inside the reader as the result arrives; otherwise a request from the other side races the handshake and is refused NotInitialized.
