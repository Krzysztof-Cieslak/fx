---
id: 01M8876B59Z9WSFESYCF4QRJXT
anchor: '@file'
created: 2026-11-25T02:05:13Z
norm: '1'
sig: d2b505f4b992760d
body_hash: e47c9164444bad0d
raw_hash: d176144e053c6503
vouched: 2026-11-25T20:21:27Z
lines: 1-859
---

spin must block every handler (cell/cancel included) until the kernel is killed, to stand for a Bun kernel whose event loop is stuck in a busy loop after an await; the conformance suite's kill-and-restart test depends on it.
