---
id: 01M87ZP1HV482QZF9T6PNBTC4D
anchor: '@file'
created: 2026-11-24T23:53:59Z
norm: '1'
sig: 2f93e74691977137
body_hash: 0239c66179989b00
raw_hash: 3b4d75795704c7da
lines: 1-323
supersedes: 01M87Y8NS8601WZ7WDX40ST8TR
---

EFF009 bans the Fx.Sqlite namespace as a whole: SQLite's file I/O happens in native code the filesystem rule cannot see, so every use outside a storage interpreter must be visible. EFF008 (P/Invoke calls) is a separate rule.
