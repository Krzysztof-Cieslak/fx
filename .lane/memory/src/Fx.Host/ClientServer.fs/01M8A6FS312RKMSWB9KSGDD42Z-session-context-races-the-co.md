---
id: 01M8A6FS312RKMSWB9KSGDD42Z
anchor: '@file'
created: 2026-11-25T20:31:22Z
norm: '1'
sig: aea2e01ee52e4c76
body_hash: 614de14258e40994
raw_hash: 0a7071f538b0784d
vouched: 2026-11-27T12:12:18Z
lines: 1-453
---

session/context races the context log's subscription with the stream forwarder: both must honour the scope's cancellation, or the request never ends.
