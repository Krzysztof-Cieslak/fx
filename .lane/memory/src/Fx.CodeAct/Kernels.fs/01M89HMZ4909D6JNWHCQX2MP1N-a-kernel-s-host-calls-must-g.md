---
id: 01M89HMZ4909D6JNWHCQX2MP1N
anchor: '@file'
created: 2026-11-25T14:27:12Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: 5c7f4306590a81e3
raw_hash: 62b7e2acdb512a67
vouched: 2026-11-28T00:44:51Z
lines: 1-418
---

A kernel's host calls must go through the running turn's guard (SessionKernel.Guard, set before each action), never the guard of the turn that launched it: hands, and so grants, are per turn.
