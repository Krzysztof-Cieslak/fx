---
id: 01M8D6NRVA4FQF0QRK8ADD6DB7
anchor: '@file'
created: 2026-11-27T00:32:22Z
norm: '1'
sig: 06e768d866788b80
body_hash: cd93bd6013a361da
raw_hash: 4982fe58a7975d14
vouched: 2026-11-27T12:12:19Z
lines: 1-749
---

Markers.guardResult writes an empty approval list as null, as the guard's unit was journaled before approvals were kept: recorded journals must keep replaying.
