---
id: 01M8DQ94CRJE3TT9ZFHTY2K9KZ
anchor: '@file'
created: 2026-11-27T05:22:34Z
norm: '1'
---

Run every scenario on a thread-pool thread with a bounded wait: a synchronous scenario runs inside Run, so only a wait outside it bounds it, and an unbounded one hangs the whole test run.
