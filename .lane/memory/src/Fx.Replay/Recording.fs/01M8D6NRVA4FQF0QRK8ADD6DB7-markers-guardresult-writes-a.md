---
id: 01M8D6NRVA4FQF0QRK8ADD6DB7
anchor: '@file'
created: 2026-11-27T00:32:22Z
norm: '1'
sig: 06e768d866788b80
body_hash: d378e3493e9f9e7c
raw_hash: 91c5ee2503508d28
vouched: 2026-11-28T13:01:56Z
lines: 1-749
---

Markers.guardResult writes an empty approval list as null, as the guard's unit was journaled before approvals were kept: recorded journals must keep replaying.
