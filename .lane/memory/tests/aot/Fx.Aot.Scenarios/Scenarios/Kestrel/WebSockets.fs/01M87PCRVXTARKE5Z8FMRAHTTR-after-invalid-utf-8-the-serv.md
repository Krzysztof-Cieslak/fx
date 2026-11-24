---
id: 01M87PCRVXTARKE5Z8FMRAHTTR
anchor: '@file'
created: 2026-11-24T21:11:38Z
norm: '1'
sig: 586414d1cea680de
body_hash: ab20f09b1be518f5
raw_hash: 1ef8e38b10349039
lines: 1-191
---

After invalid UTF-8 the server WebSocket sends a 1007 close and aborts at once; the abort can overtake the close frame, so the client may see either. Do not print which one.
