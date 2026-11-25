---
id: 01M87Y823WX1PY9ZFJBCED51AG
anchor: '@file'
created: 2026-11-24T23:28:52Z
norm: '1'
sig: 49d5b330b4dae107
body_hash: f21232efd71152e0
raw_hash: 7643b891d3ca275f
vouched: 2026-11-25T01:27:56Z
lines: 1-394
---

Run contribution code through guarded (Eff.delay inside Eff.catch): code may throw before it returns its effect, and that must still be the contribution's failure (a gate denies), not the caller's defect.
