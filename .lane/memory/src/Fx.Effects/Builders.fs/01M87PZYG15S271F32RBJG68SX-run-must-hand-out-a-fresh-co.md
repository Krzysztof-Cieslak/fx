---
id: 01M87PZYG15S271F32RBJG68SX
anchor: '@file'
created: 2026-11-24T21:22:06Z
norm: '1'
---

Run must hand out a fresh copy of the initial state machine per invocation (AfterCode returns a closure over it): one Eff value is invoked many times, also concurrently. Keep binding limited to Eff values; no Task/Async overloads, so effects stay visible in 'R.
