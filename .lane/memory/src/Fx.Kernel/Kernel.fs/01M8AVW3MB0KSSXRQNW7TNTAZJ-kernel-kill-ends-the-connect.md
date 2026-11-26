---
id: 01M8AVW3MB0KSSXRQNW7TNTAZJ
anchor: '@file'
created: 2026-11-26T02:45:06Z
norm: '1'
sig: f1ee8a2ef0996b8b
body_hash: 975002b17c5f6dc5
raw_hash: 7ac5c2486ca31c00
lines: 1-629
---

Kernel.kill ends the connection (CloseAsync) before it terminates the process: killed first, the read loop may see the output end and close the connection itself (another reason, another flow, late answers dispatched), which exact replay cannot follow.
