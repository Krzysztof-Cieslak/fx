---
id: 01M8DQ94CRJE3TT9ZFHTY2K9KZ
anchor: '@file'
created: 2026-11-27T05:22:34Z
norm: '1'
sig: 5a2f92a8d295cbe0
body_hash: 7f5ca7bbf1e5eebd
raw_hash: f85650ff3a02d094
lines: 1-117
---

Run every scenario on a thread-pool thread with a bounded wait: a synchronous scenario runs inside Run, so only a wait outside it bounds it, and an unbounded one hangs the whole test run.
