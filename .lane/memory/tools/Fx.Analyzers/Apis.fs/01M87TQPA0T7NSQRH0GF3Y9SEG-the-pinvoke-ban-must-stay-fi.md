---
id: 01M87TQPA0T7NSQRH0GF3Y9SEG
anchor: '@file'
created: 2026-11-24T22:27:30Z
norm: '1'
sig: 2f93e74691977137
body_hash: 0239c66179989b00
raw_hash: 3b4d75795704c7da
vouched: 2026-11-24T23:57:42Z
lines: 1-319
---

The PInvoke ban must stay first in `bans` and match on the DllImport attribute, not on a declaring type: extern functions can be declared in any module.
