---
id: 01M8A8K6AGH296BM6C2XPB61EV
anchor: '@file'
created: 2026-11-25T21:08:11Z
norm: '1'
sig: 8e7430f31889b761
body_hash: 86d99e09d71f4bff
raw_hash: 3aaf7e23f3fe6629
vouched: 2026-11-27T14:02:36Z
lines: 1-519
---

In the os and targets jobs every step after the build gates on `!cancelled()` and the outcomes of the steps it needs, never on the default success(): a failing test must not skip the Native AOT steps, nor one area's failure another area.
