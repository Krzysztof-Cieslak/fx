---
id: 01M87Y823WX1PY9ZFJBCED51AG
anchor: '@file'
created: 2026-11-24T23:28:52Z
norm: '1'
sig: 49d5b330b4dae107
body_hash: 7edd6f91793c1a28
raw_hash: db4b3fd82ece773b
lines: 1-394
---

Run contribution code through guarded (Eff.delay inside Eff.catch): code may throw before it returns its effect, and that must still be the contribution's failure (a gate denies), not the caller's defect.
