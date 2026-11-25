---
id: 01M8860DCEEY306H165Q7WAN57
anchor: rename
created: 2026-11-25T01:44:30Z
norm: '1'
---

Directory renames on Windows fail transiently while a scanner holds a freshly written file; activation and discard go through this retrying rename, never Fs.move directly.
