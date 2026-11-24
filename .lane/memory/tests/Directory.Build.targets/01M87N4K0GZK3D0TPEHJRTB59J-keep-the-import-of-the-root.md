---
id: 01M87N4K0GZK3D0TPEHJRTB59J
anchor: '@file'
created: 2026-11-24T20:49:41Z
norm: '1'
---

Keep the import of the root Directory.Build.targets first: MSBuild imports only the nearest Directory.Build.targets, and the root one holds the Native AOT publish guard.
