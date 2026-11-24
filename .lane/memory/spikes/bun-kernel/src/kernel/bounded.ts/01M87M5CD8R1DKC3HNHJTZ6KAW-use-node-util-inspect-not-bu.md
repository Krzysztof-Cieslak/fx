---
id: 01M87M5CD8R1DKC3HNHJTZ6KAW
anchor: '@file'
created: 2026-11-24T20:32:38Z
norm: '1'
---

Use node:util inspect, not Bun.inspect, for values from the vm context: Bun.inspect 1.4.5 prints Object.prototype's methods for objects from another realm.
