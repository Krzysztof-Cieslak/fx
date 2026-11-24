---
id: 01M87PCRVY20A6NDM5EXB2T2AV
anchor: '@file'
created: 2026-11-24T21:11:38Z
norm: '1'
sig: d01cd4ed7dd6ca02
body_hash: 95b4af985330cf58
raw_hash: 978d57bf647d0d9b
lines: 1-221
---

Clients set MaxResponseDrainSize = 0: otherwise disposing an unfinished event stream drains for up to 2 s before closing, and the server sees the disconnect late.
