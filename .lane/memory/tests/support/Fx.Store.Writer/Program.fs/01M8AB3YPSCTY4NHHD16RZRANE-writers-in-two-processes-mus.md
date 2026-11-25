---
id: 01M8AB3YPSCTY4NHHD16RZRANE
anchor: '@file'
created: 2026-11-25T21:52:18Z
norm: '1'
sig: 868a86677adda6e5
body_hash: 8bce03dde1e3ee07
raw_hash: fc272aa767634de4
lines: 1-118
---

Writers in two processes must not rely on SQLite's busy timeout being fair: it polls for the write lock without a queue, so a writer committing back to back can starve the other past the timeout. Keep each writer to one commit per round that the test starts.
