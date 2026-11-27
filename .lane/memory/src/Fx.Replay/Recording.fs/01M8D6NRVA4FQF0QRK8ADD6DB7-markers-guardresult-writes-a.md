---
id: 01M8D6NRVA4FQF0QRK8ADD6DB7
anchor: '@file'
created: 2026-11-27T00:32:22Z
norm: '1'
sig: 06e768d866788b80
body_hash: ffa0ee2ac4904e57
raw_hash: 6db1d7446e7db6e3
vouched: 2026-11-27T04:24:45Z
lines: 1-749
---

Markers.guardResult writes an empty approval list as null, as the guard's unit was journaled before approvals were kept: recorded journals must keep replaying.
