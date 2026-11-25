---
id: 01M88R1Y6H0VNB9WKEABAYHG8Y
anchor: '@file'
created: 2026-11-25T06:59:54Z
norm: '1'
sig: a87afd278bac8225
body_hash: 77f432e82503a068
raw_hash: cae9ad394e1b0fd5
vouched: 2026-11-25T09:29:44Z
lines: 1-470
---

Appending a log item may only append (role, part) pairs to the rendered history, and a reply's Shown blocks must render exactly as the suffix did (both through Piece.parts): otherwise a request stops being a prefix of the next, caches miss and Anthropic's prefix check on preserved thinking fails (400).
