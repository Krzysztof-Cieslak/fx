---
id: 01M8FT834F8K42VEMFR9FB6H4X
anchor: '@file'
created: 2026-11-28T00:52:54Z
norm: '1'
supersedes: 01M884S9V068XG7DCYCAP9CJAP
---

ProtocolError's and HostError's kinds and field names are the extension protocol's wire format (both directions: extension/* and host/*), which extension hosts in other languages implement (T115): add kinds, never rename them or their fields. Their ToString texts are what RemoteFailure and the SDKs report and the tests check.
