---
id: 01M88SKZNSFTV5SVZRSH28JQEY
anchor: '@file'
created: 2026-11-25T07:27:14Z
norm: '1'
---

fs.read checks the exclusions before touching the file: an excluded file's bytes must never reach the kernel, not even in an error.
