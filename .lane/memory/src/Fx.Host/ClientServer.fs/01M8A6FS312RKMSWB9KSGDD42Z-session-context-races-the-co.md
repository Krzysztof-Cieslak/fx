---
id: 01M8A6FS312RKMSWB9KSGDD42Z
anchor: '@file'
created: 2026-11-25T20:31:22Z
norm: '1'
---

session/context races the context log's subscription with the stream forwarder: both must honour the scope's cancellation, or the request never ends.
