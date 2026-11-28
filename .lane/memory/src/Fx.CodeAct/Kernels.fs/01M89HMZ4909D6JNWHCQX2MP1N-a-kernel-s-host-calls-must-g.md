---
id: 01M89HMZ4909D6JNWHCQX2MP1N
anchor: '@file'
created: 2026-11-25T14:27:12Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: 3a5c434f6440530a
raw_hash: db59779b12aa8b9f
vouched: 2026-11-28T01:05:25Z
lines: 1-418
---

A kernel's host calls must go through the running turn's guard (SessionKernel.Guard, set before each action), never the guard of the turn that launched it: hands, and so grants, are per turn.
