---
id: 01M89VAMVFZNXW56SZRQB1417Q
anchor: '@file'
created: 2026-11-25T17:16:20Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: 177128ea0ef33877
raw_hash: 08b68a9c9fae8b5f
vouched: 2026-11-28T00:34:15Z
lines: 1-946
---

Local functions inside an eff/effResult block must have concrete types (annotate the Eff's environment): a generalized local function makes the block fail FS3511 in Debug, which Release hides.
