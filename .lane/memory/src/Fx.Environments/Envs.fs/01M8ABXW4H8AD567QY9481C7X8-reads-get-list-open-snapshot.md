---
id: 01M8ABXW4H8AD567QY9481C7X8
anchor: '@file'
created: 2026-11-25T22:06:27Z
norm: '1'
sig: db88ec098994fb1c
body_hash: 6f09194aab0b8cf6
raw_hash: b439f3aea2ec1a2b
vouched: 2026-11-27T02:59:27Z
lines: 1-1269
---

Reads (Get, List, Open, Snapshot) must go through snapshot(), the newer of the actor's published state and the one a wait returned: the actor appends (waking log followers) before it publishes, so its published state can be behind a result already returned.
