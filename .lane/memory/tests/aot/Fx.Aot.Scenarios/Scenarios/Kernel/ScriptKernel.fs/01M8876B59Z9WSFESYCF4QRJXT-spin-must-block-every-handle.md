---
id: 01M8876B59Z9WSFESYCF4QRJXT
anchor: '@file'
created: 2026-11-25T02:05:13Z
norm: '1'
sig: d2b505f4b992760d
body_hash: fd1fe4afd6e5ee4d
raw_hash: 31d512ee92f1a92e
lines: 1-859
---

spin must block every handler (cell/cancel included) until the kernel is killed, to stand for a Bun kernel whose event loop is stuck in a busy loop after an await; the conformance suite's kill-and-restart test depends on it.
