---
id: 01M89VAMVFZNXW56SZRQB1417Q
anchor: '@file'
created: 2026-11-25T17:16:20Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: a012902d269bcb5d
raw_hash: 5bdec34c6cc21216
vouched: 2026-11-28T10:51:35Z
lines: 1-946
---

Local functions inside an eff/effResult block must have concrete types (annotate the Eff's environment): a generalized local function makes the block fail FS3511 in Debug, which Release hides.
