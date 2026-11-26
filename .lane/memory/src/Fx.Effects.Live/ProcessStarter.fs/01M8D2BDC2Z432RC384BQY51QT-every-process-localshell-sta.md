---
id: 01M8D2BDC2Z432RC384BQY51QT
anchor: '@file'
created: 2026-11-26T23:16:48Z
norm: '1'
sig: 19f9ce21898e0254
body_hash: 7f0e5ffbb87c322f
raw_hash: 1874d6f52703d73f
lines: 1-46
---

Every process LocalShell starts must be forked on this one thread, which never ends: PR_SET_PDEATHSIG (bubblewrap's --die-with-parent) kills a child when the thread that forked it ends, and thread-pool threads end after 20 s idle (T135: kernels died between turns).
