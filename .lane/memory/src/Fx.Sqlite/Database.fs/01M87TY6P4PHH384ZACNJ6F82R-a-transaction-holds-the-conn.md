---
id: 01M87TY6P4PHH384ZACNJ6F82R
anchor: '@file'
created: 2026-11-24T22:31:03Z
norm: '1'
sig: 99b76d75878ca6e2
body_hash: 07c89e8ca5fe1890
raw_hash: 1d0660b4a1f410b8
lines: 1-495
---

A transaction holds the connection's lock until it ends: SQLite transactions belong to the connection, so a statement from another thread would otherwise run inside it.
