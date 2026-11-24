---
id: 01M87TM3CWKJNG2CMYN4K1A85B
anchor: '@file'
created: 2026-11-24T22:25:32Z
norm: '1'
---

Keep StartDetached: without it .NET passes its own console handles to the callback and, if they are a terminal, reconfigures that terminal for the child (ConfigureTerminalForChildProcesses).
