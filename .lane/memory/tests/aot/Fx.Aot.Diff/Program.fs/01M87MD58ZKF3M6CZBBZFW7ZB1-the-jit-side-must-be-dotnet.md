---
id: 01M87MD58ZKF3M6CZBBZFW7ZB1
anchor: '@file'
created: 2026-11-24T20:36:53Z
norm: '1'
sig: b51efc2b8aefac44
body_hash: 907054f50965458c
raw_hash: a2ea956f556a56c6
lines: 1-225
---

The JIT side must be `dotnet artifacts/bin/<Project>/release_<rid>/<Assembly>.dll`, the RID-specific build that ILC compiled: it has the same IL and AOT feature switches, so only code generation differs.
