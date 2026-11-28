---
id: 01M89VAMVFZNXW56SZRQB1417Q
anchor: '@file'
created: 2026-11-25T17:16:20Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: e892b8f533eddaad
raw_hash: 3957b4d4891b8081
vouched: 2026-11-28T02:14:42Z
lines: 1-946
---

Local functions inside an eff/effResult block must have concrete types (annotate the Eff's environment): a generalized local function makes the block fail FS3511 in Debug, which Release hides.
