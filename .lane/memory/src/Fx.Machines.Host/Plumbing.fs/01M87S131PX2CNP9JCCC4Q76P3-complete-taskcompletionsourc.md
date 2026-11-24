---
id: 01M87S131PX2CNP9JCCC4Q76P3
anchor: '@file'
created: 2026-11-24T21:57:41Z
norm: '1'
sig: a1d6bda1c13e616a
body_hash: c64f29fa6b039e1e
raw_hash: 3f41de1e3d1e6d3c
lines: 1-119
---

Complete TaskCompletionSources outside locks and without RunContinuationsAsynchronously: actors run inline on the sender's thread, which keeps fake-clock tests deterministic and avoids deadlocks under the lock.
