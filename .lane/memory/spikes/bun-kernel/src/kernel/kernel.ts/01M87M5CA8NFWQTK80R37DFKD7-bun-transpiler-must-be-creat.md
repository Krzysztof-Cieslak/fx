---
id: 01M87M5CA8NFWQTK80R37DFKD7
anchor: '@file'
created: 2026-11-24T20:32:38Z
norm: '1'
sig: e3b0c44298fc1c14
body_hash: 2b2f7bc347b24ccb
raw_hash: 81a224cc9cb247fb
lines: 1-339
---

Bun.Transpiler must be created with macro: false. --no-macros does not reach the Transpiler API in 1.4.5, and a cell importing with { type: "macro" } would otherwise run code at transpile time in the kernel's main realm.
