---
id: 01M8A8K6AGH296BM6C2XPB61EV
anchor: '@file'
created: 2026-11-25T21:08:11Z
norm: '1'
sig: 8e7430f31889b761
body_hash: 47ead4782dcc6b72
raw_hash: bf27b69474a757f8
vouched: 2026-11-25T22:40:40Z
lines: 1-519
---

In the os and targets jobs every step after the build gates on `!cancelled()` and the outcomes of the steps it needs, never on the default success(): a failing test must not skip the Native AOT steps, nor one area's failure another area.
