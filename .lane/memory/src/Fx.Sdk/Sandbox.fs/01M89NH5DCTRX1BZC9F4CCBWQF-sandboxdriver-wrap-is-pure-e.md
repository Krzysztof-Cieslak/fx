---
id: 01M89NH5DCTRX1BZC9F4CCBWQF
anchor: '@file'
created: 2026-11-25T15:35:02Z
norm: '1'
sig: 49d5b330b4dae107
body_hash: 22d9d3b78f55c19f
raw_hash: 05e50b01392cfc04
lines: 1-737
---

SandboxDriver.Wrap is pure: everything from the world (what paths exist, envd's shell, the probe's real paths) comes in the SandboxRequest, so the host computes it per start and the JIT-vs-native diff checks it.
