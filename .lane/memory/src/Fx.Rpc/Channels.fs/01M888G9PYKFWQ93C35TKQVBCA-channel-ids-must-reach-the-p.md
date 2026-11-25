---
id: 01M888G9PYKFWQ93C35TKQVBCA
anchor: '@file'
created: 2026-11-25T02:28:08Z
norm: '1'
---

Channel ids must reach the peer in increasing order: allocate the id and queue the open frame under one lock (urgently, so it never waits).
