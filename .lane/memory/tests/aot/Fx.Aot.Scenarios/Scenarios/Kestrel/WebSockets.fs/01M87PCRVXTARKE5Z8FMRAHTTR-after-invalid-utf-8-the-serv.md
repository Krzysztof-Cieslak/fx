---
id: 01M87PCRVXTARKE5Z8FMRAHTTR
anchor: '@file'
created: 2026-11-24T21:11:38Z
norm: '1'
---

After invalid UTF-8 the server WebSocket sends a 1007 close and aborts at once; the abort can overtake the close frame, so the client may see either. Do not print which one.
