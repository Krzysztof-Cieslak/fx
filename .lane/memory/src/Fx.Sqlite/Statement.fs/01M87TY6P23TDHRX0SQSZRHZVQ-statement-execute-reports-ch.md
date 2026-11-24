---
id: 01M87TY6P23TDHRX0SQSZRHZVQ
anchor: '@file'
created: 2026-11-24T22:31:03Z
norm: '1'
---

Statement.execute reports changes only when sqlite3_total_changes64 moved: sqlite3_changes64 keeps the last INSERT/UPDATE/DELETE count, so DDL, VACUUM or a SELECT would report a stale number.
