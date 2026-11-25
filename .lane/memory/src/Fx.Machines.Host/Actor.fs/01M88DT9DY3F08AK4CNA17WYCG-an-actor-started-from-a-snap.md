---
id: 01M88DT9DY3F08AK4CNA17WYCG
anchor: '@file'
created: 2026-11-25T04:00:58Z
norm: '1'
sig: a1d6bda1c13e616a
body_hash: a8a544b09f47c407
raw_hash: 6d563f7d2b6e6c14
lines: 1-623
---

An actor started from a snapshot (Floor > 0) must look up keys it has not seen in the log (FindCommit) before Handle runs and before an interrupt cancels work: its key index lacks the commits up to the snapshot.
