---
id: 01M8A8K6BQSWH3T4QFX4T5FNZK
anchor: '@file'
created: 2026-11-25T21:08:11Z
norm: '1'
sig: 977c0f8898e6cb06
body_hash: 8eeb5fc864222aa1
raw_hash: 670290e7b6863c0d
vouched: 2026-11-25T23:15:03Z
lines: 1-240
---

Changed paths must never pick the full tier (only main, the schedule, manual runs and the ci:full label): path-triggered full runs are what queued every pull request. PLATFORM_PATHS only drives the warning.
