---
id: 01M8AVW3MB0KSSXRQNW7TNTAZJ
anchor: '@file'
created: 2026-11-26T02:45:06Z
norm: '1'
---

Kernel.kill ends the connection (CloseAsync) before it terminates the process: killed first, the read loop may see the output end and close the connection itself (another reason, another flow, late answers dispatched), which exact replay cannot follow.
