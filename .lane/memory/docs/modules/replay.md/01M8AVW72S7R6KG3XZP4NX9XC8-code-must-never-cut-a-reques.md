---
id: 01M8AVW72S7R6KG3XZP4NX9XC8
anchor: '@file'
created: 2026-11-26T02:45:10Z
norm: '1'
sig: f1bf94479792e235
body_hash: 335513b24cf85bb0
raw_hash: ae4cae05b922fd64
vouched: 2026-11-28T02:14:42Z
lines: 1-303
---

Code must never cut a request to a process short because another branch finished first: whether $/cancelRequest is written depends on whether the answer was already handled (timing).
