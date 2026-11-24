---
id: 01M87NZ7AZMM5PXZ3AHNVQGMFF
anchor: '@file'
created: 2026-11-24T21:04:14Z
norm: '1'
sig: be1de88aa6a222c7
body_hash: 0a9f89aba2d15490
raw_hash: 26d882ca38ad88c3
vouched: 2026-11-24T21:55:28Z
lines: 1-303
---

Requests reach handlers through RequestDelegate values only; Map*/Use overloads taking a Delegate go through RequestDelegateFactory (reflection, dynamic code) and break Native AOT.
