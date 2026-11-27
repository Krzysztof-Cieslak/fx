---
id: 01M8DNQTX6FAY79VRR0ENN38RD
anchor: '@file'
created: 2026-11-27T04:55:38Z
norm: '1'
sig: f7df8d5d7d292e7d
body_hash: d25b559d83c7aaf8
raw_hash: 9d8b14589166d2cf
lines: 1-198
---

After a resize, wait for a whole frame at the new size (footer on the last row, the prompt's box right above it), never for text anywhere in the output or a footer on the last row alone: VtScreen clamps the cursor, so a frame of the old size lands its lower rows on the last row of a smaller screen.
