---
id: 01M8851PMXMW1JGA3KTDGMC25S
anchor: '@file'
created: 2026-11-25T01:27:44Z
norm: '1'
sig: 1c8cc5a01680bc71
body_hash: a82d097998971fb3
raw_hash: dccbda75012ee1e5
vouched: 2026-11-25T03:18:44Z
lines: 1-556
---

The caller of a method that moves bulk data opens the channel and names it in the request; the handler accepts it before anything else, and a caller whose call fails resets it. Unaccepted channels count against maxChannels until the connection ends.
