---
id: 01M887XY8VATBKQEVEE2K5969R
anchor: '@file'
created: 2026-11-25T02:18:06Z
norm: '1'
sig: 49d5b330b4dae107
body_hash: e53ed210765a5f07
raw_hash: 41fc63a8ae55e0e1
lines: 1-463
---

RuntimeSpec.launch never passes the launcher's environment on: the child gets exactly the allowlisted names, the component's exports and EnvSet, minus every denied name (compared without case on all platforms). Keep it pure (no environment or filesystem reads); prepare is the only effect.
