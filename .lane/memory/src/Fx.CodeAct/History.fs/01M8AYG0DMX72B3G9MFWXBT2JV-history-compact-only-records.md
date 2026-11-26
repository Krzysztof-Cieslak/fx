---
id: 01M8AYG0DMX72B3G9MFWXBT2JV
anchor: '@file'
created: 2026-11-26T03:30:56Z
norm: '1'
---

history.compact only records the ask; the turn compacts before its next model call or at its end, never inside the cell. Asks of an earlier turn are dropped when a turn starts.
