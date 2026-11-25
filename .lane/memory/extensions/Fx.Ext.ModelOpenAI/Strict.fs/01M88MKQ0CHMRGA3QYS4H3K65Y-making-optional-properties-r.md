---
id: 01M88MKQ0CHMRGA3QYS4H3K65Y
anchor: '@file'
created: 2026-11-25T05:59:42Z
norm: '1'
---

Making optional properties required and nullable is safe only because our codecs read null as absent (Field.optional, Field.withDefault); a schema with keywords outside the subset is sent as given with strict: false, never loosened.
