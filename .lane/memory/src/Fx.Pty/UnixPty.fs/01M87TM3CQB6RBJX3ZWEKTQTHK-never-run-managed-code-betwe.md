---
id: 01M87TM3CQB6RBJX3ZWEKTQTHK
anchor: '@file'
created: 2026-11-24T22:25:32Z
norm: '1'
---

Never run managed code between fork and exec: children start with posix_spawn only. On macOS the slave must be reopened after setsid (XNU runs posix_spawn file actions before POSIX_SPAWN_SETSID), which the /bin/sh launcher does.
