---
id: 01M89HMZ4909D6JNWHCQX2MP1N
anchor: '@file'
created: 2026-11-25T14:27:12Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: 6ab0507c16ab37c8
raw_hash: 02edceab95549d20
vouched: 2026-11-28T13:01:56Z
lines: 1-418
---

A kernel's host calls must go through the running turn's guard (SessionKernel.Guard, set before each action), never the guard of the turn that launched it: hands, and so grants, are per turn.
