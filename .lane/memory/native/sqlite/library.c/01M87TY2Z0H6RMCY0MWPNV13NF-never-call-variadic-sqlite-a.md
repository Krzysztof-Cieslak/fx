---
id: 01M87TY2Z0H6RMCY0MWPNV13NF
anchor: '@file'
created: 2026-11-24T22:30:59Z
norm: '1'
sig: e3b0c44298fc1c14
body_hash: 9684567a090af025
raw_hash: c7960c1167611b50
lines: 1-55
---

Never call variadic SQLite APIs (sqlite3_db_config, sqlite3_config) through DllImport: variadic arguments go on the stack on Apple arm64. Add a fixed-arity shim here instead.
