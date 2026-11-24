---
id: 01M87TQPA0T7NSQRH0GF3Y9SEG
anchor: '@file'
created: 2026-11-24T22:27:30Z
norm: '1'
sig: 748043cac81fd8e0
body_hash: f0e3350d56a85d93
raw_hash: a0fee062ba0e22c5
lines: 1-319
---

The PInvoke ban must stay first in `bans` and match on the DllImport attribute, not on a declaring type: extern functions can be declared in any module.
