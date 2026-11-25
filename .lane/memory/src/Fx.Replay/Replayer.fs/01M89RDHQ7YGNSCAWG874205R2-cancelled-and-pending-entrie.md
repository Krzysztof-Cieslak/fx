---
id: 01M89RDHQ7YGNSCAWG874205R2
anchor: '@file'
created: 2026-11-25T16:25:29Z
norm: '1'
sig: 06e768d866788b80
body_hash: 89c8f2edf446ef6a
raw_hash: 2c03477a2917a613
lines: 1-389
---

Cancelled and pending entries are done from the start and never gate later entries: whether a timeout even starts its timer can depend on timing (Connection.withinTimeout), so replay may or may not make them.
