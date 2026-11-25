---
id: 01M87JT6KWKYRYPP8AP35VP0DP
anchor: '@file'
created: 2026-11-24T20:09:03Z
norm: '1'
sig: 81590f63c7cc7524
body_hash: 079402ae9327fda6
raw_hash: c187f454b8991857
vouched: 2026-11-25T03:28:09Z
lines: 1-32
---

Compute the ProductConstants.g.fs path in items or inside the target, never in a top-level property: IntermediateOutputPath is still empty there, so the file would be generated into the source tree.
