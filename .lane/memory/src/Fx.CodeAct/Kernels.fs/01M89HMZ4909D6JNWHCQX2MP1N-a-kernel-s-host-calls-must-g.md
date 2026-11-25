---
id: 01M89HMZ4909D6JNWHCQX2MP1N
anchor: '@file'
created: 2026-11-25T14:27:12Z
norm: '1'
---

A kernel's host calls must go through the running turn's guard (SessionKernel.Guard, set before each action), never the guard of the turn that launched it: hands, and so grants, are per turn.
