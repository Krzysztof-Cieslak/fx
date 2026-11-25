---
id: 01M88SKVHH9D5VGDD09QHCTDSJ
anchor: '@file'
created: 2026-11-25T07:27:10Z
norm: '1'
sig: 49d5b330b4dae107
body_hash: ebbc5c79114d639a
raw_hash: a9c88dd2311dca17
lines: 1-251
---

The exclusions transform runs first (Priority -1000) and the gate re-checks the final request: a later transform may put excluded content back, and only the gate sees what would leave.
