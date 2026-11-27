---
id: 01M8E31QJTFENRR14VDM681QFR
anchor: writeRow
created: 2026-11-27T08:48:14Z
norm: '1'
---

Never write EL (ESC[K) after a row that fills the frame's width: wrap is off (Vt.enter's ?7l), so the cursor stays on the last column and EL erases that column. Only rows narrower than the width are cleared to the end.
