---
id: 01M87PCRVZAR8CRHAKMX4QTSM1
anchor: '@file'
created: 2026-11-24T21:11:38Z
norm: '1'
sig: 36ac03da11c3f4ed
body_hash: 56a2582fe86384fd
raw_hash: dc91605b9d3fb0eb
lines: 1-138
---

The over-limit upload uses Expect: 100-continue so Kestrel answers 413 before the body is sent; without it the client can hit a connection reset instead of reading the 413.
