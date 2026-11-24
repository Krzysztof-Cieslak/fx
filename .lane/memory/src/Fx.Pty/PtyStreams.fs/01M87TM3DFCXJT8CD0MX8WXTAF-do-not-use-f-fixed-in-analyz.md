---
id: 01M87TM3DFCXJT8CD0MX8WXTAF
anchor: '@file'
created: 2026-11-24T22:25:32Z
norm: '1'
---

Do not use F# `fixed` in analyzed code: the analyzers' typed-tree walk crashes on it (FCS ConstraintSolverError). Use pinned-object-heap arrays or GCHandle.
