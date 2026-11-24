---
id: 01M87TM3CWKJNG2CMYN4K1A85B
anchor: '@file'
created: 2026-11-24T22:25:32Z
norm: '1'
sig: 59f13189daf5bb7b
body_hash: 3a9f47f75447b4b6
raw_hash: 2a0cee33d9ecdffe
vouched: 2026-11-24T22:46:19Z
lines: 1-342
---

Keep StartDetached: without it .NET passes its own console handles to the callback and, if they are a terminal, reconfigures that terminal for the child (ConfigureTerminalForChildProcesses).
