---
id: 01M8A6FS312RKMSWB9KSGDD42Z
anchor: '@file'
created: 2026-11-25T20:31:22Z
norm: '1'
sig: aea2e01ee52e4c76
body_hash: bdb04c78d8fdf405
raw_hash: fe0002fcb88ac0a7
vouched: 2026-11-27T01:06:19Z
lines: 1-453
---

session/context races the context log's subscription with the stream forwarder: both must honour the scope's cancellation, or the request never ends.
