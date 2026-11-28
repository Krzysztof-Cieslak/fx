---
id: 01M8FSXE9WXE1PNVGMD78RFS6W
anchor: CellClock
created: 2026-11-28T00:47:05Z
norm: '1'
---

The script kernel must pause its timeout around host calls as bun-ts does: the conformance suite checks that a host call answered after the cell's timeout lets the cell complete.
