---
id: 01M8FNQWYN8PPBMXFN16PZGJ61
anchor: extended
created: 2026-11-27T23:34:09Z
norm: '1'
---

Paths passed to CreateFileW must be in extended-length form (\?\, \?\UNC\), or paths over 260 characters fail where .NET's APIs succeed.
