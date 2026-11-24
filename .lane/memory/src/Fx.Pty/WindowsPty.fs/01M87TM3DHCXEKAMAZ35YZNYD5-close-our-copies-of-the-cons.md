---
id: 01M87TM3DHCXEKAMAZ35YZNYD5
anchor: '@file'
created: 2026-11-24T22:25:32Z
norm: '1'
sig: f855ba61466c04da
body_hash: 05d0d2285b94c8f6
raw_hash: 9c7eccda6a3c9db6
lines: 1-376
---

Close our copies of the console's pipe ends right after CreatePseudoConsole, and close the pseudo-console only once the program exited while the output thread keeps draining; otherwise the output never ends or ClosePseudoConsole blocks.
