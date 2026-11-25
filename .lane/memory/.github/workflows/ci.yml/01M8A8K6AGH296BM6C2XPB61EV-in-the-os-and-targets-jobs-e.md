---
id: 01M8A8K6AGH296BM6C2XPB61EV
anchor: '@file'
created: 2026-11-25T21:08:11Z
norm: '1'
---

In the os and targets jobs every step after the build gates on `!cancelled()` and the outcomes of the steps it needs, never on the default success(): a failing test must not skip the Native AOT steps, nor one area's failure another area.
