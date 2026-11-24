---
id: 01M87N4K0GZK3D0TPEHJRTB59J
anchor: '@file'
created: 2026-11-24T20:49:41Z
norm: '1'
sig: 15a767339566cc27
body_hash: 2aa5327c1a040f39
raw_hash: 728791a2fae375c9
lines: 1-13
---

Keep the import of the root Directory.Build.targets first: MSBuild imports only the nearest Directory.Build.targets, and the root one holds the Native AOT publish guard.
