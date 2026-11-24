---
id: 01M87PCRVY20A6NDM5EXB2T2AV
anchor: '@file'
created: 2026-11-24T21:11:38Z
norm: '1'
---

Clients set MaxResponseDrainSize = 0: otherwise disposing an unfinished event stream drains for up to 2 s before closing, and the server sees the disconnect late.
