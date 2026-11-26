---
id: 01M8D2BDC7GJAACMCZFNF7WNTH
anchor: '@file'
created: 2026-11-26T23:16:48Z
norm: '1'
---

Every terminal's process must be spawned on this one thread, which never ends: PR_SET_PDEATHSIG kills a child when the thread that forked it ends (T135). Fx.Effects.Live has the same starter for LocalShell.
