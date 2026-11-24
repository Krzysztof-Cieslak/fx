---
id: 01M87S131PX2CNP9JCCC4Q76P3
anchor: '@file'
created: 2026-11-24T21:57:41Z
norm: '1'
---

Complete TaskCompletionSources outside locks and without RunContinuationsAsynchronously: actors run inline on the sender's thread, which keeps fake-clock tests deterministic and avoids deadlocks under the lock.
