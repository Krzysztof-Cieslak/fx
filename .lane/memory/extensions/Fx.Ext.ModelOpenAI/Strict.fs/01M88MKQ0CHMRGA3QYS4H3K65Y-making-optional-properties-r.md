---
id: 01M88MKQ0CHMRGA3QYS4H3K65Y
anchor: '@file'
created: 2026-11-25T05:59:42Z
norm: '1'
sig: dc9212d48a7cc7c7
body_hash: 7440470dd6b37372
raw_hash: 90114a33e343ee80
lines: 1-196
---

Making optional properties required and nullable is safe only because our codecs read null as absent (Field.optional, Field.withDefault); a schema with keywords outside the subset is sent as given with strict: false, never loosened.
