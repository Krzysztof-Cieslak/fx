---
id: 01M87TY6P4PHH384ZACNJ6F82R
anchor: '@file'
created: 2026-11-24T22:31:03Z
norm: '1'
sig: 99b76d75878ca6e2
body_hash: ebc87f41eeaa4659
raw_hash: cef38e6cf8dd1748
vouched: 2026-11-27T05:48:14Z
lines: 1-495
---

A transaction holds the connection's lock until it ends: SQLite transactions belong to the connection, so a statement from another thread would otherwise run inside it.
