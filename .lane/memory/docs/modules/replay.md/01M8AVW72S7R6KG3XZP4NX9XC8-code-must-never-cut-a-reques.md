---
id: 01M8AVW72S7R6KG3XZP4NX9XC8
anchor: '@file'
created: 2026-11-26T02:45:10Z
norm: '1'
sig: f1bf94479792e235
body_hash: 00d34432c1f608ff
raw_hash: 98fd45b3d0ae0194
lines: 1-303
---

Code must never cut a request to a process short because another branch finished first: whether $/cancelRequest is written depends on whether the answer was already handled (timing).
