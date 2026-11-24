---
id: 01M87PZYG3DD76TQQGZENFRAD4
anchor: '@file'
created: 2026-11-24T21:22:06Z
norm: '1'
sig: 456aef46d0008e73
body_hash: d8d29877fc4000f8
raw_hash: 024fd3db4c4db2fa
lines: 1-144
---

Delay completions must not use RunContinuationsAsynchronously: Advance relies on inline continuations to fire chained delays in deadline order at the right time. Register cancellation outside the lock (a cancelled token runs the callback inside Register).
