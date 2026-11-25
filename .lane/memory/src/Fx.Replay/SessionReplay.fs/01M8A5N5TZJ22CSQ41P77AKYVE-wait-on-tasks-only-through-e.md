---
id: 01M8A5N5TZJ22CSQ41P77AKYVE
anchor: '@file'
created: 2026-11-25T20:16:51Z
norm: '1'
sig: 06e768d866788b80
body_hash: 14e4004c5486882c
raw_hash: e133ad87356f403e
lines: 1-802
---

Wait on tasks only through Eff.ofCancellableTask (WaitAsync): Eff.timeout's race waits for every child, so a non-cancellable wait turns a stall into a hang.
