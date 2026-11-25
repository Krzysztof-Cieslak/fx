---
id: 01M89113C0DXERWG8NBDKBYR26
anchor: '@file'
created: 2026-11-25T09:36:44Z
norm: '1'
sig: 19f9ce21898e0254
body_hash: 320810cb273f4308
raw_hash: 3ea18c006059f00e
lines: 1-207
---

Delete on Windows clears ReadOnly first so deleting a file behaves as on POSIX (envd's shadow git objects are read-only); deleting directories still requires them empty.
