---
id: 01M899TDGPGSKYHMX099KHWZ2D
anchor: '@file'
created: 2026-11-25T12:10:22Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: f820070756ed26ef
raw_hash: a924bb11b0c7d99f
lines: 1-505
---

A call is in doubt exactly when the journal has its callStarted and the context log has no receipt for it: journal the start before the call does anything, and never add a second end record that could disagree with the receipt.
