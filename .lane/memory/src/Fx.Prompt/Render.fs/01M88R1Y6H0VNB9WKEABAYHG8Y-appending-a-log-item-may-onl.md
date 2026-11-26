---
id: 01M88R1Y6H0VNB9WKEABAYHG8Y
anchor: '@file'
created: 2026-11-25T06:59:54Z
norm: '1'
sig: a87afd278bac8225
body_hash: 8ff64c670027a6e2
raw_hash: 12a444c8d7e7b878
vouched: 2026-11-26T03:48:17Z
lines: 1-470
---

Appending a log item may only append (role, part) pairs to the rendered history, and a reply's Shown blocks must render exactly as the suffix did (both through Piece.parts): otherwise a request stops being a prefix of the next, caches miss and Anthropic's prefix check on preserved thinking fails (400).
