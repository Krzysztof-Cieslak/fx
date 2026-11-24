---
id: 01M87NZ7AZMM5PXZ3AHNVQGMFF
anchor: '@file'
created: 2026-11-24T21:04:14Z
norm: '1'
---

Requests reach handlers through RequestDelegate values only; Map*/Use overloads taking a Delegate go through RequestDelegateFactory (reflection, dynamic code) and break Native AOT.
