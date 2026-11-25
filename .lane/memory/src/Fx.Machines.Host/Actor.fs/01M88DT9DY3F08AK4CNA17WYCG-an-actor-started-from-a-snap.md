---
id: 01M88DT9DY3F08AK4CNA17WYCG
anchor: '@file'
created: 2026-11-25T04:00:58Z
norm: '1'
---

An actor started from a snapshot (Floor > 0) must look up keys it has not seen in the log (FindCommit) before Handle runs and before an interrupt cancels work: its key index lacks the commits up to the snapshot.
