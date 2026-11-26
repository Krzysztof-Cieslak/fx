---
id: 01M8C0B82PN9P7ZQM2XG9J6HBT
anchor: '@file'
created: 2026-11-26T13:22:31Z
norm: '1'
sig: 918e4a8de87248e2
body_hash: 67ecbce1ff6a1ca7
raw_hash: 41a9aef31cc953ca
lines: 1-1155
---

Markdown.render must never return a row wider than asked at any width (nested lists and quotes eat columns): render cuts every row with Row.fit last, and the property test checks widths down to 4.
