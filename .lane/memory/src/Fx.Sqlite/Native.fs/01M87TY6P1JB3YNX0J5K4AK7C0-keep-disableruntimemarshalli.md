---
id: 01M87TY6P1JB3YNX0J5K4AK7C0
anchor: '@file'
created: 2026-11-24T22:31:03Z
norm: '1'
---

Keep DisableRuntimeMarshalling and blittable signatures only: with it, a signature needing a marshalling stub fails on the JIT tests too instead of only under Native AOT.
