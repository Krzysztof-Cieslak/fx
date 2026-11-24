---
id: 01M87M5CA8NFWQTK80R37DFKD7
anchor: '@file'
created: 2026-11-24T20:32:38Z
norm: '1'
---

Bun.Transpiler must be created with macro: false. --no-macros does not reach the Transpiler API in 1.4.5, and a cell importing with { type: "macro" } would otherwise run code at transpile time in the kernel's main realm.
