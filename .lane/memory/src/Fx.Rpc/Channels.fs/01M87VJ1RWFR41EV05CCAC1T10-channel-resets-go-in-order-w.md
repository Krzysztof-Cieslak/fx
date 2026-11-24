---
id: 01M87VJ1RWFR41EV05CCAC1T10
anchor: '@file'
created: 2026-11-24T22:41:53Z
norm: '1'
sig: b70dad81299f621a
body_hash: bb46899e150ca922
raw_hash: d84298cfb21657fa
lines: 1-474
---

Channel resets go in order with data, not urgently: a reset that overtakes the channel's open frame is a frame for a never-opened channel, a protocol violation at the peer.
