---
id: 01M89NH5DCTRX1BZC9F4CCBWQF
anchor: '@file'
created: 2026-11-25T15:35:02Z
norm: '1'
---

SandboxDriver.Wrap is pure: everything from the world (what paths exist, envd's shell, the probe's real paths) comes in the SandboxRequest, so the host computes it per start and the JIT-vs-native diff checks it.
