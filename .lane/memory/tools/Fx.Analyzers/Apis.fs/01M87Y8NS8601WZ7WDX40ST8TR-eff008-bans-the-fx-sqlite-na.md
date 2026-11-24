---
id: 01M87Y8NS8601WZ7WDX40ST8TR
anchor: '@file'
created: 2026-11-24T23:29:12Z
norm: '1'
---

EFF008 bans the Fx.Sqlite namespace as a whole: SQLite's file I/O happens in native code the filesystem rule cannot see, so every use outside a storage interpreter must be visible.
