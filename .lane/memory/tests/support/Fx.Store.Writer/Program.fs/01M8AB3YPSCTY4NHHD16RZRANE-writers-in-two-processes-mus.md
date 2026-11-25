---
id: 01M8AB3YPSCTY4NHHD16RZRANE
anchor: '@file'
created: 2026-11-25T21:52:18Z
norm: '1'
---

Writers in two processes must not rely on SQLite's busy timeout being fair: it polls for the write lock without a queue, so a writer committing back to back can starve the other past the timeout. Keep each writer to one commit per round that the test starts.
