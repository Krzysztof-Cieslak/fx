---
id: 01M87TY6P4PHH384ZACNJ6F82R
anchor: '@file'
created: 2026-11-24T22:31:03Z
norm: '1'
sig: 99b76d75878ca6e2
body_hash: bfa958fbabfad4c7
raw_hash: ee3c2cbd519bf3ce
vouched: 2026-11-24T22:40:40Z
lines: 1-495
---

A transaction holds the connection's lock until it ends: SQLite transactions belong to the connection, so a statement from another thread would otherwise run inside it.
