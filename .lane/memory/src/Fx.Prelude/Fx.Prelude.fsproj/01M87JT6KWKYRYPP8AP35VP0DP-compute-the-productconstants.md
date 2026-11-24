---
id: 01M87JT6KWKYRYPP8AP35VP0DP
anchor: '@file'
created: 2026-11-24T20:09:03Z
norm: '1'
sig: 81590f63c7cc7524
body_hash: 9e81f6e5292a3598
raw_hash: 30b69147c6cba034
lines: 1-32
---

Compute the ProductConstants.g.fs path in items or inside the target, never in a top-level property: IntermediateOutputPath is still empty there, so the file would be generated into the source tree.
