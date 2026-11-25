---
id: 01M87Q0239AZEHQH6PWM98Q3S9
anchor: '@file'
created: 2026-11-24T21:22:10Z
norm: '1'
sig: 19f9ce21898e0254
body_hash: 320810cb273f4308
raw_hash: 3ea18c006059f00e
vouched: 2026-11-25T09:49:20Z
lines: 1-199
---

Check expected failures (is a directory, not empty, exists, missing parent) before calling .NET: its exceptions differ per OS, and LocalFs must pass the same conformance suite as InMemoryFs everywhere.
