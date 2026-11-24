---
id: 01M87MD58ZKF3M6CZBBZFW7ZB1
anchor: '@file'
created: 2026-11-24T20:36:53Z
norm: '1'
---

The JIT side must be `dotnet artifacts/bin/<Project>/release_<rid>/<Assembly>.dll`, the RID-specific build that ILC compiled: it has the same IL and AOT feature switches, so only code generation differs.
