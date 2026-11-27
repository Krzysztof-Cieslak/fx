---
id: 01M8A6FS312RKMSWB9KSGDD42Z
anchor: '@file'
created: 2026-11-25T20:31:22Z
norm: '1'
sig: aea2e01ee52e4c76
body_hash: 7c0e81f498905429
raw_hash: cda4d24e66949997
vouched: 2026-11-27T18:25:48Z
lines: 1-453
---

session/context races the context log's subscription with the stream forwarder: both must honour the scope's cancellation, or the request never ends.
