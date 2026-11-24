---
id: 01M87TY6P4PHH384ZACNJ6F82R
anchor: '@file'
created: 2026-11-24T22:31:03Z
norm: '1'
---

A transaction holds the connection's lock until it ends: SQLite transactions belong to the connection, so a statement from another thread would otherwise run inside it.
