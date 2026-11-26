---
id: 01M8D2BDC7GJAACMCZFNF7WNTH
anchor: '@file'
created: 2026-11-26T23:16:48Z
norm: '1'
sig: 5a7b6ee0947aa502
body_hash: 729cbf02aa3cdca0
raw_hash: 6bf261fef8908b2e
lines: 1-44
---

Every terminal's process must be spawned on this one thread, which never ends: PR_SET_PDEATHSIG kills a child when the thread that forked it ends (T135). Fx.Effects.Live has the same starter for LocalShell.
