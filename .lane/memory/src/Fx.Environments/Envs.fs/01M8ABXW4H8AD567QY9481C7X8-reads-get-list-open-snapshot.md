---
id: 01M8ABXW4H8AD567QY9481C7X8
anchor: '@file'
created: 2026-11-25T22:06:27Z
norm: '1'
---

Reads (Get, List, Open, Snapshot) must go through snapshot(), the newer of the actor's published state and the one a wait returned: the actor appends (waking log followers) before it publishes, so its published state can be behind a result already returned.
