---
id: 01M8FT7B2Z1PC12N296RB1TSJF
anchor: '@file'
created: 2026-11-28T00:52:29Z
norm: '1'
sig: b39b6392c6a95b71
body_hash: 1d269485b534bb36
raw_hash: 4870d08fbec04cfc
lines: 1-161
---

Slots are fetched once, before the extension activates (host/variants), because IVariants.Read is synchronous; the host's values must not change while it runs. TypeScript's host.ts does the same.
