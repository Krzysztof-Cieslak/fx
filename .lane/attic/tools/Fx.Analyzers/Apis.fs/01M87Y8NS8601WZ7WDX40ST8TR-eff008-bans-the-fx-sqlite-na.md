---
id: 01M87Y8NS8601WZ7WDX40ST8TR
anchor: '@file'
created: 2026-11-24T23:29:12Z
norm: '1'
sig: 748043cac81fd8e0
body_hash: 8af2ed0154acd27b
raw_hash: 5821e830683310c4
lines: 1-307
---

EFF008 bans the Fx.Sqlite namespace as a whole: SQLite's file I/O happens in native code the filesystem rule cannot see, so every use outside a storage interpreter must be visible.
