---
id: 01M87MCGBVJJ6FMERJ386E63T3
anchor: '@file'
created: 2026-11-24T20:36:32Z
norm: '1'
---

Scenario output must be deterministic across processes: never print times, pids, temp paths or string hash codes (randomized per process), or the JIT and native runs differ spuriously.
