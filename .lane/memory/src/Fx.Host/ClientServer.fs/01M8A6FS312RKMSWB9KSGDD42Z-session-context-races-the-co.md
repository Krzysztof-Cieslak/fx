---
id: 01M8A6FS312RKMSWB9KSGDD42Z
anchor: '@file'
created: 2026-11-25T20:31:22Z
norm: '1'
sig: aea2e01ee52e4c76
body_hash: c3f4e36bcf15e45f
raw_hash: 9ddbbc90d50524ca
vouched: 2026-11-25T23:15:03Z
lines: 1-453
---

session/context races the context log's subscription with the stream forwarder: both must honour the scope's cancellation, or the request never ends.
