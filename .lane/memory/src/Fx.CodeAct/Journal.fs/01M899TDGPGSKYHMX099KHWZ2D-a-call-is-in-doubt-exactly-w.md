---
id: 01M899TDGPGSKYHMX099KHWZ2D
anchor: '@file'
created: 2026-11-25T12:10:22Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: b66284ae0f60279e
raw_hash: 935882bd23457bb1
vouched: 2026-11-27T01:06:19Z
lines: 1-505
---

A call is in doubt exactly when the journal has its callStarted and the context log has no receipt for it: journal the start before the call does anything, and never add a second end record that could disagree with the receipt.
