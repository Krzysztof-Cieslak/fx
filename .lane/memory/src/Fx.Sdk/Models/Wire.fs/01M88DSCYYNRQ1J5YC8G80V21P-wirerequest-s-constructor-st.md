---
id: 01M88DSCYYNRQ1J5YC8G80V21P
anchor: WireRequest
created: 2026-11-25T04:00:29Z
norm: '1'
---

WireRequest's constructor stays internal and only ModelAccess makes one, after the model-egress hooks: that is what keeps any model-bound payload from bypassing them (spec §18).
