---
id: 01M89113C0DXERWG8NBDKBYR26
anchor: '@file'
created: 2026-11-25T09:36:44Z
norm: '1'
---

Delete on Windows clears ReadOnly first so deleting a file behaves as on POSIX (envd's shadow git objects are read-only); deleting directories still requires them empty.
