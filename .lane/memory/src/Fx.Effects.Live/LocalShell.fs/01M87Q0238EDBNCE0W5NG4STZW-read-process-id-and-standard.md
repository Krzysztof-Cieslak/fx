---
id: 01M87Q0238EDBNCE0W5NG4STZW
anchor: '@file'
created: 2026-11-24T21:22:10Z
norm: '1'
sig: 19f9ce21898e0254
body_hash: 91c9670945758cdc
raw_hash: 22e7726093cd661a
vouched: 2026-11-25T02:21:58Z
lines: 1-195
---

Read Process.Id and StandardInput before starting the disposal watcher: a fast process can exit, drain and be disposed before ofProcess returns. Stdout and stderr must keep feeding one channel, so a reader of one stream never blocks the other.
