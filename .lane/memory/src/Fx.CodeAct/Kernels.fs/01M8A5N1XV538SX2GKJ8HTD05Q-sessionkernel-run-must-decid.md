---
id: 01M8A5N1XV538SX2GKJ8HTD05Q
anchor: '@file'
created: 2026-11-25T20:16:47Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: 3afecb4a7af5f8d0
raw_hash: a75c61bdc991d7e0
vouched: 2026-11-25T23:30:35Z
lines: 1-496
---

SessionKernel.Run must decide from what was done (the interrupt's result, kept even when it loses the race), never from timing (Kernel.exited completing): exact replay (T131) takes the same path only if the code does.
