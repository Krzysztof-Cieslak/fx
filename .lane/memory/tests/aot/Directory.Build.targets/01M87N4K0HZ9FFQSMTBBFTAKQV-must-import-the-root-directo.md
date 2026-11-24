---
id: 01M87N4K0HZ9FFQSMTBBFTAKQV
anchor: '@file'
created: 2026-11-24T20:49:41Z
norm: '1'
---

Must import the root Directory.Build.targets directly: otherwise tests/Directory.Build.targets (or nothing) applies and the FxNativeAot publish guard is lost.
