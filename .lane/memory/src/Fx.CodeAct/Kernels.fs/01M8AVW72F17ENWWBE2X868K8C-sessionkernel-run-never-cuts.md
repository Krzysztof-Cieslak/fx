---
id: 01M8AVW72F17ENWWBE2X868K8C
anchor: '@file'
created: 2026-11-26T02:45:10Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: 0c2c03f3fce2daa0
raw_hash: 3c2d60fa0fd6b73a
lines: 1-520
---

SessionKernel.Run never cuts an interrupt short when the cell answers first (Eff.both, not Eff.race): cancelled mid-flight, its cell/cancel is cancelled at the kernel ($/cancelRequest) or not depending on whether the kernel's answer was handled yet, and exact replay stalls on a write it does not make.
