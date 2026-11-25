---
id: 01M899TDGPGSKYHMX099KHWZ2D
anchor: '@file'
created: 2026-11-25T12:10:22Z
norm: '1'
---

A call is in doubt exactly when the journal has its callStarted and the context log has no receipt for it: journal the start before the call does anything, and never add a second end record that could disagree with the receipt.
