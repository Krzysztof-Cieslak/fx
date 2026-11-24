---
id: 01M87TM3DHCXEKAMAZ35YZNYD5
anchor: '@file'
created: 2026-11-24T22:25:32Z
norm: '1'
---

Close our copies of the console's pipe ends right after CreatePseudoConsole, and close the pseudo-console only once the program exited while the output thread keeps draining; otherwise the output never ends or ClosePseudoConsole blocks.
