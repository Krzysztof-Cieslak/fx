---
id: 01M87Y823WX1PY9ZFJBCED51AG
anchor: '@file'
created: 2026-11-24T23:28:52Z
norm: '1'
---

Run contribution code through guarded (Eff.delay inside Eff.catch): code may throw before it returns its effect, and that must still be the contribution's failure (a gate denies), not the caller's defect.
