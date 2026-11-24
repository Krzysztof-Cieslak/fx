---
id: 01M87PZYG15S271F32RBJG68SX
anchor: '@file'
created: 2026-11-24T21:22:06Z
norm: '1'
sig: e1a178df2be44477
body_hash: 21c872d617bd7448
raw_hash: a30b0e34b7aa22b1
lines: 1-395
---

Run must hand out a fresh copy of the initial state machine per invocation (AfterCode returns a closure over it): one Eff value is invoked many times, also concurrently. Keep binding limited to Eff values; no Task/Async overloads, so effects stay visible in 'R.
