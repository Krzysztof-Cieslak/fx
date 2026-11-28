---
id: 01M89VAMVFZNXW56SZRQB1417Q
anchor: '@file'
created: 2026-11-25T17:16:20Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: 40b75df3846aff4d
raw_hash: 89704b0b4e309e66
vouched: 2026-11-28T01:27:27Z
lines: 1-946
---

Local functions inside an eff/effResult block must have concrete types (annotate the Eff's environment): a generalized local function makes the block fail FS3511 in Debug, which Release hides.
