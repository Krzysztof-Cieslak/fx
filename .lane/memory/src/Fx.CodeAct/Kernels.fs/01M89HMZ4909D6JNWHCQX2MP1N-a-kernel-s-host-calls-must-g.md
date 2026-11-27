---
id: 01M89HMZ4909D6JNWHCQX2MP1N
anchor: '@file'
created: 2026-11-25T14:27:12Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: bf9bf4ee74d79feb
raw_hash: 32d74e8cd96492fa
vouched: 2026-11-27T00:39:39Z
lines: 1-418
---

A kernel's host calls must go through the running turn's guard (SessionKernel.Guard, set before each action), never the guard of the turn that launched it: hands, and so grants, are per turn.
