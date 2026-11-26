---
id: 01M8B3396E3A5ZND29XP9EM5X9
anchor: '@file'
created: 2026-11-26T04:51:21Z
norm: '1'
---

The lint job's Debug test step is CI's only run of Debug code: keep Fx.Effects.Tests and Fx.Aot.Tests there (network-free), since Debug compiles inline helpers and tail calls differently from the Release the os jobs test.
