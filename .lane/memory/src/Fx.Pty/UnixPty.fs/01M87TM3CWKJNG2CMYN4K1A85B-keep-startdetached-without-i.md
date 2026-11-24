---
id: 01M87TM3CWKJNG2CMYN4K1A85B
anchor: '@file'
created: 2026-11-24T22:25:32Z
norm: '1'
sig: 59f13189daf5bb7b
body_hash: 2927d79be1e2257f
raw_hash: 5ec0166cecba46b1
lines: 1-342
---

Keep StartDetached: without it .NET passes its own console handles to the callback and, if they are a terminal, reconfigures that terminal for the child (ConfigureTerminalForChildProcesses).
