---
id: 01M8DQ94BWV2P9Q43Y9246HQYS
anchor: '@file'
created: 2026-11-27T05:22:33Z
norm: '1'
sig: b97b735ac06941b5
body_hash: 8f5be568deb30fea
raw_hash: 8babf7b51c2c4acc
lines: 1-656
---

Bound waits on a running statement by interrupting it before failing: disposing the database waits for the statement on its lock, so a timed-out wait followed by `use db` hangs for ever.
