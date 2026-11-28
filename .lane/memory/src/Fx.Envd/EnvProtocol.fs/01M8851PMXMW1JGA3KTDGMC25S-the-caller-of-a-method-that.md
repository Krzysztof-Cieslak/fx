---
id: 01M8851PMXMW1JGA3KTDGMC25S
anchor: '@file'
created: 2026-11-25T01:27:44Z
norm: '1'
sig: 1c8cc5a01680bc71
body_hash: c86cb666d447088c
raw_hash: 61a68bc4c380d3c4
vouched: 2026-11-28T18:33:29Z
lines: 1-556
---

The caller of a method that moves bulk data opens the channel and names it in the request; the handler accepts it before anything else, and a caller whose call fails resets it. Unaccepted channels count against maxChannels until the connection ends.
