---
id: 01M87PZYG3DD76TQQGZENFRAD4
anchor: '@file'
created: 2026-11-24T21:22:06Z
norm: '1'
---

Delay completions must not use RunContinuationsAsynchronously: Advance relies on inline continuations to fire chained delays in deadline order at the right time. Register cancellation outside the lock (a cancelled token runs the callback inside Register).
