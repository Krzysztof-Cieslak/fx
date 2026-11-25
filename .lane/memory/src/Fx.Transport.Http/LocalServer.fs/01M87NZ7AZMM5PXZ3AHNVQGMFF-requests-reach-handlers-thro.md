---
id: 01M87NZ7AZMM5PXZ3AHNVQGMFF
anchor: '@file'
created: 2026-11-24T21:04:14Z
norm: '1'
sig: be1de88aa6a222c7
body_hash: ba91dcd7f6ebd453
raw_hash: 309b8752b639db5a
vouched: 2026-11-25T10:41:43Z
lines: 1-303
---

Requests reach handlers through RequestDelegate values only; Map*/Use overloads taking a Delegate go through RequestDelegateFactory (reflection, dynamic code) and break Native AOT.
