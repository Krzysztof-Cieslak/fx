---
id: 01M8DNQTX6FAY79VRR0ENN38RD
anchor: '@file'
created: 2026-11-27T04:55:38Z
norm: '1'
---

After a resize, wait for a whole frame at the new size (footer on the last row, the prompt's box right above it), never for text anywhere in the output or a footer on the last row alone: VtScreen clamps the cursor, so a frame of the old size lands its lower rows on the last row of a smaller screen.
