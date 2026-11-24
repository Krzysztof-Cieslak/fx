---
id: 01M87TM3CQB6RBJX3ZWEKTQTHK
anchor: '@file'
created: 2026-11-24T22:25:32Z
norm: '1'
sig: 59f13189daf5bb7b
body_hash: 3a9f47f75447b4b6
raw_hash: 2a0cee33d9ecdffe
vouched: 2026-11-24T22:46:19Z
lines: 1-342
---

Never run managed code between fork and exec: children start with posix_spawn only. On macOS the slave must be reopened after setsid (XNU runs posix_spawn file actions before POSIX_SPAWN_SETSID), which the /bin/sh launcher does.
