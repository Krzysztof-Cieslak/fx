---
id: 01M8876B59Z9WSFESYCF4QRJXT
anchor: '@file'
created: 2026-11-25T02:05:13Z
norm: '1'
sig: d2b505f4b992760d
body_hash: dc2ec6750de1b3f6
raw_hash: 3d6b87a8c8a346d7
vouched: 2026-11-25T05:00:54Z
lines: 1-859
---

spin must block every handler (cell/cancel included) until the kernel is killed, to stand for a Bun kernel whose event loop is stuck in a busy loop after an await; the conformance suite's kill-and-restart test depends on it.
