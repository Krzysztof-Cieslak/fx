---
id: 01M899TDGPGSKYHMX099KHWZ2D
anchor: '@file'
created: 2026-11-25T12:10:22Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: e043eb0d12bf9821
raw_hash: d2537887980ff8b4
vouched: 2026-11-26T23:31:33Z
lines: 1-505
---

A call is in doubt exactly when the journal has its callStarted and the context log has no receipt for it: journal the start before the call does anything, and never add a second end record that could disagree with the receipt.
