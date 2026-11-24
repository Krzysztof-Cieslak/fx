---
id: 01M87Q0239AZEHQH6PWM98Q3S9
anchor: '@file'
created: 2026-11-24T21:22:10Z
norm: '1'
---

Check expected failures (is a directory, not empty, exists, missing parent) before calling .NET: its exceptions differ per OS, and LocalFs must pass the same conformance suite as InMemoryFs everywhere.
