---
id: 01M87TY6P23TDHRX0SQSZRHZVQ
anchor: '@file'
created: 2026-11-24T22:31:03Z
norm: '1'
sig: 99b76d75878ca6e2
body_hash: 380ae763ca7867f5
raw_hash: 86ec3451f1293ab3
lines: 1-274
---

Statement.execute reports changes only when sqlite3_total_changes64 moved: sqlite3_changes64 keeps the last INSERT/UPDATE/DELETE count, so DDL, VACUUM or a SELECT would report a stale number.
