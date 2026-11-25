---
id: 01M89113BX3TR19CER6BE64VGP
anchor: embed
created: 2026-11-25T09:36:44Z
norm: '1'
---

Stop aborts both ends, waits for the server side to end, then disposes envd (kills its processes, closes the blob store): only then may another envd start on the same state directory.
