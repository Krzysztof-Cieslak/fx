---
id: 01M8851PMXMW1JGA3KTDGMC25S
anchor: '@file'
created: 2026-11-25T01:27:44Z
norm: '1'
---

The caller of a method that moves bulk data opens the channel and names it in the request; the handler accepts it before anything else, and a caller whose call fails resets it. Unaccepted channels count against maxChannels until the connection ends.
