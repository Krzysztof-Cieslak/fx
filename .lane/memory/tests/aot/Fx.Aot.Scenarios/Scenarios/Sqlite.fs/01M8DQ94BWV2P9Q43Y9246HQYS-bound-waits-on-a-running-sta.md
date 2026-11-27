---
id: 01M8DQ94BWV2P9Q43Y9246HQYS
anchor: '@file'
created: 2026-11-27T05:22:33Z
norm: '1'
---

Bound waits on a running statement by interrupting it before failing: disposing the database waits for the statement on its lock, so a timed-out wait followed by `use db` hangs for ever.
