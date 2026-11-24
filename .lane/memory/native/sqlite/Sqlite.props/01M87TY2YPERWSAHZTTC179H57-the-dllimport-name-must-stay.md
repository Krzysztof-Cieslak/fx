---
id: 01M87TY2YPERWSAHZTTC179H57
anchor: '@file'
created: 2026-11-24T22:30:59Z
norm: '1'
sig: 15a767339566cc27
body_hash: aa55851029c85b46
raw_hash: f468748fbffa0430
lines: 1-32
---

The DllImport name must stay unique to the binding (not sqlite3 or e_sqlite3): DllImport falls back to system paths, so a common name could make the JIT load a system SQLite.
