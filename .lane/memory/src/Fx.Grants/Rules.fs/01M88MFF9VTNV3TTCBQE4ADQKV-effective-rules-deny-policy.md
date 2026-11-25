---
id: 01M88MFF9VTNV3TTCBQE4ADQKV
anchor: '@file'
created: 2026-11-25T05:57:23Z
norm: '1'
---

Effective rules: deny (policy or grant) wins, then a policy ask, then the most specific allow, else ask. Policy rules may only ask or deny, so adding one never permits more; keep it so (the 'policy only tightens' property test).
