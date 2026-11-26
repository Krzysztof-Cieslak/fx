---
id: 01M8AVWH4V0BE6HCFVW01WJJQ3
anchor: '@file'
created: 2026-11-26T02:45:20Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: 0c2c03f3fce2daa0
raw_hash: 3c2d60fa0fd6b73a
lines: 1-520
supersedes: 01M8A5N1XV538SX2GKJ8HTD05Q
---

SessionKernel.Run must decide from what was done (how the interrupt stopped the cell, the cell's answer), never from timing (Kernel.exited completing, which branch finished first): exact replay (T131) takes the same path only if the code does.
