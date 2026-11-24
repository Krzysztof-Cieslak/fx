---
id: 01M87TY6NZ0TAQ15BKYG9NPV1X
anchor: '@file'
created: 2026-11-24T22:31:03Z
norm: '1'
sig: ed06c91644e79aa4
body_hash: ef86ab3f753affa9
raw_hash: 806ae5d68f87c94f
lines: 1-261
---

Pin arrays with Native.pinned (a GCHandle), never `use p = fixed array`: the analyzers' typed-tree walk (FSharpExprConvert) throws on `fixed`, which fails the analyzers job.
