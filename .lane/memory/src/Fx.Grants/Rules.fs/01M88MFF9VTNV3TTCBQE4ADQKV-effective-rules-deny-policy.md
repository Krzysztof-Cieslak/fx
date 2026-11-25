---
id: 01M88MFF9VTNV3TTCBQE4ADQKV
anchor: '@file'
created: 2026-11-25T05:57:23Z
norm: '1'
sig: 16d4050a8067cd99
body_hash: a34c5fd8b2ae5ade
raw_hash: 45e80def74e78388
lines: 1-234
---

Effective rules: deny (policy or grant) wins, then a policy ask, then the most specific allow, else ask. Policy rules may only ask or deny, so adding one never permits more; keep it so (the 'policy only tightens' property test).
