---
id: 01M8EDB5J09MDQVSCQCTQ455TN
anchor: '@file'
created: 2026-11-27T11:48:09Z
norm: '1'
sig: b70dad81299f621a
body_hash: 4c10dd36b882527f
raw_hash: 0a250e9f2db12303
lines: 1-493
---

An end releases a channel before it wakes a reader with the end (EndInput) and before it sends its close (CloseOutput) or a reset, so whoever sees a channel end sees it released at both ends; tests check Peer.OpenChannels right after a transfer. Keep that order.
