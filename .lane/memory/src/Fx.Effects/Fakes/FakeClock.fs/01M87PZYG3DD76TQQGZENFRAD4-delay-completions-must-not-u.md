---
id: 01M87PZYG3DD76TQQGZENFRAD4
anchor: '@file'
created: 2026-11-24T21:22:06Z
norm: '1'
sig: 456aef46d0008e73
body_hash: 948c626e64e26755
raw_hash: 18681b0926452675
vouched: 2026-11-24T21:37:41Z
lines: 1-144
---

Delay completions must not use RunContinuationsAsynchronously: Advance relies on inline continuations to fire chained delays in deadline order at the right time. Register cancellation outside the lock (a cancelled token runs the callback inside Register).
