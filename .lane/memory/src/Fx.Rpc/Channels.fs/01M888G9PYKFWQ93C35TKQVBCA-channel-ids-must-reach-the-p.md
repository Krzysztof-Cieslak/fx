---
id: 01M888G9PYKFWQ93C35TKQVBCA
anchor: '@file'
created: 2026-11-25T02:28:08Z
norm: '1'
sig: b70dad81299f621a
body_hash: 4c10dd36b882527f
raw_hash: 0a250e9f2db12303
vouched: 2026-11-27T11:48:28Z
lines: 1-468
---

Channel ids must reach the peer in increasing order: allocate the id and queue the open frame under one lock (urgently, so it never waits).
