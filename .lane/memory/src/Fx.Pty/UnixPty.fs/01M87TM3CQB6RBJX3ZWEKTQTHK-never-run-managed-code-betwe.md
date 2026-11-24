---
id: 01M87TM3CQB6RBJX3ZWEKTQTHK
anchor: '@file'
created: 2026-11-24T22:25:32Z
norm: '1'
sig: 59f13189daf5bb7b
body_hash: 2927d79be1e2257f
raw_hash: 5ec0166cecba46b1
lines: 1-342
---

Never run managed code between fork and exec: children start with posix_spawn only. On macOS the slave must be reopened after setsid (XNU runs posix_spawn file actions before POSIX_SPAWN_SETSID), which the /bin/sh launcher does.
