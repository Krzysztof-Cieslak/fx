---
id: 01M87TM3DFCXJT8CD0MX8WXTAF
anchor: '@file'
created: 2026-11-24T22:25:32Z
norm: '1'
sig: 5a7b6ee0947aa502
body_hash: 3e803064d367c2c0
raw_hash: 4e290ba552c4ba5f
lines: 1-154
---

Do not use F# `fixed` in analyzed code: the analyzers' typed-tree walk crashes on it (FCS ConstraintSolverError). Use pinned-object-heap arrays or GCHandle.
